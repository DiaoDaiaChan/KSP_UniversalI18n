using System.Globalization;

namespace UI18n.Core.Capture
{
    /// <summary>
    /// 一条「缺命中」记录：某个原文在某个 sink 上第一次未命中时建一条，之后同一原文只累加 <see cref="Count"/>。
    /// <para>
    /// 落盘格式见 <see cref="CaptureJson"/>：
    /// <c>{h, n, s, sink, caller, count, firstSeen, template, reason, bucket}</c>
    /// （task-34 起后三个字段是新增的分类标记，老字段顺序与语义不变）。
    /// v1.5（task-59）只在<b>末尾</b>追加可选的 <c>"truncated": true</c>：正文被
    /// <see cref="CaptureOptions.MaxTextLength"/> 截断时才写这个字段，为 false 时整行字节与旧版完全一致。
    /// </para>
    /// </summary>
    public sealed class CaptureRecord
    {
        /// <summary>FNV-1a 64（对归一化文本的 UTF-8 字节），去重键。</summary>
        public ulong Hash { get; private set; }

        /// <summary>归一化后的文本（04 §4.1 五步）。</summary>
        public string Normalized { get; private set; }

        /// <summary>屏幕上的原始文本（未经归一化，保留换行/空白原貌）。</summary>
        public string Source { get; private set; }

        /// <summary>首次未命中的 sink（patch 短名，如 <c>GUILayout.DoLabel</c>）。</summary>
        public string Sink { get; private set; }

        /// <summary>首次未命中时的真实调用者（<c>Type.Method</c>；取不到为 <c>unknown</c>）。</summary>
        public string Caller { get; private set; }

        /// <summary>这条原文被未命中的次数（含首次）。</summary>
        public int Count { get; internal set; }

        /// <summary>首次出现时刻（ISO8601 UTC，形如 <c>2026-10-23T10:00:00Z</c>）。</summary>
        public string FirstSeen { get; private set; }

        /// <summary>
        /// 含数字（不能按字面直接翻，要翻只能做模板）。见 <see cref="CaptureClassify"/>。
        /// </summary>
        public bool Template { get; private set; }

        /// <summary>
        /// 分类原因：<c>path</c> / <c>progress</c> / <c>counter</c> / 空串（静态文本）。
        /// </summary>
        public string Reason { get; private set; }

        /// <summary>
        /// 下游分桶：<c>candidate</c>（可直接翻）/ <c>template</c>（需模板）/ <c>never</c>（资源路径，永不翻）。
        /// </summary>
        public string Bucket { get; private set; }

        /// <summary>
        /// 正文是否被 <see cref="CaptureOptions.MaxTextLength"/> 截断过（v1.5）。
        /// 落盘时只有 true 才在行尾追加 <c>"truncated": true</c>，false 时不写字段。
        /// </summary>
        public bool Truncated { get; private set; }

        /// <summary><see cref="Hash"/> 的 16 位小写 hex（与字典的 <c>h</c> 字段同一格式）。</summary>
        public string HashHex
        {
            get { return Hash.ToString("x16", CultureInfo.InvariantCulture); }
        }

        /// <summary>
        /// 建一条记录（<see cref="Count"/> 恒从 1 开始，之后由捕获器累加）；
        /// <see cref="Template"/> / <see cref="Reason"/> / <see cref="Bucket"/> 由 <paramref name="normalized"/> 现算。
        /// </summary>
        public CaptureRecord(ulong hash, string source, string normalized, string sink, string caller, string firstSeen)
            : this(hash, source, normalized, sink, caller, firstSeen, ClassifyOf(normalized, source))
        {
        }

        /// <summary>建一条记录，标记正文是否被截断（v1.5：不丢正文，超长只截断留痕）。</summary>
        public CaptureRecord(ulong hash, string source, string normalized, string sink, string caller, string firstSeen,
                             bool truncated)
            : this(hash, source, normalized, sink, caller, firstSeen, null, truncated)
        {
        }

        /// <summary>建一条记录，分类由调用方给定（单测用）。</summary>
        public CaptureRecord(ulong hash, string source, string normalized, string sink, string caller, string firstSeen,
                             CaptureClassification classification)
            : this(hash, source, normalized, sink, caller, firstSeen, classification, false)
        {
        }

        /// <summary>建一条记录（分类与截断标记都由调用方给定）。</summary>
        public CaptureRecord(ulong hash, string source, string normalized, string sink, string caller, string firstSeen,
                             CaptureClassification classification, bool truncated)
        {
            Hash = hash;
            Source = source ?? string.Empty;
            Normalized = normalized ?? string.Empty;
            Sink = sink ?? string.Empty;
            Caller = caller ?? string.Empty;
            FirstSeen = firstSeen ?? string.Empty;
            Count = 1;
            Truncated = truncated;

            CaptureClassification c = classification ?? ClassifyOf(Normalized, Source);
            Template = c.Template;
            Reason = c.Reason ?? string.Empty;
            Bucket = c.Bucket ?? CaptureClassify.BucketCandidate;
        }

        /// <summary>调试用单行表示（<b>不是</b>落盘格式，落盘走 <see cref="CaptureJson.ToJsonLine(CaptureRecord)"/>）。</summary>
        public override string ToString()
        {
            return HashHex + " x" + Count + " [" + Sink + "] (" + Bucket + ") " + Source;
        }

        private static CaptureClassification ClassifyOf(string normalized, string source)
        {
            string text = string.IsNullOrEmpty(normalized) ? (source ?? string.Empty) : normalized;
            return CaptureClassification.Of(text);
        }
    }

    /// <summary>
    /// <see cref="CaptureClassify.Classify"/> 的结果对象（不可变；<see cref="CaptureRecord"/> 存的就是它的三个字段）。
    /// </summary>
    public sealed class CaptureClassification
    {
        private CaptureClassification(bool template, string reason, string bucket)
        {
            Template = template;
            Reason = reason ?? string.Empty;
            Bucket = bucket ?? CaptureClassify.BucketCandidate;
        }

        /// <summary>含数字。</summary>
        public bool Template { get; private set; }

        /// <summary><c>path</c> / <c>progress</c> / <c>counter</c> / 空串。</summary>
        public string Reason { get; private set; }

        /// <summary><c>candidate</c> / <c>template</c> / <c>never</c>。</summary>
        public string Bucket { get; private set; }

        /// <summary>按文本现算分类。</summary>
        public static CaptureClassification Of(string text)
        {
            bool template;
            string reason;
            string bucket;
            CaptureClassify.Classify(text, out template, out reason, out bucket);
            return new CaptureClassification(template, reason, bucket);
        }
    }
}
