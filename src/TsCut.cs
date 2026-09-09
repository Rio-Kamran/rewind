using System;
using System.Collections.Generic;

namespace Rewind
{
    /// <summary>Where a clip should start inside the ring's chunks. Immutable.</summary>
    internal sealed class CutPlan
    {
        /// <summary>The PAT and PMT packets to write before the cut (empty for a raw cut).</summary>
        public readonly byte[] Prefix;
        public readonly int StartChunk;
        public readonly int StartOffset;
        /// <summary>True when the clip starts on a keyframe; false = raw cut from byte 0.</summary>
        public readonly bool Clean;
        /// <summary>Bytes skipped before the cut.</summary>
        public readonly long TrimmedBytes;

        public CutPlan(byte[] prefix, int startChunk, int startOffset, bool clean, long trimmedBytes)
        {
            if (prefix == null) throw new ArgumentNullException("prefix");
            if (startChunk < 0 || startOffset < 0 || trimmedBytes < 0) throw new ArgumentOutOfRangeException("startChunk");
            Prefix = prefix;
            StartChunk = startChunk;
            StartOffset = startOffset;
            Clean = clean;
            TrimmedBytes = trimmedBytes;
        }

        public static CutPlan Raw()
        {
            return new CutPlan(new byte[0], 0, 0, false, 0);
        }
    }

    /// <summary>
    /// Finds the first keyframe in a slice of the MPEG-TS ring so a clip can start exactly there.
    /// ffmpeg's TS muxer sets the random-access indicator on the first packet of every keyframe,
    /// and re-sends the PAT/PMT tables (which say what the streams are) every tenth of a second,
    /// so the clean cut is: the newest PAT + PMT before the keyframe, then everything from the
    /// keyframe on. Without it ffmpeg has to chew through and throw away up to a second of
    /// frames, complaining about every one of them.
    ///
    /// Pure: reads the chunks, copies nothing but two 188-byte table packets.
    /// </summary>
    internal static class TsCut
    {
        public const int PacketSize = 188;
        private const byte SyncByte = 0x47;
        // Keyframes are one second apart, so this is generous even at the top bitrate.
        private const long MaxScanBytes = 32L * 1024 * 1024;

        private static readonly byte[] VideoTypes = { 0x01, 0x02, 0x10, 0x1b, 0x24, 0x42, 0xd1, 0xea };
        private static readonly byte[] AudioTypes = { 0x03, 0x04, 0x0f, 0x11, 0x1c, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x8a, 0x91 };

        public static CutPlan Plan(IList<Chunk> chunks)
        {
            if (chunks == null) throw new ArgumentNullException("chunks");
            var reader = new Reader(chunks);
            if (reader.Total < PacketSize * 3) return CutPlan.Raw();
            var limit = Math.Min(reader.Total, MaxScanBytes);

            var pos = Resync(reader, 0, limit);
            if (pos < 0) return CutPlan.Raw();

            int pmtPid = -1, videoPid = -1;
            long lastPat = -1, lastPmt = -1;
            while (pos + PacketSize <= limit)
            {
                if (reader.At(pos) != SyncByte)
                {
                    pos = Resync(reader, pos + 1, limit);
                    if (pos < 0) break;
                    continue;
                }
                var b1 = reader.At(pos + 1);
                var pusi = (b1 & 0x40) != 0;
                var pid = ((b1 & 0x1f) << 8) | reader.At(pos + 2);
                var afc = (reader.At(pos + 3) >> 4) & 3;
                var payload = pos + 4;
                var rai = false;
                if ((afc & 2) != 0)
                {
                    int afLen = reader.At(pos + 4);
                    if (afLen > 0) rai = (reader.At(pos + 5) & 0x40) != 0;
                    payload = pos + 5 + afLen;
                }
                var hasPayload = (afc & 1) != 0 && payload < pos + PacketSize;

                if (pid == 0 && pusi && hasPayload)
                {
                    var found = ParsePat(reader, payload, pos + PacketSize);
                    if (found > 0) { pmtPid = found; lastPat = pos; }
                }
                else if (pid == pmtPid && pusi && hasPayload)
                {
                    var found = ParsePmt(reader, payload, pos + PacketSize);
                    if (found > 0) { videoPid = found; lastPmt = pos; }
                }
                else if (pid == videoPid && pusi && rai && lastPat >= 0 && lastPmt >= 0)
                {
                    var prefix = new byte[PacketSize * 2];
                    reader.CopyTo(lastPat, prefix, 0, PacketSize);
                    reader.CopyTo(lastPmt, prefix, PacketSize, PacketSize);
                    int offset;
                    var chunk = reader.ChunkAt(pos, out offset);
                    return new CutPlan(prefix, chunk, offset, true, pos);
                }
                pos += PacketSize;
            }
            return CutPlan.Raw();
        }

