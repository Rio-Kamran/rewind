using System;
using System.Collections.Generic;
using System.IO;
using Rewind;

namespace Rewind.Tests
{
    /// <summary>
    /// Plain asserts, no framework: the in-box C# compiler is all this project needs. Covers the
    /// parts that can be wrong without a GPU or a microphone: config parsing, hotkey parsing, the
    /// ring buffer, the keyframe cut, the ffmpeg command lines and the file-name cleanup.
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
            Run("config: config.example.txt is the default config, byte for byte", () =>
            {
                var example = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.example.txt"));
                Equal(Config.DefaultText().Replace("\r\n", "\n").Trim(), example.Replace("\r\n", "\n").Trim());
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
            Run("config: new defaults (short hotkey, games mode)", () =>
            {
                var c = Config.Parse("");
                Equal("ctrl+alt+o", c.HotkeyShort); Equal(15, c.ShortSeconds); Equal("always", c.Record); True(!c.GamesOnly, "always");
                Equal(45, c.GameGraceSeconds); Equal(6, c.Games.Count); Equal("javaw", c.Games[0]);
            });
            Run("config: hotkey_short=off disables it", () => Equal("", Config.Parse("hotkey_short=OFF").HotkeyShort));
            Run("config: same key twice is refused", () => Throws<ConfigException>(() => Config.Parse("hotkey=ctrl+alt+p\nhotkey_short=Alt+Ctrl+P")));
            Run("config: short_seconds must be below seconds", () =>
            {
                Throws<ConfigException>(() => Config.Parse("seconds=20\nshort_seconds=20"));
                Equal(19, Config.Parse("seconds=20\nshort_seconds=19").ShortSeconds);
            });
            Run("config: bad record mode is refused", () => Throws<ConfigException>(() => Config.Parse("record=sometimes")));
            Run("config: grace out of range is refused", () => Throws<ConfigException>(() => Config.Parse("game_grace_seconds=2")));
            Run("config: games list is trimmed, .exe dropped, repeats gone", () =>
            {
                var games = Config.ParseGames(" javaw , cs2.exe;Roblox,,JAVAW, RocketLeague.EXE ");
                Equal("javaw|cs2|Roblox|RocketLeague", string.Join("|", games));
                Equal(0, Config.ParseGames("").Count);
            });
            Run("config: text round-trips a non-default config", () =>
            {
                var c = Config.Parse("hotkey=F9\nhotkey_short=shift+F9\nseconds=90\nshort_seconds=20\nfps=30\nbitrate_mbps=8\ncodec=hevc\nmonitor=1\n"
                    + "game_audio=off\nmic=off\nmic_filter=off\naudio_offset_ms=50\nclips=D:\\Clips\nffmpeg=C:\\ff\\ffmpeg.exe\nrecord=games\ngames=javaw, cs2.exe\ngame_grace_seconds=30");
                var text = c.Text();
                Contains(text, "# Where clips go." + Environment.NewLine + "clips=D:\\Clips" + Environment.NewLine);
                var back = Config.Parse(text);
                Equal("F9", back.Hotkey); Equal("shift+F9", back.HotkeyShort); Equal(90, back.Seconds); Equal(20, back.ShortSeconds);
                Equal(30, back.Fps); Equal(8, back.BitrateMbps); Equal("hevc", back.Codec); Equal("1", back.Monitor);
                True(!back.GameAudio && !back.Mic, "audio off"); Equal("", back.MicFilter); Equal(50, back.AudioOffsetMs);
                Equal(@"D:\Clips", back.ClipsFolder); Equal(@"C:\ff\ffmpeg.exe", back.FfmpegPath); True(back.GamesOnly, "games mode");
                Equal("javaw|cs2", string.Join("|", back.Games)); Equal(30, back.GameGraceSeconds);
            });
            Run("config: text with missing values falls back to defaults", () =>
            {
                var text = Config.Text(new Dictionary<string, string> { { "seconds", "30" } });
                var c = Config.Parse(text);
                Equal(30, c.Seconds); Equal(60, c.Fps); Equal("ctrl+alt+p", c.Hotkey);
            });

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
            Run("ring: chunks are the ring's own arrays, oldest first", () =>
            {
                var ring = new ChunkRing(TimeSpan.FromSeconds(60));
                var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                ring.Add(new byte[] { 1 }, 1, t0);
                ring.Add(new byte[] { 2 }, 1, t0.AddSeconds(10));
                var chunks = ring.Chunks(TimeSpan.FromSeconds(30), t0.AddSeconds(10));
                Equal(2, chunks.Count); Equal((byte)1, chunks[0].Data[0]); Equal((byte)2, chunks[1].Data[0]);
                Equal(1, ring.Chunks(TimeSpan.FromSeconds(5), t0.AddSeconds(10)).Count);
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

            var pat = TsPacket(0, true, false, Pat(4096));
            var pmt = TsPacket(4096, true, false, Pmt(new byte[] { 0x1b, 0x0f }, new[] { 256, 257 }));
            Run("tscut: cuts at the first video keyframe after PAT+PMT, across chunk edges", () =>
            {
                var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7 };            // the torn tail of an evicted packet
                var frame = TsPacket(256, true, false, Payload(0xAA));    // a P-frame: not a cut
                var audioKey = TsPacket(257, true, true, Payload(0xBB));  // audio marks every packet: not a cut
                var key = TsPacket(256, true, true, Payload(0xCC));       // the keyframe
                var rest = TsPacket(256, false, false, Payload(0xDD));
                var all = Join(junk, pat, pmt, frame, audioKey, key, rest, rest, rest);
                var plan = TsCut.Plan(Split(all, 100, 400));
                True(plan.Clean, "clean cut");
                Equal(7L + 188 * 4, plan.TrimmedBytes);
                Equal(2, plan.StartChunk); Equal(259, plan.StartOffset);
                Equal(BitConverter.ToString(Join(pat, pmt)), BitConverter.ToString(plan.Prefix));
            });
            Run("tscut: a keyframe before the tables waits for the next one", () =>
            {
                var key = TsPacket(256, true, true, Payload(1));
                var plan = TsCut.Plan(Split(Join(pat, key, pmt, key, key), 1000));
                True(plan.Clean, "clean cut"); Equal(188L * 3, plan.TrimmedBytes);
            });
            Run("tscut: no keyframe means a raw cut from byte 0", () =>
            {
                var frame = TsPacket(256, true, false, Payload(9));
                var plan = TsCut.Plan(Split(Join(pat, pmt, frame, frame, frame), 300));
                True(!plan.Clean, "raw cut"); Equal(0, plan.StartChunk); Equal(0, plan.StartOffset);
                Equal(0, plan.Prefix.Length); Equal(0L, plan.TrimmedBytes);
            });
            Run("tscut: too little data is a raw cut, not a crash", () =>
            {
                True(!TsCut.Plan(new List<Chunk>()).Clean, "empty");
                True(!TsCut.Plan(Split(new byte[100], 50)).Clean, "tiny");
            });
            Run("tscut: video pid comes from the stream type; private data (av1) beats audio", () =>
            {
                var pmtAudioFirst = TsPacket(4096, true, false, Pmt(new byte[] { 0x0f, 0x24 }, new[] { 300, 301 }));
                var audio = TsPacket(300, true, true, Payload(1));
                var video = TsPacket(301, true, true, Payload(2));
                Equal(188L * 3, TsCut.Plan(Split(Join(pat, pmtAudioFirst, audio, video, video), 1000)).TrimmedBytes);
                var pmtAv1 = TsPacket(4096, true, false, Pmt(new byte[] { 0x0f, 0x06 }, new[] { 300, 302 }));
                var av1 = TsPacket(302, true, true, Payload(3));
                Equal(188L * 3, TsCut.Plan(Split(Join(pat, pmtAv1, audio, av1, av1), 1000)).TrimmedBytes);
            });

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
                Contains(a, "-f f32le -ar 48000 -ac 2 -ch_layout stereo -i \"\\\\.\\pipe\\rewind_game\"");
                Contains(a, "-f s16le -ar 44100 -ac 1 -ch_layout mono -i \"\\\\.\\pipe\\rewind_mic\"");
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
            Run("ffmpeg: remux line reads the ring from stdin", () =>
            {
                var a = FfmpegArgs.Remux(@"C:\x\Rewind clip.mp4", new[] { "Game", "Mic" }, "h264");
                Contains(a, "-loglevel error -nostdin -y -f mpegts -i pipe:0 -map 0:v -map 0:a? -c copy -metadata:s:a:0 handler_name=\"Game\" -metadata:s:a:1 handler_name=\"Mic\" -movflags +faststart \"C:\\x\\Rewind clip.mp4\"");
                True(!a.Contains("hvc1"), "no hvc1 tag for h264");
                Contains(FfmpegArgs.Remux("b.mp4", new string[0], "hevc"), "-c copy -tag:v hvc1 ");
            });
            Run("ffmpeg: quoting escapes inner quotes", () => Equal("\"a\\\"b\"", FfmpegArgs.Quote("a\"b")));
            Run("ffmpeg: audio source validates its format", () =>
            {
                Throws<ArgumentException>(() => new AudioSource("x", "p", "u8", 48000, 2, ""));
                Throws<ArgumentOutOfRangeException>(() => new AudioSource("x", "p", "f32le", 100, 2, ""));
            });

            Run("shell: explorer gets /select with a quoted path", () =>
                Equal("/select,\"C:\\x\\a b.mp4\"", Shell.SelectInExplorerArgs(@"C:\x\a b.mp4")));

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

        // ---- synthetic MPEG-TS packets for the TsCut tests ----

        /// <summary>A 188-byte packet with an adaptation field (carrying the RAI flag) and a short payload.</summary>
        private static byte[] TsPacket(int pid, bool pusi, bool rai, byte[] payload)
        {
            if (payload.Length > 182) throw new ArgumentException("payload too long for one packet");
            var packet = new byte[188];
            packet[0] = 0x47;
            packet[1] = (byte)((pusi ? 0x40 : 0) | (pid >> 8));
            packet[2] = (byte)(pid & 0xff);
            packet[3] = 0x30; // adaptation field + payload
            var afLength = 188 - 4 - 1 - payload.Length;
            packet[4] = (byte)afLength;
            packet[5] = (byte)(rai ? 0x40 : 0x00);
            for (var i = 6; i < 5 + afLength; i++) packet[i] = 0xff;
            Buffer.BlockCopy(payload, 0, packet, 5 + afLength, payload.Length);
            return packet;
        }

        private static byte[] Payload(byte fill)
        {
            var bytes = new byte[100];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = fill;
            return bytes;
        }

        private static byte[] Pat(int pmtPid)
        {
            return new byte[]
            {
                0x00,                   // pointer field
                0x00, 0xB0, 0x0D,       // table id, section length 13
                0x00, 0x01, 0xC1, 0x00, 0x00,
                0x00, 0x01, (byte)(0xE0 | (pmtPid >> 8)), (byte)(pmtPid & 0xff),
                0, 0, 0, 0              // crc (not checked)
            };
        }

        private static byte[] Pmt(byte[] types, int[] pids)
        {
            var section = new List<byte> { 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0xC1, 0x00, 0x00, 0xE1, 0x00, 0xF0, 0x00 };
            for (var i = 0; i < types.Length; i++)
                section.AddRange(new[] { types[i], (byte)(0xE0 | (pids[i] >> 8)), (byte)(pids[i] & 0xff), (byte)0xF0, (byte)0x00 });
            section.AddRange(new byte[] { 0, 0, 0, 0 });
            var length = section.Count - 4; // everything after the length field
            section[2] = (byte)(0xB0 | (length >> 8));
            section[3] = (byte)(length & 0xff);
            return section.ToArray();
        }

        private static byte[] Join(params byte[][] parts)
        {
            var total = 0;
            foreach (var part in parts) total += part.Length;
            var all = new byte[total];
            var at = 0;
            foreach (var part in parts) { Buffer.BlockCopy(part, 0, all, at, part.Length); at += part.Length; }
            return all;
        }

        /// <summary>Chops bytes into chunks of the given sizes; the last chunk takes whatever is left.</summary>
        private static IList<Chunk> Split(byte[] all, params int[] sizes)
        {
            var chunks = new List<Chunk>();
            var at = 0;
            foreach (var size in sizes)
            {
                var take = Math.Min(size, all.Length - at);
                if (take <= 0) break;
                var data = new byte[take];
                Buffer.BlockCopy(all, at, data, 0, take);
                chunks.Add(new Chunk(DateTime.UtcNow, data));
                at += take;
            }
            if (at < all.Length)
            {
                var data = new byte[all.Length - at];
                Buffer.BlockCopy(all, at, data, 0, data.Length);
                chunks.Add(new Chunk(DateTime.UtcNow, data));
            }
            return chunks;
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
