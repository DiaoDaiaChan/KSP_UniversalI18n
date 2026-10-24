using System;
using System.Collections.Generic;
using System.Globalization;
using UI18n.Core.Text;

namespace UI18n.Core.Diagnostics
{
    /// <summary>
    /// task-31 任务 B：prefab 烘焙文本缺口量化（task-34 起按「活动对象」分三档重测）。
    /// <para>
    /// uGUI 的 <c>Text.text</c> 除了运行期 <c>set_text</c> 写入，还有一条完全绕过 setter 的路径：
    /// <b>prefab / 场景里烘焙好的初始值</b>（反序列化直接写字段，不调 setter）。这类文本我们的
    /// <c>Text.set_text</c> 前缀永远看不到，属于「结构性覆盖缺口」。
    /// </para>
    /// <para>
    /// <b>task-34 的教训</b>：round 8 只按「<c>Resources.FindObjectsOfTypeAll</c> 扫到的全部组件」
    /// 算缺口，得到 1,066 —— 那个数被<b>未实例化的 prefab 资产</b>灌水了（样例是单字母 <c>II</c>/<c>U</c>/<c>N</c>、
    /// lorem ipsum 占位、报纸 prefab <c>Space Gazette</c>）。现在分三档：
    /// </para>
    /// <list type="number">
    /// <item><b>active</b> —— 在场景里（<c>gameObject.scene.IsValid()</c>）且 <c>isActiveAndEnabled</c>：真在屏幕上可见，<b>只有这一档算缺口</b>。</item>
    /// <item><b>inactive</b> —— 在场景里但未激活（隐藏面板 / 未打开的窗口）。</item>
    /// <item><b>asset</b> —— 不在任何场景（prefab 资产 / 未实例化对象）。</item>
    /// </list>
    /// <para>
    /// 本类只做<b>统计</b>，<b>绝不写回组件</b>（红线 4 的同类约束：只读游戏状态）。
    /// </para>
    /// </summary>
    public sealed class PrefabGapReport
    {
        private readonly List<string> _samples;
        private readonly bool _tiered;

        private PrefabGapReport(int active, int activeNever, int inactive, int inactiveNever, int assets, int assetNever,
                                int recoverable, List<string> samples, bool tiered)
        {
            ActiveComponents = active;
            ActiveNeverSetText = activeNever;
            InactiveComponents = inactive;
            InactiveNeverSetText = inactiveNever;
            AssetComponents = assets;
            AssetNeverSetText = assetNever;
            RecoverableFromSceneScan = recoverable;
            _samples = samples;
            _tiered = tiered;
        }

        /// <summary>第一档：在场景里且激活、文本非空的组件数（<b>唯一计入缺口的口径</b>）。</summary>
        public int ActiveComponents { get; private set; }

        /// <summary>第一档里文本从未在 <c>set_text</c> 出口出现过的组件数（= 真实缺口）。</summary>
        public int ActiveNeverSetText { get; private set; }

        /// <summary>第二档：在场景里但未激活、文本非空的组件数。</summary>
        public int InactiveComponents { get; private set; }

        /// <summary>第二档里从未经过 <c>set_text</c> 的组件数。</summary>
        public int InactiveNeverSetText { get; private set; }

        /// <summary>第三档：不在任何场景（prefab 资产）、文本非空的组件数。</summary>
        public int AssetComponents { get; private set; }

        /// <summary>第三档里从未经过 <c>set_text</c> 的组件数。</summary>
        public int AssetNeverSetText { get; private set; }

        /// <summary>
        /// 第一档缺口里，我们字典<b>确实有</b>译文的条数 —— 即「如果做一次场景扫描，一次能补多少条」。
        /// 没传判定函数时为 0。
        /// </summary>
        public int RecoverableFromSceneScan { get; private set; }

        /// <summary>扫到的、文本非空的组件数（= 第一档；保留 task-31 的旧语义）。</summary>
        public int Components
        {
            get { return ActiveComponents; }
        }

        /// <summary>缺口（= <see cref="ActiveNeverSetText"/>；保留 task-31 的旧语义）。</summary>
        public int NeverSetText
        {
            get { return ActiveNeverSetText; }
        }

        /// <summary>样例（第一档、去重、按首次出现顺序，最多 <c>sampleCount</c> 条）。</summary>
        public string[] Samples
        {
            get { return _samples.ToArray(); }
        }

        /// <summary>
        /// 旧口径（task-31）：把 <paramref name="texts"/> 全部当成「在场景里且激活」。
        /// 空文本会被跳过（不计入 <see cref="Components"/>），每个元素先归一化再与 <paramref name="legacy"/> 比对。
        /// </summary>
        public static PrefabGapReport Analyze(IList<string> texts, TextSinkTracker legacy, int sampleCount)
        {
            var samples = new List<string>();
            int components;
            int never;
            Collect(texts, legacy, sampleCount, samples, out components, out never);
            return new PrefabGapReport(components, never, 0, 0, 0, 0, 0, samples, false);
        }

