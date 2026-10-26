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

        /// <summary>
        /// <b>v1.5 退役</b>（task-59 / docs/08 D-T2）：捕获端不再因为总条数丢弃任何正文，这个上限<b>不再生效</b>。
        /// 字段保留只为两个原因：① 老 <c>UI18n.cfg</c> 里还有 <c>CaptureMaxEntries</c>，配置解析不能炸；
        /// ② <c>src/UI18n.Runtime/Capture/MissCapture.cs</c> 仍在读它打日志。
        /// 想限制磁盘占用请用 <see cref="MaxSessionBytes"/>。
        /// </summary>
        public int MaxEntries { get; set; } = 5000;

        /// <summary>
        /// <b>v1.5 退役</b>：模板档（<c>Loading sound asset 0/234</c> 这类含数字的计数器）也照收不丢，
        /// 这个额度<b>不再生效</b>。字段保留同样只为老配置解析（<c>CaptureMaxTemplateEntries</c>）与运行时日志。
        /// <para>
        /// 退役理由（docs/08 D-T2）：模板档<b>是</b>候选 —— 「Loading sound asset #/#」照样能翻成
        /// 「正在加载音效 #/#」；旧策略把它当噪声丢掉，等于让最常见的用户可见英文永远翻不出来。
        /// </para>
        /// </summary>
        public int MaxTemplateEntries { get; set; } = 200;

        /// <summary>
        /// <b>v1.5 退役</b>：单个调用者配额<b>不再生效</b>（<c>ProfilerUI.DrawPluginsContent</c> 独占 130 条、
        /// <c>LoadingScreen</c> 3,028 条的观测仍然有效，但那不再是丢弃理由 —— caller 现在只影响
        /// <c>capture callers:</c> 的优先级排序）。字段保留只为老配置解析（<c>CaptureMaxPerCaller</c>）。
        /// </summary>
        public int MaxPerCaller { get; set; } = 60;

        /// <summary>
        /// 单条文本上限。v1.5 起<b>超长只截断、不丢弃</b>（截断的记录带 <see cref="CaptureRecord.Truncated"/>，
        /// 落盘追加 <c>"truncated": true</c>）。
        /// <para>
        /// 默认 <c>8000</c>：零件 description 的 p99=577 / max=1577，2000 本来够用；
        /// 提到 8000 是为了让「整段长描述 / 长 changelog / 帮助文本」这类真正有翻译价值的长串也能整条留档，
        /// 同时给单条记录一个硬上界（防止某个 mod 把整篇 cfg dump 到一个 label 里）。
        /// </para>
        /// </summary>
        public int MaxTextLength { get; set; } = 8000;

        /// <summary>
        /// 单条文本下限（v1.5 起<b>只影响优先级/统计口径，不再丢弃</b>；<see cref="CaptureFilter"/> 仍在用）。
        /// </summary>
        public int MinTextLength { get; set; } = 2;

        /// <summary>
        /// 单次会话写出的字节预算（默认 64 MiB；<c>0</c> 与 <c>&lt; 0</c> 表示不限制）。
        /// <para>
        /// 这是 v1.5 唯一还允许「不留正文」的闸门（docs/08 D-T2）：用尽后
        /// <see cref="CaptureStore"/> 停止写正文、只累加计数，并由
        /// <see cref="CaptureStore.ConsumeBudgetNotice"/> 保证日志只通报一次。
        /// 与旧上限的区别：<b>它是字节而不是条数</b> —— 一条 8,000 字符的长描述和一条 20 字符的按钮，
        /// 对磁盘的压力差 400 倍，用条数计既挡不住长文本也不会误伤短文本。
        /// </para>
        /// </summary>
        public long MaxSessionBytes { get; set; } = 64L * 1024 * 1024;

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
            copy.MaxSessionBytes = MaxSessionBytes;
            return copy;
        }
    }
}
