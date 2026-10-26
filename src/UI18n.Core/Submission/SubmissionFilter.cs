using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UI18n.Core.Capture;

namespace UI18n.Core.Submission
{
    /// <summary>玩家私有标识的种类（决定替换成哪个占位符，§6.3 第 2 步）。</summary>
    public enum SubmissionPrivateKind
    {
        /// <summary>飞行器名 → <c>&lt;vessel&gt;</c>。</summary>
        Vessel = 0,

        /// <summary>Kerbal 名 → <c>&lt;kerbal&gt;</c>。</summary>
        Kerbal = 1,

        /// <summary>存档名（HighLogic.SaveFolder）→ <c>&lt;save&gt;</c>。</summary>
        Save = 2,

        /// <summary>玩家操作系统账户名（§6.3「玩家目录名」）。</summary>
        UserName = 3
    }

    /// <summary>一个玩家私有值（构造 <see cref="SubmissionFilter"/> 时由 Runtime 灌进来）。</summary>
    public sealed class SubmissionPrivateValue
    {
        /// <summary>种类。</summary>
        public SubmissionPrivateKind Kind { get; set; }

        /// <summary>真实值（<b>只用于匹配</b>，绝不会被写进 bundle）。</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>便捷构造。</summary>
        public static SubmissionPrivateValue Of(SubmissionPrivateKind kind, string value)
        {
            SubmissionPrivateValue item = new SubmissionPrivateValue();
            item.Kind = kind;
            item.Value = value ?? string.Empty;
            return item;
        }
    }

    /// <summary>一次过滤的统计（日志与 <c>redactions</c> 用）。</summary>
    public sealed class SubmissionFilterResult
    {
        /// <summary>留下来的行（<b>拷贝**：调用方仍持有原始行）。</summary>
        public List<SubmissionEntry> Entries { get; } = new List<SubmissionEntry>();

        /// <summary>脱敏审计计数（原样写进 bundle 的 <c>redactions</c>）。</summary>
        public SubmissionRedactions Redactions { get; }

        /// <summary>输入行数。</summary>
        public int Input { get; internal set; }

        /// <summary>第 1 步丢掉的「非正文」行数（path / progress / counter / 标识符 / 超长 / 空 / 纯标点）。</summary>
        public int DroppedNonProse { get; internal set; }

        /// <summary>其中属于 path 判据的行数（日志 <c>path=</c>）。</summary>
        public int DroppedPath { get; internal set; }

        /// <summary>其中属于「已经是中文」的行数（红线：中文绝不外投）。</summary>
        public int DroppedAlreadyCjk { get; internal set; }

        /// <summary>第 2 步因为命中玩家私有标识被丢的行数。</summary>
        public int DroppedPrivate { get; internal set; }

        /// <summary>第 2 步命中私有标识但只做了占位替换、仍然投的行数。</summary>
        public int RedactedPrivate { get; internal set; }

        /// <summary>第 3 步丢掉的「只出现一次的长文本」行数。</summary>
        public int DroppedSingleton { get; internal set; }

        /// <summary>因为值太短而被忽略的私有值个数（&lt;3 字符，拿去做替换会误伤正文；Runtime 记 Warn）。</summary>
        public int IgnoredShortPrivateValues { get; internal set; }

        /// <summary>留下的行数。</summary>
        public int Kept
        {
            get { return Entries.Count; }
        }

        /// <summary>丢掉的总行数。</summary>
        public int DroppedTotal
        {
            get { return Input - Entries.Count; }
        }

        /// <summary>构造。</summary>
        public SubmissionFilterResult()
        {
            Redactions = new SubmissionRedactions();
        }

