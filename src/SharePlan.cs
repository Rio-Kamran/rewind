using System;

namespace Rewind
{
    /// <summary>
    /// How to get a clip under Discord's upload limit: the bitrate that fits the length, and a
    /// smaller picture / slower frame rate when that bitrate would look like soup. Immutable, pure.
    /// </summary>
    internal sealed class SharePlan
    {
        /// <summary>Discord counts megabytes as a million bytes; a little headroom for the MP4 wrapper.</summary>
        private const double Headroom = 0.92;
        private const int AudioKbpsStereo = 96;
        private const int MinVideoKbps = 250;

        public readonly bool NeedsEncode;
        /// <summary>What the file may be at most, in bytes.</summary>
        public readonly long LimitBytes;
        public readonly int VideoKbps;
        /// <summary>0 when the clip has no audio.</summary>
        public readonly int AudioKbps;
        /// <summary>Output height in pixels; 0 = keep the source.</summary>
        public readonly int Height;
        /// <summary>Output frame rate; 0 = keep the source.</summary>
        public readonly int Fps;
        private readonly int _sourceHeight;

        private SharePlan(bool needsEncode, long limitBytes, int videoKbps, int audioKbps, int height, int fps, int sourceHeight)
        {
            NeedsEncode = needsEncode;
            LimitBytes = limitBytes;
            VideoKbps = videoKbps;
            AudioKbps = audioKbps;
            Height = height;
            Fps = fps;
            _sourceHeight = sourceHeight;
        }

        public static SharePlan For(long bytes, double durationSeconds, int maxMb, int sourceHeight, int audioTracks)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException("bytes");
            if (durationSeconds <= 0) throw new ArgumentOutOfRangeException("durationSeconds");
            if (maxMb < 1) throw new ArgumentOutOfRangeException("maxMb");
            if (audioTracks < 0) throw new ArgumentOutOfRangeException("audioTracks");

            var limit = maxMb * 1000000L;
            if (bytes <= limit) return new SharePlan(false, limit, 0, 0, 0, 0, sourceHeight);

            var audioKbps = audioTracks > 0 ? AudioKbpsStereo : 0;
            var totalKbps = limit * Headroom * 8 / 1000 / durationSeconds;
            var videoKbps = Math.Max(MinVideoKbps, (int)(totalKbps - audioKbps));
            return new SharePlan(true, limit, videoKbps, audioKbps, HeightFor(videoKbps, sourceHeight), videoKbps < 3000 ? 30 : 0, sourceHeight);
        }

        /// <summary>The same plan with the video bitrate scaled down, for a second try when the first came out too big.</summary>
        public SharePlan Shrunk(long actualBytes)
        {
            if (actualBytes <= 0) throw new ArgumentOutOfRangeException("actualBytes");
            var factor = Math.Min(0.95, LimitBytes / (double)actualBytes * 0.95);
            var videoKbps = Math.Max(MinVideoKbps, (int)(VideoKbps * factor));
            return new SharePlan(true, LimitBytes, videoKbps, AudioKbps, HeightFor(videoKbps, _sourceHeight), videoKbps < 3000 ? 30 : Fps, _sourceHeight);
        }

        /// <summary>Fewer pixels per bit: 1080p is the ceiling for Discord anyway, 720p under 3 Mbps, 480p under 1.2 Mbps.</summary>
        private static int HeightFor(int videoKbps, int sourceHeight)
        {
            var wanted = videoKbps < 1200 ? 480 : videoKbps < 3000 ? 720 : 1080;
            return sourceHeight > 0 && sourceHeight <= wanted ? 0 : wanted;
        }

        public override string ToString()
        {
            if (!NeedsEncode) return "fits as it is";
            return string.Format("{0} kbps video + {1} kbps audio{2}{3}", VideoKbps, AudioKbps,
                Height > 0 ? ", " + Height + "p" : "", Fps > 0 ? ", " + Fps + " fps" : "");
        }
    }
}
