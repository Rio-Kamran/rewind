using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;

namespace Rewind
{
    /// <summary>
    /// Gets ffmpeg when there is none: downloads the yt-dlp FFmpeg-Builds zip (the build known to
    /// have ddagrab + NVENC) and pulls ffmpeg.exe out of it. About 110 MB, once, into the user's
    /// local app data. The helpers are pure and tested; the download runs on a worker thread.
    /// </summary>
    internal static class FfmpegFetcher
    {
        public const string ZipUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
        private const int BufferSize = 256 * 1024;
        private const long ReportEvery = 2 * 1048576;

        /// <summary>%LOCALAPPDATA%\Rewind\ffmpeg — survives moving or updating Rewind.exe.</summary>
        public static string DefaultFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rewind", "ffmpeg"); }
        }

        /// <summary>The one entry worth extracting: bin/ffmpeg.exe, whatever the top folder is called.</summary>
        public static bool IsFfmpegEntry(string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return false;
            var name = entryName.Replace('\\', '/');
            return name.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"43 MB of 110 MB", or "43 MB" when the total isn't known.</summary>
        public static string Progress(long done, long total)
        {
            var doneMb = (done / 1048576.0).ToString("0", CultureInfo.InvariantCulture);
            if (total <= 0) return doneMb + " MB";
            return doneMb + " MB of " + (total / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>Downloads and unpacks; returns the ffmpeg.exe path. Throws InvalidOperationException with a plain message.</summary>
        public static string Fetch(string folder, Action<string> progress)
        {
            if (string.IsNullOrEmpty(folder)) throw new ArgumentException("folder");
            Directory.CreateDirectory(folder);
            var zip = Path.Combine(folder, "ffmpeg.zip.part");
            var exe = Path.Combine(folder, "ffmpeg.exe");
            try
            {
                Download(zip, progress);
                Report(progress, "Unpacking ffmpeg");
                Extract(zip, exe);
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); }
                catch (IOException) { /* a leftover .part is harmless */ }
            }
            if (!File.Exists(exe)) throw new InvalidOperationException("The ffmpeg download didn't contain ffmpeg.exe.");
            return exe;
        }

        private static void Download(string target, Action<string> progress)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(ZipUrl);
            request.UserAgent = "Rewind (+https://github.com/Rio-Kamran/rewind)";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
                {
                    var total = response.ContentLength;
                    var buffer = new byte[BufferSize];
                    long done = 0;
                    long reported = 0;
                    int read;
                    Report(progress, "Downloading ffmpeg: " + Progress(0, total));
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        done += read;
                        if (done - reported >= ReportEvery)
                        {
                            reported = done;
                            Report(progress, "Downloading ffmpeg: " + Progress(done, total));
                        }
                    }
                }
            }
            catch (WebException error)
            {
                throw new InvalidOperationException("Couldn't download ffmpeg (" + error.Message
                    + "). Check the internet connection, or install ffmpeg yourself and set ffmpeg= in config.txt.", error);
            }
            catch (IOException error)
            {
                throw new InvalidOperationException("Couldn't save the ffmpeg download: " + error.Message, error);
            }
        }

        private static void Extract(string zip, string exe)
        {
            try
            {
                using (var archive = ZipFile.OpenRead(zip))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (!IsFfmpegEntry(entry.FullName)) continue;
                        using (var input = entry.Open())
                        using (var output = new FileStream(exe, FileMode.Create, FileAccess.Write))
                            input.CopyTo(output);
                        return;
                    }
                }
            }
            catch (InvalidDataException error)
            {
                throw new InvalidOperationException("The ffmpeg download was damaged; start Rewind again to retry.", error);
            }
            throw new InvalidOperationException("The ffmpeg download didn't contain bin/ffmpeg.exe.");
        }

        private static void Report(Action<string> progress, string text)
        {
            if (progress != null) progress(text);
        }
    }
}