        /// <summary>日志用一行摘要：<c>path=1 private=2 singleton=3 cjk=4</c>。</summary>
        public string DescribeDrops()
        {
            return "path=" + DroppedPath.ToString(CultureInfo.InvariantCulture)
                + " private=" + DroppedPrivate.ToString(CultureInfo.InvariantCulture)
                + " singleton=" + DroppedSingleton.ToString(CultureInfo.InvariantCulture)
                + " nonProse=" + DroppedNonProse.ToString(CultureInfo.InvariantCulture)
                + " cjk=" + DroppedAlreadyCjk.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// §6.3 三步脱敏（顺序固定，不许调换）。
    /// <para>
    /// 第 1 步丢非正文：判据复用 <see cref="CaptureClassify"/> 与 <see cref="CaptureFilter"/>
    /// （path / progress / counter / identifier / too-long / punct / empty / too-short / already-cjk）。
    /// 第 2 步清玩家私有标识：命中 <c>HighLogic.SaveFolder</c> / 飞船名 / Kerbal 名就替换成
    /// <c>&lt;save&gt;</c> / <c>&lt;vessel&gt;</c> / <c>&lt;kerbal&gt;</c>；命中 URL 或绝对路径整条丢。
    /// 第 3 步丢「只出现一次且 &gt;120 字符」的长文本（同意界面可以放开）。
    /// </para>
    /// <para>
    /// **契约歧义与我的取舍**（已报 Lead）：任务书写「命中则整条替换成 <c>&lt;vessel&gt;</c>」，
    /// 字面读法是把整行正文换成占位符 —— 那样这条投稿对翻译没有价值（剩一个 <c>&lt;vessel&gt;</c>），
    /// 所以这里实现成**把命中的值替换成占位符、保留其余正文**（例如 <c>Duna Falcon 5 landed</c> →
    /// <c>Duna &lt;vessel&gt; 5 landed</c>）；替换后没有可见正文才整条丢。
    /// </para>
    /// <para>本类纯函数、不抛异常、不依赖 UnityEngine（Core 契约）。</para>
    /// </summary>
    public sealed class SubmissionFilter
    {
        /// <summary>第 3 步的长文本阈值（§6.3：&gt;120 字符）。</summary>
        public const int LongSingletonThreshold = 120;

        /// <summary>短于这个长度的私有值不参与替换（<c>"a"</c> 会命中一切正文）。</summary>
        public const int MinPrivateValueLength = 3;

        /// <summary>URL 判据（与 tools/i18n_tools/submission.py 的 _SUSPECT_RE 同源，略放宽）。</summary>
        private static readonly Regex UrlRegex =
            new Regex(@"(?i)https?://|www\.", RegexOptions.CultureInvariant);

        /// <summary>绝对路径 / UNC 判据。</summary>
        private static readonly Regex AbsolutePathRegex =
            new Regex(@"(?i)[A-Za-z]:[\\/]|\\\\|/GameData/|/home/|/Users/", RegexOptions.CultureInvariant);

        private readonly List<SubmissionPrivateValue> _private = new List<SubmissionPrivateValue>();

        /// <summary>构造：只保留长度足够、可安全替换的私有值，按长度降序（长值先替换，避免子串互吃）。</summary>
        public SubmissionFilter(IList<SubmissionPrivateValue> privateValues)
        {
            int ignored = 0;
            if (privateValues != null)
            {
                for (int i = 0; i < privateValues.Count; i++)
                {
                    SubmissionPrivateValue item = privateValues[i];
                    if (item == null || string.IsNullOrEmpty(item.Value))
                    {
                        continue;
                    }

                    string value = item.Value.Trim();
                    if (value.Length < MinPrivateValueLength)
                    {
                        ignored++;
                        continue;
                    }

                    if (!Contains(item))
                    {
                        _private.Add(SubmissionPrivateValue.Of(item.Kind, value));
                    }
                }
            }

            _private.Sort(delegate(SubmissionPrivateValue left, SubmissionPrivateValue right)
            {
                int byLength = right.Value.Length.CompareTo(left.Value.Length);
                return byLength != 0 ? byLength : string.CompareOrdinal(left.Value, right.Value);
            });

            IgnoredValues = ignored;
        }

        /// <summary>因为太短被忽略的私有值个数（Runtime 记一条 Warn，不逐条刷屏）。</summary>
        public int IgnoredValues { get; private set; }

        /// <summary>占位符（对外也暴露，供面板文案与测试复用）。</summary>
        public static string PlaceholderOf(SubmissionPrivateKind kind)
        {
            switch (kind)
            {
                case SubmissionPrivateKind.Vessel:
                    return "<vessel>";
                case SubmissionPrivateKind.Kerbal:
                    return "<kerbal>";
                case SubmissionPrivateKind.Save:
                    return "<save>";
                case SubmissionPrivateKind.UserName:
                    return "<user>";
                default:
                    return "<private>";
            }
        }

        /// <summary>对一批行做三步脱敏（不修改入参，返回拷贝）。</summary>
        public SubmissionFilterResult Filter(IList<SubmissionEntry> entries, bool allowLongSingletons)
        {
            SubmissionFilterResult result = new SubmissionFilterResult();
            result.IgnoredShortPrivateValues = IgnoredValues;
            if (entries == null)
            {
                return result;
            }

            result.Input = entries.Count;
            for (int i = 0; i < entries.Count; i++)
            {
                SubmissionEntry source = entries[i];
                if (source == null)
                {
                    continue;
                }

                SubmissionEntry candidate = source.Clone();

                // ---- 第 1 步：非正文 ----
                string reason = NonProseReason(candidate.S);
                if (reason != null)
                {
                    result.DroppedNonProse++;
                    if (string.Equals(reason, "path", StringComparison.Ordinal))
                    {
                        result.DroppedPath++;
                        result.Redactions.Paths++;
                    }

                    if (string.Equals(reason, "already-cjk", StringComparison.Ordinal))
                    {
                        result.DroppedAlreadyCjk++;
                    }

                    continue;
                }

                // ---- 第 2 步：玩家私有标识 / URL / 绝对路径 ----
                if (UrlRegex.IsMatch(candidate.S))
                {
                    result.DroppedPrivate++;
                    result.Redactions.Urls++;
                    continue;
                }

                if (AbsolutePathRegex.IsMatch(candidate.S))
                {
                    result.DroppedPrivate++;
                    result.Redactions.AbsolutePaths++;
                    continue;
                }

                bool replaced;
                candidate.S = Redact(candidate.S, out replaced);

                // 口径（Lead 裁决，docs/10 §6.3 三条）：命中私有值只替换值、正文其余原样保留；
                // 替换后整串再无可见字母（只剩占位符 / 数字 / 标点）→ 整条丢；
                // redactions 把「替换」与「丢弃」记成两类数：userNames / userNamesDropped。
                // 判「还有没有可见字母」时必须把占位符当透明 —— <vessel> 自己含字母，否则
                // 「整条只是飞行器名」这种串会被判成有正文而投出去。
                string visible = CaptureClassify.StripRichText(candidate.S);
                if (replaced && !HasLetterOutsidePlaceholders(visible))
                {
                    result.DroppedPrivate++;
                    result.Redactions.UserNamesDropped++;
                    continue;
                }

                if (!replaced && !HasVisibleLetter(visible))
                {
                    // 本来就没有可译内容（只剩标签 / 数字 / 标点）：按第 1 步的「非正文」丢，
                    // 不碰 redactions（与私有标识无关）。
                    result.DroppedNonProse++;
                    continue;
                }

                if (replaced)
                {
                    result.RedactedPrivate++;
                    result.Redactions.UserNames++;
                }

                // ---- 第 3 步：只出现一次的长文本 ----
                if (!allowLongSingletons && candidate.Count <= 1 && candidate.S.Length > LongSingletonThreshold)
                {
                    result.DroppedSingleton++;
                    continue;
                }

                result.Entries.Add(candidate);
            }

            return result;
        }

        /// <summary>第 1 步的判据（返回 null = 是正文）。</summary>
        private static string NonProseReason(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "empty";
            }

            if (text.Length < 2)
            {
                return "too-short";
            }

            if (text.Length > SubmissionBundle.MaxTextLength)
            {
                return "too-long";
            }

            if (CaptureFilter.IsAlreadyCjk(text))
            {
                return "already-cjk";
            }

            if (CaptureFilter.IsWhitespaceOnly(text))
            {
                return "whitespace";
            }

            if (CaptureFilter.IsPunctuationOnly(text))
            {
                return "punct";
            }

            if (text.Length <= 3 && CaptureFilter.IsIdentifierLike(text))
            {
                return "identifier";
            }

            bool template;
            string reason;
            string bucket;
            CaptureClassify.Classify(text, out template, out reason, out bucket);
            if (string.Equals(reason, CaptureClassify.ReasonPath, StringComparison.Ordinal))
            {
                return CaptureClassify.ReasonPath;
            }

            if (string.Equals(reason, CaptureClassify.ReasonProgress, StringComparison.Ordinal))
            {
                return CaptureClassify.ReasonProgress;
            }

            if (string.Equals(reason, CaptureClassify.ReasonCounter, StringComparison.Ordinal))
            {
                return CaptureClassify.ReasonCounter;
            }

            return null;
        }

        /// <summary>
        /// 第 2 步的真替换：命中私有值就换成占位符（不区分大小写），返回替换后的串（**永不返回 null**）。
        /// 「替换后还有没有正文」由调用方用 <c>HasLetterOutsidePlaceholders</c> 判定：判据必须看得见
        /// 占位符才知道那算不算正文，写在这里会把 <c>&lt;vessel&gt;</c> 自己的字母当成正文。
        /// </summary>
        private string Redact(string text, out bool replaced)
        {
            replaced = false;
            if (string.IsNullOrEmpty(text) || _private.Count == 0)
            {
                return text;
            }

            string current = text;
            for (int i = 0; i < _private.Count; i++)
            {
                SubmissionPrivateValue item = _private[i];
                if (current.IndexOf(item.Value, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                current = ReplaceIgnoreCase(current, item.Value, PlaceholderOf(item.Kind));
                replaced = true;
            }

            return current;
        }

        /// <summary>
        /// 占位符字面量：判「可见字母」时当透明用。<b>精确字面量</b>，不做正则白名单 ——
        /// 玩家正文里本来就写了 <c>&lt;vessel&gt;</c> 的串不能被判成「无可见字母」（那属于误删数据）。
        /// </summary>
        private static readonly string[] PlaceholderLiterals =
        {
            "<vessel>", "<kerbal>", "<save>", "<user>", "<private>"
        };

        /// <summary>是否还剩正文（占位符当透明：只剩占位符 / 数字 / 标点 = 没有正文）。</summary>
        private static bool HasLetterOutsidePlaceholders(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int i = 0;
            while (i < text.Length)
            {
                bool placeholder = false;
                for (int p = 0; p < PlaceholderLiterals.Length; p++)
                {
                    string literal = PlaceholderLiterals[p];
                    if (i + literal.Length <= text.Length
                        && string.CompareOrdinal(text, i, literal, 0, literal.Length) == 0)
                    {
                        i += literal.Length;
                        placeholder = true;
                        break;
                    }
                }

                if (placeholder)
                {
                    continue;
                }

                if (char.IsLetter(text[i]))
                {
                    return true;
                }

                i++;
            }

            return false;
        }

        /// <summary>是否含任何字母（占位符不算透明；用于「本来就没有可译内容」的判定）。</summary>
        private static bool HasVisibleLetter(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsLetter(text[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ReplaceIgnoreCase(string text, string value, string replacement)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            {
                return text;
            }

            int index = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return text;
            }

            StringBuilder sb = new StringBuilder(text.Length + 8);
            int cursor = 0;
            while (index >= 0)
            {
                sb.Append(text, cursor, index - cursor);
                sb.Append(replacement);
                cursor = index + value.Length;
                index = text.IndexOf(value, cursor, StringComparison.OrdinalIgnoreCase);
            }

            sb.Append(text, cursor, text.Length - cursor);
            return sb.ToString();
        }

        private bool Contains(SubmissionPrivateValue item)
        {
            for (int i = 0; i < _private.Count; i++)
            {
                if (_private[i].Kind == item.Kind
                    && string.Equals(_private[i].Value, item.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
