using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UI18n.Core.Config;
using UI18n.Core.Diagnostics;
using TMPro;
using UI18n.Core.Text;
using UnityEngine;

namespace UI18n.Runtime.Diagnostics
{
    /// <summary>
    /// task-31 影子计数：给 IMGUI 的统一出口 <c>GUIStyle.Internal_Draw</c> / <c>Internal_Draw2</c> 装两个
    /// <b>只读影子前缀</b>，把「统一出口看到的文本」与「现有 13 个 IMGUI hook 看到的文本」做差集，
    /// 用数据回答「13 个 hook 能不能收敛成 2 个」。
    /// <para>
    /// <b>默认关闭时零开销</b>（配置 <c>ShadowStyleDraw=False</c>）：不装 patch、不打日志、所有入口
    /// 第一行就是 <c>if (!_enabled) return;</c>。开着时也只有「归一化 + 查表 + 去重计数」，
    /// 绝不修改 <c>GUIContent</c>（影子前缀的参数是<b>按值</b>的，结构上写不回去）。
    /// </para>
    /// <para>
    /// 三条统计口径，回答的是不同问题：
    /// <list type="bullet">
    /// <item><see cref="StyleDrawDistinct"/>：统一出口看到多少种不同文本（上限 20000，超出只计 overflow）。</item>
    /// <item><see cref="TranslatableDistinct"/>：其中字典里<b>有译文</b>的种数（= 真的会翻的）。</item>
    /// <item><see cref="LegacyDistinct"/>：现有 13 个 IMGUI hook 看到多少种（<see cref="ShadowReport"/> 用它算差集）。</item>
    /// </list>
    /// 收敛决策看 <see cref="ShadowReport.StyleDrawOnlyTranslatable"/>（统一出口独有<b>且</b>可翻），
    /// 不是看「统一出口独有」的总数 —— 后者里绝大多数是数字/标识符，本来就不该翻。
    /// </para>
    /// <para>线程模型：只允许主线程调用（IMGUI 绘制与 Update 都在主线程）。</para>
    /// </summary>
    internal static class ShadowTracker
    {
        /// <summary>汇总日志间隔（秒）。</summary>
        internal const float ReportIntervalSeconds = 60f;

        /// <summary>退出时每组日志最多打印多少条（JSONL 里是全量）。</summary>
        internal const int TopCount = 50;

        /// <summary>prefab 缺口样例条数。</summary>
        internal const int PrefabSampleCount = 20;

        /// <summary>场景切换后延迟多久扫 prefab（等 UI 建完，避免扫到半成品）。</summary>
        private const float PrefabScanDelaySeconds = 3f;

        private const string SinkDraw = "GUIStyle.Internal_Draw";
        private const string SinkDraw2 = "GUIStyle.Internal_Draw2";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly TextSinkTracker StyleDraw = new TextSinkTracker();
        private static readonly TextSinkTracker Translatable = new TextSinkTracker();
        private static readonly TextSinkTracker Legacy = new TextSinkTracker();
        private static readonly HashSet<string> ReportedFailures = new HashSet<string>(StringComparer.Ordinal);

        private static bool _enabled;
        private static string _outputPath;
        private static float _nextReportAt;
        private static long _lastReportedCalls = -1;
        private static bool _dumped;
        private static bool _writeFailureLogged;

        private static GameScenes _scannedScene;
        private static bool _hasScanned;
        private static bool _scanPending;
        private static float _scanAt;
        private static int _prefabScans;

        /// <summary>影子计数是否生效（<c>ShadowStyleDraw=True</c> 且插件 Enabled）。</summary>
        internal static bool Enabled
        {
            get { return _enabled; }
        }

        internal static long StyleDrawCalls
        {
            get { return StyleDraw.Calls; }
        }

        internal static int StyleDrawDistinct
        {
            get { return StyleDraw.Distinct; }
        }

        internal static int TranslatableDistinct
        {
            get { return Translatable.Distinct; }
        }

