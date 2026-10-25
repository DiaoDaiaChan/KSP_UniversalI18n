using System;
using System.Globalization;
using System.Security.Cryptography;

namespace UI18n.Core.Update
{
    /// <summary>
    /// sha256 十六进制串的**唯一**表示与校验入口：全大写、64 个字符、无前缀。
    /// <para>
    /// 约定来源：<c>dist/manifest.json</c> 里 <c>zip.sha256</c> 与 <c>files[].sha256</c> 都是大写 64 位；
    /// PowerShell 的 <c>Get-FileHash -Algorithm SHA256</c> 也是大写。比较一律走
    /// <see cref="EqualsIgnoreCase"/>（大小写不敏感 + 容忍 <c>0x</c> 前缀与前后空白），避免因为大小写误判成「校验失败」。
    /// </para>
    /// </summary>
    public static class Sha256Hex
    {
        /// <summary>sha256 十六进制串的长度。</summary>
        public const int HexLength = 64;

        /// <summary>算一段字节的 sha256，返回<b>大写</b> 64 位十六进制。</summary>
        public static string OfBytes(byte[] data)
        {
            if (data == null)
            {
                return string.Empty;
            }

            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(data));
            }
        }

        /// <summary>算一段字节（可指定区间）的 sha256，返回大写 64 位十六进制。</summary>
        public static string OfBytes(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                return string.Empty;
            }

            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException("offset");
            }

            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(data, offset, count));
            }
        }

        /// <summary>二进制摘要 → 大写十六进制串。</summary>
        public static string ToHex(byte[] hash)
        {
            if (hash == null || hash.Length == 0)
            {
                return string.Empty;
            }

            char[] chars = new char[hash.Length * 2];
            const string digits = "0123456789ABCDEF";
            for (int i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = digits[(hash[i] >> 4) & 0xF];
                chars[i * 2 + 1] = digits[hash[i] & 0xF];
            }

            return new string(chars);
        }

        /// <summary>是否是合法的 64 位十六进制串（忽略前后空白与 <c>0x</c> 前缀、大小写不敏感）。</summary>
        public static bool IsHex64(string text)
        {
            return Normalize(text) != null;
        }

        /// <summary>
        /// 归一化：去空白、去 <c>0x</c> 前缀、转大写。非法（不是 64 位 hex）返回 <c>null</c>，
        /// <b>不抛异常</b>——调用方按「空 = 没有期望值」处理。
        /// </summary>
        public static string Normalize(string text)
        {
            if (text == null)
            {
                return null;
            }

            string s = text.Trim();
            if (s.Length >= 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
            {
                s = s.Substring(2);
            }

            if (s.Length != HexLength)
            {
                return null;
            }

            char[] chars = new char[HexLength];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok)
                {
                    return null;
                }

                chars[i] = (c >= 'a' && c <= 'f') ? (char)(c - 'a' + 'A') : c;
            }

            return new string(chars);
        }

        /// <summary>大小写不敏感比较；任一侧不是 64 位 hex 时返回 false（<b>空不等于空</b>，避免「两边都没有就通过」）。</summary>
        public static bool EqualsIgnoreCase(string a, string b)
        {
            string left = Normalize(a);
            string right = Normalize(b);
            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(left, right, StringComparison.Ordinal);
        }

        /// <summary>把秒级别的字节数格式化成日志里好读的形态（如 <c>1,089,492 B</c>）。</summary>
        public static string DescribeBytes(long bytes)
        {
            return bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";
        }
    }
}
