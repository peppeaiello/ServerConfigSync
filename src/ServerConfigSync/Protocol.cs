using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace ServerConfigSync
{
    internal sealed class SyncedValue
    {
        public string Section;
        public string Key;
        public string Value;
    }

    internal sealed class Snapshot
    {
        // plugin GUID -> entries
        public readonly Dictionary<string, List<SyncedValue>> Plugins = new Dictionary<string, List<SyncedValue>>();
        public string ServerVersion = "";

        // Server-side log lines describing what was selected. Not sent.
        public readonly List<string> Summary = new List<string>();

        public int EntryCount
        {
            get
            {
                int n = 0;
                foreach (var list in Plugins.Values) n += list.Count;
                return n;
            }
        }
    }

    /// <summary>
    /// Wire format. Everything a client reads is bounded, so a hostile or broken server
    /// cannot make it allocate unbounded memory.
    /// </summary>
    internal static class Protocol
    {
        public const int Version = 1;
        public const string RpcHello = "ServerConfigSync_Hello";
        public const string RpcData = "ServerConfigSync_Data";

        public const int ChunkSize = 250_000;
        public const int MaxChunks = 64;
        public const int MaxDecompressedBytes = 32 * 1024 * 1024;
        public const int MaxPlugins = 2_000;
        public const int MaxEntriesPerPlugin = 20_000;
        public const int MaxEntries = 100_000;
        public const int MaxStringLength = 64 * 1024;

        public static byte[] Serialize(Snapshot snapshot)
        {
            using (var raw = new MemoryStream())
            {
                using (var gzip = new GZipStream(raw, CompressionMode.Compress, true))
                using (var w = new BinaryWriter(gzip))
                {
                    w.Write(Version);
                    w.Write(snapshot.ServerVersion ?? "");
                    w.Write(snapshot.Plugins.Count);
                    foreach (var plugin in snapshot.Plugins)
                    {
                        w.Write(plugin.Key);
                        w.Write(plugin.Value.Count);
                        foreach (var e in plugin.Value)
                        {
                            w.Write(e.Section);
                            w.Write(e.Key);
                            w.Write(e.Value ?? "");
                        }
                    }
                }
                return raw.ToArray();
            }
        }

        public static Snapshot Deserialize(byte[] compressed)
        {
            using (var input = new MemoryStream(compressed, false))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var limited = new LimitedReadStream(gzip, MaxDecompressedBytes))
            using (var r = new BinaryReader(limited))
            {
                int version = r.ReadInt32();
                if (version != Version)
                    throw new InvalidDataException($"unsupported protocol version {version}");

                var snapshot = new Snapshot { ServerVersion = ReadString(r) };
                int pluginCount = ReadCount(r, MaxPlugins);
                int total = 0;
                for (int i = 0; i < pluginCount; i++)
                {
                    string guid = ReadString(r);
                    int entryCount = ReadCount(r, MaxEntriesPerPlugin);
                    total += entryCount;
                    if (total > MaxEntries) throw new InvalidDataException("too many entries");

                    var list = new List<SyncedValue>(entryCount);
                    for (int j = 0; j < entryCount; j++)
                    {
                        list.Add(new SyncedValue
                        {
                            Section = ReadString(r),
                            Key = ReadString(r),
                            Value = ReadString(r)
                        });
                    }
                    snapshot.Plugins[guid] = list;
                }
                if (limited.ReadByte() != -1) throw new InvalidDataException("unexpected data after the payload");
                return snapshot;
            }
        }

        private static int ReadCount(BinaryReader r, int max)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > max) throw new InvalidDataException($"count {n} out of range");
            return n;
        }

        // Same format as BinaryWriter.Write(string), but the length is checked before allocating.
        private static string ReadString(BinaryReader r)
        {
            int length = 0;
            for (int shift = 0; ; shift += 7)
            {
                if (shift > 28) throw new InvalidDataException("bad string length");
                byte b = r.ReadByte();
                length |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
            }
            if (length < 0 || length > MaxStringLength) throw new InvalidDataException("string too long");
            byte[] bytes = r.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        public static List<byte[]> Split(byte[] data)
        {
            var chunks = new List<byte[]>();
            for (int offset = 0; offset < data.Length || chunks.Count == 0; offset += ChunkSize)
            {
                int len = Math.Min(ChunkSize, data.Length - offset);
                var chunk = new byte[len];
                Buffer.BlockCopy(data, offset, chunk, 0, len);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private sealed class LimitedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _limit;
            private long _read;

            public LimitedReadStream(Stream inner, long limit)
            {
                _inner = inner;
                _limit = limit;
            }

            public override int ReadByte()
            {
                var one = new byte[1];
                return Read(one, 0, 1) == 1 ? one[0] : -1;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = _inner.Read(buffer, offset, count);
                _read += n;
                if (_read > _limit) throw new InvalidDataException("payload too large");
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _read; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>Reassembles chunked transfers on the client.</summary>
    internal sealed class ChunkAssembler
    {
        private int _transferId = -1;
        private byte[][] _chunks;
        private int _received;

        /// <returns>The complete payload once all chunks arrived, otherwise null.</returns>
        public byte[] Add(int transferId, int index, int count, byte[] data)
        {
            if (count <= 0 || count > Protocol.MaxChunks || index < 0 || index >= count)
                throw new InvalidDataException("bad chunk header");
            if (data == null || data.Length > Protocol.ChunkSize)
                throw new InvalidDataException("bad chunk size");

            if (transferId != _transferId || _chunks == null || _chunks.Length != count)
            {
                _transferId = transferId;
                _chunks = new byte[count][];
                _received = 0;
            }

            if (_chunks[index] == null) _received++;
            _chunks[index] = data;
            if (_received < count) return null;

            int total = 0;
            foreach (var c in _chunks) total += c.Length;
            var result = new byte[total];
            int offset = 0;
            foreach (var c in _chunks)
            {
                Buffer.BlockCopy(c, 0, result, offset, c.Length);
                offset += c.Length;
            }
            _chunks = null;
            return result;
        }
    }
}
