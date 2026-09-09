using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rewind
{
    /// <summary>One audio input for ffmpeg: which pipe, and what raw PCM is coming down it.</summary>
    internal sealed class AudioSource
    {
        public readonly string Label;
        public readonly string PipeName;
        /// <summary>ffmpeg raw format name: f32le, s16le or s32le.</summary>
        public readonly string SampleFormat;
        public readonly int SampleRate;
        public readonly int Channels;
        /// <summary>ffmpeg audio filter chain for this track, or empty.</summary>
        public readonly string Filter;

        public AudioSource(string label, string pipeName, string sampleFormat, int sampleRate, int channels, string filter)
        {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("label");
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentException("pipeName");
            if (sampleFormat != "f32le" && sampleFormat != "s16le" && sampleFormat != "s32le")
                throw new ArgumentException("Unsupported sample format " + sampleFormat);
            if (sampleRate < 8000 || sampleRate > 384000) throw new ArgumentOutOfRangeException("sampleRate");
            if (channels < 1 || channels > 16) throw new ArgumentOutOfRangeException("channels");
            Label = label;
            PipeName = pipeName;
            SampleFormat = sampleFormat;
            SampleRate = sampleRate;
            Channels = channels;
            Filter = filter ?? "";
        }
    }

    /// <summary>
    /// Builds ffmpeg command lines. Pure functions of their inputs, so the tests can check them
    /// without touching a GPU.
    /// </summary>
    internal static class FfmpegArgs
    {
        /// <summary>
        /// The always-on capture: monitor -> NVENC -> MPEG-TS on stdout, with one AAC track per
        /// audio source. MPEG-TS is the one container that can be cut at any byte and still play,
        /// which is what lets the ring buffer be a plain pile of bytes.
        /// </summary>
        public static string Capture(Config config, int outputIndex, IList<AudioSource> audio)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (audio == null) throw new ArgumentNullException("audio");
            if (outputIndex < 0) throw new ArgumentOutOfRangeException("outputIndex");

            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel warning -nostdin ");

            // Video: Desktop Duplication straight into D3D11 textures; NVENC reads those directly,
            // so no frame ever crosses back to the CPU.
            var graph = string.Format(CultureInfo.InvariantCulture,
                "ddagrab=output_idx={0}:framerate={1}:draw_mouse=1:output_fmt=8bit", outputIndex, config.Fps);
            sb.Append("-f lavfi -i ").Append(Quote(graph)).Append(' ');

            // Audio: raw PCM over named pipes, paced to wall-clock by AudioTap.
            foreach (var source in audio)
            {
                if (config.AudioOffsetMs != 0)
                    sb.Append("-itsoffset ")
                      .Append((config.AudioOffsetMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture))
                      .Append(' ');
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "-thread_queue_size 4096 -f {0} -ar {1} -ac {2} -i {3} ",
                    source.SampleFormat, source.SampleRate, source.Channels, Quote(@"\\.\pipe\" + source.PipeName)));
            }

            sb.Append("-map 0:v ");
            for (var i = 0; i < audio.Count; i++) sb.Append("-map ").Append(i + 1).Append(":a ");

            sb.Append(VideoEncoder(config)).Append(' ');

            for (var i = 0; i < audio.Count; i++)
            {
                if (audio[i].Filter.Length > 0)
                    sb.Append("-filter:a:").Append(i).Append(' ').Append(Quote(audio[i].Filter)).Append(' ');
            }
            if (audio.Count > 0) sb.Append("-c:a aac -b:a 192k -ar 48000 ");

            // muxdelay/muxpreload 0 + flush_packets: bytes reach the ring within a frame or two of
            // being encoded, so a clip ends where the hotkey was pressed, not half a second before.
            sb.Append("-f mpegts -muxdelay 0 -muxpreload 0 -flush_packets 1 pipe:1");
            return sb.ToString();
        }

        private static string VideoEncoder(Config config)
        {
            string encoder, profile;
            switch (config.Codec)
            {
                case "hevc": encoder = "hevc_nvenc"; profile = "-profile:v main "; break;
                case "av1": encoder = "av1_nvenc"; profile = ""; break;
                default: encoder = "h264_nvenc"; profile = "-profile:v high "; break;
            }
            // One keyframe per second (-g fps, forced IDR): a clip can start at most one second
            // after where the buffer starts, and every second boundary is a clean cut point.
            return string.Format(CultureInfo.InvariantCulture,
                "-c:v {0} -preset p5 -tune hq {1}-rc cbr -b:v {2}M -maxrate {2}M -bufsize {3}M -g {4} -forced-idr 1",
                encoder, profile, config.BitrateMbps, config.BitrateMbps * 2, config.Fps);
        }

        /// <summary>
        /// Turns a dumped slice of the ring into an MP4 without re-encoding. ffmpeg drops video
        /// packets before the first keyframe on its own when stream-copying, so the cut is clean.
        /// </summary>
        public static string Remux(string tsPath, string mp4Path, IList<string> audioLabels, string codec)
        {
            if (string.IsNullOrEmpty(tsPath)) throw new ArgumentException("tsPath");
            if (string.IsNullOrEmpty(mp4Path)) throw new ArgumentException("mp4Path");
            if (audioLabels == null) throw new ArgumentNullException("audioLabels");

            var sb = new StringBuilder();
            // error, not warning: the parser always grumbles about the few frames before the first
            // keyframe, which are the ones it is supposed to throw away.
            sb.Append("-hide_banner -loglevel error -nostdin -y -i ").Append(Quote(tsPath)).Append(' ');
            sb.Append("-map 0:v -map 0:a? -c copy ");
            if (codec == "hevc") sb.Append("-tag:v hvc1 "); // the tag Apple players insist on
            for (var i = 0; i < audioLabels.Count; i++)
                sb.Append("-metadata:s:a:").Append(i).Append(" title=").Append(Quote(audioLabels[i])).Append(' ');
            sb.Append("-movflags +faststart ").Append(Quote(mp4Path));
            return sb.ToString();
        }

        /// <summary>Wraps an argument in double quotes, escaping any it contains.</summary>
        public static string Quote(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