        internal static long LegacyCalls
        {
            get { return Legacy.Calls; }
        }

        internal static int LegacyDistinct
        {
            get { return Legacy.Distinct; }
        }

        /// <summary>本局做过几次 prefab 扫描。</summary>
        internal static int PrefabScans
        {
            get { return _prefabScans; }
        }

        /// <summary>影子 JSONL 的输出路径（关闭时为 <c>null</c>）。</summary>
        internal static string OutputPath
        {
            get { return _outputPath; }
        }

        /// <summary>
        /// 按配置初始化。<b>关闭时一行日志都不打</b>（默认关 = 与没有这个功能时逐字节等价）。
        /// </summary>
        internal static void Configure(UI18nConfig config)
        {
            StyleDraw.Reset();
            Translatable.Reset();
            Legacy.Reset();
            ReportedFailures.Clear();
            _lastReportedCalls = -1;
            _dumped = false;
            _writeFailureLogged = false;
            _scanPending = false;
            _prefabScans = 0;
            _hasScanned = false;

            _enabled = config != null && config.Enabled && config.ShadowStyleDraw;
            if (!_enabled)
            {
                _outputPath = null;
                return;
            }

            try
            {
                string dir = Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "PluginData"), "UI18n");
                _outputPath = Path.Combine(dir, "shadow-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
            }
            catch (Exception ex)
            {
                _outputPath = null;
                ReportFailure("path", ex);
            }

            _nextReportAt = Time.realtimeSinceStartup + ReportIntervalSeconds;
            Log.Info("shadow: enabled (ShadowStyleDraw=True): counting " + SinkDraw + " / " + SinkDraw2
                     + " against the 17 hooks; report every " + ReportIntervalSeconds.ToString(CultureInfo.InvariantCulture)
                     + " s, dump on exit -> " + (_outputPath ?? "(no path)"));
        }

        /// <summary>现有 14 个 IMGUI hook 每处理一个 <c>GUIContent</c> 就记一次（差集的另一半）。
        /// <para><c>GUI.DoButtonGrid</c>（task-35）不在这里记：它的文本在 <c>GUIContent[]</c> 元素里，
        /// 绘制时由 <c>GUIStyle.Draw</c> 逐个交给 <see cref="NoteStyleDraw"/>，本身不走 <c>ref GUIContent</c> 前缀。</para></summary>
        internal static void NoteLegacy(string text)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                string normalized = TextNormalizer.Normalize(text);
                if (normalized.Length == 0)
                {
                    return;
                }

