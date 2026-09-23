using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;

namespace Rewind
{
    /// <summary>
    /// Medal's screenshot key, done the Rewind way: the newest frame is already in the ring as
    /// encoded video, so the bytes from the last keyframe on go through ffmpeg, which hands back
    /// every decoded frame as a BMP on stdout; the last one is the screen as it is right now
    /// (within a frame or two). Saved as a PNG next to the clips.
    /// </summary>
    internal static class Screenshot
    {
        private const int TimeoutMs = 20000;
        private const int BmpHeaderBytes = 14;
        private static readonly TimeSpan Reach = TimeSpan.FromSeconds(3); // keyframes are 1 s apart; the ring is coarse

        public static string Take(ChunkRing ring, Config config, string ffmpegPath)
        {
            if (ring == null) throw new ArgumentNullException("ring");
            if (config == null) throw new ArgumentNullException("config");
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");

            var chunks = ring.Chunks(Reach, DateTime.UtcNow);
            long total = 0;
            foreach (var chunk in chunks) total += chunk.Data.Length;
            if (total == 0) throw new InvalidOperationException("Nothing captured yet.");
            if (CaptureFault.LooksAudioOnly(ring.Bytes, ring.Span, config.BitrateMbps))
                throw new InvalidOperationException("No video in the buffer: the screen capture is restarting. Try again in a few seconds.");
            var plan = TsCut.PlanLast(chunks);
            if (!plan.Clean) throw new InvalidOperationException("No keyframe in the buffer yet; try again in a second.");

            var bmp = Decode(ffmpegPath, chunks, plan);
            Directory.CreateDirectory(config.ClipsFolder);
            var path = Path.Combine(config.ClipsFolder, ClipLibrary.NewName(ForegroundApp.Name(), DateTime.Now, "", "png"));
            using (var stream = new MemoryStream(bmp))
            using (var image = Image.FromStream(stream))
                image.Save(path, ImageFormat.Png);
            return path;
        }

        /// <summary>Feeds the slice to ffmpeg on one thread and keeps the last BMP it writes on another (both directions at once, or the pipes deadlock).</summary>
        private static byte[] Decode(string ffmpegPath, IList<Chunk> chunks, CutPlan plan)
        {
            var info = new ProcessStartInfo(ffmpegPath, FfmpegArgs.Screenshot())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var stderr = new StringBuilder();
            using (var process = Process.Start(info))
            {
                if (process == null) throw new InvalidOperationException("ffmpeg didn't start.");
                var drain = new Thread(() => ClipSaver.Collect(process, stderr)) { IsBackground = true, Name = "rewind-shot-stderr" };
                drain.Start();
                var feeder = new Thread(() =>
                {
                    try
                    {
                        var stdin = process.StandardInput.BaseStream;
                        ClipSaver.Feed(stdin, chunks, plan);
                        stdin.Flush();
                        process.StandardInput.Close();
                    }
                    catch (IOException) { /* ffmpeg closed the pipe: stderr says why */ }
                    catch (ObjectDisposedException) { }
                }) { IsBackground = true, Name = "rewind-shot-feed" };
                feeder.Start();

                var last = LastBmp(process.StandardOutput.BaseStream);
                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new InvalidOperationException("ffmpeg took too long to decode the frame.");
                }
                feeder.Join(1000);
                drain.Join(1000);
                if (last == null)
                {
                    string text;
                    lock (stderr) text = stderr.ToString().Trim();
                    throw new InvalidOperationException("ffmpeg gave no frame: " + ClipSaver.Tail(text));
                }
                return last;
            }
        }

        /// <summary>
        /// Walks a stream of BMP files back to back (what ffmpeg's image2pipe + bmp writes) and
        /// returns the last complete one, or null. Each BMP says its own size in its header, so no
        /// frame ever needs to be held except the newest. Pure, tested.
        /// </summary>
        public static byte[] LastBmp(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            byte[] last = null;
            var header = new byte[BmpHeaderBytes];
            while (true)
            {
                if (!Fill(stream, header, BmpHeaderBytes)) return last;
                if (header[0] != (byte)'B' || header[1] != (byte)'M') return last; // lost sync: keep what we have
                var size = (int)(header[2] | (uint)header[3] << 8 | (uint)header[4] << 16 | (uint)header[5] << 24);
                if (size < BmpHeaderBytes) return last;
                var frame = new byte[size];
                Buffer.BlockCopy(header, 0, frame, 0, BmpHeaderBytes);
                if (!Fill(stream, frame, size - BmpHeaderBytes, BmpHeaderBytes)) return last; // torn tail: the one before it stands
                last = frame;
            }
        }

        private static bool Fill(Stream stream, byte[] into, int count)
        {
            return Fill(stream, into, count, 0);
        }

        private static bool Fill(Stream stream, byte[] into, int count, int offset)
        {
            var got = 0;
            while (got < count)
            {
                var read = stream.Read(into, offset + got, count - got);
                if (read <= 0) return false;
                got += read;
            }
            return true;
        }
    }
}
