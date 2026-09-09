using System;
using System.IO;

namespace Rewind
{
    /// <summary>Finds ffmpeg.exe: an explicit config path first, then every folder on PATH.</summary>
    internal static class FfmpegLocator
    {
        public static string Find(string configured)
        {
            if (!string.IsNullOrEmpty(configured))
            {
                if (File.Exists(configured)) return Path.GetFullPath(configured);
                throw new InvalidOperationException("config.txt points ffmpeg= at a file that isn't there: " + configured);
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var raw in path.Split(';'))
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    var candidate = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters a path can't have: skip it, keep looking.
                }
            }

            throw new InvalidOperationException(
                "ffmpeg.exe wasn't found on PATH. Install it (winget install ffmpeg) or set ffmpeg= in config.txt.");
        }
    }
}
