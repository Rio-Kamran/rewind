using System;
using System.Collections.Generic;

namespace Rewind
{
    /// <summary>One read from ffmpeg's output: when it arrived and the bytes. Never modified.</summary>
    internal sealed class Chunk
    {
        public readonly DateTime AtUtc;
        public readonly byte[] Data;

        public Chunk(DateTime atUtc, byte[] data)
        {
            AtUtc = atUtc;
            Data = data;
        }
    }

    /// <summary>
    /// The replay buffer. Holds the last N seconds of the encoded MPEG-TS stream in memory as a
    /// queue of timestamped chunks; anything older than the retain window is dropped as new data
    /// arrives, so memory stays flat at roughly bitrate x seconds.
    /// </summary>
    internal sealed class ChunkRing
    {
        private readonly object _gate = new object();
        private readonly Queue<Chunk> _chunks = new Queue<Chunk>();
        private readonly TimeSpan _retain;
        private long _bytes;
        private DateTime _newestUtc = DateTime.MinValue;

        public ChunkRing(TimeSpan retain)
        {
            if (retain <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("retain");
            _retain = retain;
        }

        public long Bytes { get { lock (_gate) return _bytes; } }

        /// <summary>How much time the buffer currently covers, oldest chunk to newest.</summary>
        public TimeSpan Span
        {
            get
            {
                lock (_gate)
                {
                    if (_chunks.Count == 0) return TimeSpan.Zero;
                    return _newestUtc - _chunks.Peek().AtUtc;
                }
            }
        }

        /// <summary>Copies count bytes from data into the ring and drops chunks that fell out of the window.</summary>
        public void Add(byte[] data, int count, DateTime atUtc)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (count < 0 || count > data.Length) throw new ArgumentOutOfRangeException("count");
            if (count == 0) return;

            var copy = new byte[count];
            Buffer.BlockCopy(data, 0, copy, 0, count);
            var chunk = new Chunk(atUtc, copy);

            lock (_gate)
            {
                _chunks.Enqueue(chunk);
                _bytes += count;
                if (atUtc > _newestUtc) _newestUtc = atUtc;
                var cutoff = atUtc - _retain;
                while (_chunks.Count > 0 && _chunks.Peek().AtUtc < cutoff)
                {
                    _bytes -= _chunks.Dequeue().Data.Length;
                }
            }
        }

        /// <summary>
        /// The chunks received in the last span, oldest first. Chunks are immutable, so this hands
        /// out references: nothing is copied, and the list stays valid however the ring moves on.
        /// </summary>
        public IList<Chunk> Chunks(TimeSpan span, DateTime nowUtc)
        {
            var cutoff = nowUtc - span;
            lock (_gate)
            {
                var result = new List<Chunk>(_chunks.Count);
                foreach (var chunk in _chunks) if (chunk.AtUtc >= cutoff) result.Add(chunk);
                return result.AsReadOnly();
            }
        }

        /// <summary>A fresh byte array holding everything received in the last span. Empty if nothing.</summary>
        public byte[] Snapshot(TimeSpan span, DateTime nowUtc)
        {
            var chunks = Chunks(span, nowUtc);
            long total = 0;
            foreach (var chunk in chunks) total += chunk.Data.Length;

            var result = new byte[total];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                Buffer.BlockCopy(chunk.Data, 0, result, offset, chunk.Data.Length);
                offset += chunk.Data.Length;
            }
            return result;
        }

        public void Clear()
        {
            lock (_gate)
            {
                _chunks.Clear();
                _bytes = 0;
                _newestUtc = DateTime.MinValue;
            }
        }
    }
}
