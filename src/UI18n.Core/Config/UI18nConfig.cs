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
        /// <summary>
        /// 日志级别（配置键 <c>LogLevel</c>，默认 <see cref="global::UI18n.Core.Config.LogLevel.Info"/>）。
        /// <para>
        /// 只影响日志，不影响翻译行为：设成 <c>Error</c> 时连启动摘要都不打，但 UI 照样翻译。
        /// 放在类的最前面是刻意的——它决定后面每一条日志是否出得来。
        /// </para>
        /// </summary>
        public LogLevel LogLevel { get; set; } = global::UI18n.Core.Config.LogLevel.Info;

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
        /// 缺命中捕获（配置键 <c>CaptureMisses</c>，默认 <c>true</c>）：把字典里没有的 UI 文本按 FNV-1a 去重聚合，
        /// 写到 <c>PluginData/UI18n/capture-*.jsonl</c>，供翻译流水线补齐。
        /// <b>只写本地文件、绝不上传</b>；上传只受 <c>ReportOptIn</c> 控制，且当前版本没有任何上传代码。
        /// </summary>
        public bool CaptureMisses { get; set; } = true;

        /// <summary>
        /// <b>v1.5 退役</b>（docs/08 D-T2）：配置键 <c>CaptureMaxEntries</c> 仍然解析（老配置照读），
        /// 但捕获端<b>不再因为条数丢弃任何正文</b>。想限制磁盘占用请用 <see cref="CaptureMaxSessionBytes"/>。
        /// </summary>
        public int CaptureMaxEntries { get; set; } = 5000;

        /// <summary>
        /// <b>v1.5 退役</b>：配置键 <c>CaptureMaxTemplateEntries</c> 仍然解析（老配置照读），
        /// 但模板档也照收不丢 —— 「Loading sound asset #/#」本身就是可翻的候选（D-T2）。
        /// </summary>
        public int CaptureMaxTemplateEntries { get; set; } = 200;

        /// <summary>
        /// 单条捕获文本的上限（配置键 <c>CaptureMaxTextLength</c>，默认 <c>8000</c>）。
        /// v1.5 起超长<b>只截断、不丢弃</b>：截断的记录落盘追加 <c>"truncated": true</c>。
        /// </summary>
        public int CaptureMaxTextLength { get; set; } = 8000;

        /// <summary>
        /// 单次会话写出的字节预算（配置键 <c>CaptureMaxSessionBytes</c>，默认 67108864 = 64 MiB；0 = 不限制）。
        /// v1.5 唯一还允许「不留正文」的闸门：用尽后停止写正文、只累加计数，日志只通报一次。
        /// </summary>
        public long CaptureMaxSessionBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>
        /// 投稿默认同意状态（配置键 <c>SubmissionConsent</c>，默认 <c>None</c> = 不投）。
        /// <para>
        /// docs/10 §6.2：默认不投。玩家在游戏内面板点「同意」写的是
        /// <c>PluginData/UI18n/prefs.json</c> 的 <c>submission.consent.granted</c>；
        /// 这个安装级键只给「手工编辑 Config.cfg 表达一次总同意」的场景用，且
        /// <b>面板里的明确决定优先</b>（面板取消勾选后，这里写 Granted 也不投）。
        /// 只认 <c>None</c> / <c>Granted</c>（不分大小写），其它值按 <c>None</c> 处理。
        /// </para>
        /// </summary>
        public string SubmissionConsent { get; set; } = global::UI18n.Core.Submission.SubmissionPolicy.ConsentNone;

        /// <summary>
        /// 数字骨架（pattern）通道开关（配置键 <c>PatternMatching</c>，默认 <c>true</c>；v1.3）。
        /// <para>
        /// 开启后：精确查表未命中时把原文里的数字换成 <c>#</c> 得到骨架，按骨架查表，命中再把数字填回
        /// （<c>Mass Flow: 2.20917 ton/s</c> → 骨架 <c>Mass Flow: # ton/s</c> → 一条译文覆盖全部实例）。
        /// 只在<b>首次</b>未命中路径上多一次查表，重复未命中仍走既有缓存短路，零分配性质不变。
        /// 关掉它 = 完全回到 v1.2 行为（骨架行即使载入了也一条都不用）。
        /// </para>
        /// </summary>
        public bool PatternMatching { get; set; } = true;

        /// <summary>
        /// 实验开关（配置键 <c>ShadowStyleDraw</c>，默认 <c>false</c>）：给 IMGUI 的统一出口
        /// <c>GUIStyle.Internal_Draw</c> / <c>Internal_Draw2</c> 装两个<b>影子前缀</b>，
        /// 只统计「统一出口看到哪些文本」，用来回答「13 个 IMGUI hook 能不能收敛成 2 个」。
        /// <para>
        /// 默认关闭时<b>零 patch、零日志、零开销</b>，与没有这个开关时逐字节等价。
        /// 影子前缀按值接收 <c>GUIContent</c>（不用 <c>ref</c>），所以结构上不可能改动游戏对象。
        /// </para>
        /// </summary>
        public bool ShadowStyleDraw { get; set; } = false;

        /// <summary>
        /// 更新源根地址（配置键 <c>UpdateBaseUrl</c>，默认<b>空 = 完全不联网</b>）。
        /// <para>
        /// 仓库地址尚未公开，所以默认值必须是空串：空串时整个更新子系统连一个 socket 都不会开，
        /// 日志只打一行 <c>update: disabled (UpdateBaseUrl empty)</c>。
        /// 只接受 <c>https://</c> 任意主机，或 <c>http://</c> 且主机是回环地址（本地验证用）；
        /// 其它 scheme / 非回环 http 一律拒绝并 Warn（见 <c>UI18n.Core.Update.UpdateUrlGate</c>）。
        /// </para>
        /// </summary>
        public string UpdateBaseUrl { get; set; } = string.Empty;

        /// <summary>更新通道（配置键 <c>UpdateChannel</c>，默认 <c>stable</c>）；清单地址是 <c>&lt;base&gt;/&lt;channel&gt;/manifest.json</c>。</summary>
        public string UpdateChannel { get; set; } = "stable";

        /// <summary>单次网络请求的超时秒数（配置键 <c>UpdateTimeoutSeconds</c>，默认 10）。</summary>
        public int UpdateTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// 两次检查之间的最小天数（配置键 <c>UpdateCheckDays</c>，默认 7；<b>0 = 每次启动都查</b>）。
        /// 上次成功检查时间记在 <c>PluginData/UI18n/update-state.json</c>。
        /// </summary>
        public int UpdateCheckDays { get; set; } = 7;

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
