using System;

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
    /// 判定全部手写（无正则、无分配）：只在某条串<b>首次</b>进捕获时跑一次。
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
        /// 路径样：按 <c>/</c> 切成 ≥3 段、每段非空、整串无空白、且至少有一个 ASCII 字母。
        /// <para>
        /// 「至少一个字母」是为了不把 <c>1/2/3</c> 这类纯数字三段式误判成路径（那是进度/比例）。
        /// </para>
        /// </summary>
        public static bool IsPathLike(string text)
        {
            if (string.IsNullOrEmpty(text))
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
            template = ContainsDigit(text);

            if (IsPathLike(text))
            {
                reason = ReasonPath;
                bucket = BucketNever;
                return;
            }

            if (IsProgressLike(text))
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

        /// <summary>桶名是否合法（<see cref="BucketCandidate"/> / <see cref="BucketTemplate"/> / <see cref="BucketNever"/>）。</summary>
        public static bool IsKnownBucket(string bucket)
        {
            return bucket == BucketCandidate || bucket == BucketTemplate || bucket == BucketNever;
        }
    }
}
