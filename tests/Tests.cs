using System;
using System.Collections.Generic;
using Rewind;

namespace Rewind.Tests
{
    /// <summary>
    /// Plain asserts, no framework: the in-box C# compiler is all this project needs. Covers the
    /// parts that can be wrong without a GPU or a microphone: config parsing, hotkey parsing, the
    /// ring buffer, the ffmpeg command lines and the file-name cleanup.
    /// </summary>
    internal static class Tests
    {
        private static int _passed, _failed;

        private static int Main()
        {
            Run("config: empty text gives the defaults", () =>
            {
                var c = Config.Parse("");
                Equal(60, c.Seconds); Equal(60, c.Fps); Equal(20, c.BitrateMbps);
                Equal("h264", c.Codec); Equal("primary", c.Monitor); Equal("ctrl+alt+p", c.Hotkey);
                True(c.GameAudio, "game audio on"); True(c.Mic, "mic on"); Equal(0, c.AudioOffsetMs);
                True(c.ClipsFolder.EndsWith("Rewind"), "clips folder ends with Rewind");
            });
            Run("config: the default file parses back to the defaults", () =>
            {
                var c = Config.Parse(Config.DefaultText());
                var d = Config.Defaults();
                Equal(d.Seconds, c.Seconds); Equal(d.MicFilter, c.MicFilter); Equal(d.ClipsFolder, c.ClipsFolder); Equal("", c.FfmpegPath);
            });
            Run("config: values, inline comments and case", () =>
            {
                var c = Config.Parse("Seconds=30 # half a minute\nFPS=120\ncodec=AV1\nmonitor=2\nmic=off\nmic_filter=OFF\naudio_offset_ms=-120\nbitrate_mbps=35");
                Equal(30, c.Seconds); Equal(120, c.Fps); Equal("av1", c.Codec); Equal("2", c.Monitor);
                True(!c.Mic, "mic off"); Equal("", c.MicFilter); Equal(-120, c.AudioOffsetMs); Equal(35, c.BitrateMbps);
            });
            Run("config: unknown key is refused", () => Throws<ConfigException>(() => Config.Parse("bogus=1")));
            Run("config: seconds out of range is refused", () => Throws<ConfigException>(() => Config.Parse("seconds=1000")));
            Run("config: non-number is refused", () => Throws<ConfigException>(() => Config.Parse("fps=fast")));
            Run("config: bad bool is refused", () => Throws<ConfigException>(() => Config.Parse("mic=maybe")));
            Run("config: bad codec is refused", () => Throws<ConfigException>(() => Config.Parse("codec=vp9")));
            Run("config: bad monitor is refused", () => Throws<ConfigException>(() => Config.Parse("monitor=left")));
            Run("config: duplicate key is refused", () => Throws<ConfigException>(() => Config.Parse("seconds=10\nseconds=20")));
            Run("config: line without = is refused", () => Throws<ConfigException>(() => Config.Parse("seconds")));
            Run("config: bad hotkey is refused", () => Throws<ConfigException>(() => Config.Parse("hotkey=ctrl+banana")));

            Run("hotkey: ctrl+alt+p", () =>
            {
                var h = HotkeySpec.Parse("ctrl+alt+p");
                Equal(HotkeySpec.ModControl | HotkeySpec.ModAlt, h.Modifiers); Equal(0x50u, h.VirtualKey); Equal("Ctrl+Alt+P", h.Text);
            });
            Run("hotkey: bare F9", () =>
            {
                var h = HotkeySpec.Parse("F9");
                Equal(0u, h.Modifiers); Equal(0x78u, h.VirtualKey); Equal("F9", h.Text);
            });
            Run("hotkey: shift+5 is the digit key", () =>
            {
                var h = HotkeySpec.Parse(" Shift + 5 ");
                Equal(HotkeySpec.ModShift, h.Modifiers); Equal(0x35u, h.VirtualKey); Equal("Shift+5", h.Text);
            });
            Run("hotkey: modifiers only is refused", () => Throws<ConfigException>(() => HotkeySpec.Parse("ctrl+alt")));
            Run("hotkey: two keys is refused", () => Throws<ConfigException>(() => HotkeySpec.Parse("a+b")));
            Run("hotkey: modifier as the key is refused", () => Throws<ConfigException>(() => HotkeySpec.Parse("ctrl+shiftkey")));
            Run("hotkey: empty is refused", () => Throws<ConfigException>(() => HotkeySpec.Parse("  ")));

            Run("ring: keeps only the window", () =>
            {
                var ring = new ChunkRing(TimeSpan.FromSeconds(10));
                var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                ring.Add(new byte[100], 100, t0);
                ring.Add(new byte[50], 50, t0.AddSeconds(5));
                ring.Add(new byte[10], 10, t0.AddSeconds(11));
                Equal(60L, ring.Bytes); Equal(TimeSpan.FromSeconds(6), ring.Span);
            });
            Run("ring: snapshot is the last span, in order, and a copy", () =>
            {
                var ring = new ChunkRing(TimeSpan.FromSeconds(60));
                var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var first = new byte[] { 1, 2, 3 };
                ring.Add(first, 3, t0);
                ring.Add(new byte[] { 4, 5 }, 2, t0.AddSeconds(10));
                ring.Add(new byte[] { 6 }, 1, t0.AddSeconds(20));
                first[0] = 99; // the ring must have copied, not kept our array
                var all = ring.Snapshot(TimeSpan.FromSeconds(30), t0.AddSeconds(20));
                Equal("1,2,3,4,5,6", string.Join(",", all));
                var recent = ring.Snapshot(TimeSpan.FromSeconds(10), t0.AddSeconds(20));
                Equal("4,5,6", string.Join(",", recent));
                Equal(0, ring.Snapshot(TimeSpan.FromSeconds(1), t0.AddSeconds(60)).Length);
            });
            Run("ring: partial add copies only count bytes", () =>
            {
                var ring = new ChunkRing(TimeSpan.FromSeconds(1));
                ring.Add(new byte[] { 7, 8, 9 }, 2, DateTime.UtcNow);
                Equal(2L, ring.Bytes);
            });
            Run("ring: clear empties it", () =>
            {
                var ring = new ChunkRing(TimeSpan.FromSeconds(1));
                ring.Add(new byte[] { 1 }, 1, DateTime.UtcNow);
                ring.Clear();
                Equal(0L, ring.Bytes); Equal(TimeSpan.Zero, ring.Span);
            });
            Run("ring: bad count is refused", () => Throws<ArgumentOutOfRangeException>(() => new ChunkRing(TimeSpan.FromSeconds(1)).Add(new byte[2], 3, DateTime.UtcNow)));
            Run("ring: zero window is refused", () => Throws<ArgumentOutOfRangeException>(() => new ChunkRing(TimeSpan.Zero)));

            Run("ffmpeg: capture line has every piece", () =>
            {
                var c = Config.Parse("seconds=30\nfps=60\nbitrate_mbps=20");
                var audio = new[]
                {
                    new AudioSource("Game", "rewind_game", "f32le", 48000, 2, ""),
                    new AudioSource("Mic", "rewind_mic", "s16le", 44100, 1, "afftdn=nr=12:nf=-40")
                };
                var a = FfmpegArgs.Capture(c, 2, audio);
                Contains(a, "-f lavfi -i \"ddagrab=output_idx=2:framerate=60:draw_mouse=1:output_fmt=8bit\"");
                Contains(a, "-f f32le -ar 48000 -ac 2 -i \"\\\\.\\pipe\\rewind_game\"");
                Contains(a, "-f s16le -ar 44100 -ac 1 -i \"\\\\.\\pipe\\rewind_mic\"");
                Contains(a, "-map 0:v -map 1:a -map 2:a");
                Contains(a, "-c:v h264_nvenc -preset p5 -tune hq -profile:v high -rc cbr -b:v 20M -maxrate 20M -bufsize 40M -g 60 -forced-idr 1");
                Contains(a, "-filter:a:1 \"afftdn=nr=12:nf=-40\"");
                True(!a.Contains("-filter:a:0"), "no filter on the game track");
                Contains(a, "-c:a aac -b:a 192k -ar 48000");
                Contains(a, "-f mpegts -muxdelay 0 -muxpreload 0 -flush_packets 1 pipe:1");
                True(!a.Contains("-itsoffset"), "no offset by default");
            });
            Run("ffmpeg: audio offset goes on every audio input", () =>
            {
                var c = Config.Parse("audio_offset_ms=-120");
                var a = FfmpegArgs.Capture(c, 0, new[] { new AudioSource("Game", "g", "f32le", 48000, 2, ""), new AudioSource("Mic", "m", "f32le", 48000, 2, "") });
                Equal(2, Count(a, "-itsoffset -0.12 "));
            });
            Run("ffmpeg: no audio means video only", () =>
            {
                var a = FfmpegArgs.Capture(Config.Parse(""), 0, new AudioSource[0]);
                True(!a.Contains("-map 1:a") && !a.Contains("-c:a"), "no audio mapping");
            });
            Run("ffmpeg: av1 and hevc encoders", () =>
            {
                Contains(FfmpegArgs.Capture(Config.Parse("codec=av1"), 0, new AudioSource[0]), "-c:v av1_nvenc -preset p5 -tune hq -rc cbr");
                Contains(FfmpegArgs.Capture(Config.Parse("codec=hevc"), 0, new AudioSource[0]), "-c:v hevc_nvenc -preset p5 -tune hq -profile:v main -rc cbr");
            });
            Run("ffmpeg: remux line", () =>
            {
                var a = FfmpegArgs.Remux(@"C:\x\a.ts", @"C:\x\Rewind clip.mp4", new[] { "Game", "Mic" }, "h264");
                Contains(a, "-y -i \"C:\\x\\a.ts\" -map 0:v -map 0:a? -c copy -metadata:s:a:0 title=\"Game\" -metadata:s:a:1 title=\"Mic\" -movflags +faststart \"C:\\x\\Rewind clip.mp4\"");
                True(!a.Contains("hvc1"), "no hvc1 tag for h264");
                Contains(FfmpegArgs.Remux("a.ts", "b.mp4", new string[0], "hevc"), "-c copy -tag:v hvc1 ");
            });
            Run("ffmpeg: quoting escapes inner quotes", () => Equal("\"a\\\"b\"", FfmpegArgs.Quote("a\"b")));
            Run("ffmpeg: audio source validates its format", () =>
            {
                Throws<ArgumentException>(() => new AudioSource("x", "p", "u8", 48000, 2, ""));
                Throws<ArgumentOutOfRangeException>(() => new AudioSource("x", "p", "f32le", 100, 2, ""));
            });

            Run("file name: process names are cleaned", () =>
            {
                Equal("Fortnite", ForegroundApp.Clean("FortniteClient-Win64-Shipping"));
                Equal("Minecraft", ForegroundApp.Clean("javaw"));
                Equal("weirdname", ForegroundApp.Clean("weird<>:name"));
                Equal("", ForegroundApp.Clean(""));
                Equal("explorer", ForegroundApp.Clean("explorer"));
                Equal(40, ForegroundApp.Clean(new string('a', 80)).Length);
            });

            Console.WriteLine();
            Console.WriteLine(string.Format("{0} passed, {1} failed", _passed, _failed));
            return _failed == 0 ? 0 : 1;
        }

        // ---- the tiniest test harness ----

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("  ok   " + name);
            }
            catch (Exception error)
            {
                _failed++;
                Console.WriteLine("  FAIL " + name + ": " + error.Message);
            }
        }

        private static void True(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

        private static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(string.Format("expected [{0}] but got [{1}]", expected, actual));
        }

        private static void Contains(string haystack, string needle)
        {
            if (haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new Exception("expected to find [" + needle + "] in [" + haystack + "]");
        }

        private static int Count(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            catch (Exception other) { throw new Exception("expected " + typeof(T).Name + " but got " + other.GetType().Name + ": " + other.Message); }
            throw new Exception("expected " + typeof(T).Name + " but nothing was thrown");
        }
    }
}
