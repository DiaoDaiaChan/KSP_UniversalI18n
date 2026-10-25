namespace UI18n.Core.Update
{
    /// <summary>
    /// CRC-32（IEEE 802.3，反射多项式 0xEDB88320），zip 条目校验用。
    /// <para>纯 BCL、无分配（查表）；与 .NET 的 System.IO.Hashing.Crc32 结果一致。</para>
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }

        /// <summary>整块算 CRC32。</summary>
        public static uint Compute(byte[] data)
        {
            return data == null ? 0u : Compute(data, 0, data.Length);
        }

        /// <summary>算一段的 CRC32。</summary>
        public static uint Compute(byte[] data, int offset, int length)
        {
            return Update(0u, data, offset, length);
        }

        /// <summary>增量算 CRC32（<paramref name="crc"/> 传上一次的结果，首次传 0）。</summary>
        public static uint Update(uint crc, byte[] data, int offset, int length)
        {
            if (data == null || length <= 0)
            {
                return crc;
            }

            uint value = crc ^ 0xFFFFFFFFu;
            int end = offset + length;
            for (int i = offset; i < end; i++)
            {
                value = (value >> 8) ^ Table[(value ^ data[i]) & 0xFF];
            }

            return value ^ 0xFFFFFFFFu;
        }
    }
}
