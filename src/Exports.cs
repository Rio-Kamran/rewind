using System.Threading;

namespace Rewind
{
    /// <summary>
    /// Counts the window's ffmpeg exports in flight (Copy for Discord, trim, GIF), so the
    /// auto-updater never restarts Rewind in the middle of one.
    /// </summary>
    internal static class Exports
    {
        private static int _running;

        public static bool Running { get { return Volatile.Read(ref _running) > 0; } }

        public static void Begin() { Interlocked.Increment(ref _running); }

        public static void End() { Interlocked.Decrement(ref _running); }
    }
}
