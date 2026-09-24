using System;
using System.IO;
using System.Reflection;
using System.Text;
using Rewind;

namespace Rewind.Tests
{
    /// <summary>The auto-updater's decisions: versions, the hash check, dev builds, idle, and the file swap.</summary>
    internal static partial class Tests
    {
        private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        private static void UpdateTests()
        {
            Run("update: tags become versions", () =>
            {
                Equal(new Version(2, 2, 3), UpdatePolicy.ParseVersion("v2.2.3"));
                Equal(new Version(2, 2, 0), UpdatePolicy.ParseVersion("v2.2"));
                Equal(new Version(10, 0, 1), UpdatePolicy.ParseVersion(" V10.0.1 "));
                Equal(new Version(2, 3, 0), UpdatePolicy.ParseVersion("v2.3.0-beta.1"));
                Equal(new Version(2, 2, 3), UpdatePolicy.ParseVersion("2.2.3-4-g010834c"));
                Equal(new Version(2, 2, 3), UpdatePolicy.ParseVersion("2.2.3.0"));
                True(UpdatePolicy.ParseVersion("main") == null, "branch name");
                True(UpdatePolicy.ParseVersion("v2.x") == null, "letters");
                True(UpdatePolicy.ParseVersion("v2") == null, "one number");
                True(UpdatePolicy.ParseVersion("") == null, "empty");
                True(UpdatePolicy.ParseVersion(null) == null, "null");
                Equal("v2.2.0", UpdatePolicy.Tag(new Version(2, 2)));
            });
            Run("update: only a strictly newer release is taken", () =>
            {
                True(UpdatePolicy.IsNewer(new Version(2, 2, 4), new Version(2, 2, 3)), "patch");
                True(UpdatePolicy.IsNewer(new Version(2, 10, 0), new Version(2, 9, 9)), "numbers, not text");
                True(UpdatePolicy.IsNewer(new Version(3, 0, 0), new Version(2, 2, 3, 7)), "major");
                True(!UpdatePolicy.IsNewer(new Version(2, 2, 3), new Version(2, 2, 3)), "same");
                True(!UpdatePolicy.IsNewer(new Version(2, 2, 3), new Version(2, 2, 3, 0)), "same, fourth number ignored");
                True(!UpdatePolicy.IsNewer(new Version(2, 2), new Version(2, 2, 0)), "2.2 is 2.2.0");
                True(!UpdatePolicy.IsNewer(new Version(2, 2, 3), new Version(2, 2, 4)), "older");
                True(!UpdatePolicy.IsNewer(new Version(2, 2, 4), new Version(0, 0, 0)), "unstamped build never updates");
                True(!UpdatePolicy.IsNewer(new Version(2, 2, 4), null), "no running version");
                True(!UpdatePolicy.IsNewer(null, new Version(2, 2, 3)), "no release version");
            });
            Run("update: the .sha256 file is read", () =>
            {
                Equal(AbcSha256, UpdatePolicy.ParseSha256(AbcSha256.ToUpperInvariant() + "  Rewind.exe\n"));
                Equal(AbcSha256, UpdatePolicy.ParseSha256("\uFEFF" + AbcSha256 + "\r\n"));
                Equal(AbcSha256, UpdatePolicy.ParseSha256(AbcSha256 + " *Rewind.exe"));
                True(UpdatePolicy.ParseSha256(AbcSha256.Substring(1)) == null, "too short");
                True(UpdatePolicy.ParseSha256(AbcSha256.Substring(1) + "g") == null, "not hex");
                True(UpdatePolicy.ParseSha256("Not Found") == null, "an error page");
                True(UpdatePolicy.ParseSha256("") == null, "empty");
                True(UpdatePolicy.ParseSha256(null) == null, "null");
            });
            Run("update: hash compare", () =>
            {
                True(UpdatePolicy.HashMatches(AbcSha256, AbcSha256.ToUpperInvariant()), "case doesn't matter");
                True(!UpdatePolicy.HashMatches(AbcSha256, AbcSha256.Replace('b', 'c')), "different");
                True(!UpdatePolicy.HashMatches(null, AbcSha256), "no expected hash");
                True(!UpdatePolicy.HashMatches("", ""), "both empty");
            });
            Run("update: a file's SHA-256", () => WithTempDir(dir =>
            {
                var file = Path.Combine(dir, "abc.bin");
                File.WriteAllBytes(file, Encoding.ASCII.GetBytes("abc"));
                Equal(AbcSha256, UpdatePolicy.Sha256OfFile(file));
            }));
            Run("update: GitHub's latest-release reply is read", () =>
            {
                var json = "{\"tag_name\": \"v2.2.4\", \"name\": \"v2.2.4\", \"draft\": false, \"body\": \"notes \\\"quoted\\\"\", \"assets\": ["
                    + "{\"name\": \"Rewind.exe.sha256\", \"size\": 77, \"browser_download_url\": \"https://github.com/Rio-Kamran/rewind/releases/download/v2.2.4/Rewind.exe.sha256\"},"
                    + "{\"name\": \"Rewind.exe\", \"size\": 300000, \"browser_download_url\": \"https://github.com/Rio-Kamran/rewind/releases/download/v2.2.4/Rewind.exe\"}]}";
                var release = UpdatePolicy.ParseRelease(json);
                Equal("v2.2.4", release.Tag);
                Equal(new Version(2, 2, 4), release.Version);
                Equal("https://github.com/Rio-Kamran/rewind/releases/download/v2.2.4/Rewind.exe", release.ExeUrl);
                Equal("https://github.com/Rio-Kamran/rewind/releases/download/v2.2.4/Rewind.exe.sha256", release.HashUrl);
            });
            Run("update: a release without the hash file has no HashUrl", () =>
            {
                var release = UpdatePolicy.ParseRelease("{\"tag_name\": \"v2.2.3\", \"assets\": [{\"name\": \"Rewind.exe\", \"browser_download_url\": \"https://x/Rewind.exe\"}]}");
                Equal("https://x/Rewind.exe", release.ExeUrl);
                True(release.HashUrl == null, "no hash url");
            });
            Run("update: a broken reply is refused", () =>
            {
                Throws<InvalidOperationException>(() => UpdatePolicy.ParseRelease("<html>rate limited</html>"));
                Throws<InvalidOperationException>(() => UpdatePolicy.ParseRelease("{\"message\": \"Not Found\"}"));
                Throws<InvalidOperationException>(() => UpdatePolicy.ParseRelease("{\"tag_name\": \"nightly\", \"assets\": []}"));
                Throws<InvalidOperationException>(() => UpdatePolicy.ParseRelease("[1, 2]"));
                Throws<InvalidOperationException>(() => UpdatePolicy.ParseRelease(""));
            });
            Run("update: dev builds and auto_update=off are left alone", () => WithTempDir(dir =>
            {
                True(UpdatePolicy.SkipReason(dir, true) == null, "a plain folder updates");
                Contains(UpdatePolicy.SkipReason(dir, false), "auto_update=off");
                Directory.CreateDirectory(Path.Combine(dir, ".git"));
                Contains(UpdatePolicy.SkipReason(dir, true), "dev build");
                Contains(UpdatePolicy.SkipReason(dir, false), "dev build");
            }));
            Run("update: a .git file (a worktree) is a dev build too", () => WithTempDir(dir =>
            {
                File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: C:/elsewhere");
                Contains(UpdatePolicy.SkipReason(dir, true), "dev build");
            }));
            Run("update: only swaps when nothing is in flight", () =>
            {
                True(UpdatePolicy.BusyReason(false, false, false, false, false) == null, "idle");
                Contains(UpdatePolicy.BusyReason(true, false, false, false, false), "recording");
                Contains(UpdatePolicy.BusyReason(false, true, false, false, false), "recording");
                Contains(UpdatePolicy.BusyReason(false, false, true, false, false), "saving");
                Contains(UpdatePolicy.BusyReason(false, false, false, true, false), "export");
                Contains(UpdatePolicy.BusyReason(false, false, false, false, true), "game");
                Contains(UpdatePolicy.BusyReason(true, false, true, true, true), "recording"); // the first reason wins
            });
            Run("update: a version that failed to start is skipped, a newer one isn't", () =>
            {
                True(UpdatePolicy.IsSkipped(new Version(2, 2, 4), "v2.2.4\n"), "same version");
                True(!UpdatePolicy.IsSkipped(new Version(2, 2, 5), "v2.2.4"), "newer one");
                True(!UpdatePolicy.IsSkipped(new Version(2, 2, 4), ""), "nothing skipped");
                True(!UpdatePolicy.IsSkipped(new Version(2, 2, 4), null), "no file");
            });
            Run("update: a download must match the hash and be the promised version", () => WithTempDir(dir =>
            {
                var junk = Path.Combine(dir, "junk.exe");
                File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("abc"));
                Contains(UpdatePolicy.CheckDownload(junk, AbcSha256.Replace('b', 'c'), new Version(2, 2, 4)), "hash");
                Contains(UpdatePolicy.CheckDownload(junk, AbcSha256, new Version(2, 2, 4)), "isn't a Rewind build");

                // This test exe is a real stamped assembly: right hash + its own version passes, any other version doesn't.
                var self = Assembly.GetExecutingAssembly().Location;
                var hash = UpdatePolicy.Sha256OfFile(self);
                var own = AssemblyName.GetAssemblyName(self).Version;
                True(UpdatePolicy.CheckDownload(self, hash, own) == null, "own version passes");
                Contains(UpdatePolicy.CheckDownload(self, hash, new Version(own.Major + 1, 0, 0)), "says it is");
            }));
            Run("update: the running exe is swapped by renaming", () => WithTempDir(dir =>
            {
                var exe = Path.Combine(dir, "Rewind.exe");
                File.WriteAllText(exe, "old");
                File.WriteAllText(Updater.OldPath(exe), "older leftover");
                File.WriteAllText(Updater.NewPath(exe), "new");
                Updater.Swap(exe, Updater.NewPath(exe));
                Equal("new", File.ReadAllText(exe));
                Equal("old", File.ReadAllText(Updater.OldPath(exe)));
                True(!File.Exists(Updater.NewPath(exe)), "the download was moved, not copied");
            }));
            Run("update: a swap works while the old exe is open (running)", () => WithTempDir(dir =>
            {
                var exe = Path.Combine(dir, "Rewind.exe");
                File.WriteAllText(exe, "old");
                File.WriteAllText(Updater.NewPath(exe), "new");
                // A running exe is open with delete sharing, which is what lets it be renamed.
                using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    Updater.Swap(exe, Updater.NewPath(exe));
                Equal("new", File.ReadAllText(exe));
            }));
            Run("update: a failed swap puts the old exe back", () => WithTempDir(dir =>
            {
                var exe = Path.Combine(dir, "Rewind.exe");
                File.WriteAllText(exe, "old");
                Throws<IOException>(() => Updater.Swap(exe, Path.Combine(dir, "missing.exe")));
                Equal("old", File.ReadAllText(exe));
                True(!File.Exists(Updater.OldPath(exe)), "nothing left renamed");
            }));
            Run("update: roll back restores .old and keeps the bad one aside", () => WithTempDir(dir =>
            {
                var exe = Path.Combine(dir, "Rewind.exe");
                File.WriteAllText(exe, "new but broken");
                File.WriteAllText(Updater.OldPath(exe), "old");
                Updater.RollBack(exe);
                Equal("old", File.ReadAllText(exe));
                Equal("new but broken", File.ReadAllText(Updater.BadPath(exe)));
                True(!File.Exists(Updater.OldPath(exe)), ".old used up");
                Throws<IOException>(() => Updater.RollBack(exe)); // nothing to roll back to
                Equal("old", File.ReadAllText(exe));
            }));
            Run("update: leftovers are cleaned up next start", () => WithTempDir(dir =>
            {
                var exe = Path.Combine(dir, "Rewind.exe");
                File.WriteAllText(exe, "current");
                File.WriteAllText(Updater.OldPath(exe), "x");
                File.WriteAllText(Updater.NewPath(exe), "x");
                File.WriteAllText(Updater.BadPath(exe), "x");
                True(Updater.CleanUp(exe), "all gone");
                True(!File.Exists(Updater.OldPath(exe)) && !File.Exists(Updater.NewPath(exe)) && !File.Exists(Updater.BadPath(exe)), "files deleted");
                Equal("current", File.ReadAllText(exe));
                using (new FileStream(Updater.OldPath(exe), FileMode.Create, FileAccess.Write, FileShare.None))
                    True(!Updater.CleanUp(exe), "a locked .old reports false instead of throwing");
            }));
            Run("config: auto_update defaults on and can be turned off", () =>
            {
                True(Config.Parse("").AutoUpdate, "default on");
                True(!Config.Parse("auto_update=off").AutoUpdate, "off");
                True(!Config.Parse(Config.Parse("auto_update=off").Text()).AutoUpdate, "round trip");
                Contains(Config.DefaultText(), "auto_update=on");
                Throws<ConfigException>(() => Config.Parse("auto_update=sometimes"));
            });
        }

        private static void WithTempDir(Action<string> test)
        {
            var dir = Path.Combine(Path.GetTempPath(), "rewind-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try { test(dir); }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
