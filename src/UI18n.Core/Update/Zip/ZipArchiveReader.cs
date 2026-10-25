using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UI18n.Core.Update
{
    /// <summary>zip 里的一个条目（中央目录里的记录）。</summary>
    public sealed class ZipEntry
    {
        internal ZipEntry(
            string name,
            ushort method,
            ushort flags,
            uint crc32,
            long compressedBytes,
            long uncompressedBytes,
            int localHeaderOffset)
        {
            Name = name;
            Method = method;
            Flags = flags;
            Crc = crc32;
            CompressedBytes = compressedBytes;
            UncompressedBytes = uncompressedBytes;
            LocalHeaderOffset = localHeaderOffset;
        }

        /// <summary>条目名（已把反斜杠统一成正斜杠）。</summary>
        public string Name { get; private set; }

        /// <summary>压缩方法：0 = stored，8 = deflate。</summary>
        public ushort Method { get; private set; }

        /// <summary>通用标志位（bit0 = 加密）。</summary>
        public ushort Flags { get; private set; }

        /// <summary>未压缩内容的 CRC32。</summary>
        public uint Crc { get; private set; }

        /// <summary>压缩后字节数。</summary>
        public long CompressedBytes { get; private set; }

        /// <summary>解压后字节数。</summary>
        public long UncompressedBytes { get; private set; }

        internal int LocalHeaderOffset { get; private set; }

        /// <summary>是否是目录条目（名字以 / 结尾）。</summary>
        public bool IsDirectory
        {
            get { return Name.Length > 0 && Name[Name.Length - 1] == '/'; }
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Name + " (method=" + Method.ToString(CultureInfo.InvariantCulture)
                + ", " + Sha256Hex.DescribeBytes(CompressedBytes) + " -> "
                + Sha256Hex.DescribeBytes(UncompressedBytes) + ")";
        }
    }

    /// <summary>
    /// 只读的 zip 容器视图。**只做「读条目 + 解压到内存」两件事，绝不落盘**：
    /// 落盘由调用方（Runtime）决定，这样「先校验全部内容、再一次性写入」才可能做到。
    /// <para>
    /// 不支持 zip64（显式报错而不是猜测），不支持加密条目，不支持多分卷 ——
    /// 我们的发布包是 .NET 的 ZipFile 产出的普通 zip，这三样永远用不到。
    /// </para>
    /// </summary>
    public sealed class ZipArchive
    {
        private readonly byte[] _zip;
        private readonly List<ZipEntry> _entries;
        private readonly Dictionary<string, ZipEntry> _byName;

        internal ZipArchive(byte[] zip, List<ZipEntry> entries)
        {
            _zip = zip;
            _entries = entries;
            _byName = new Dictionary<string, ZipEntry>(entries.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                _byName[entries[i].Name] = entries[i];
            }
        }

        /// <summary>条目列表（按中央目录顺序）。</summary>
        public IList<ZipEntry> Entries { get { return _entries; } }

        /// <summary>条目数。</summary>
        public int Count { get { return _entries.Count; } }

        /// <summary>按名字找条目（忽略大小写）；没有返回 null。</summary>
        public ZipEntry Find(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            ZipEntry entry;
            return _byName.TryGetValue(name, out entry) ? entry : null;
        }

        /// <summary>按名字解压；条目不存在返回 false（原因 not-found）。</summary>
        public bool TryExtract(string name, out byte[] content, out string error)
        {
            return TryExtract(name, Inflater.DefaultMaxOutputBytes, out content, out error);
        }

        /// <summary>按名字解压（带输出上限）。</summary>
        public bool TryExtract(string name, int maxBytes, out byte[] content, out string error)
        {
            content = null;
            error = null;

            ZipEntry entry = Find(name);
            if (entry == null)
            {
                error = "not-found: " + name;
                return false;
            }

            return TryExtract(entry, maxBytes, out content, out error);
        }

        /// <summary>解压一个条目到内存，并校验大小与 CRC32。</summary>
        public bool TryExtract(ZipEntry entry, out byte[] content, out string error)
        {
            return TryExtract(entry, Inflater.DefaultMaxOutputBytes, out content, out error);
        }

        /// <summary>解压一个条目到内存，并校验大小与 CRC32。</summary>
        public bool TryExtract(ZipEntry entry, int maxBytes, out byte[] content, out string error)
        {
            content = null;
            error = null;

            if (entry == null)
            {
                error = "null-entry";
                return false;
            }

            if (entry.IsDirectory)
            {
                error = "is-directory: " + entry.Name;
                return false;
            }

            if (entry.CompressedBytes < 0 || entry.CompressedBytes > int.MaxValue)
            {
                error = "too-large: " + entry.Name;
                return false;
            }

            int local = entry.LocalHeaderOffset;
            if (local < 0 || local + 30 > _zip.Length)
            {
                error = "bad-local-header: " + entry.Name;
                return false;
            }

            uint signature = ReadUInt32(_zip, local);
            if (signature != 0x04034B50u)
            {
                error = "bad-local-header-signature: " + entry.Name;
                return false;
            }

            int nameLen = ReadUInt16(_zip, local + 26);
            int extraLen = ReadUInt16(_zip, local + 28);
            int dataStart = local + 30 + nameLen + extraLen;
            if (dataStart < 0 || dataStart + entry.CompressedBytes > _zip.Length)
            {
                error = "truncated-entry: " + entry.Name;
                return false;
            }

            byte[] data;
            if (entry.Method == 0)
            {
                // stored 条目也要守 maxBytes：否则「输出上限」只对 deflate 生效，
                // 一个声明了巨大 stored 条目的包会绕开调用方给的内存预算。
                if (maxBytes > 0 && entry.CompressedBytes > maxBytes)
                {
                    error = "too-large: " + entry.Name + " (" + entry.CompressedBytes.ToString(CultureInfo.InvariantCulture)
                        + " B > " + maxBytes.ToString(CultureInfo.InvariantCulture) + " B cap)";
                    return false;
                }

                int length = (int)entry.CompressedBytes;
                data = new byte[length];
                Buffer.BlockCopy(_zip, dataStart, data, 0, length);
            }
            else if (entry.Method == 8)
            {
                string inflateError;
                if (!Inflater.TryInflate(_zip, dataStart, (int)entry.CompressedBytes, maxBytes, out data, out inflateError))
                {
                    error = "inflate-failed: " + entry.Name + ": " + inflateError;
                    return false;
                }
            }
            else
            {
                error = "unsupported-method: " + entry.Name + ": " + entry.Method.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (entry.UncompressedBytes >= 0 && data.LongLength != entry.UncompressedBytes)
            {
                error = "size-mismatch: " + entry.Name + " (expected "
                    + entry.UncompressedBytes.ToString(CultureInfo.InvariantCulture) + " B, got "
                    + data.LongLength.ToString(CultureInfo.InvariantCulture) + " B)";
                return false;
            }

            uint crc = Crc32.Compute(data, 0, data.Length);
            if (crc != entry.Crc)
            {
                error = "crc-mismatch: " + entry.Name + " (expected 0x"
                    + entry.Crc.ToString("x8", CultureInfo.InvariantCulture) + ", got 0x"
                    + crc.ToString("x8", CultureInfo.InvariantCulture) + ")";
                return false;
            }

            content = data;
            return true;
        }

        internal static ushort ReadUInt16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        internal static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }
    }

    /// <summary>
    /// zip 容器解析（EOCD → 中央目录）。任何非法输入返回 false + 原因串，**绝不抛异常**。
    /// </summary>
    public static class ZipArchiveReader
    {
        private const uint EndOfCentralDirectorySignature = 0x06054B50u;
        private const uint CentralDirectorySignature = 0x02014B50u;
        private const int EndOfCentralDirectorySize = 22;
        private const int MaxCommentLength = 0xFFFF;

        /// <summary>解析 zip 字节流。</summary>
        public static bool TryRead(byte[] zip, out ZipArchive archive, out string error)
        {
            archive = null;
            error = null;

            if (zip == null || zip.Length < EndOfCentralDirectorySize)
            {
                error = "not-zip: too-small";
                return false;
            }

            int eocd = FindEndOfCentralDirectory(zip);
            if (eocd < 0)
            {
                error = "not-zip: end-of-central-directory not found";
                return false;
            }

            int entriesTotal = ZipArchive.ReadUInt16(zip, eocd + 10);
            uint centralSize = ZipArchive.ReadUInt32(zip, eocd + 12);
            uint centralOffset = ZipArchive.ReadUInt32(zip, eocd + 16);

            if (entriesTotal == 0xFFFF || centralSize == 0xFFFFFFFFu || centralOffset == 0xFFFFFFFFu)
            {
                error = "zip64-unsupported";
                return false;
            }

            if (centralOffset + centralSize > (uint)zip.Length)
            {
                error = "bad-central-directory: out-of-range";
                return false;
            }

            List<ZipEntry> entries = new List<ZipEntry>(entriesTotal);
            int pos = (int)centralOffset;
            for (int i = 0; i < entriesTotal; i++)
            {
                if (pos + 46 > zip.Length)
                {
                    error = "bad-central-directory: truncated at entry " + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                if (ZipArchive.ReadUInt32(zip, pos) != CentralDirectorySignature)
                {
                    error = "bad-central-directory: signature mismatch at entry " + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                ushort flags = ZipArchive.ReadUInt16(zip, pos + 8);
                ushort method = ZipArchive.ReadUInt16(zip, pos + 10);
                uint crc = ZipArchive.ReadUInt32(zip, pos + 16);
                uint compressed = ZipArchive.ReadUInt32(zip, pos + 20);
                uint uncompressed = ZipArchive.ReadUInt32(zip, pos + 24);
                int nameLen = ZipArchive.ReadUInt16(zip, pos + 28);
                int extraLen = ZipArchive.ReadUInt16(zip, pos + 30);
                int commentLen = ZipArchive.ReadUInt16(zip, pos + 32);
                uint localOffset = ZipArchive.ReadUInt32(zip, pos + 42);

                if (compressed == 0xFFFFFFFFu || uncompressed == 0xFFFFFFFFu || localOffset == 0xFFFFFFFFu)
                {
                    error = "zip64-unsupported";
                    return false;
                }

                int nameStart = pos + 46;
                if (nameStart + nameLen > zip.Length)
                {
                    error = "bad-central-directory: name out-of-range";
                    return false;
                }

                if ((flags & 1) != 0)
                {
                    error = "encrypted-unsupported";
                    return false;
                }

                string name = Encoding.UTF8.GetString(zip, nameStart, nameLen).Replace('\\', '/');

                entries.Add(new ZipEntry(
                    name,
                    method,
                    flags,
                    crc,
                    compressed,
                    uncompressed,
                    (int)localOffset));

                pos = nameStart + nameLen + extraLen + commentLen;
            }

            archive = new ZipArchive(zip, entries);
            return true;
        }

        private static int FindEndOfCentralDirectory(byte[] zip)
        {
            int maxBack = Math.Min(zip.Length, EndOfCentralDirectorySize + MaxCommentLength);
            int minPos = zip.Length - maxBack;
            for (int pos = zip.Length - EndOfCentralDirectorySize; pos >= minPos && pos >= 0; pos--)
            {
                if (ZipArchive.ReadUInt32(zip, pos) == EndOfCentralDirectorySignature)
                {
                    int commentLen = ZipArchive.ReadUInt16(zip, pos + 20);
                    if (pos + EndOfCentralDirectorySize + commentLen == zip.Length)
                    {
                        return pos;
                    }
                }
            }

            return -1;
        }
    }
}
