namespace UI18n.Core.Config
{
    /// <summary>
    /// 用户配置（纯 POCO，无 IO、无 Unity 类型；序列化由 Runtime 层负责，net472 侧用 ConfigNode）。
    /// <para>
    /// 默认值即「安全默认」：Enabled 开、ForceOverride 关（D32）、更新检查开、自动下载关、上报关，
    /// MaxEntries 60000 是内存上限（05 §3 估算 6 万条 × 约 120 B ≈ 7 MB，KSP 可接受）。
    /// </para>
    /// </summary>
    public sealed class UI18nConfig
    {
        /// <summary>总开关（false 时 Runtime 不装任何 patch）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>非中文环境也强制翻译（默认关，D32；仅供测试与特殊需求）。</summary>
        public bool ForceOverride { get; set; } = false;

        /// <summary>启动时检查字典更新。</summary>
        public bool CheckForUpdates { get; set; } = true;

        /// <summary>发现更新后自动下载（默认关，需用户显式同意）。</summary>
        public bool AutoDownload { get; set; } = false;

        /// <summary>匿名使用统计上报（默认关，opt-in）。</summary>
        public bool ReportOptIn { get; set; } = false;

        /// <summary>单语言包最大条目数，超出则拒绝载入（防止异常包吃爆内存）。</summary>
        public int MaxEntries { get; set; } = 60000;

        /// <summary>
        /// 「首次命中样本」上限（配置键 <c>HitSampleLimit</c>，默认 64，<b>0 = 关闭采样</b>）。
        /// 只控制诊断日志 <c>hit sample:</c> 的条数：Runtime 在某个原文<b>第一次</b>被翻译成功时
        /// 记一条样本（原文 + 译文 + 命中层级 + 首次命中的 patch 名），超过上限只加计数不再记样本。
        /// 与 <c>distinct hits</c>（全量去重计数）无关——那个不受本上限影响，永远精确。
        /// </summary>
        public int HitSampleLimit { get; set; } = 64;

        /// <summary>
        /// 旧版 uGUI 字体兜底模式（配置键 <c>FontMode</c>，默认 <c>OsFont</c>）。
        /// uGUI 能从系统字体动态建字面，中文可以真修；TMP 不行，见 <c>TmpMode</c>。
        /// </summary>
        public FontMode FontMode { get; set; } = global::UI18n.Core.Config.FontMode.OsFont;

        /// <summary>
        /// 候选系统字体名（配置键 <c>OsFontName</c>，默认 <c>Microsoft YaHei</c>）。只是<b>首选</b>：
        /// Runtime 会先与 <c>Font.GetOSInstalledFontNames()</c> 精确比对，不在列表里就按内置候选顺序继续找，
        /// 建出来的字面还要 <c>HasCharacter('中')</c> 实测通过才会用（不用注册表家族名，D 系列决策见 README §9）。
        /// </summary>
        public string OsFontName { get; set; } = "Microsoft YaHei";

        /// <summary>
        /// <c>FontMode=Bundled</c> 时载入的 AssetBundle 绝对或相对路径（配置键 <c>BundledFontPath</c>）。
        /// 路径不存在 / 载入失败只记原因并回落 <c>OsFont</c>，绝不抛。
        /// </summary>
        public string BundledFontPath { get; set; } = string.Empty;

        /// <summary>
        /// TMP 处理档位（配置键 <c>TmpMode</c>，默认 <c>Auto</c> = 可判定降级：
        /// 能用 <c>HasCharacter</c> 证明能显示才翻译，否则保留原文）。
        /// </summary>
        public TmpMode TmpMode { get; set; } = global::UI18n.Core.Config.TmpMode.Auto;

        /// <summary>
        /// 一份默认配置。注意<b>每次访问都返回新实例</b>：配置是可变的，共享同一个静态实例
        /// 会让「读取默认值」意外变成「持有全局可变状态」。
        /// </summary>
        public static UI18nConfig Default
        {
            get { return new UI18nConfig(); }
        }
    }
}
