using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Rewind
{
    /// <summary>One clip on disk. Immutable; WithDuration() returns a copy that knows its length.</summary>
    internal sealed class ClipInfo
    {
        public readonly string Path;
        public readonly string FileName;
        /// <summary>The game the file name says it was, or empty.</summary>
        public readonly string Game;
        /// <summary>"trim" and the like: whatever follows the timestamp in the name.</summary>
        public readonly string Suffix;
        public readonly DateTime Taken;
        public readonly long Bytes;
        /// <summary>Known once probed; null until then.</summary>
        public readonly TimeSpan? Duration;

        public ClipInfo(string path, string game, string suffix, DateTime taken, long bytes, TimeSpan? duration)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path");
            Path = path;
            FileName = System.IO.Path.GetFileName(path);
            Game = game ?? "";
            Suffix = suffix ?? "";
            Taken = taken;
            Bytes = bytes;
            Duration = duration;
        }

        /// <summary>"Fortnite", "Desktop" for unnamed clips, plus the suffix: "Fortnite (trim)".</summary>
        public string Title
        {
            get
            {
                var title = Game.Length > 0 ? Game : "Desktop";
                return Suffix.Length > 0 ? title + " (" + Suffix + ")" : title;
            }
        }

        public ClipInfo WithDuration(TimeSpan duration)
        {
            return new ClipInfo(Path, Game, Suffix, Taken, Bytes, duration);
        }
    }

    /// <summary>Lists the clips folder and reads what the file names say. Name parsing is pure and tested.</summary>
    internal static class ClipLibrary
    {
        private static readonly Regex NamePattern = new Regex(
            @"^Rewind (?:(?<game>.+?) )?(?<date>\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2})(?: (?<suffix>.+?))?\.mp4$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Every .mp4 in the folder, newest first. A missing or unreadable folder is an empty list.</summary>
        public static IList<ClipInfo> Scan(string folder)
        {
            var clips = new List<ClipInfo>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return clips.AsReadOnly();
            try
            {
                foreach (var path in Directory.GetFiles(folder, "*.mp4"))
                {
                    var file = new FileInfo(path);
                    clips.Add(Describe(path, file.Length, file.LastWriteTime));
                }
            }
            catch (IOException error)
            {
                Log.Warn("couldn't list clips: " + error.Message);
            }
            catch (UnauthorizedAccessException error)
            {
                Log.Warn("couldn't list clips: " + error.Message);
            }
            clips.Sort((a, b) => b.Taken.CompareTo(a.Taken));
            return clips.AsReadOnly();
        }

        /// <summary>What the name says, falling back to the file's own time for clips Rewind didn't name.</summary>
        public static ClipInfo Describe(string path, long bytes, DateTime lastWrite)
        {
            string game, suffix;
            DateTime taken;
            if (!TryParseName(Path.GetFileName(path), out game, out taken, out suffix))
            {
                game = "";
                suffix = "";
                taken = lastWrite;
            }
            return new ClipInfo(path, game, suffix, taken, bytes, null);
        }

        /// <summary>"Rewind Fortnite 2026-09-08 18-20-32 trim.mp4" -> Fortnite, 2026-09-08 18:20:32, "trim".</summary>
        public static bool TryParseName(string fileName, out string game, out DateTime taken, out string suffix)
        {
            game = "";
            suffix = "";
            taken = DateTime.MinValue;
            if (string.IsNullOrEmpty(fileName)) return false;
            var match = NamePattern.Match(fileName);
            if (!match.Success) return false;
            if (!DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out taken))
                return false;
            game = match.Groups["game"].Value;
            suffix = match.Groups["suffix"].Value;
            return true;
        }

        /// <summary>A sibling name for a copy of the clip: "<name> trim.mp4", numbered if that exists.</summary>
        public static string CopyName(string clipPath, string suffix, Func<string, bool> exists)
        {
            if (string.IsNullOrEmpty(clipPath)) throw new ArgumentException("clipPath");
            if (exists == null) throw new ArgumentNullException("exists");
            var folder = Path.GetDirectoryName(clipPath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(clipPath);
            var candidate = Path.Combine(folder, stem + " " + suffix + ".mp4");
            for (var n = 2; exists(candidate); n++)
                candidate = Path.Combine(folder, stem + " " + suffix + " " + n + ".mp4");
            return candidate;
        }
    }
}
