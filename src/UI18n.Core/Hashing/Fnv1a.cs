using System;

namespace UI18n.Core.Hashing
{
    /// <summary>
    /// FNV-1a 64 位哈希（决策 D11：不用 string.GetHashCode，跨进程 / 跨版本不稳定，不能做持久化键）。
    /// <para>
    /// <b>算法约定</b>：输入是字符串的 UTF-8 编码字节，与 python 侧 <c>tools/i18n_tools/fnv.py</c> 逐字节一致
    /// （04 §11.3 缓存键 / 字典包 h 字段）。改这里就必须同步改 python 侧，否则字典全 miss。
    /// </para>
    /// <para>
    /// 自测向量（与 python 侧共享）："" =&gt; 0xcbf29ce484222325，"a" =&gt; 0xaf63dc4c8601ec8c，
    /// "foobar" =&gt; 0x85944171f73967e8。
    /// </para>
    /// <para>
    /// 实现约束：命中路径要求 <b>零分配</b>（05 §4.3⑥，KSP 是 Boehm GC），所以 <see cref="Hash(string)"/>
    /// 手工按 UTF-8 规则逐码点喂字节，<b>不</b> 调 Encoding.UTF8.GetBytes（那会分配一个 byte[]）。
    /// </para>
    /// </summary>
    public static class Fnv1a
    {
        /// <summary>FNV-1a 64 位偏移基：0xcbf29ce484222325。</summary>
        public const ulong OffsetBasis = 14695981039346656037UL;

        /// <summary>FNV-1a 64 位质数：0x100000001b3。</summary>
        public const ulong Prime = 1099511628211UL;

        /// <summary>
        /// 对字符串的 <b>UTF-8 编码字节</b> 求 FNV-1a 64。null 与空串等价（返回 <see cref="OffsetBasis"/>）。
        /// 孤立代理对（非法 UTF-16）按 Encoding.UTF8 的默认行为替换为 U+FFFD，因此本方法与
        /// <c>Hash(Encoding.UTF8.GetBytes(s))</c> 恒等价，且永不抛异常。
        /// </summary>
        public static ulong Hash(string s)
        {
            ulong h = OffsetBasis;
            if (string.IsNullOrEmpty(s))
            {
                return h;
            }

            unchecked
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];

                    if (c < 0x80)
                    {
                        h = (h ^ (byte)c) * Prime;
                    }
                    else if (c < 0x800)
                    {
                        h = (h ^ (byte)(0xC0 | (c >> 6))) * Prime;
                        h = (h ^ (byte)(0x80 | (c & 0x3F))) * Prime;
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        int cp = char.ConvertToUtf32(c, s[i + 1]);
                        i++;
                        h = (h ^ (byte)(0xF0 | (cp >> 18))) * Prime;
                        h = (h ^ (byte)(0x80 | ((cp >> 12) & 0x3F))) * Prime;
                        h = (h ^ (byte)(0x80 | ((cp >> 6) & 0x3F))) * Prime;
                        h = (h ^ (byte)(0x80 | (cp & 0x3F))) * Prime;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        // 孤立代理：U+FFFD => EF BF BD（与 Encoding.UTF8 的替换行为一致）
                        h = (h ^ 0xEF) * Prime;
                        h = (h ^ 0xBF) * Prime;
                        h = (h ^ 0xBD) * Prime;
                    }
                    else
                    {
                        h = (h ^ (byte)(0xE0 | (c >> 12))) * Prime;
                        h = (h ^ (byte)(0x80 | ((c >> 6) & 0x3F))) * Prime;
                        h = (h ^ (byte)(0x80 | (c & 0x3F))) * Prime;
                    }
                }
            }

            return h;
        }

        /// <summary>
        /// 对已有 UTF-8 字节求 FNV-1a 64。null 等价于空数组（返回 <see cref="OffsetBasis"/>）。
        /// </summary>
        public static ulong Hash(byte[] utf8)
        {
            ulong h = OffsetBasis;
            if (utf8 == null || utf8.Length == 0)
            {
                return h;
            }

            unchecked
            {
                for (int i = 0; i < utf8.Length; i++)
                {
                    h = (h ^ utf8[i]) * Prime;
                }
            }

            return h;
        }

        /// <summary>对字节区间求 FNV-1a 64（供无分配场景使用）。</summary>
        public static ulong Hash(byte[] utf8, int offset, int count)
        {
            ulong h = OffsetBasis;
            if (utf8 == null || count <= 0)
            {
                return h;
            }

            if (offset < 0 || offset > utf8.Length || offset + count > utf8.Length)
            {
                throw new ArgumentOutOfRangeException("count", "offset/count 越界");
            }

            unchecked
            {
                int end = offset + count;
                for (int i = offset; i < end; i++)
                {
                    h = (h ^ utf8[i]) * Prime;
                }
            }

            return h;
        }
    }
}
