using System;
using System.IO;
using System.Text;

namespace Rewind
{
    /// <summary>
    /// Append-only text log next to the exe (rewind.log). Thread-safe. When the file passes 1 MB
    /// it is renamed to rewind.log.old and a fresh one starts, so it can never eat the disk.
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();
        private static string _path;

        public static string Path { get { return _path; } }

        public static void Init(string path) { _path = path; }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        private static void Write(string level, string message)
        {
            if (_path == null) return;
            var line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} {1,-5} {2}{3}",
                DateTime.Now, level, message, Environment.NewLine);
            lock (Gate)
            {
                try
                {
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        var old = _path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_path, old);
                    }
                    File.AppendAllText(_path, line, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // The log is the one place an error can't be reported to: a locked or full
                    // disk must not take the recorder down with it.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
