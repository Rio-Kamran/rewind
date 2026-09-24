using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Rewind
{
    /// <summary>A verified new Rewind.exe sitting next to the running one as Rewind.exe.new, waiting for an idle moment.</summary>
    internal sealed class StagedUpdate
    {
        public readonly Version Version;
        public readonly string Path;

        public StagedUpdate(Version version, string path)
        {
            Version = version;
            Path = path;
        }
    }

    /// <summary>What the old copy needs after its tray is gone: which exe to start, and the versions either side.</summary>
    internal sealed class UpdateHandOff
    {
        public readonly string ExePath;
        public readonly Version From;
        public readonly Version To;

        public UpdateHandOff(string exePath, Version from, Version to)
        {
            ExePath = exePath;
            From = from;
            To = to;
        }
    }

    /// <summary>
    /// Keeps a friend's Rewind.exe current. Asks GitHub for the latest release, downloads Rewind.exe
    /// next to the running one as Rewind.exe.new and refuses it unless it matches the release's
    /// Rewind.exe.sha256 and carries the release's version. A running exe can't be overwritten but
    /// can be renamed, so the swap is Rewind.exe -> Rewind.exe.old, .new -> Rewind.exe, then a
    /// restart; the new copy deletes .old once it is up. If the new copy doesn't come up, the old
    /// one is put back, started again, and that version is never tried again (update-skipped.txt).
    /// </summary>
    internal static class Updater
    {
        public const string LatestReleaseUrl = "https://api.github.com/repos/Rio-Kamran/rewind/releases/latest";
        public const string StartedEventName = "Local\\Rewind.UpdateStarted";
        public const string AfterUpdateArg = "--after-update";
        public const string UpdateFailedArg = "--update-failed";
        public const string SkipFileName = "update-skipped.txt";
        private const string UserAgent = "Rewind (+https://github.com/Rio-Kamran/rewind)";
        private const int StartWaitMs = 30000;
        private const long MaxTextBytes = 1024 * 1024;
        private const long MaxExeBytes = 64L * 1024 * 1024;

        public static string OldPath(string exe) { return exe + ".old"; }
        public static string NewPath(string exe) { return exe + ".new"; }
        public static string BadPath(string exe) { return exe + ".bad"; }

        /// <summary>
        /// Where to ask. REWIND_UPDATE_URL can point it at a fake release on this PC (how the updater
        /// is tested end to end); anything that isn't localhost is ignored, so it can't be aimed elsewhere.
        /// </summary>
        public static string ReleaseUrl
        {
            get
            {
                var custom = Environment.GetEnvironmentVariable("REWIND_UPDATE_URL");
                Uri uri;
                if (!string.IsNullOrEmpty(custom) && Uri.TryCreate(custom, UriKind.Absolute, out uri) && uri.IsLoopback) return custom;
                return LatestReleaseUrl;
            }
        }

        // ---- check + download (a worker thread) ----

        /// <summary>
        /// A verified download of a newer release, or null when there is nothing to do or the release
        /// can't be trusted (the reason is logged). Network trouble throws WebException for the caller to log.
        /// </summary>
        public static StagedUpdate CheckAndDownload(string exePath, Version running, string skippedText)
        {
            var url = ReleaseUrl;
            var release = UpdatePolicy.ParseRelease(GetText(url));
            if (!UpdatePolicy.IsNewer(release.Version, running))
            {
                Log.Info("update check: " + UpdatePolicy.Tag(running) + " is current (latest release " + release.Tag + ")");
                return null;
            }
            if (UpdatePolicy.IsSkipped(release.Version, skippedText))
            {
                Log.Info("update check: " + release.Tag + " didn't start last time, skipping it (" + SkipFileName + ")");
                return null;
            }
            if (release.ExeUrl == null)
            {
                Log.Warn("update check: release " + release.Tag + " has no " + UpdatePolicy.ExeAsset);
                return null;
            }
            if (release.HashUrl == null)
            {
                Log.Warn("update check: release " + release.Tag + " has no " + UpdatePolicy.HashAsset + ", so it can't be verified; not updating");
                return null;
            }
            var expected = UpdatePolicy.ParseSha256(GetText(release.HashUrl));
            if (expected == null)
            {
                Log.Warn("update check: " + UpdatePolicy.HashAsset + " of " + release.Tag + " holds no SHA-256; not updating");
                return null;
            }

            Log.Info("update check: " + release.Tag + " is out (running " + UpdatePolicy.Tag(running) + "), downloading it");
            var target = NewPath(exePath);
            Download(release.ExeUrl, target);
            string problem;
            try
            {
                problem = UpdatePolicy.CheckDownload(target, expected, release.Version);
            }
            catch
            {
                TryDelete(target);
                throw;
            }
            if (problem != null)
            {
                TryDelete(target);
                Log.Error("update " + release.Tag + " refused: " + problem);
                return null;
            }
            Log.Info("update " + release.Tag + " downloaded and verified (sha256 " + expected + "); swapping it in when Rewind is idle");
            return new StagedUpdate(release.Version, target);
        }

        private static HttpWebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Accept = "application/vnd.github+json, */*";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            return request;
        }

        private static string GetText(string url)
        {
            using (var response = (HttpWebResponse)Request(url).GetResponse())
            using (var input = response.GetResponseStream())
            using (var buffer = new MemoryStream())
            {
                Copy(input, buffer, MaxTextBytes);
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static void Download(string url, string target)
        {
            try
            {
                using (var response = (HttpWebResponse)Request(url).GetResponse())
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    Copy(input, output, MaxExeBytes);
            }
            catch
            {
                TryDelete(target); // a half download must never be swapped in
                throw;
            }
        }

        private static void Copy(Stream input, Stream output, long limit)
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > limit) throw new InvalidOperationException("the download is bigger than " + limit / 1048576 + " MB; not trusting it");
                output.Write(buffer, 0, read);
            }
        }

        // ---- the files ----

        /// <summary>Rewind.exe -> .old, the download -> Rewind.exe. If the second step fails the first is undone and the error thrown.</summary>
        public static void Swap(string exePath, string newPath)
        {
            if (!File.Exists(newPath)) throw new FileNotFoundException("The downloaded update is gone.", newPath);
            var old = OldPath(exePath);
            if (File.Exists(old)) File.Delete(old);
            File.Move(exePath, old);
            try
            {
                File.Move(newPath, exePath);
            }
            catch
            {
                File.Move(old, exePath);
                throw;
            }
        }

        /// <summary>The new exe didn't come up: it goes aside as .bad and .old becomes Rewind.exe again. Throws IOException when there is no .old.</summary>
        public static void RollBack(string exePath)
        {
            var old = OldPath(exePath);
            if (!File.Exists(old)) throw new FileNotFoundException("There is no " + Path.GetFileName(old) + " to go back to.", old);
            var bad = BadPath(exePath);
            if (File.Exists(bad)) File.Delete(bad);
            if (File.Exists(exePath)) File.Move(exePath, bad);
            File.Move(old, exePath);
        }

        /// <summary>Drops a download that won't be used (only .new: .old may be the one copy that runs).</summary>
        public static void DiscardDownload(string exePath)
        {
            if (!TryDelete(NewPath(exePath))) Log.Warn("update: couldn't delete " + NewPath(exePath) + "; next start tries again");
        }

        /// <summary>Deletes .old, .new and .bad. False (not an exception) when one is still in use.</summary>
        public static bool CleanUp(string exePath)
        {
            var clean = true;
            foreach (var path in new[] { OldPath(exePath), NewPath(exePath), BadPath(exePath) })
                if (!TryDelete(path)) clean = false;
            return clean;
        }

        /// <summary>After an update the old copy is still exiting for a moment: keep trying for a minute, off the UI thread.</summary>
        public static void CleanUpSoon(string exePath)
        {
            var worker = new Thread(() =>
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    if (CleanUp(exePath)) return;
                    Thread.Sleep(2000);
                }
                Log.Warn("update: couldn't delete the leftovers next to " + exePath + " yet; next start tries again");
            }) { IsBackground = true, Name = "rewind-update-cleanup" };
            worker.Start();
        }

        public static string ReadSkipped(string appDir)
        {
            try
            {
                var path = Path.Combine(appDir, SkipFileName);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // ---- the restart ----

        /// <summary>The new copy says it is up; the old one is waiting for this before it lets go.</summary>
        public static void SignalStarted()
        {
            try
            {
                using (var started = EventWaitHandle.OpenExisting(StartedEventName)) started.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Started by hand with the flag, or the old copy already gave up: nobody to tell.
            }
        }

        /// <summary>
        /// The old copy's last job, run after its tray is gone and the single-instance lock is free:
        /// start the new exe and wait for it to say it is up. If it exits or stays silent, stop it,
        /// put the old exe back, remember not to try that version again, and start the old one.
        /// </summary>
        public static int HandOff(UpdateHandOff handOff)
        {
            if (handOff == null) throw new ArgumentNullException("handOff");
            var to = UpdatePolicy.Tag(handOff.To);
            var from = UpdatePolicy.Tag(handOff.From);
            using (var started = new EventWaitHandle(false, EventResetMode.ManualReset, StartedEventName))
            {
                started.Reset();
                Process child = null;
                string failure;
                try
                {
                    child = Launch(handOff.ExePath, AfterUpdateArg + " " + from);
                    failure = WaitForStart(child, started);
                }
                catch (Win32Exception error)
                {
                    failure = "couldn't be started: " + error.Message;
                }
                if (failure == null)
                {
                    Log.Info("update: " + to + " is up (pid " + child.Id + "); " + from + " steps aside");
                    return 0;
                }

                Log.Error("update: " + to + " " + failure + "; rolling back to " + from);
                Stop(child);
                try
                {
                    RollBackPatiently(handOff.ExePath);
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(handOff.ExePath), SkipFileName), to + Environment.NewLine);
                    Launch(handOff.ExePath, UpdateFailedArg + " " + to);
                    Log.Info("update: back on " + from + "; " + to + " won't be tried again");
                }
                catch (Exception error)
                {
                    Log.Error("update: roll back failed: " + error.Message);
                }
                return 1;
            }
        }

        private static Process Launch(string exePath, string arguments)
        {
            return Process.Start(new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            });
        }

        /// <summary>Null once the new copy signals; otherwise why it didn't.</summary>
        private static string WaitForStart(Process child, EventWaitHandle started)
        {
            var waited = 0;
            while (waited < StartWaitMs)
            {
                if (started.WaitOne(250)) return null;
                waited += 250;
                if (child.HasExited) return "exited (code " + child.ExitCode + ") before it was up";
            }
            return started.WaitOne(0) ? null : "didn't come up within " + StartWaitMs / 1000 + " s";
        }

        private static void Stop(Process child)
        {
            if (child == null) return;
            try
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    child.WaitForExit(5000);
                }
            }
            catch (Win32Exception error) { Log.Warn("update: couldn't stop the new copy: " + error.Message); }
            catch (InvalidOperationException) { /* already gone */ }
        }

        /// <summary>The killed copy can hold its file for a moment after it dies: retry for a few seconds.</summary>
        private static void RollBackPatiently(string exePath)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    RollBack(exePath);
                    return;
                }
                catch (FileNotFoundException)
                {
                    throw; // no .old at all: waiting won't help
                }
                catch (IOException)
                {
                    if (attempt >= 10) throw;
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt >= 10) throw;
                }
                Thread.Sleep(500);
            }
        }
    }
}
