using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Rewind
{
    /// <summary>What a save produced. Immutable.</summary>
    internal sealed class SavedClip
    {
        public readonly string Path;
        public readonly long Bytes;
        /// <summary>How much time the ring held when the hotkey was pressed.</summary>
        public readonly TimeSpan Buffered;

        public SavedClip(string path, long bytes, TimeSpan buffered)
        {
            Path = path;
            Bytes = bytes;
            Buffered = buffered;
        }
    }

    /// <summary>
    /// Turns the ring buffer into a clip: dump the last N seconds of MPEG-TS to a temp file, let
    /// ffmpeg wrap it in an MP4 without re-encoding (a second or so of work), delete the temp file.
    /// </summary>
    internal static class ClipSaver
    {
        private const int MinUsefulBytes = 200 * 1024;
        private const int RemuxTimeoutMs = 60000;

        public static SavedClip Save(ChunkRing ring, Config config, string ffmpegPath, IList<string> audioLabels)
        {
            if (ring == null) throw new ArgumentNullException("ring");
            if (config == null) throw new ArgumentNullException("config");
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");
            if (audioLabels == null) throw new ArgumentNullException("audioLabels");

            // One extra second: the clip can only start at a keyframe, and there is one per second.
            var buffered = ring.Span;
            var data = ring.Snapshot(TimeSpan.FromSeconds(config.Seconds + 1), DateTime.UtcNow);
            if (data.Length < MinUsefulBytes)
                throw new InvalidOperationException("Nothing to save yet: the buffer is still filling (" + data.Length / 1024 + " KB).");

            Directory.CreateDirectory(config.ClipsFolder);
            var game = ForegroundApp.Name();
            var name = string.Format("Rewind {0}{1:yyyy-MM-dd HH-mm-ss}.mp4", game.Length > 0 ? game + " " : "", DateTime.Now);
            var mp4 = Path.Combine(config.ClipsFolder, name);
            var ts = Path.Combine(config.ClipsFolder, ".rewind-" + Guid.NewGuid().ToString("N") + ".ts");

            try
            {
                File.WriteAllBytes(ts, data);
                Remux(ffmpegPath, FfmpegArgs.Remux(ts, mp4, audioLabels, config.Codec), mp4);
                return new SavedClip(mp4, new FileInfo(mp4).Length, buffered);
            }
            finally
            {
                try
                {
                    if (File.Exists(ts)) File.Delete(ts);
                }
                catch (IOException error)
                {
                    Log.Warn("couldn't delete temp file " + ts + ": " + error.Message);
                }
            }
        }

        private static void Remux(string ffmpegPath, string args, string mp4)
        {
            var info = new ProcessStartInfo(ffmpegPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(info))
            {
                if (process == null) throw new InvalidOperationException("ffmpeg didn't start.");
                var stderr = process.StandardError.ReadToEnd().Trim();
                if (!process.WaitForExit(RemuxTimeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new InvalidOperationException("ffmpeg took over a minute to write the clip.");
                }
                if (process.ExitCode != 0 || !File.Exists(mp4))
                    throw new InvalidOperationException(string.Format("ffmpeg couldn't write the clip (code {0}): {1}",
                        process.ExitCode, Tail(stderr)));
                if (stderr.Length > 0) Log.Warn("remux: " + stderr);
            }
        }

        private static string Tail(string text)
        {
            var lines = text.Split('\n');
            return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
        }
    }

    /// <summary>Names the app in the foreground window, cleaned up for use in a file name.</summary>
    internal static class ForegroundApp
    {
        private const int MaxLength = 40;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        public static string Name()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "";
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return "";
                using (var process = Process.GetProcessById((int)pid)) return Clean(process.ProcessName);
            }
            catch (ArgumentException)
            {
                return ""; // the process closed between the two calls
            }
            catch (InvalidOperationException)
            {
                return "";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return ""; // a protected process we're not allowed to ask about
            }
        }

        /// <summary>"FortniteClient-Win64-Shipping" -> "Fortnite", "javaw" -> "Minecraft", junk stripped.</summary>
        public static string Clean(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return "";
            var name = processName;
            foreach (var suffix in new[] { "Client-Win64-Shipping", "-Win64-Shipping", "_x64", "-x64", "64" })
            {
                if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                    break;
                }
            }
            if (name.Equals("javaw", StringComparison.OrdinalIgnoreCase) || name.Equals("java", StringComparison.OrdinalIgnoreCase))
                name = "Minecraft";
            name = Regex.Replace(name, "[^A-Za-z0-9 _-]", "");
            return name.Length > MaxLength ? name.Substring(0, MaxLength) : name;
        }
    }
}
