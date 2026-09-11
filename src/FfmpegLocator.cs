using System;
using System.Collections.Generic;
using System.IO;

namespace Rewind
{
    /// <summary>
    /// Finds ffmpeg.exe: an explicit config path, else next to Rewind.exe, else the copy Rewind
    /// downloaded itself, else every folder on PATH.
    /// </summary>
    internal static class FfmpegLocator
    {
        /// <summary>Where to look, in order. Pure: the caller checks which exist.</summary>
        public static IList<string> Candidates(string configured, string appDir, string fetchedFolder, string pathVariable)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(configured)) list.Add(configured);
            if (!string.IsNullOrEmpty(appDir)) list.Add(Path.Combine(appDir, "ffmpeg.exe"));
            if (!string.IsNullOrEmpty(fetchedFolder)) list.Add(Path.Combine(fetchedFolder, "ffmpeg.exe"));
            foreach (var raw in (pathVariable ?? "").Split(';'))
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    list.Add(Path.Combine(dir, "ffmpeg.exe"));
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters a path can't have: skip it, keep looking.
                }
            }
            return list.AsReadOnly();
        }

        /// <summary>The first candidate that exists, or null. A configured path that is missing throws: that's a typo to fix.</summary>
        public static string TryFind(string configured)
        {
            if (!string.IsNullOrEmpty(configured))
            {
                if (File.Exists(configured)) return Path.GetFullPath(configured);
                throw new InvalidOperationException("config.txt points ffmpeg= at a file that isn't there: " + configured);
            }
            var candidates = Candidates(null, AppDomain.CurrentDomain.BaseDirectory, FfmpegFetcher.DefaultFolder,
                Environment.GetEnvironmentVariable("PATH"));
            foreach (var candidate in candidates)
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        /// <summary>Like TryFind, but a missing ffmpeg is an error with a plain message.</summary>
        public static string Find(string configured)
        {
            var found = TryFind(configured);
            if (found != null) return found;
            throw new InvalidOperationException("ffmpeg.exe wasn't found next to Rewind, in " + FfmpegFetcher.DefaultFolder
                + " or on PATH. Start Rewind normally and it downloads one; or install it (winget install yt-dlp.FFmpeg) or set ffmpeg= in config.txt.");
        }
    }
}
