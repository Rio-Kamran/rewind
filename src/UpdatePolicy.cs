using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Rewind
{
    /// <summary>The newest GitHub release, as far as the updater cares. HashUrl is null when the release has no Rewind.exe.sha256.</summary>
    internal sealed class ReleaseInfo
    {
        public readonly string Tag;
        public readonly Version Version;
        public readonly string ExeUrl;
        public readonly string HashUrl;

        public ReleaseInfo(string tag, Version version, string exeUrl, string hashUrl)
        {
            Tag = tag;
            Version = version;
            ExeUrl = exeUrl;
            HashUrl = hashUrl;
        }
    }

    /// <summary>
    /// The auto-updater's decisions, kept pure so they are tested: what a tag means as a version,
    /// whether a release is newer, what the .sha256 file says, whether a download is the real
    /// thing, and when Rewind must be left alone (a dev build, auto_update=off, or busy).
    /// </summary>
    internal static class UpdatePolicy
    {
        public const string ExeAsset = "Rewind.exe";
        public const string HashAsset = "Rewind.exe.sha256";

        /// <summary>The first check, right after start: a restart is when a friend expects the new version.</summary>
        public static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(2);
        /// <summary>Then this often: 12 asks an hour, well inside the 60 an hour GitHub gives an address without a login.</summary>
        public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);
        /// <summary>This soon after a start the replay buffer holds next to nothing, so a game in front doesn't hold an update back.</summary>
        public static readonly TimeSpan FreshStart = TimeSpan.FromMinutes(3);

        /// <summary>"v2.2.3", "2.2", "v2.3.0-beta.1", "2.2.3-4-g010834c" -> 2.2.3 / 2.2.0 / 2.3.0 / 2.2.3; null for anything else.</summary>
        public static Version ParseVersion(string text)
        {
            if (text == null) return null;
            var s = text.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            var cut = s.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0) s = s.Substring(0, cut);
            var parts = s.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return null;
            var numbers = new int[3];
            for (var i = 0; i < parts.Length; i++)
            {
                int n;
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n)) return null;
                if (i < 3) numbers[i] = n;
            }
            return new Version(numbers[0], numbers[1], numbers[2]);
        }

        /// <summary>True only when latest is a higher major.minor.patch. An unstamped build (0.0.0) never updates.</summary>
        public static bool IsNewer(Version latest, Version running)
        {
            if (latest == null || running == null) return false;
            var a = Normal(latest);
            var b = Normal(running);
            if (b == new Version(0, 0, 0)) return false;
            return a > b;
        }

        /// <summary>"v2.2.4": how a version is shown to the user and written as a tag.</summary>
        public static string Tag(Version version)
        {
            var v = Normal(version);
            return string.Format(CultureInfo.InvariantCulture, "v{0}.{1}.{2}", v.Major, v.Minor, v.Build);
        }

        /// <summary>The version this exe was stamped with (0.0.0 when unstamped).</summary>
        public static Version RunningVersion
        {
            get { return Normal(Assembly.GetExecutingAssembly().GetName().Version); }
        }

        private static Version Normal(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        /// <summary>The hash from a sha256sum-style line ("&lt;hex&gt;  Rewind.exe"), lowercased; null if there isn't one.</summary>
        public static string ParseSha256(string text)
        {
            if (text == null) return null;
            var trimmed = text.Trim('﻿', ' ', '\t', '\r', '\n');
            var end = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            var first = end >= 0 ? trimmed.Substring(0, end) : trimmed;
            if (first.Length != 64) return null;
            foreach (var c in first)
                if (!Uri.IsHexDigit(c)) return null;
            return first.ToLowerInvariant();
        }

        public static bool HashMatches(string expected, string actual)
        {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        public static string Sha256OfFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var sb = new StringBuilder(64);
                foreach (var b in sha.ComputeHash(stream)) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>Reads api.github.com's releases/latest reply. Throws InvalidOperationException when it isn't a usable release.</summary>
        public static ReleaseInfo ParseRelease(string json)
        {
            object parsed;
            try
            {
                parsed = new JavaScriptSerializer().DeserializeObject(json ?? "");
            }
            catch (ArgumentException error)
            {
                throw new InvalidOperationException("GitHub's reply wasn't JSON: " + error.Message, error);
            }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException("GitHub's reply wasn't JSON: " + error.Message, error);
            }
            var release = parsed as IDictionary<string, object>;
            if (release == null) throw new InvalidOperationException("GitHub's reply wasn't a release.");
            var tag = Text(release, "tag_name");
            if (tag == null)
            {
                var message = Text(release, "message");
                throw new InvalidOperationException("GitHub's reply has no release" + (message != null ? ": " + message : "."));
            }
            var version = ParseVersion(tag);
            if (version == null) throw new InvalidOperationException("The latest release's tag isn't a version: " + tag);

            string exeUrl = null, hashUrl = null;
            object assets;
            if (release.TryGetValue("assets", out assets) && assets is IEnumerable)
            {
                foreach (var item in (IEnumerable)assets)
                {
                    var asset = item as IDictionary<string, object>;
                    if (asset == null) continue;
                    var name = Text(asset, "name");
                    var url = Text(asset, "browser_download_url");
                    if (url == null) continue;
                    if (string.Equals(name, ExeAsset, StringComparison.OrdinalIgnoreCase)) exeUrl = url;
                    else if (string.Equals(name, HashAsset, StringComparison.OrdinalIgnoreCase)) hashUrl = url;
                }
            }
            return new ReleaseInfo(tag, version, exeUrl, hashUrl);
        }

        private static string Text(IDictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) ? value as string : null;
        }

        /// <summary>Why the updater must do nothing at all here, or null. A .git next to the exe = a dev build (Rio's own copy).</summary>
        public static string SkipReason(string exeDir, bool autoUpdate)
        {
            var git = Path.Combine(exeDir ?? "", ".git");
            if (Directory.Exists(git) || File.Exists(git)) return "dev build (.git next to Rewind.exe), leaving it alone";
            if (!autoUpdate) return "auto_update=off";
            return null;
        }

        /// <summary>
        /// Why a restart now would lose something, or null when it is safe to swap. The first reason wins.
        /// Just after a start (freshStart) the buffer is nearly empty, so only real work in flight holds it back.
        /// </summary>
        public static string BusyReason(bool recording, bool finishingRecording, bool saving, bool exporting, bool gameInFront, bool freshStart)
        {
            if (recording) return "a long recording is running";
            if (finishingRecording) return "a recording is still being finished";
            if (saving) return "a clip or screenshot is saving";
            if (exporting) return "an export (Discord copy, trim or GIF) is running";
            if (gameInFront && !freshStart) return "a game is in front (a restart would empty the replay buffer)";
            return null;
        }

        /// <summary>update-skipped.txt names a version that was tried and didn't start; that one is never tried again.</summary>
        public static bool IsSkipped(Version latest, string skippedText)
        {
            var skipped = ParseVersion(skippedText);
            return latest != null && skipped != null && Normal(latest) == skipped;
        }

        /// <summary>Null when the downloaded file is the promised Rewind.exe: the hash matches and it carries the release's version.</summary>
        public static string CheckDownload(string path, string expectedHash, Version expectedVersion)
        {
            var actual = Sha256OfFile(path);
            if (!HashMatches(expectedHash, actual))
                return "the download's SHA-256 hash (" + actual + ") doesn't match the release's (" + (expectedHash ?? "none") + ")";
            Version stamped;
            try
            {
                stamped = AssemblyName.GetAssemblyName(path).Version;
            }
            catch (BadImageFormatException)
            {
                return "the download isn't a Rewind build (not a .NET exe)";
            }
            catch (FileLoadException error)
            {
                return "the download isn't a Rewind build (" + error.Message + ")";
            }
            // Without this, a release whose exe was stamped wrong would update itself forever.
            if (Normal(stamped) != Normal(expectedVersion))
                return "the download says it is " + Tag(stamped) + ", not " + Tag(expectedVersion);
            return null;
        }
    }
}
