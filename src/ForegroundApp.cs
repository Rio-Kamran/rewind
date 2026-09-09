using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>What's in front right now: the process, its window and the monitor it's on. Immutable.</summary>
    internal sealed class ForegroundInfo
    {
        public static readonly ForegroundInfo None = new ForegroundInfo("", Rectangle.Empty, Rectangle.Empty, false);

        public readonly string ProcessName;
        public readonly Rectangle Window;
        public readonly Rectangle Monitor;
        /// <summary>True for an ordinary window with a title bar; fullscreen and borderless games have none.</summary>
        public readonly bool HasCaption;

        public ForegroundInfo(string processName, Rectangle window, Rectangle monitor, bool hasCaption)
        {
            ProcessName = processName ?? "";
            Window = window;
            Monitor = monitor;
            HasCaption = hasCaption;
        }
    }

    /// <summary>Asks Windows what's in front, and cleans process names up for file names.</summary>
    internal static class ForegroundApp
    {
        private const int MaxLength = 40;
        private const int GwlStyle = -16;
        private const int WsCaption = 0x00C00000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        /// <summary>The foreground app's name, cleaned for a file name; empty if there is none.</summary>
        public static string Name()
        {
            return Clean(Probe().ProcessName);
        }

        public static ForegroundInfo Probe()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return ForegroundInfo.None;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return ForegroundInfo.None;
                string name;
                using (var process = Process.GetProcessById((int)pid)) name = process.ProcessName;
                NativeRect rect;
                var window = GetWindowRect(hwnd, out rect) ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : Rectangle.Empty;
                var monitor = Screen.FromHandle(hwnd).Bounds;
                var caption = (GetWindowLong(hwnd, GwlStyle) & WsCaption) == WsCaption;
                return new ForegroundInfo(name, window, monitor, caption);
            }
            catch (ArgumentException)
            {
                return ForegroundInfo.None; // the process closed between the two calls
            }
            catch (InvalidOperationException)
            {
                return ForegroundInfo.None;
            }
            catch (Win32Exception)
            {
                return ForegroundInfo.None; // a protected process we're not allowed to ask about
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
