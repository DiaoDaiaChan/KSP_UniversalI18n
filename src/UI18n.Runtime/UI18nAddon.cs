using System;
using System.Reflection;
using HarmonyLib;
using UI18n.Core.Config;
using UI18n.Core.Dict;
using UI18n.Core.Policy;
using UI18n.Runtime.Diagnostics;
using UI18n.Runtime.Fonts;
using UI18n.Runtime.Patches;
using UI18n.Runtime.Update;
using UnityEngine;

namespace UI18n.Runtime
{
    /// <summary>
    /// 启动入口。<c>Startup.Instantly</c>：在主菜单之前就跑，保证第一帧 UI 已被接管。
    /// <para>
    /// 启动顺序（严格按此顺序，任何一步失败都只记日志、绝不抛给 KSP）：
    /// 读配置 → 语言 → <b>语言门禁</b>（非中文直接 return，装 0 个 patch）→ 读字典 → 字体配置 →
    /// 装 patch → 一行汇总。
    /// </para>
    /// <para>
    /// <b>Instantly 阶段不做全场景扫描</b>：<c>Resources.FindObjectsOfTypeAll</c> 这类调用会遍历已加载资产，
    /// 在 loading 场景里既慢又可能触发引擎内部重入；字体探测因此推迟到 <c>HighLogic.LoadedScene == MAINMENU</c>
    /// （或启动 180 秒后的兜底），见 <see cref="TryProbe"/>。
    /// </para>
    /// <para>红线 5：语言不是中文时一个 patch 都不装。红线 6：失败只 log 不抛。</para>
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class UI18nAddon : MonoBehaviour
    {
        internal const string HarmonyId = "com.universali18n.runtime";

        private static string _version;

        /// <summary>
        /// 程序集版本串，形如 <c>1.0.0+9238f1a…</c>：<c>+</c> 前是发布版本（构建时 <c>-p:Version=</c> 写进去的），
        /// <c>+</c> 后是构建时 HEAD 的 commit（.NET SDK 的 <c>SourceRevisionId</c>，见 README §10.8）。
        /// 日志<b>原样打印整串</b> —— 玩家贴一行日志就能定位到确切 commit，比只打 <c>1.0.0</c> 有用。
        /// 只读一次并缓存；读不到就回落 <c>AssemblyName.Version</c>。
        /// </summary>
        internal static string Version
        {
            get
            {
                if (_version == null) { _version = ReadVersion(); }
                return _version;
            }
        }

        /// <summary>读程序集版本：优先 <see cref="AssemblyInformationalVersionAttribute"/>（带 commit），回落 <c>AssemblyName.Version</c>。</summary>
        private static string ReadVersion()
        {
            try
            {
                Assembly assembly = typeof(UI18nAddon).Assembly;

                object[] attributes = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (attributes != null && attributes.Length > 0)
                {
                    AssemblyInformationalVersionAttribute info = attributes[0] as AssemblyInformationalVersionAttribute;
                    if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                    {
                        return info.InformationalVersion;
                    }
                }

                System.Version assemblyVersion = assembly.GetName().Version;
                if (assemblyVersion != null) { return assemblyVersion.ToString(); }
            }
            catch (Exception ex)
            {
                Log.Warn("version probe failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return "unknown";
        }

        /// <summary>统计日志间隔（P2：16 个目标的命中数必须能在日志里看到）。</summary>
        private const float StatsIntervalSeconds = 20f;

        /// <summary>字体探测的兜底时刻：即使没进主菜单（例如直接从存档启动），到点也探一次。</summary>
        private const float ProbeFallbackSeconds = 180f;

        private static Harmony _harmony;
        private static bool _started;

        /// <summary>已装 patch 数（供后续联调/状态面板读取）。</summary>
        internal static int InstalledPatches { get; private set; }

        /// <summary>安装失败的 patch 数。</summary>
        internal static int FailedPatches { get; private set; }

        /// <summary>启动时看到的游戏语言。</summary>
        internal static string Language { get; private set; }

        private bool _statsActive;
        private float _nextStatsAt;
        private long _lastTotalCalls = -1;
        private bool _probePending;
        private float _startedAt;
        private bool _finalDumped;
        private bool _updateErrorLogged;

        private void Start()
        {
            if (_started)
            {
                // KSPAddon(..., once: true) 正常情况下只实例化一次；真被再实例化一遍也不许重复装 patch
                Log.Info("already initialized, skip (patches=" + InstalledPatches + ")");
                return;
            }

            _started = true;
            _startedAt = Time.realtimeSinceStartup;

            try
            {
                Run();
            }
            catch (Exception ex)
            {
                Log.Error("startup failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            // 本对象必须活过场景切换（loading → mainmenu），否则 Update 里的周期统计与字体探测都不会发生。
            try
            {
                DontDestroyOnLoad(gameObject);
            }
            catch (Exception ex)
            {
                Log.Warn("DontDestroyOnLoad failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Update()
        {
            try
            {
                TryProbe();
                MissCapture.Tick();

                // task-40 联网分发：后台线程只做入队，这里把攒下的行吐出去（没有新行时是空操作）
                UpdateRunner.Tick();

                // task-31 影子计数（默认关闭，所有入口第一行就是 if (!Enabled) return）
                ShadowTracker.Tick();
                ShadowTracker.ScanPrefabsIfDue();

                if (_statsActive)
                {
                    DumpStatsIfDue();
                }
            }
            catch (Exception ex)
            {
                if (!_updateErrorLogged)
                {
                    _updateErrorLogged = true;
                    Log.Error("update loop failed: " + ex.GetType().Name + ": " + ex.Message + "（本局不再重复记录）");
                }
            }
        }

        private void OnApplicationQuit()
        {
            FinalDump("quit");
            MissCapture.Shutdown();
            ShadowTracker.Shutdown();
            UpdateRunner.Shutdown();
        }

        private void OnDestroy()
        {
            FinalDump("destroy");
            MissCapture.Shutdown();
            ShadowTracker.Shutdown();
            UpdateRunner.Shutdown();
        }

        private void Run()
        {
            Log.Info("v" + Version + " starting (harmony id " + HarmonyId + ")");

            // 1) 配置
            UI18nConfig config = ConfigLoader.Load();

            // 2) 字体配置（只灌参数 + 打一行，不做任何探测/注入）
            FontResolver.Configure(config);

            // 3) 语言
            string language = ReadLanguage();
            Language = language;
            Log.Info("language=" + Printable(language) + ", enabled=" + config.Enabled + ", forceOverride=" + config.ForceOverride);

            if (!config.Enabled)
            {
                Log.Info("disabled in Config.cfg (Enabled=False), patches skipped");
                return;
            }

            // 4) 语言门禁：非中文 → 装 0 个 patch，一行日志说明原因（红线 5）
            if (!LanguageGate.ShouldTranslate(language, config.ForceOverride))
            {
                Log.Info("language=" + Printable(language) + ", patches skipped"
                         + (config.ForceOverride ? string.Empty : " (only zh* is translated; set ForceOverride=True to override)"));
                return;
            }

            // 5) 字典
            DictPack pack = ConfigLoader.LoadPacks(config, language);
            Translator.Initialize(config, pack);
            Log.Info("dictionary: entries=" + pack.Entries + ", parseErrors=" + pack.ParseErrors);

            if (pack.Entries == 0)
            {
                Log.Warn("dictionary is empty, patches skipped (install a pack under "
                         + ConfigLoader.ModFolder + "/" + ConfigLoader.PacksFolderName + "/<lang>/)");
                return;
            }

            // 5b) 缺命中捕获（task-30）：默认开启，只写 PluginData/UI18n/capture-*.jsonl，绝不上传。
            //     必须在 Translator.Initialize 之后挂（Initialize 会清掉挂载），且必须在装 patch 之前。
            MissCapture.Configure(config);
            Translator.AttachCapture(MissCapture.Store);

            // 6) 装 patch
            Harmony harmony = GetHarmony();
            if (harmony == null)
            {
                Log.Error("Harmony instance unavailable, patches skipped (is 0Harmony.dll loaded?)");
                return;
            }

            int ok;
            int failed;
            PatchRegistry.InstallAll(harmony, pack, out ok, out failed);
            InstalledPatches = ok;
            FailedPatches = failed;

            // 6b) 影子计数（task-31，实验开关）：默认关 = 不装 patch、不打日志。
            //     必须放在 InstallAll 之后 —— 正式目标的计数与日志都已就位，影子只做加法。
            ShadowTracker.Configure(config);
            if (ShadowTracker.Enabled)
            {
                int shadowOk;
                int shadowFailed;
                PatchRegistry.InstallShadowPatches(harmony, out shadowOk, out shadowFailed);
            }

            // 6c) 联网分发骨架（task-40）：只起后台线程，主线程零等待。
            //     门禁顺序在 UpdateWorkflow 里：地址空 -> 完全不联网（连 socket 都不开）。
            StartUpdater(config, language);

            // 7) 一行汇总
            Log.Info("ready: patches=" + ok + "/" + (ok + failed) + ", dict entries=" + pack.Entries
                     + ", language=" + Printable(language) + ", version=" + Version);

            _statsActive = ok > 0;
            _probePending = true;
            _nextStatsAt = Time.realtimeSinceStartup + StatsIntervalSeconds;
        }

        /// <summary>
        /// 起更新线程（task-40）。<b>这里只做三件事</b>：把日志出口接上、算出两个路径、起线程。
        /// 网络/解压/写盘全在后台线程里，主线程绝不等待 —— 加载时间与主菜单到达时间不受影响。
        /// </summary>
        private static void StartUpdater(UI18nConfig config, string language)
        {
            try
            {
                UpdateRunner.Sink = delegate(string message, bool warning)
                {
                    if (warning)
                    {
                        Log.Warn(message);
                    }
                    else
                    {
                        Log.Info(message);
                    }
                };

                string modRoot = ConfigLoader.ModPath;
                string pluginDataRoot = System.IO.Path.Combine(
                    System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "PluginData"), "UI18n");

                UpdateRunner.ConfigureAndStart(config, modRoot, pluginDataRoot, Version, language);
            }
            catch (Exception ex)
            {
                // 更新是可选功能：起不来也绝不影响翻译
                Log.Warn("update: could not start (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        /// <summary>
        /// 字体探测（惰性、一次性）。等主菜单再扫，避免在 loading 场景里做全场景资产遍历。
        /// </summary>
        private void TryProbe()
        {
            if (!_probePending)
            {
                return;
            }

            bool atMainMenu = false;
            try
            {
                atMainMenu = HighLogic.LoadedScene == GameScenes.MAINMENU;
            }
            catch (Exception)
            {
                atMainMenu = false;
            }

            bool overdue = Time.realtimeSinceStartup - _startedAt > ProbeFallbackSeconds;
            if (!atMainMenu && !overdue)
            {
                return;
            }

            _probePending = false;

            Log.Info("font probe: scene=" + SafeSceneName() + (overdue && !atMainMenu ? " (fallback after " + (int)ProbeFallbackSeconds + "s)" : string.Empty));
            FontProbe.ProbeAndLog();
        }

        /// <summary>周期统计：有变化才打（静止场景不刷屏），但至少打一次。</summary>
        private void DumpStatsIfDue()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextStatsAt)
            {
                return;
            }

            _nextStatsAt = now + StatsIntervalSeconds;

            long calls = PatchStats.TotalCalls;
            if (_lastTotalCalls >= 0 && calls == _lastTotalCalls)
            {
                return;
            }

            _lastTotalCalls = calls;
            Log.Info(PatchStats.Dump());
            Log.Info("distinct hits: " + Translator.DistinctHits);
            Log.Info(FontResolver.Describe());

            if (MissCapture.Store != null)
            {
                Log.Info(MissCapture.Describe());
            }
        }

        private void FinalDump(string reason)
        {
            if (!_statsActive || _finalDumped)
            {
                return;
            }

            _finalDumped = true;
            Log.Info("final dump (" + reason + "): calls=" + PatchStats.TotalCalls + ", hits=" + PatchStats.TotalHits);

            string stats = PatchStats.Dump();
            if (stats.Length > 0)
            {
                Log.Info(stats);
            }

            Log.Info("distinct hits: " + Translator.DistinctHits);
            Log.Info(FontResolver.Describe());

            // 「到底翻了哪些串」（round 2）：只在退出时打，条数受 HitSampleLimit 限制（默认 64，0 = 关）。
            // 追加在既有行之后，不改任何既有日志行。
            var sampleLines = new System.Collections.Generic.List<string>();
            Translator.AppendSampleLines(sampleLines);
            for (int i = 0; i < sampleLines.Count; i++)
            {
                Log.Info(sampleLines[i]);
            }
        }

        private static string SafeSceneName()
        {
            try
            {
                return HighLogic.LoadedScene.ToString();
            }
            catch (Exception)
            {
                return "<unknown>";
            }
        }

        private static string ReadLanguage()
        {
            try
            {
                // 必须写全名：本命名空间里有个自己的 Translator，裸写容易误读成 KSP 的 Localizer
                return KSP.Localization.Localizer.CurrentLanguage;
            }
            catch (Exception ex)
            {
                Log.Warn("read language failed: " + ex.GetType().Name + ": " + ex.Message + ", treating as non-Chinese");
                return null;
            }
        }

        private static string Printable(string language)
        {
            return string.IsNullOrEmpty(language) ? "<null>" : language;
        }

        private static Harmony GetHarmony()
        {
            if (_harmony != null)
            {
                return _harmony;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);
                return _harmony;
            }
            catch (Exception ex)
            {
                Log.Error("Harmony init failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }
    }
}
