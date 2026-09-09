using System;
using System.Collections.Generic;
using System.Drawing;

namespace Rewind
{
    /// <summary>
    /// Decides whether what's in front counts as a game: anything on the games list does, and so
    /// does any app covering its whole monitor without a title bar (fullscreen or borderless).
    /// Pure, tested.
    /// </summary>
    internal static class GameDetector
    {
        /// <summary>Windows' own surfaces: they can cover the monitor with no title bar but are never a game.</summary>
        private static readonly string[] ShellProcesses =
        {
            "explorer", "dwm", "SearchHost", "SearchApp", "StartMenuExperienceHost", "ShellExperienceHost",
            "LockApp", "TextInputHost", "Taskmgr", "Rewind"
        };

        public static bool IsGame(ForegroundInfo info, IList<string> games)
        {
            if (info == null) throw new ArgumentNullException("info");
            return IsGame(info.ProcessName, info.Window, info.Monitor, info.HasCaption, games);
        }

        public static bool IsGame(string processName, Rectangle window, Rectangle monitor, bool hasCaption, IList<string> games)
        {
            if (games == null) throw new ArgumentNullException("games");
            if (string.IsNullOrEmpty(processName)) return false;
            if (IsListed(processName, games)) return true;
            if (IsListed(processName, ShellProcesses)) return false;
            return !hasCaption && CoversMonitor(window, monitor);
        }

        /// <summary>True when the window reaches every edge of the monitor, give or take a pixel.</summary>
        public static bool CoversMonitor(Rectangle window, Rectangle monitor)
        {
            if (monitor.Width <= 0 || monitor.Height <= 0 || window.Width <= 0 || window.Height <= 0) return false;
            return window.Left <= monitor.Left + 1 && window.Top <= monitor.Top + 1
                && window.Right >= monitor.Right - 1 && window.Bottom >= monitor.Bottom - 1;
        }

        /// <summary>One line for --list: what is in front and why it does or doesn't count.</summary>
        public static string Describe(ForegroundInfo info, IList<string> games)
        {
            if (info == null) throw new ArgumentNullException("info");
            if (info.ProcessName.Length == 0) return "Nothing in front.";
            string why;
            if (IsListed(info.ProcessName, games)) why = "on the games list";
            else if (IsListed(info.ProcessName, ShellProcesses)) why = "part of Windows";
            else if (info.HasCaption) why = "an ordinary window with a title bar";
            else if (CoversMonitor(info.Window, info.Monitor)) why = "covers the monitor with no title bar";
            else why = "doesn't cover the monitor";
            return string.Format("{0}: {1}x{2} window on a {3}x{4} monitor, {5} -> {6}",
                info.ProcessName, info.Window.Width, info.Window.Height, info.Monitor.Width, info.Monitor.Height,
                why, IsGame(info, games) ? "counts as a game" : "not a game");
        }

        private static bool IsListed(string processName, IList<string> names)
        {
            foreach (var name in names)
                if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