        /// <summary>First position at or after from where three packets in a row start with the sync byte.</summary>
        private static long Resync(Reader reader, long from, long limit)
        {
            for (var p = from; p + PacketSize * 2 < limit && p + PacketSize * 2 < reader.Total; p++)
            {
                if (reader.At(p) == SyncByte && reader.At(p + PacketSize) == SyncByte && reader.At(p + PacketSize * 2) == SyncByte)
                    return p;
            }
            return -1;
        }

        /// <summary>The PMT pid of the first real program in a PAT, or -1.</summary>
        private static int ParsePat(Reader reader, long payload, long end)
        {
            var section = payload + 1 + reader.At(payload); // skip the pointer field
            if (section + 8 > end || reader.At(section) != 0x00) return -1;
            var length = ((reader.At(section + 1) & 0x0f) << 8) | reader.At(section + 2);
            var stop = Math.Min(end, section + 3 + length - 4);
            for (var p = section + 8; p + 4 <= stop; p += 4)
            {
                var program = (reader.At(p) << 8) | reader.At(p + 1);
                var pid = ((reader.At(p + 2) & 0x1f) << 8) | reader.At(p + 3);
                if (program != 0) return pid;
            }
            return -1;
        }

        /// <summary>The video stream's pid from a PMT: the first video type, else the first non-audio stream. -1 if none.</summary>
        private static int ParsePmt(Reader reader, long payload, long end)
        {
            var section = payload + 1 + reader.At(payload);
            if (section + 12 > end || reader.At(section) != 0x02) return -1;
            var length = ((reader.At(section + 1) & 0x0f) << 8) | reader.At(section + 2);
            var infoLength = ((reader.At(section + 10) & 0x0f) << 8) | reader.At(section + 11);
            var stop = Math.Min(end, section + 3 + length - 4);
            var fallback = -1;
            for (var p = section + 12 + infoLength; p + 5 <= stop; )
            {
                var type = reader.At(p);
                var pid = ((reader.At(p + 1) & 0x1f) << 8) | reader.At(p + 2);
                var esLength = ((reader.At(p + 3) & 0x0f) << 8) | reader.At(p + 4);
                if (Array.IndexOf(VideoTypes, type) >= 0) return pid;
                if (fallback < 0 && Array.IndexOf(AudioTypes, type) < 0) fallback = pid;
                p += 5 + esLength;
            }
            return fallback;
        }

        /// <summary>Byte access across a list of chunks without joining them. Positions are read mostly in order.</summary>
        private sealed class Reader
        {
            private readonly IList<Chunk> _chunks;
            private readonly long[] _starts;
            private int _index;
            public readonly long Total;

            public Reader(IList<Chunk> chunks)
            {
                _chunks = chunks;
                _starts = new long[chunks.Count];
                long total = 0;
                for (var i = 0; i < chunks.Count; i++)
                {
                    _starts[i] = total;
                    total += chunks[i].Data.Length;
                }
                Total = total;
            }

            public byte At(long pos)
            {
                Seek(pos);
                return _chunks[_index].Data[(int)(pos - _starts[_index])];
            }

            public int ChunkAt(long pos, out int offset)
            {
                Seek(pos);
                offset = (int)(pos - _starts[_index]);
                return _index;
            }

            public void CopyTo(long pos, byte[] target, int targetOffset, int count)
            {
                for (var i = 0; i < count; i++) target[targetOffset + i] = At(pos + i);
            }

            private void Seek(long pos)
            {
                if (pos < 0 || pos >= Total) throw new ArgumentOutOfRangeException("pos");
                while (pos < _starts[_index]) _index--;
                while (pos >= _starts[_index] + _chunks[_index].Data.Length) _index++;
            }
        }
    }
}
