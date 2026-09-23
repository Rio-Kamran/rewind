using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Rewind
{
    /// <summary>What one look at a clip found. Immutable.</summary>
    internal sealed class ProbeResult
    {
        /// <summary>Path of the thumbnail JPEG, or null when ffmpeg couldn't make one.</summary>
        public readonly string ThumbnailPath;
        public readonly TimeSpan? Duration;

        public ProbeResult(string thumbnailPath, TimeSpan? duration)
        {
            ThumbnailPath = thumbnailPath;
            Duration = duration;
        }
    }

    /// <summary>What ffmpeg says a media file is. Immutable.</summary>
    internal sealed class MediaInfo
    {
        public readonly TimeSpan? Duration;
        /// <summary>Video height in pixels; 0 if unknown.</summary>
        public readonly int Height;
        public readonly int AudioTracks;

        public MediaInfo(TimeSpan? duration, int height, int audioTracks)
        {
            Duration = duration;
            Height = height;
            AudioTracks = audioTracks;
        }
    }

    /// <summary>
    /// Asks ffmpeg about clips: one run per clip gives a thumbnail and (from the same run's log)
    /// the duration; FrameAt() fetches a single frame in memory for the trim preview; Inspect()
    /// reads length, size and track count for the share export. The command lines and parsers
    /// are pure and tested.
    /// </summary>
    internal static class ClipProbe
    {
        public const int ThumbWidth = 480; // tiles stretch to ~350-560 px, so the source must not be tiny
        public const int PreviewWidth = 640;
        private const int TimeoutMs = 20000;
        private static readonly Regex DurationPattern = new Regex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
        private static readonly Regex VideoSizePattern = new Regex(@"Video:.*?\b(\d{2,5})x(\d{2,5})\b", RegexOptions.CultureInvariant);
        private static readonly Regex AudioPattern = new Regex(@"Stream #\d+:\d+.*?: Audio:", RegexOptions.CultureInvariant);

        /// <summary>The height from the first "Video: ... 2560x1440" line, or 0.</summary>
        public static int ParseHeight(string ffmpegOutput)
        {
            if (string.IsNullOrEmpty(ffmpegOutput)) return 0;
            var match = VideoSizePattern.Match(ffmpegOutput);
            return match.Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>How many "Stream #0:N: Audio:" lines there are.</summary>
        public static int ParseAudioTracks(string ffmpegOutput)
        {
            return string.IsNullOrEmpty(ffmpegOutput) ? 0 : AudioPattern.Matches(ffmpegOutput).Count;
        }

        /// <summary>ffmpeg -i on its own: it refuses to run, but prints everything about the file first.</summary>
        public static string InspectArgs(string clipPath)
        {
            if (string.IsNullOrEmpty(clipPath)) throw new ArgumentException("clipPath");
            return "-hide_banner -nostdin -i " + FfmpegArgs.Quote(clipPath);
        }

        /// <summary>Duration, height and audio-track count of a file. Throws InvalidOperationException when ffmpeg can't read it.</summary>
        public static MediaInfo Inspect(string ffmpegPath, string clipPath)
        {
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");
            string output;
            Run(ffmpegPath, InspectArgs(clipPath), null, out output); // exit code 1 is normal: no output file was asked for
            var duration = ParseDuration(output);
            var height = ParseHeight(output);
            if (!duration.HasValue && height == 0)
                throw new InvalidOperationException("ffmpeg couldn't read the file: " + Tail(output));
            return new MediaInfo(duration, height, ParseAudioTracks(output));
        }

        /// <summary>The "Duration: 00:01:00.46" line ffmpeg prints for an input, as a TimeSpan; null if absent.</summary>
        public static TimeSpan? ParseDuration(string ffmpegOutput)
        {
            if (string.IsNullOrEmpty(ffmpegOutput)) return null;
            var match = DurationPattern.Match(ffmpegOutput);
            if (!match.Success) return null;
            var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            return TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds);
        }

        /// <summary>A stable name for a clip's thumbnail: changes when the file does.</summary>
        public static string ThumbKey(string path, long bytes, DateTime lastWriteUtc)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path");
            var text = path.ToLowerInvariant() + "|" + bytes.ToString(CultureInfo.InvariantCulture) + "|"
                + lastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|w" + ThumbWidth.ToString(CultureInfo.InvariantCulture);
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>One frame half a second in, scaled to the thumbnail width. Default log level so the Duration line shows.</summary>
        public static string ThumbArgs(string clipPath, string thumbPath)
        {
            return ThumbArgs(clipPath, thumbPath, false);
        }

        /// <summary>The same for a still image (a screenshot): no seeking into it, there is only the one frame.</summary>
        public static string ThumbArgs(string clipPath, string thumbPath, bool stillImage)
        {
            if (string.IsNullOrEmpty(clipPath)) throw new ArgumentException("clipPath");
            if (string.IsNullOrEmpty(thumbPath)) throw new ArgumentException("thumbPath");
            return string.Format(CultureInfo.InvariantCulture,
                "-hide_banner -nostdin -y {3}-i {0} -frames:v 1 -vf scale={1}:-2 -q:v 4 {2}",
                FfmpegArgs.Quote(clipPath), ThumbWidth, FfmpegArgs.Quote(thumbPath), stillImage ? "" : "-ss 0.5 ");
        }

        /// <summary>One frame at a time, as a JPEG on stdout.</summary>
        public static string FrameArgs(string clipPath, double seconds)
        {
            if (string.IsNullOrEmpty(clipPath)) throw new ArgumentException("clipPath");
            if (seconds < 0) throw new ArgumentOutOfRangeException("seconds");
            return string.Format(CultureInfo.InvariantCulture,
                "-hide_banner -loglevel error -nostdin -ss {0} -i {1} -frames:v 1 -vf scale={2}:-2 -f image2pipe -c:v mjpeg -q:v 4 pipe:1",
                seconds.ToString("0.###", CultureInfo.InvariantCulture), FfmpegArgs.Quote(clipPath), PreviewWidth);
        }

        /// <summary>Makes (or reuses) the thumbnail for a clip and reads its duration. Never throws; null parts mean "couldn't".</summary>
        public static ProbeResult Probe(string ffmpegPath, ClipInfo clip, string thumbFolder)
        {
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");
            if (clip == null) throw new ArgumentNullException("clip");
            if (string.IsNullOrEmpty(thumbFolder)) throw new ArgumentException("thumbFolder");
            try
            {
                Directory.CreateDirectory(thumbFolder);
                var thumb = Path.Combine(thumbFolder, ThumbKey(clip.Path, clip.Bytes, File.GetLastWriteTimeUtc(clip.Path)) + ".jpg");
                string output;
                var code = Run(ffmpegPath, ThumbArgs(clip.Path, thumb, clip.IsImage), null, out output);
                var duration = clip.IsImage ? null : ParseDuration(output);
                if (code != 0 || !File.Exists(thumb))
                {
                    Log.Warn("no thumbnail for " + clip.FileName + ": " + Tail(output));
                    return new ProbeResult(null, duration);
                }
                return new ProbeResult(thumb, duration);
            }
            catch (Exception error)
            {
                Log.Warn("probe failed for " + clip.FileName + ": " + error.Message);
                return new ProbeResult(null, null);
            }
        }

        /// <summary>The frame at a moment, as JPEG bytes; empty when ffmpeg couldn't produce one.</summary>
        public static byte[] FrameAt(string ffmpegPath, string clipPath, double seconds)
        {
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");
            try
            {
                var frame = new MemoryStream();
                string output;
                var code = Run(ffmpegPath, FrameArgs(clipPath, seconds), frame, out output);
                if (code != 0) Log.Warn("no frame at " + seconds + " s: " + Tail(output));
                return frame.ToArray();
            }
            catch (Exception error)
            {
                Log.Warn("frame fetch failed: " + error.Message);
                return new byte[0];
            }
        }

        /// <summary>Runs ffmpeg, collecting stderr (and stdout into the stream when given). -1 = timed out.</summary>
        private static int Run(string ffmpegPath, string args, Stream stdout, out string stderrText)
        {
            var info = new ProcessStartInfo(ffmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = stdout != null
            };
            var stderr = new StringBuilder();
            using (var process = Process.Start(info))
            {
                if (process == null) throw new InvalidOperationException("ffmpeg didn't start.");
                var drain = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = process.StandardError.ReadLine()) != null) lock (stderr) stderr.AppendLine(line);
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                }) { IsBackground = true, Name = "rewind-probe-stderr" };
                drain.Start();
                if (stdout != null) process.StandardOutput.BaseStream.CopyTo(stdout);
                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    lock (stderr) stderrText = stderr.ToString();
                    return -1;
                }
                drain.Join(1000);
                lock (stderr) stderrText = stderr.ToString();
                return process.ExitCode;
            }
        }

        private static string Tail(string text)
        {
            var lines = (text ?? "").Trim().Split('\n');
            return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
        }
    }
}
