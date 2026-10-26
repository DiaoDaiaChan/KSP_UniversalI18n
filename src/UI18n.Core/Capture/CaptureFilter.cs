using System;

namespace UI18n.Core.Capture
{
    /// <summary>
    /// 缺命中捕获的「噪声判据」：给出一个串属于哪一类噪声。
    /// <para>
    /// <b>v1.5 起它不再决定「录不录」</b>（docs/08 D-T2：捕获端不丢正文）。<see cref="CaptureStore"/>
    /// 已经不再调用它 —— 判据仍然可用（单测、离线 ingest 复用同一口径），但只作为优先级/统计标签，
    /// 任何一条串被这里判成噪声都<b>照样</b>会建记录、照样会落盘。
    /// </para>
    /// <para>
    /// 全部是纯 BCL 判断（无正则、无分配），因为虽然只在某条串<b>首次</b>未命中时调用一次，
    /// 单测里会拿成千上万条喂它。
    /// </para>
    /// <para>
    /// 规则（顺序即优先级，命中即返回原因）：
    /// 空 → 太短（&lt; MinTextLength）→ 太长（&gt; MaxTextLength）→ 纯空白 →
    /// <b>已经含 CJK</b>（别人已经处理好的中文）→ 数字串（纯数字或数字+数字分隔符）→ 纯标点/符号 →
    /// 「标识符样」短串（只含 <c>[A-Za-z0-9_.-]</c> 且长度 ≤ 3）。
    /// 后三条都是为了压掉引擎/配置里的键名噪声（<c>KSP</c>、<c>1.5</c>、<c>---</c> 这类），
    /// 它们永远翻不出来、录下来只会把 cap 挤满。
    /// </para>
    /// </summary>
    public static class CaptureFilter
    {
        /// <summary>空串（或 null）。</summary>
        public const string ReasonEmpty = "empty";

        /// <summary>比 <see cref="CaptureOptions.MinTextLength"/> 短。</summary>
        public const string ReasonTooShort = "too-short";

        /// <summary>比 <see cref="CaptureOptions.MaxTextLength"/> 长。</summary>
        public const string ReasonTooLong = "too-long";

        /// <summary>纯空白。</summary>
        public const string ReasonWhitespace = "whitespace";

        /// <summary>
        /// 数字串：纯数字（含全角数字，按 Unicode 数字类别判断），或只由数字与数字分隔符
        /// （<c>. , : / - + % ° × ~ ±</c> 与空白）组成且至少有一个数字，例如 <c>3.14159</c>、<c>1,234</c>、<c>12%</c>。
        /// 这类串在任何语言下都一样，录下来只会把 cap 挤满。
        /// </summary>
        public const string ReasonDigits = "digits";

        /// <summary>只有标点/符号（可夹空白），例如 <c>--- ---</c>。</summary>
        public const string ReasonPunctuation = "punctuation";

        /// <summary>标识符样短串：只含 <c>[A-Za-z0-9_.-]</c> 且长度 ≤ 3，例如 <c>KSP</c>、<c>v1</c>。</summary>
        public const string ReasonIdentifier = "identifier";

        /// <summary>
        /// 已经是中文（含 CJK 字符）。
        /// <para>
        /// task-34 缺陷 2：round 8 捕获里混进 <c>设置</c>、<c>性能</c>、<c>正在加载…</c>、<c>删除全部信息</c>
        /// 等至少 26 条 —— 它们是原生字典 / 其它 mod 已经处理好的文本，我们的字典只有英文键，
        /// 永远命不中，属于纯噪声。口径与 <c>tools/i18n_tools/harvest.py</c> 的 <c>already-cjk</c> 一致：
        /// <b>含 CJK 字符即跳</b>（不要求整串都是 CJK）。
        /// </para>
        /// </summary>
        public const string ReasonAlreadyCjk = "already-cjk";

        /// <summary>该录吗（默认参数）。</summary>
        public static bool ShouldCapture(string normalized, string source)
        {
            return RejectReason(normalized, source, null) == null;
        }

