using System;

namespace Rewind
{
    /// <summary>
    /// Spots the capture that is alive but blind. When Windows takes the screen away from ddagrab
    /// (a display re-plug, a resolution switch, the secure desktop) the video input ends, yet ffmpeg
    /// keeps running on the two audio pipes: the ring fills with sound only and every save fails.
    /// Pure checks, so the tests can cover them without a GPU.
    /// </summary>
    internal static class CaptureFault
    {
        /// <summary>The ring has to hold this much before its size means anything.</summary>
        private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(20);
        /// <summary>Under this share of the expected bytes, the video is gone. Audio alone is ~2 %.</summary>
        private const double AudioOnlyShare = 0.05;

        /// <summary>True for the ffmpeg stderr lines that mean the screen grab has ended for good.</summary>
        public static bool IsVideoLost(string stderrLine)
        {
            if (string.IsNullOrEmpty(stderrLine)) return false;
            return stderrLine.IndexOf("AcquireNextFrame failed", StringComparison.OrdinalIgnoreCase) >= 0
                || (stderrLine.IndexOf("lavfi", StringComparison.OrdinalIgnoreCase) >= 0
                    && stderrLine.IndexOf("Error during demuxing", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>True when the buffered bytes are far too few for the video bitrate: only audio is arriving.</summary>
        public static bool LooksAudioOnly(long bytes, TimeSpan span, int bitrateMbps)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException("bytes");
            if (bitrateMbps < 1) throw new ArgumentOutOfRangeException("bitrateMbps");
            if (span < MinSpan) return false;
            var expected = bitrateMbps * 1000000.0 / 8.0 * span.TotalSeconds;
            return bytes < expected * AudioOnlyShare;
        }
    }
}
