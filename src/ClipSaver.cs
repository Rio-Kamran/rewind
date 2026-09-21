using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Rewind
{
    /// <summary>What a save produced. Immutable.</summary>
    internal sealed class SavedClip
    {
        public readonly string Path;
        public readonly long Bytes;
        /// <summary>How much time the ring held when the hotkey was pressed.</summary>
        public readonly TimeSpan Buffered;
        /// <summary>How many seconds were asked for.</summary>
        public readonly int Seconds;
        /// <summary>True when the clip starts exactly on a keyframe.</summary>
        public readonly bool CleanCut;
        public readonly long TrimmedBytes;

        public SavedClip(string path, long bytes, TimeSpan buffered, int seconds, bool cleanCut, long trimmedBytes)
        {
            Path = path;
            Bytes = bytes;
            Buffered = buffered;
            Seconds = seconds;
            CleanCut = cleanCut;
            TrimmedBytes = trimmedBytes;
        }
    }

    /// <summary>
    /// Turns the ring buffer into a clip: find the first keyframe in the last N seconds, then pour
    /// the MPEG-TS bytes from there straight into an ffmpeg that wraps them in an MP4 without
    /// re-encoding. No temp file, no copy of the buffer: the ring's own chunks are written as-is.
    /// </summary>
    internal static class ClipSaver
    {
        private const int MinUsefulBytes = 200 * 1024;
        private const int RemuxTimeoutMs = 60000;

        public static SavedClip Save(ChunkRing ring, Config config, string ffmpegPath, IList<string> audioLabels, int seconds)
        {
            if (ring == null) throw new ArgumentNullException("ring");
            if (config == null) throw new ArgumentNullException("config");
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");
            if (audioLabels == null) throw new ArgumentNullException("audioLabels");
            if (seconds < 1) throw new ArgumentOutOfRangeException("seconds");

            // One extra second: the clip can only start at a keyframe, and there is one per second.
            var buffered = ring.Span;
            var chunks = ring.Chunks(TimeSpan.FromSeconds(seconds + 1), DateTime.UtcNow);
            long total = 0;
            foreach (var chunk in chunks) total += chunk.Data.Length;
            if (total < MinUsefulBytes)
                throw new InvalidOperationException("Nothing to save yet: the buffer is still filling (" + total / 1024 + " KB).");
            if (CaptureFault.LooksAudioOnly(total, DateTime.UtcNow - chunks[0].AtUtc, config.BitrateMbps)
                || CaptureFault.LooksAudioOnly(ring.Bytes, buffered, config.BitrateMbps))
                throw new InvalidOperationException("No video in the buffer: the screen capture is restarting. Try again in a few seconds.");

            var plan = TsCut.Plan(chunks);

            Directory.CreateDirectory(config.ClipsFolder);
            var game = ForegroundApp.Name();
            var name = string.Format("Rewind {0}{1:yyyy-MM-dd HH-mm-ss}.mp4", game.Length > 0 ? game + " " : "", DateTime.Now);
            var mp4 = Path.Combine(config.ClipsFolder, name);

            Remux(ffmpegPath, FfmpegArgs.Remux(mp4, audioLabels, config.Codec), mp4, chunks, plan);
            return new SavedClip(mp4, new FileInfo(mp4).Length, buffered, seconds, plan.Clean, plan.TrimmedBytes);
        }

        private static void Remux(string ffmpegPath, string args, string mp4, IList<Chunk> chunks, CutPlan plan)
        {
            var info = new ProcessStartInfo(ffmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true
            };
            var stderr = new StringBuilder();
            using (var process = Process.Start(info))
            {
                if (process == null) throw new InvalidOperationException("ffmpeg didn't start.");
                var drain = new Thread(() => Collect(process, stderr)) { IsBackground = true, Name = "rewind-remux-stderr" };
                drain.Start();

                var fed = true;
                try
                {
                    var stdin = process.StandardInput.BaseStream;
                    Feed(stdin, chunks, plan);
                    stdin.Flush();
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    fed = false; // ffmpeg closed the pipe early: its stderr says why
                }

                if (!process.WaitForExit(RemuxTimeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new InvalidOperationException("ffmpeg took over a minute to write the clip.");
                }
                drain.Join(1000);
                string errorText;
                lock (stderr) errorText = stderr.ToString().Trim();
                if (process.ExitCode != 0 || !File.Exists(mp4) || !fed)
                {
                    DeleteIfEmpty(mp4);
                    throw new InvalidOperationException(string.Format("ffmpeg couldn't write the clip (code {0}): {1}",
                        process.ExitCode, Tail(errorText)));
                }
                if (errorText.Length > 0) Log.Warn("remux: " + errorText);
            }
        }

        /// <summary>Writes the tables, then every chunk from the cut on. Chunks are already pipe-sized (64 KB).</summary>
        private static void Feed(Stream stdin, IList<Chunk> chunks, CutPlan plan)
        {
            if (plan.Prefix.Length > 0) stdin.Write(plan.Prefix, 0, plan.Prefix.Length);
            for (var i = plan.StartChunk; i < chunks.Count; i++)
            {
                var data = chunks[i].Data;
                var offset = i == plan.StartChunk ? plan.StartOffset : 0;
                stdin.Write(data, offset, data.Length - offset);
            }
        }

        /// <summary>A failed save leaves a 0-byte MP4 behind; the clip window would list it as a clip that can't play.</summary>
        private static void DeleteIfEmpty(string mp4)
        {
            try
            {
                var file = new FileInfo(mp4);
                if (file.Exists && file.Length == 0) file.Delete();
            }
            catch (IOException error)
            {
                Log.Warn("couldn't remove the empty clip " + mp4 + ": " + error.Message);
            }
            catch (UnauthorizedAccessException error)
            {
                Log.Warn("couldn't remove the empty clip " + mp4 + ": " + error.Message);
            }
        }

        private static void Collect(Process process, StringBuilder into)
        {
            try
            {
                string line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    lock (into) into.AppendLine(line);
                }
            }
            catch (IOException)
            {
                // The pipe went away with the process: nothing more to read.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static string Tail(string text)
        {
            var lines = text.Split('\n');
            return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
        }
    }
}
