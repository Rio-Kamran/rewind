using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Rewind
{
    /// <summary>
    /// Runs one ffmpeg command that reads and writes files (trim, share, GIF), waits for it, and
    /// turns a failure into an exception whose message is ffmpeg's last line. Used everywhere
    /// ffmpeg isn't fed through a pipe.
    /// </summary>
    internal static class FfmpegRun
    {
        /// <summary>Runs to completion; throws InvalidOperationException with the reason when ffmpeg fails or the output is missing.</summary>
        public static void Execute(string ffmpegPath, string args, string expectedOutput, int timeoutMs)
        {
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");
            if (string.IsNullOrEmpty(args)) throw new ArgumentException("args");
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");

            var info = new ProcessStartInfo(ffmpegPath, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            var stderr = new StringBuilder();
            using (var process = Process.Start(info))
            {
                if (process == null) throw new InvalidOperationException("ffmpeg didn't start.");
                var drain = new Thread(() => ClipSaver.Collect(process, stderr)) { IsBackground = true, Name = "rewind-ffmpeg-run-stderr" };
                drain.Start();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new InvalidOperationException("ffmpeg took too long (over " + timeoutMs / 1000 + " s).");
                }
                drain.Join(1000);
                string text;
                lock (stderr) text = stderr.ToString().Trim();
                if (process.ExitCode != 0 || (expectedOutput != null && !File.Exists(expectedOutput)))
                    throw new InvalidOperationException(text.Length > 0 ? ClipSaver.Tail(text) : "ffmpeg exit code " + process.ExitCode);
                if (text.Length > 0) Log.Warn("ffmpeg: " + text);
            }
        }
    }
}
