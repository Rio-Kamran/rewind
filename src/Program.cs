using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>
    /// Rewind: a replay buffer for the main monitor. Runs in the tray; the hotkey (Ctrl+Alt+P by
    /// default) saves the last N seconds as an MP4 with game audio and mic on separate tracks.
    ///
    ///   Rewind.exe          start (one copy only; a second start just says so)
    ///   Rewind.exe --save   tell the running Rewind to save a clip (Stream Deck, scripts)
    ///   Rewind.exe --list   show the monitors and audio devices it can see
    /// </summary>
    internal static class Program
    {
        private const string InstanceMutexName = "Local\\Rewind.SingleInstance";
        public const string SaveEventName = "Local\\Rewind.SaveClip";
        public const string QuitEventName = "Local\\Rewind.Quit";

        [STAThread]
        private static int Main(string[] args)
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            Log.Init(Path.Combine(appDir, "rewind.log"));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";
            if (mode == "--save") return Signal(SaveEventName, "Rewind isn't running, so there's nothing to save.");
            if (mode == "--quit") return Signal(QuitEventName, null);
            if (mode == "--list") return ShowDevices();
            if (mode.Length > 0)
            {
                MessageBox.Show("Rewind.exe            start in the tray\nRewind.exe --save     save a clip from the running Rewind\nRewind.exe --quit     stop the running Rewind\nRewind.exe --list     list monitors and audio devices",
                    "Rewind");
                return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, InstanceMutexName, out created))
            {
                if (!created)
                {
                    MessageBox.Show("Rewind is already running: look for the red dot in the tray.", "Rewind");
                    return 1;
                }

                Application.ThreadException += (s, e) => Log.Error("unhandled: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("fatal: " + e.ExceptionObject);

                using (var app = new TrayApp(appDir))
                {
                    if (!app.Start()) return 2;
                    Application.Run();
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// <summary>Pokes the running Rewind through a named event. Quiet if there is none and no message is given.</summary>
        private static int Signal(string eventName, string notRunningMessage)
        {
            EventWaitHandle handle;
            try
            {
                handle = EventWaitHandle.OpenExisting(eventName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                if (notRunningMessage != null) MessageBox.Show(notRunningMessage, "Rewind");
                return 1;
            }
            using (handle) handle.Set();
            return 0;
        }

        private static int ShowDevices()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Monitors (use the number as monitor= in config.txt, or primary):");
            try
            {
                sb.AppendLine(Dxgi.Describe(Dxgi.ListOutputs()));
            }
            catch (Exception error)
            {
                sb.AppendLine("Couldn't list monitors: " + error.Message);
            }
            sb.AppendLine();

            var probes = new[]
            {
                new AudioTap("Game", "rewind_probe_game", true, ""),
                new AudioTap("Mic", "rewind_probe_mic", false, "")
            };
            foreach (var probe in probes)
            {
                using (probe)
                {
                    try
                    {
                        probe.Open(TimeSpan.FromSeconds(5));
                        sb.AppendLine(probe.Label + " audio (Windows default): " + probe.Format);
                    }
                    catch (InvalidOperationException error)
                    {
                        sb.AppendLine(probe.Label + " audio: " + error.Message);
                    }
                }
            }

            MessageBox.Show(sb.ToString(), "Rewind: devices");
            return 0;
        }
    }
}
