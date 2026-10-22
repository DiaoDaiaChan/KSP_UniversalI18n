using System;
using System.Text;

namespace UI18n.Core.Text
{
    /// <summary>
    /// 文本归一化（04 §4.1 五步，顺序不能变）。字典源的 <c>n</c> 字段与本类输出必须逐字符一致，
    /// 否则哈希对不上、全盘 miss。
    /// <para>
    /// 1. Unicode <b>NFKC</b>（全角转半角、连字展开）<br/>
    /// 2. <c>\r\n</c> / <c>\r</c> → <c>\n</c><br/>
    /// 3. 连续空白（含制表符、不换行空格 U+00A0）折叠为单个空格<br/>
    /// 4. 首尾 trim<br/>
    /// 5. <b>不做</b>大小写折叠（"Run" vs "run" 在 UI 里有意义）；<b>不做</b>引号统一（TMP 里字形不同）
    /// </para>
    /// <para>
    /// 说明：第 2/3 步在实现上是同一趟扫描——CR / LF / TAB / 各类空白都属于「空白」，
    /// 连续一段（含换行）一律折叠成一个空格，与 04 §4.1 的文字描述等价。
    /// </para>
    /// <para>
    /// 性能：命中路径先算哈希、只有含空白或全角字符的串才走慢路径（00 §9）；本方法在输入
    /// 已归一的情况下<b>直接返回原引用</b>，零分配。
    /// </para>
    /// </summary>
    public static class TextNormalizer
    {
        /// <summary>
        /// 归一化。null / 空串返回 <see cref="string.Empty"/>。非法 UTF-16（孤立代理）导致
        /// NFKC 抛 ArgumentException 时降级为原文，<b>不抛</b>（红线 6：失败只记不抛）。
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            string nfkc;
            try
            {
                nfkc = s.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                // 孤立代理对等非法输入：放弃 NFKC，用原文继续走空白折叠
                nfkc = s;
            }

            if (nfkc.Length == 0)
            {
                return string.Empty;
            }

            if (string.Equals(nfkc, s, StringComparison.Ordinal) && !NeedsCollapsing(s))
            {
                return s;
            }

            if (!NeedsCollapsing(nfkc))
            {
                return nfkc;
            }

            return CollapseWhitespace(nfkc);
        }

        /// <summary>判断单个字符是否属于 CJK 文字块（汉字 / 假名 / 谚文 / 注音 / CJK 标点 / 全角形式）。</summary>
        public static bool IsCjk(char c)
        {
            if (c < 0x1100)
            {
                return false;
            }

            return (c >= 0x1100 && c <= 0x11FF)   // Hangul Jamo
                || (c >= 0x2E80 && c <= 0x2EFF)   // CJK Radicals Supplement
                || (c >= 0x2F00 && c <= 0x2FDF)   // Kangxi Radicals
                || (c >= 0x3000 && c <= 0x303F)   // CJK Symbols and Punctuation（。、「」等）
                || (c >= 0x3040 && c <= 0x309F)   // Hiragana
                || (c >= 0x30A0 && c <= 0x30FF)   // Katakana
                || (c >= 0x3100 && c <= 0x312F)   // Bopomofo
                || (c >= 0x3130 && c <= 0x318F)   // Hangul Compatibility Jamo
                || (c >= 0x3190 && c <= 0x31FF)   // Kanbun / Bopomofo Extended
                || (c >= 0x3200 && c <= 0x33FF)   // Enclosed CJK Letters / CJK Compatibility
                || (c >= 0x3400 && c <= 0x4DBF)   // CJK Unified Ideographs Extension A
                || (c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified Ideographs
                || (c >= 0xA960 && c <= 0xA97F)   // Hangul Jamo Extended-A
                || (c >= 0xAC00 && c <= 0xD7A3)   // Hangul Syllables
                || (c >= 0xF900 && c <= 0xFAFF)   // CJK Compatibility Ideographs
                || (c >= 0xFE10 && c <= 0xFE1F)   // Vertical Forms
                || (c >= 0xFE30 && c <= 0xFE4F)   // CJK Compatibility Forms
                || (c >= 0xFF00 && c <= 0xFFEF);  // Halfwidth and Fullwidth Forms（，！？Ａ-Ｚ等）
        }

        /// <summary>
        /// 字符串里是否含 CJK 字符。除 BMP 内的字块外，也识别扩展 B 区（U+20000–U+2FA1F）的代理对。
        /// null / 空串返回 false。
        /// </summary>
        public static bool ContainsCjk(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (IsCjk(c))
                {
                    return true;
                }

                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, s[i + 1]);
                    if (cp >= 0x20000 && cp <= 0x2FA1F)
                    {
                        return true;
                    }

                    i++;
                }
            }

            return false;
        }

        /// <summary>空白判定：char.IsWhiteSpace 覆盖不到的 NEL / 行分隔符一并算上，U+00A0 显式列出以便审计。</summary>
        private static bool IsAnySpace(char c)
        {
            return char.IsWhiteSpace(c) || c == '\u00A0' || c == '\u0085' || c == '\u2028' || c == '\u2029';
        }

        /// <summary>快速判据：串里是否存在「会被折叠」的空白（连续空白、非空格空白、首尾空白）。</summary>
        private static bool NeedsCollapsing(string s)
        {
            int len = s.Length;
            for (int i = 0; i < len; i++)
            {
                char c = s[i];
                if (!IsAnySpace(c))
                {
                    continue;
                }

                if (c != ' ')
                {
                    return true;                       // 制表符 / 换行 / U+00A0 ……
                }

                if (i == 0 || i == len - 1)
                {
                    return true;                       // 首尾空白（trim）
                }

                if (IsAnySpace(s[i - 1]))
                {
                    return true;                       // 连续空白
                }
            }

            return false;
        }

        /// <summary>单趟扫描：折叠空白 + trim（等价于 04 §4.1 的第 2–4 步）。</summary>
        private static string CollapseWhitespace(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool pendingSpace = false;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (IsAnySpace(c))
                {
                    if (sb.Length > 0)
                    {
                        pendingSpace = true;           // 首部空白直接丢弃（trim）
                    }
                }
                else
                {
                    if (pendingSpace)
                    {
                        sb.Append(' ');
                        pendingSpace = false;
                    }

                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }
}
