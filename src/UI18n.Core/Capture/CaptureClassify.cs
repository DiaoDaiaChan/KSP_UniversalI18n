using System;
using System.Text.RegularExpressions;

namespace UI18n.Core.Capture
{
    /// <summary>
    /// task-34 缺陷 3：给捕获到的文本分桶，<b>不丢数据</b>，让下游（data-engineer 的 capture_ingest）
    /// 自己决定哪些进翻译候选、哪些只能做模板、哪些永远不翻。
    /// <para>
    /// round 8 实测：2,585 条加载屏文本里混着资源路径（<c>ReStockPlus/Parts/Control/RCS/…</c>）、
    /// 每次运行都不同的计数（<c>ModuleManager: 8448 patches applied</c>）与进度（<c>Loading sound asset 0/234</c>）。
    /// 它们是真实用户可见文本，只是性质不同：
    /// </para>
    /// <list type="bullet">
    /// <item><c>path</c> —— 资源路径/URL，永远不该翻（<see cref="BucketNever"/>）。</item>
    /// <item><c>progress</c> —— 形如 <c>0/234</c> 的进度，要翻只能做模板（<see cref="BucketTemplate"/>）。</item>
    /// <item><c>counter</c> —— 含数字的其它文本（<c>… 8448 patches applied</c>），同上。</item>
    /// <item>空 —— 不含数字的静态文本，正常候选（<see cref="BucketCandidate"/>）。</item>
    /// </list>
    /// <para>
    /// <b>task-42 修复</b>：判定前先剥掉 TMP 富文本标签（<see cref="TmpTagPattern"/>），否则
    /// `</color>` 里的斜杠会让 <c>&lt;b&gt;&lt;color=#79C0FF&gt;Cinematic&lt;/color&gt;&lt;/b&gt;</c>
    /// 被当成路径（真实的 35 个 h 因此被判 never）。剥完为空/纯空白 → <see cref="ReasonEmpty"/>。
    /// </para>
    /// <para>
    /// 除了一次标签剥离用的静态正则，其余判定仍然手写（无分配）：只在某条串<b>首次</b>进捕获时跑一次。
    /// </para>
    /// </summary>
    public static class CaptureClassify
    {
        /// <summary>不含数字、也不是路径 —— 正常翻译候选。</summary>
        public const string ReasonNone = "";

        /// <summary>资源路径 / URL（≥3 段斜杠分隔、无空白、含字母）。</summary>
        public const string ReasonPath = "path";

        /// <summary>进度形态（数字 / 数字，例如 <c>0/234</c>）。</summary>
        public const string ReasonProgress = "progress";

        /// <summary>含数字的其它文本（每次运行都可能不同）。</summary>
        public const string ReasonCounter = "counter";

        /// <summary>
        /// 剥掉富文本标签后没有可显示文本（例如只有 `&lt;b&gt;&lt;/b&gt;` 或孤立的 `&lt;/color&gt;`）。
        /// <para>
        /// task-42 之前这种串会被判成 <see cref="ReasonPath"/>（因为 `&lt;/color&gt;` 带斜杠），现在是独立的空原因。
        /// 取值与 <see cref="CaptureFilter.ReasonEmpty"/> 一致（都是 `empty`），但两者的判定点不同：
        /// 那个是原文就空/全空白，这个是「剥完标签才空」。
        /// </para>
        /// </summary>
        public const string ReasonEmpty = "empty";

        /// <summary>
        /// TMP 富文本标签白名单（与 python 侧 <c>tools/i18n_tools/resolve_keyrefs.py::_TMP_TAG_RE</c> <b>同口径</b>：
        /// 同一个正则、同样大小写敏感 —— <c>&lt;COLOR=red&gt;</c> 不算标签）。
        /// <para>
        /// <b>改一处必须改两处</b>：这里与 python 侧任意一边改了白名单，捕获分类与离线重算就会给出不同结论，
        /// 下游报告里的 reason 分布会自相矛盾。
        /// </para>
        /// </summary>
        public const string TmpTagPattern = "</?(?:b|i|u|s|color|size|material|quad|sprite|link)(?:=[^>]*)?>";

        /// <summary>标签剥离用的正则（静态编译一次，捕获路径上每条串只跑一次）。</summary>
        private static readonly Regex TmpTagRegex = new Regex(TmpTagPattern, RegexOptions.CultureInvariant);

        /// <summary>
        /// 剥掉 TMP 富文本标签，只留用户真正看到的文字；<paramref name="text"/> 为 null 时返回空串。
        /// <para>只剥 <see cref="TmpTagPattern"/> 白名单里的标签（与 python 侧同口径），未知的尖括号内容原样保留。</para>
        /// </summary>
        public static string StripRichText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return TmpTagRegex.Replace(text, string.Empty);
        }