        /// <summary>
        /// task-34 口径：三档分别统计，只有第一档（在场景里且激活）算缺口；
        /// <paramref name="translatable"/> 用来估算「一次场景扫描能补多少条」（可传 null）。
        /// </summary>
        public static PrefabGapReport AnalyzeTiers(IList<string> activeTexts, IList<string> inactiveTexts,
                                                   IList<string> assetTexts, TextSinkTracker legacy, int sampleCount,
                                                   Func<string, bool> translatable)
        {
            var samples = new List<string>();
            int activeComponents;
            int activeNever;
            Collect(activeTexts, legacy, sampleCount, samples, out activeComponents, out activeNever);

            var unused = new List<string>();
            int inactiveComponents;
            int inactiveNever;
            Collect(inactiveTexts, legacy, 0, unused, out inactiveComponents, out inactiveNever);

            int assetComponents;
            int assetNever;
            Collect(assetTexts, legacy, 0, unused, out assetComponents, out assetNever);

            int recoverable = 0;
            if (translatable != null && activeTexts != null)
            {
                for (int i = 0; i < activeTexts.Count; i++)
                {
                    string raw = activeTexts[i];
                    if (string.IsNullOrEmpty(raw))
                    {
                        continue;
                    }

                    string normalized = TextNormalizer.Normalize(raw);
                    if (normalized.Length == 0)
                    {
                        continue;
                    }

                    if (legacy != null && legacy.Contains(normalized))
                    {
                        continue;
                    }

                    if (translatable(normalized))
                    {
                        recoverable++;
                    }
                }
            }

            return new PrefabGapReport(activeComponents, activeNever, inactiveComponents, inactiveNever,
                                       assetComponents, assetNever, recoverable, samples, true);
        }

        /// <summary>
        /// 一行汇总。旧口径：<c>prefab gap: components=N, never-through-set_text=M</c>；
        /// 三档口径：<c>prefab gap: active=N (never=M, scene-scan recoverable=K), inactive=N (never=M), assets=N (never=M)</c>。
        /// </summary>
        public string Describe()
        {
            if (!_tiered)
            {
                return "prefab gap: components=" + Components.ToString(CultureInfo.InvariantCulture)
                       + ", never-through-set_text=" + NeverSetText.ToString(CultureInfo.InvariantCulture);
            }

            return "prefab gap: active=" + ActiveComponents.ToString(CultureInfo.InvariantCulture)
                   + " (never=" + ActiveNeverSetText.ToString(CultureInfo.InvariantCulture)
                   + ", scene-scan recoverable=" + RecoverableFromSceneScan.ToString(CultureInfo.InvariantCulture) + ")"
                   + ", inactive=" + InactiveComponents.ToString(CultureInfo.InvariantCulture)
                   + " (never=" + InactiveNeverSetText.ToString(CultureInfo.InvariantCulture) + ")"
                   + ", assets=" + AssetComponents.ToString(CultureInfo.InvariantCulture)
                   + " (never=" + AssetNeverSetText.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>三档明细（每档一行，便于 grep 与人工核对）。</summary>
        public string[] TierLines()
        {
            return new[]
            {
                "prefab gap tier 1 (in scene, active - counts as gap): components="
                + ActiveComponents.ToString(CultureInfo.InvariantCulture)
                + ", never-through-set_text=" + ActiveNeverSetText.ToString(CultureInfo.InvariantCulture)
                + ", recoverable-from-scene-scan=" + RecoverableFromSceneScan.ToString(CultureInfo.InvariantCulture),
                "prefab gap tier 2 (in scene, inactive - not counted as gap): components="
                + InactiveComponents.ToString(CultureInfo.InvariantCulture)
                + ", never-through-set_text=" + InactiveNeverSetText.ToString(CultureInfo.InvariantCulture),
                "prefab gap tier 3 (not in any scene = prefab assets - not counted as gap): components="
                + AssetComponents.ToString(CultureInfo.InvariantCulture)
                + ", never-through-set_text=" + AssetNeverSetText.ToString(CultureInfo.InvariantCulture)
            };
        }

        /// <summary>样例日志行：<c>prefab gap sample: "…"</c>（单行、截断 100 字符）。</summary>
        public string[] SampleLines()
        {
            var lines = new string[_samples.Count];
            for (int i = 0; i < _samples.Count; i++)
            {
                string text = _samples[i].Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                if (text.Length > 100)
                {
                    text = text.Substring(0, 100) + "...";
                }

                lines[i] = "prefab gap sample: \"" + text + "\"";
            }

            return lines;
        }

        private static void Collect(IList<string> texts, TextSinkTracker legacy, int sampleCount, List<string> samples,
                                    out int components, out int never)
        {
            components = 0;
            never = 0;
            if (texts == null)
            {
                return;
            }

            var seenSamples = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < texts.Count; i++)
            {
                string raw = texts[i];
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                string normalized = TextNormalizer.Normalize(raw);
                if (normalized.Length == 0)
                {
                    continue;
                }

                components++;
                if (legacy != null && legacy.Contains(normalized))
                {
                    continue;
                }

                never++;
                if (sampleCount > 0 && samples.Count < sampleCount && seenSamples.Add(normalized))
                {
                    samples.Add(normalized);
                }
            }
        }
    }
}
