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
            copy.MaxTextLength = MaxTextLength;
            copy.MinTextLength = MinTextLength;
            return copy;
        }
    }
}