        /// <summary>剥掉标签后有没有非空白字符（只有空白 / 只有标签 → false）。</summary>
        public static bool HasVisibleText(string text)
        {
            string visible = StripRichText(text);
            for (int i = 0; i < visible.Length; i++)
            {
                if (!char.IsWhiteSpace(visible[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>可直接当翻译候选的静态文本。</summary>
        public const string BucketCandidate = "candidate";

        /// <summary>动态文本：要翻必须先做模板（<c>{0}</c> / <c>%s</c> 之类）。</summary>
        public const string BucketTemplate = "template";

        /// <summary>永远不翻（资源路径 / URL）。</summary>
        public const string BucketNever = "never";

        /// <summary>含至少一个数字（<see cref="char.IsDigit(char)"/>，含全角数字）。</summary>
        public static bool ContainsDigit(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsDigit(text[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 路径样：<b>先剥掉富文本标签</b>，再按 <c>/</c> 切成 ≥3 段、每段非空、整串无空白、且至少有一个 ASCII 字母。
        /// <para>
        /// 「至少一个字母」是为了不把 <c>1/2/3</c> 这类纯数字三段式误判成路径（那是进度/比例）。
        /// </para>
        /// <para>
        /// task-42：标签必须先剥 —— <c>&lt;/color&gt;</c> 里的斜杠曾被当成分隔符，
        /// 于是 <c>&lt;b&gt;&lt;color=#79C0FF&gt;Cinematic&lt;/color&gt;&lt;/b&gt;</c> 被判成路径（真实 35 个 h 因此 never）。
        /// </para>
        /// </summary>
        public static bool IsPathLike(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            text = StripRichText(text);
            if (text.Length == 0)
            {
                return false;
            }

            int segments = 1;
            int segmentLength = 0;
            bool anyLetter = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '/')
                {
                    if (segmentLength == 0)
                    {
                        return false;
                    }

                    segments++;
                    segmentLength = 0;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    return false;
                }

                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                {
                    anyLetter = true;
                }

                segmentLength++;
            }

            return segments >= 3 && segmentLength > 0 && anyLetter;
        }

        /// <summary>
        /// 进度样：存在某个 <c>/</c>，它左边（跳过空白）是数字、右边（跳过空白）也是数字，
        /// 例如 <c>0/234</c>、<c>12 / 100</c>。<c>ReStockPlus/Parts/…</c> 左边是字母，不算。
        /// </summary>
        public static bool IsProgressLike(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '/')
                {
                    continue;
                }

                int left = i - 1;
                while (left >= 0 && char.IsWhiteSpace(text[left]))
                {
                    left--;
                }

                int right = i + 1;
                while (right < text.Length && char.IsWhiteSpace(text[right]))
                {
                    right++;
                }

                if (left >= 0 && right < text.Length && char.IsDigit(text[left]) && char.IsDigit(text[right]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 分类：<paramref name="template"/> 表示「含数字、不能按字面直接翻」；
        /// <paramref name="reason"/> 是 <see cref="ReasonNone"/> / <see cref="ReasonPath"/> /
        /// <see cref="ReasonProgress"/> / <see cref="ReasonCounter"/>；
        /// <paramref name="bucket"/> 是 <see cref="BucketCandidate"/> / <see cref="BucketTemplate"/> / <see cref="BucketNever"/>。
        /// </summary>
        public static void Classify(string text, out bool template, out string reason, out string bucket)
        {
            // task-42：分类只看「用户真正看到的文字」。剥完标签什么都没剩（例如 <b></b> 或孤立 </color>）
            // 就没有可翻的东西：reason=empty、bucket=never（不是 path，也不是 candidate）。
            string visible = StripRichText(text);
            if (!HasNonSpace(visible))
            {
                template = false;
                reason = ReasonEmpty;
                bucket = BucketNever;
                return;
            }

            template = ContainsDigit(visible);

            if (IsPathLike(visible))
            {
                reason = ReasonPath;
                bucket = BucketNever;
                return;
            }

            if (IsProgressLike(visible))
            {
                reason = ReasonProgress;
                bucket = BucketTemplate;
                return;
            }

            if (template)
            {
                reason = ReasonCounter;
                bucket = BucketTemplate;
                return;
            }

            reason = ReasonNone;
            bucket = BucketCandidate;
        }

        /// <summary>有非空白可见字符吗（<see cref="HasVisibleText"/> 的内部版，输入已剥过标签）。</summary>
        private static bool HasNonSpace(string visible)
        {
            if (visible == null)
            {
                return false;
            }

            for (int i = 0; i < visible.Length; i++)
            {
                if (!char.IsWhiteSpace(visible[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>桶名是否合法（<see cref="BucketCandidate"/> / <see cref="BucketTemplate"/> / <see cref="BucketNever"/>）。</summary>
        public static bool IsKnownBucket(string bucket)
        {
            return bucket == BucketCandidate || bucket == BucketTemplate || bucket == BucketNever;
        }
    }
}
