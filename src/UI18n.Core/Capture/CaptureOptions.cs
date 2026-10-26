namespace UI18n.Core.Capture
{
    /// <summary>
    /// 缺命中捕获的参数（纯 POCO，无 IO、无 Unity 类型）。
    /// <para>
    /// 与 <see cref="UI18n.Core.Config.UI18nConfig"/> 分开的原因：配置是「玩家可见的开关」，
    /// 这里是「捕获器的运行参数」，test-engineer 可以只 new 一个 CaptureOptions 就单测捕获器，
    /// 不需要构造整份配置。
    /// </para>
    /// </summary>
    public sealed class CaptureOptions
    {
        /// <summary>捕获总开关。关闭时 <see cref="CaptureStore.Record"/> 立即返回 false（零分配、无字典查询）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>单次会话最多记录多少条<b>不同</b>原文（去重键 = FNV-1a 64）。达到上限后停止捕获。</summary>
        public int MaxEntries { get; set; } = 5000;

        /// <summary>
        /// 单次会话最多记录多少条<b>模板档</b>（<c>bucket=template</c>：含数字的计数器 / 进度串，如
        /// <c>Loading sound asset 0/234</c>）。<b>这个额度与 <see cref="MaxEntries"/> 分开算</b>，
        /// 所以噪声再也不能把整局配额吃光（v1.3 修的缺陷，实测见 <c>reports/capture-triage.md</c>）。
        /// <para>
        /// 取值口径<b>与 <see cref="MaxEntries"/> 不同，故意如此</b>：
        /// <c>&gt; 0</c> 限额、<c>0</c> 完全不录模板档、<c>&lt; 0</c> 不限制。
        /// 原因：模板档是唯一「用户最可能想彻底关掉」的一档（它是噪声源，翻译价值最低），
        /// 若沿用 <c>0 = 不限制</c> 就没法表达「一条都别录」。
        /// </para>
        /// </summary>
        public int MaxTemplateEntries { get; set; } = 200;

        /// <summary>
        /// 单个调用者最多贡献多少条 distinct 记录（默认 <c>60</c>；<c>0</c> 与 <c>&lt; 0</c> 都<b>不限制</b>，
        /// 口径与 <see cref="MaxEntries"/> 一致）。
        /// <para>
        /// 为什么需要（v1.3 实测）：<c>ProfilerUI.DrawPluginsContent</c> 一个 caller 独占 130 条、
        /// <c>LoadingScreen</c> 独占 3,028 条 —— 一两个渲染循环就能把整局配额（以及候选档）挤掉。
        /// </para>
        /// <para>
        /// 只对<b>已知 caller</b> 生效：<see cref="CaptureCaller.Unknown"/> 与空串一律豁免
        /// （取不到栈的那些本来就该照收，否则丢掉的是唯一线索）。
        /// </para>
        /// </summary>
        public int MaxPerCaller { get; set; } = 60;

        /// <summary>单条文本上限：超过这个长度的串不录（不是截断）。</summary>
        public int MaxTextLength { get; set; } = 400;

        /// <summary>单条文本下限：比它短的串不录（单个字符的串没有翻译价值）。</summary>
        public int MinTextLength { get; set; } = 2;

        /// <summary>一份默认参数。<b>每次访问返回新实例</b>（与 <c>UI18nConfig.Default</c> 同一约定，避免共享可变状态）。</summary>
        public static CaptureOptions Default
        {
            get { return new CaptureOptions(); }
        }

        /// <summary>浅拷贝（捕获器持有自己的副本，调用方之后改配置不会影响已建好的捕获器）。</summary>
        public CaptureOptions Clone()
        {
            CaptureOptions copy = new CaptureOptions();
            copy.Enabled = Enabled;
            copy.MaxEntries = MaxEntries;
            copy.MaxTemplateEntries = MaxTemplateEntries;
            copy.MaxPerCaller = MaxPerCaller;
            copy.MaxTextLength = MaxTextLength;
            copy.MinTextLength = MinTextLength;
            return copy;
        }
    }
}