                Legacy.Note(normalized);
            }
            catch (Exception ex)
            {
                ReportFailure("legacy", ex);
            }
        }

        /// <summary>
        /// 影子前缀每看到一次绘制就记一次。<paramref name="sink"/> 只是日志/失败去重用的标签，
        /// <b>不影响</b>统计（两个出口共用同一个计数器，因为它们是同一个漏斗的两半）。
        /// </summary>
        internal static void NoteStyleDraw(string text, string sink)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                string normalized = TextNormalizer.Normalize(text);
                if (normalized.Length == 0)
                {
                    return;
                }

                // 只有「第一次见到的文本」才去查字典：Translatable 是集合，重复绘制不会改变结论。
                // 稳态下（同一批字符串每帧重画）每次绘制的成本 = 归一化 + 一次字典自增。
                if (!StyleDraw.Note(normalized))
                {
                    return;
                }

                string translation;
                if (Translator.Peek(normalized, out translation))
                {
                    Translatable.Note(normalized);
                }
            }
            catch (Exception ex)
            {
                ReportFailure(sink, ex);
            }
        }

        /// <summary>每帧调：每 60 s 且计数有变化时打一行汇总（只拼数字，不算差集）。</summary>
        internal static void Tick()
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                float now = Time.realtimeSinceStartup;
                if (now < _nextReportAt)
                {
                    return;
                }

                _nextReportAt = now + ReportIntervalSeconds;
                if (StyleDraw.Calls == _lastReportedCalls)
                {
                    return;
                }

                _lastReportedCalls = StyleDraw.Calls;
                Log.Info(Describe());
            }
            catch (Exception ex)
            {
                ReportFailure("tick", ex);
            }
        }

        /// <summary>一行汇总（便宜版：只读计数器，不算差集；差集在退出时算）。</summary>
        internal static string Describe()
        {
            return "shadow: styleDraw calls=" + StyleDrawCalls.ToString(CultureInfo.InvariantCulture)
                   + " distinct=" + StyleDrawDistinct.ToString(CultureInfo.InvariantCulture)
                   + ", legacy calls=" + LegacyCalls.ToString(CultureInfo.InvariantCulture)
                   + " distinct=" + LegacyDistinct.ToString(CultureInfo.InvariantCulture)
                   + ", translatable distinct=" + TranslatableDistinct.ToString(CultureInfo.InvariantCulture)
                   + (StyleDraw.Capped ? " (styleDraw capped)" : string.Empty);
        }

        /// <summary>
        /// 退出/销毁时收尾（幂等）：算差集 → 打 <see cref="TopCount"/> 条日志 → 全量写 JSONL。
        /// </summary>
        internal static void Shutdown()
        {
            if (!_enabled || _dumped)
            {
                return;
            }

            _dumped = true;

            try
            {
                var report = new ShadowReport(StyleDraw, Translatable, Legacy, SinkDraw);
                string[] lines = report.LogLines(TopCount);
                for (int i = 0; i < lines.Length; i++)
                {
                    Log.Info(lines[i]);
                }

                if (report.StyleDrawOnlyDistinct > TopCount || report.StyleDrawOnlyTranslatable > TopCount)
                {
                    Log.Info("shadow: (log truncated at " + TopCount.ToString(CultureInfo.InvariantCulture)
                             + " entries per group; full list in the jsonl)");
                }

                WriteJson(report);
            }
            catch (Exception ex)
            {
                ReportFailure("shutdown", ex);
            }
        }

        /// <summary>
        /// 任务 B：场景切换后（延迟 3 s、每场景一次）扫一遍已建好的 <c>Text</c> / <c>TMP_Text</c>，
        /// 统计「文本非空但从未经过 <c>set_text</c>」的组件 —— 那是 prefab / 场景烘焙的初始值，
        /// 结构上绕过了我们的 setter hook。<b>只读，绝不写回组件。</b>
        /// </summary>
        internal static void ScanPrefabsIfDue()
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                GameScenes scene = HighLogic.LoadedScene;
                if (scene != GameScenes.MAINMENU && scene != GameScenes.FLIGHT)
                {
                    return;
                }

                if (!_scanPending)
                {
                    if (_hasScanned && scene == _scannedScene)
                    {
                        return;
                    }

                    _scanPending = true;
                    _scanAt = Time.realtimeSinceStartup + PrefabScanDelaySeconds;
                    return;
                }

                if (Time.realtimeSinceStartup < _scanAt)
                {
                    return;
                }

                _scanPending = false;
                _scannedScene = scene;
                _hasScanned = true;
                ScanPrefabs(scene);
            }
            catch (Exception ex)
            {
                ReportFailure("prefab-scan", ex);
            }
        }

        /// <summary>同类失败只记一次（红线 6：失败只 log 不抛，但原因必须明确）。</summary>
        internal static void ReportFailure(string key, Exception exception)
        {
            try
            {
                if (exception == null || !ReportedFailures.Add(key ?? string.Empty))
                {
                    return;
                }

                Log.Warn("shadow: " + key + " failed → " + exception.GetType().Name + ": " + exception.Message
                         + "（同类错误每局只记一次）");
            }
            catch
            {
                // 记日志本身失败也只能吞掉（红线 6：绝不向游戏抛）
            }
        }

        /// <summary>
        /// task-34：按「活动对象」分三档统计。
        /// <para>
        /// round 8 的 1,066 是被<b>未实例化的 prefab 资产</b>灌水的（单字母、lorem ipsum、报纸 prefab），
        /// 所以这里先判 <c>gameObject.scene.IsValid()</c>（在场景里）与 <c>isActiveAndEnabled</c>（真激活），
        /// <b>只有第一档算缺口</b>；第二、三档照样给数，方便判断「这些是不是永远上不了屏幕」。
        /// </para>
        /// </summary>
        private static void ScanPrefabs(GameScenes scene)
        {
            var active = new List<string>();
            var inactive = new List<string>();
            var assets = new List<string>();

            UnityEngine.UI.Text[] legacyTexts = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>();
            for (int i = 0; i < legacyTexts.Length; i++)
            {
                UnityEngine.UI.Text component = legacyTexts[i];
                if (component != null)
                {
                    BucketText(component.text, component.gameObject, component.isActiveAndEnabled, active, inactive, assets);
                }
            }

            int legacyCount = legacyTexts.Length;

            TMP_Text[] tmpTexts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < tmpTexts.Length; i++)
            {
                TMP_Text component = tmpTexts[i];
                if (component != null)
                {
                    BucketText(component.text, component.gameObject, component.isActiveAndEnabled, active, inactive, assets);
                }
            }

            PrefabGapReport report = PrefabGapReport.AnalyzeTiers(active, inactive, assets, Legacy, PrefabSampleCount, IsTranslatable);
            _prefabScans++;

            Log.Info("prefab gap: scanned scene=" + scene + ", Text=" + legacyCount.ToString(CultureInfo.InvariantCulture)
                     + ", TMP_Text=" + tmpTexts.Length.ToString(CultureInfo.InvariantCulture)
                     + ", " + report.Describe());

            string[] tiers = report.TierLines();
            for (int i = 0; i < tiers.Length; i++)
            {
                Log.Info(tiers[i]);
            }

            string[] samples = report.SampleLines();
            for (int i = 0; i < samples.Length; i++)
            {
                Log.Info(samples[i]);
            }

            Log.Info("prefab gap: stats only, nothing was written back to any component (scan #"
                     + _prefabScans.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>把一条文本放进三档之一（空文本直接丢；不在这里归一化，交给 <see cref="PrefabGapReport"/>）。</summary>
        private static void BucketText(string text, GameObject owner, bool activeAndEnabled,
                                       List<string> active, List<string> inactive, List<string> assets)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (owner == null || !owner.scene.IsValid())
            {
                // 不在任何场景 = prefab 资产 / 未实例化对象，永远上不了屏幕
                assets.Add(text);
                return;
            }

            if (activeAndEnabled)
            {
                active.Add(text);
            }
            else
            {
                inactive.Add(text);
            }
        }

        /// <summary>「如果做场景扫描，这条能补吗」的判据：字典里确实有它的译文。</summary>
        private static bool IsTranslatable(string normalized)
        {
            string translation;
            return Translator.Peek(normalized, out translation);
        }

        private static void WriteJson(ShadowReport report)
        {
            if (string.IsNullOrEmpty(_outputPath))
            {
                return;
            }

            string[] lines = report.JsonLines(int.MaxValue);
            var builder = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                builder.Append(lines[i]).Append('\n');
            }

            try
            {
                string dir = Path.GetDirectoryName(_outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(_outputPath, builder.ToString(), Utf8NoBom);
                Log.Info("shadow: json written: " + _outputPath + " (" + lines.Length.ToString(CultureInfo.InvariantCulture) + " line(s))");
            }
            catch (Exception ex)
            {
                if (!_writeFailureLogged)
                {
                    _writeFailureLogged = true;
                    Log.Warn("shadow: json write failed (" + _outputPath + ") → " + ex.GetType().Name + ": " + ex.Message
                             + "（统计仍在上面的日志里）");
                }
            }
        }
    }
}
