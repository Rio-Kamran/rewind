using System;
using System.IO;

namespace Rewind
{
    /// <summary>What "Copy for Discord" produced. Immutable.</summary>
    internal sealed class ShareResult
    {
        /// <summary>The file to hand over: the clip itself when it already fits, else the shrunk copy.</summary>
        public readonly string Path;
        public readonly bool Encoded;
        public readonly long Bytes;
        /// <summary>How it was shrunk, or null.</summary>
        public readonly SharePlan Plan;

        public ShareResult(string path, bool encoded, long bytes, SharePlan plan)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path");
            Path = path;
            Encoded = encoded;
            Bytes = bytes;
            Plan = plan;
        }
    }

    /// <summary>
    /// Gets a clip under Discord's upload limit: nothing to do if it already fits, otherwise one
    /// NVENC pass at the bitrate that fits (SharePlan), and a second, smaller pass if the first
    /// still came out too big. Runs on a background thread; progress lines go to the window.
    /// </summary>
    internal static class ShareExport
    {
        public const string Suffix = "share";
        private const int TimeoutMs = 600000;

        public static ShareResult Run(string ffmpegPath, ClipInfo clip, int maxMb, Action<string> progress)
        {
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");
            if (clip == null) throw new ArgumentNullException("clip");
            if (maxMb < 1) throw new ArgumentOutOfRangeException("maxMb");
            if (progress == null) throw new ArgumentNullException("progress");

            var bytes = new FileInfo(clip.Path).Length;
            var limit = maxMb * 1000000L;
            if (clip.IsImage || bytes <= limit) return new ShareResult(clip.Path, false, bytes, null);

            progress("Reading the clip…");
            var media = ClipProbe.Inspect(ffmpegPath, clip.Path);
            if (!media.Duration.HasValue) throw new InvalidOperationException("Couldn't read how long the clip is.");
            var plan = SharePlan.For(bytes, media.Duration.Value.TotalSeconds, maxMb, media.Height, media.AudioTracks);
            var output = ClipLibrary.CopyName(clip.Path, Suffix, File.Exists);

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                progress(string.Format("Shrinking to fit {0} MB ({1}){2}…", maxMb, plan, attempt == 2 ? ", second try" : ""));
                Log.Info(string.Format("share: {0} -> {1}: {2}", clip.FileName, Path.GetFileName(output), plan));
                FfmpegRun.Execute(ffmpegPath, FfmpegArgs.Share(clip.Path, output, plan, media.AudioTracks), output, TimeoutMs);
                var made = new FileInfo(output).Length;
                if (made <= limit)
                {
                    Log.Info(string.Format("share: {0} is {1:0.0} MB", Path.GetFileName(output), made / 1048576.0));
                    return new ShareResult(output, true, made, plan);
                }
                Log.Warn(string.Format("share: {0} came out at {1:0.0} MB, over the limit", Path.GetFileName(output), made / 1048576.0));
                plan = plan.Shrunk(made);
            }
            throw new InvalidOperationException("Even the second try came out over " + maxMb + " MB. Trim the clip shorter and try again.");
        }
    }
}
