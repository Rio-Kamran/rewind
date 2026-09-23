using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Rewind
{
    /// <summary>
    /// "Start with Windows": a shortcut to Rewind.exe in the user's Startup folder, which is how
    /// Windows starts tray apps at login. Made through the Windows Script Host shell object (late
    /// bound, so no interop assembly is needed).
    /// </summary>
    internal static class Startup
    {
        public const string ShortcutName = "Rewind.lnk";

        public static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName); }
        }

        public static bool Enabled { get { return File.Exists(ShortcutPath); } }

        /// <summary>Creates or removes the shortcut. Throws InvalidOperationException with a plain message when Windows won't.</summary>
        public static void Set(bool on, string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentException("exePath");
            var link = ShortcutPath;
            try
            {
                if (!on)
                {
                    if (File.Exists(link)) File.Delete(link);
                    Log.Info("start with Windows: off (" + link + " removed)");
                    return;
                }
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) throw new InvalidOperationException("Windows Script Host isn't available on this PC.");
                var shell = Activator.CreateInstance(shellType);
                var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                var type = shortcut.GetType();
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exePath) ?? "" });
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { exePath + ",0" });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Rewind: clip the last 60 seconds" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                Log.Info("start with Windows: on (" + link + ")");
            }
            catch (IOException error) { throw new InvalidOperationException("Couldn't change the Startup shortcut: " + error.Message); }
            catch (UnauthorizedAccessException error) { throw new InvalidOperationException("Couldn't change the Startup shortcut: " + error.Message); }
            catch (COMException error) { throw new InvalidOperationException("Windows refused to make the shortcut: " + error.Message); }
            catch (TargetInvocationException error) { throw new InvalidOperationException("Windows refused to make the shortcut: " + (error.InnerException ?? error).Message); }
        }
    }
}