        /// <summary>该录吗（自定义参数）。</summary>
        public static bool ShouldCapture(string normalized, string source, CaptureOptions options)
        {
            return RejectReason(normalized, source, options) == null;
        }

        /// <summary>
        /// 返回不录的原因常量；<c>null</c> 表示应该录。参数为 null 时用默认上下限（2 / 2000）。
        /// </summary>
        public static string RejectReason(string normalized, string source, CaptureOptions options)
        {
            int min = options != null ? options.MinTextLength : 2;
            int max = options != null ? options.MaxTextLength : 2000;

            string text = normalized;
            if (string.IsNullOrEmpty(text))
            {
                text = source;
            }

            if (string.IsNullOrEmpty(text))
            {
                return ReasonEmpty;
            }

            if (text.Length < min)
            {
                return ReasonTooShort;
            }

            if (text.Length > max || (source != null && source.Length > max))
            {
                return ReasonTooLong;
            }

            if (IsWhitespaceOnly(text))
            {
                return ReasonWhitespace;
            }

            if (IsAlreadyCjk(text) || (source != null && !ReferenceEquals(source, text) && IsAlreadyCjk(source)))
            {
                return ReasonAlreadyCjk;
            }

            if (IsNumericOnly(text))
            {
                return ReasonDigits;
            }

            if (IsPunctuationOnly(text))
            {
                return ReasonPunctuation;
            }

            if (text.Length <= 3 && IsIdentifierLike(text))
            {
                return ReasonIdentifier;
            }

            return null;
        }

        /// <summary>
        /// 已经含 CJK 字符（委托 <see cref="UI18n.Core.Text.TextNormalizer.ContainsCjk(string)"/>，
        /// 与归一化/键生成用的是同一套 CJK 区间判定，避免两处口径漂移）。
        /// </summary>
        public static bool IsAlreadyCjk(string text)
        {
            return UI18n.Core.Text.TextNormalizer.ContainsCjk(text);
        }

        /// <summary>全是空白字符。</summary>
        public static bool IsWhitespaceOnly(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>全是数字（<see cref="char.IsDigit(char)"/>，含全角数字）。</summary>
        public static bool IsDigitsOnly(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 数字串：至少一个数字，且每个字符都是数字或数字分隔符（<c>. , : / - + % ° × ~ ±</c> / 空白）。
        /// 用来一并压掉 <c>3.14159</c>、<c>1,234.5</c>、<c>-12</c>、<c>45%</c> 这类「翻译不了也没必要翻」的读数。
        /// </summary>
        public static bool IsNumericOnly(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            bool anyDigit = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsDigit(c))
                {
                    anyDigit = true;
                    continue;
                }

                if (IsNumericSeparator(c))
                {
                    continue;
                }

                return false;
            }

            return anyDigit;
        }

        /// <summary>只有标点/符号（允许夹空白），且至少有一个标点或符号。</summary>
        public static bool IsPunctuationOnly(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            bool anySymbol = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsPunctuation(c) || char.IsSymbol(c))
                {
                    anySymbol = true;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                return false;
            }

            return anySymbol;
        }

        /// <summary>数字串允许的分隔符（<c>. , : / - + % ° × ~ ±</c> 与空白）。</summary>
        private static bool IsNumericSeparator(char c)
        {
            switch (c)
            {
                case '.':
                case ',':
                case ':':
                case '/':
                case '-':
                case '+':
                case '%':
                case '\u00B0':
                case '\u00D7':
                case '~':
                case '\u00B1':
                    return true;
                default:
                    return char.IsWhiteSpace(c);
            }
        }

        /// <summary>只含 <c>[A-Za-z0-9_.-]</c>（等价于正则 <c>^[A-Za-z0-9_\.\-]+$</c>，手写以避免正则开销）。</summary>
        public static bool IsIdentifierLike(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                          || c == '_' || c == '.' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
