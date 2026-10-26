using System;
using System.Collections.Generic;
using UI18n.Core.Capture;
using UI18n.Core.Config;
using UI18n.Core.Dict;
using UI18n.Core.Hashing;
using UI18n.Core.Text;
using UnityEngine;

namespace UI18n.Runtime
{
    /// <summary>
    /// 运行时翻译门面：原文 → 归一化 → FNV-1a 64 → 字典查表，带两级缓存。
    /// <para>
    /// 名字刻意不叫 Localizer，避免与本工程大量使用的 <c>KSP.Localization.Localizer</c> 撞名
    /// （在 UI18n.Runtime 命名空间里，裸写 Localizer 会先解析到本程序集）。
    /// </para>
    /// <para>
    /// <b>为什么缓存 GUIContent 实例而不是字符串</b>（决策 D25）：命中路径每帧都在跑，KSP 用 Boehm GC，
    /// 每帧新建字符串/对象约 2.9 MB/s 分配；这里把「原文 → 译文」和「原文 → 新 GUIContent」都缓存下来，
    /// 命中后只剩两次字典查找。
    /// </para>
    /// <para>
    /// <b>绝不能做的事</b>：不写 <c>content.text</c>（<c>GUIContent.Temp</c> 返回共享实例，写它会污染所有同文本控件），
    /// 只 <c>new GUIContent(...)</c> 并写回 <c>ref</c> 参数；本类只读游戏状态，不碰玩法 API。
    /// </para>
    /// <para>
    /// <b>诊断（round 2）</b>：某个原文<b>第一次</b>被翻译成功时，记一条样本（原文 + 译文 + 命中层级 +
    /// 首次命中的 patch 名），并有全量去重计数 <see cref="DistinctHits"/>。两者都只在缓存未命中路径上
    /// 各做一次，不改变任何既有日志行，也不参与翻译决策。
    /// </para>
    /// <para>线程模型：只允许主线程调用（IMGUI / uGUI 都在主线程）。</para>
    /// </summary>
    internal static class Translator
    {
        /// <summary>缓存上限，超过就整体清空（UI 文本种类远小于此，正常游戏不会触发）。</summary>
        private const int CacheLimit = 20000;

        /// <summary>样本里 source / translation 的截断长度（超出只留前 N 个字符并加 <c>...</c>）。</summary>
        private const int SampleTextLimit = 80;

        private static readonly Dictionary<string, CacheEntry> TextCache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GUIContent> ContentCache = new Dictionary<string, GUIContent>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReportedFailures = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>首次命中样本（有上限，见 <c>UI18nConfig.HitSampleLimit</c>）。</summary>
        private static readonly List<HitSample> Samples = new List<HitSample>();

        /// <summary>已经翻译成功过的原文集合：distinct 计数与样本去重都靠它。</summary>
        private static readonly HashSet<string> HitSources = new HashSet<string>(StringComparer.Ordinal);

        private static UI18nConfig _config;
        private static DictPack _pack;
        private static CaptureStore _capture;
        private static int _sampleLimit;
        private static bool _sampleCapped;
        private static long _distinctHits;

        /// <summary>
        /// 缓存项：译文（空串 = 未命中）+ 该原文的 FNV-1a 64。
        /// <para>
        /// 为什么连 hash 一起缓存：缺命中捕获（task-30）在<b>重复</b>未命中时只允许做「查表 + 自增」，
        /// 不允许再归一化/算哈希/分配字符串；hash 存进缓存项后，重复未命中路径只剩一次字典查找。
        /// </para>
        /// </summary>
        private struct CacheEntry
        {
            internal string Translation;
            internal ulong Hash;
        }

        /// <summary>一条首次命中样本（只读快照，dump 时才拼字符串，热路径不分配）。</summary>
        private struct HitSample
        {
            internal string Patch;
            internal string Source;
            internal string Translation;
            internal KeyLevel Level;
        }

        /// <summary>语言 / 开关已过门禁且字典非空 —— patch 前缀的第一道短路（05 §6.2）。</summary>
        internal static bool HasAnyTranslation
        {
            get { return _pack != null && _pack.Entries > 0; }
        }

        internal static UI18nConfig Config
        {
            get { return _config; }
        }

        internal static DictPack Pack
        {
            get { return _pack; }
        }

        internal static int CachedTexts
        {
            get { return TextCache.Count; }
        }

        /// <summary>当前挂着的缺命中捕获器（没挂就是 <c>null</c>，热路径上只多一次字段判空）。</summary>
        internal static CaptureStore Capture
        {
            get { return _capture; }
        }

        /// <summary>
        /// 挂上缺命中捕获器（task-30）：<b>必须</b>在 <see cref="Initialize"/> 之后调用（Initialize 会把它清掉）。
        /// 传 <c>null</c> 等于关掉捕获。
        /// </summary>
        internal static void AttachCapture(CaptureStore store)
        {
            _capture = store;
        }

        /// <summary>本局翻译成功过的<b>不同原文</b>条数（全量去重，不受采样上限影响）。</summary>
        internal static long DistinctHits
        {
            get { return _distinctHits; }
        }

        /// <summary>样本是否因达到上限而被截断（true 时 <c>hit sample:</c> 只是前 N 条）。</summary>
        internal static bool SampleCapped
        {
            get { return _sampleCapped; }
        }

        /// <summary>已记录的样本条数。</summary>
        internal static int SampleCount
        {
            get { return Samples.Count; }
        }

        internal static void Initialize(UI18nConfig config, DictPack pack)
        {
            _config = config ?? UI18nConfig.Default;
            _pack = pack;
            TextCache.Clear();
            ContentCache.Clear();
            ReportedFailures.Clear();
            Samples.Clear();
            HitSources.Clear();
            _capture = null;
            _distinctHits = 0;
            _sampleCapped = false;

            int limit = _config.HitSampleLimit;
            _sampleLimit = limit < 0 ? 0 : limit;
        }

        /// <summary>
        /// 原文 → 译文。命中返回译文；未命中 / keep / 空输入返回 <c>null</c>（调用方据此零分配跳过）。
        /// 未命中也会缓存（存空串），避免同一串被反复哈希。
        /// </summary>
        internal static string Translate(string source)
        {
            return Translate(source, null);
        }

        /// <summary>
        /// 同上，另外把「谁第一个翻到它」记进样本表（<paramref name="patch"/> 是 patch 短名，可为 null）。
        /// 样本只在首次命中时记录，所以热路径上没有额外分配。
        /// </summary>
        internal static string Translate(string source, string patch)
        {
            if (string.IsNullOrEmpty(source) || _pack == null)
            {
                return null;
            }

            UI18nConfig config = _config;
            if (config != null && !config.Enabled)
            {
                return null;
            }

            CacheEntry cached;
            if (TextCache.TryGetValue(source, out cached))
            {
                if (cached.Translation.Length == 0)
                {
                    // 重复未命中：只允许「查表 + 自增」（task-30 性能契约），零分配
                    CaptureStore capture = _capture;
                    if (capture != null)
                    {
                        capture.CountMiss(cached.Hash);
                    }

                    return null;
                }

                return cached.Translation;
            }

            // 归一化（04 §4.1 五步）+ FNV-1a 64（D11）；已归一时 Normalize 返回原引用，零分配
            string normalized = TextNormalizer.Normalize(source);
            string translation = null;
            ulong hash = 0;

            if (normalized.Length > 0)
            {
                hash = Fnv1a.Hash(normalized);
                string hit;
                KeyLevel level;
                long keepsBefore = _pack.Keeps;
                if (_pack.TryGet(hash, normalized, out hit, out level))
                {
                    translation = hit;
                    RecordHit(patch, source, translation, level);
                }
                else
                {
                    // 骨架兜底（v1.3 pattern 通道）：精确未命中 → 骨化 → 骨架查表 → 数字填回。
                    // 只在「首次未命中」走这条路（重复未命中在方法开头就由缓存短路了，零分配性质不变）。
                    translation = TryPattern(normalized, config, patch);
                }

                if (translation == null && _capture != null && _pack.Keeps == keepsBefore)
                {
                    // 真未命中才捕获：keep（译文 == 原文）说明字典里有它，不算缺命中（04 §6.3 门禁 5）；
                    // 骨架命中的也不算（translation 非 null 时分母不进来）
                    _capture.Record(hash, source, normalized, patch);
                }
            }

            if (TextCache.Count >= CacheLimit)
            {
                TextCache.Clear();
                ContentCache.Clear();
            }

            CacheEntry entry = new CacheEntry();
            entry.Translation = translation ?? string.Empty;
            entry.Hash = hash;
            TextCache[source] = entry;
            return translation;
        }

        /// <summary>
        /// 骨架兜底（v1.3）：归一化文本 → <see cref="TextSkeleton.TryBuild"/> → 骨架查表 → <see cref="TextSkeleton.FillBack"/>。
        /// 返回 <c>null</c> 表示未命中（骨化失败 / 没有骨架行 / 槽位不匹配 / 通道关闭）。
        /// <para>
        /// 调用点固定在「精确查表未命中之后、缺命中捕获之前」：骨架命中的串<b>不进</b>捕获文件，
        /// 否则捕获库会被成千上万条只差数字的模板串重新灌满 —— 那正是这个通道要消灭的东西。
        /// </para>
        /// <para>分配：一次骨架串 + 数字数组 + 填回结果，只在首次未命中路径上发生（05 §4 的零分配约束针对命中路径）。</para>
        /// </summary>
        private static string TryPattern(string normalized, UI18nConfig config, string patch)
        {
            DictPack pack = _pack;
            if (pack == null || pack.PatternEntries == 0)
            {
                return null;                                // 包里没有骨架行：连正则都不碰
            }

            if (config != null && !config.PatternMatching)
            {
                return null;
            }

            string skeleton;
            string[] numbers;
            if (!TextSkeleton.TryBuild(normalized, out skeleton, out numbers))
            {
                return null;                                // 没有数字 / 槽位超限：不是模板
            }

            string patternTranslation;
            if (!pack.TryGetPattern(Fnv1a.Hash(skeleton), skeleton, out patternTranslation))
            {
                return null;
            }

            string filled = TextSkeleton.FillBack(patternTranslation, numbers);
            if (filled == null)
            {
                // 槽位数和数字个数不一致 = 语言包坏了（t 里 # 的个数必须等于骨架占位符数）。
                // 只 log 不抛（红线 6），并且按未命中处理 —— 绝不能把带 # 的半成品显示出去。
                Log.Warn("pattern: fill-back mismatch, skeleton=\"" + Clip(skeleton) + "\", numbers=" + numbers.Length
                         + "（译文里 # 的个数应与骨架占位符数相等）");
                return null;
            }

            // 样本里记「原文」（不是骨架）：distinct 的口径保持「不同原文」，日志里也看得懂是谁被骨架救的
            RecordHit(patch, normalized, filled, KeyLevel.Pattern);
            return filled;
        }

        /// <summary>
        /// 只读探针（task-31 影子计数用）：问「这句在字典里有译文吗」，<b>不</b>替换任何东西。
        /// <para>
        /// 与 <see cref="Translate(string, string)"/> 的三条硬差别，都是为了不污染现有统计：
        /// <list type="number">
        /// <item>不写 <c>TextCache</c>（不改变缓存命中率，也不把影子独有的串灌进缓存）。</item>
        /// <item>不动任何计数：不 <c>RecordHit</c>、不 <c>DistinctHits</c>、不 <c>CountMiss</c>、不 <c>Record</c>。</item>
        /// <item>不检查 <c>Enabled</c>（影子只在 Enabled 且 ShadowStyleDraw 都成立时才会被调用）。</item>
        /// </list>
        /// 成本 = 归一化 + FNV-1a + 一次桶查找（未命中不分配；已归一时 Normalize 返回原引用）。
        /// </para>
        /// <para>
        /// 只查<b>精确</b>通道，<b>故意不走</b>骨架通道（v1.3）：影子实验测的是「精确字典还漏什么」，
        /// 混进骨架会让「缺口条数」在骨架包上线前后不可比；何况影子开关（<c>ShadowStyleDraw</c>）已归档。
        /// </para>
        /// </summary>
        internal static bool Peek(string source, out string translation)
        {
            translation = null;
            if (string.IsNullOrEmpty(source) || _pack == null)
            {
                return false;
            }

            string normalized = TextNormalizer.Normalize(source);
            if (normalized.Length == 0)
            {
                return false;
            }

            string hit;
            KeyLevel level;
            if (!_pack.TryGet(Fnv1a.Hash(normalized), normalized, out hit, out level))
            {
                return false;
            }

            translation = hit;
            return true;
        }

        /// <summary>
        /// GUIContent 版本：命中返回<b>新建并缓存</b>的 GUIContent（保留 image / tooltip），
        /// 未命中返回 <c>null</c>（调用方保留原对象，不产生分配）。
        /// </summary>
        internal static GUIContent TranslateContent(GUIContent content)
        {
            return TranslateContent(content, null);
        }

        /// <summary>同上，<paramref name="patch"/> 只用于首次命中样本（诊断）。</summary>
        internal static GUIContent TranslateContent(GUIContent content, string patch)
        {
            if (content == null)
            {
                return null;
            }

            string text = content.text;
            string translation = Translate(text, patch);
            if (translation == null)
            {
                return null;
            }

            // 带图标 / tooltip 的控件很少，这类不进缓存：缓存按文本做 key，混用会串图标
            if (content.image != null || !string.IsNullOrEmpty(content.tooltip))
            {
                return new GUIContent(translation, content.image, content.tooltip);
            }

            GUIContent cached;
            if (ContentCache.TryGetValue(text, out cached))
            {
                return cached;
            }

            // 只能是新对象：GUIContent.Temp(...) 返回共享实例，就地改写会污染工坊里所有同文本控件
            cached = new GUIContent(translation);
            ContentCache[text] = cached;
            return cached;
        }

        /// <summary>
        /// 拼 <c>hit sample:</c> 诊断行（含表头）。表头格式固定：
        /// <c>hit sample: distinct=&lt;N&gt; (capped=&lt;bool&gt;)</c>；每条样本一行：
        /// <c>hit sample: [&lt;patch&gt;] &lt;原文&gt; =&gt; &lt;译文&gt; (&lt;层级&gt;)</c>。
        /// 原文/译文各截断到 80 字符、换行转义成 <c>\n</c>，保证一条样本永远只占一行。
        /// </summary>
        internal static void AppendSampleLines(List<string> sink)
        {
            if (sink == null)
            {
                return;
            }

            sink.Add("hit sample: distinct=" + _distinctHits + " (capped=" + (_sampleCapped ? "true" : "false") + ")");

            for (int i = 0; i < Samples.Count; i++)
            {
                HitSample sample = Samples[i];
                sink.Add("hit sample: [" + (string.IsNullOrEmpty(sample.Patch) ? "unknown" : sample.Patch) + "] "
                         + Clip(sample.Source) + " => " + Clip(sample.Translation) + " (" + LevelName(sample.Level) + ")");
            }
        }

        /// <summary>patch 前缀里的异常只记一次（红线 6），不打断游戏、不刷屏。</summary>
        internal static void ReportPrefixFailure(string patch, Exception exception)
        {
            if (!ReportedFailures.Add(patch))
            {
                return;
            }

            Log.Error("prefix failed: " + patch + " → " + exception.GetType().Name + ": " + exception.Message
                      + "（同类错误每局只记一次）");
        }

        /// <summary>记一条首次命中：distinct 计数 + 有上限的样本表。同一原文只记一次。</summary>
        private static void RecordHit(string patch, string source, string translation, KeyLevel level)
        {
            if (!HitSources.Add(source))
            {
                return;
            }

            _distinctHits++;

            if (_sampleLimit <= 0)
            {
                return;
            }

            if (Samples.Count >= _sampleLimit)
            {
                _sampleCapped = true;
                return;
            }

            HitSample sample = new HitSample();
            sample.Patch = patch;
            sample.Source = source;
            sample.Translation = translation;
            sample.Level = level;
            Samples.Add(sample);
        }

        /// <summary>单行化 + 截断：日志里一条样本必须只占一行（原文可能带换行）。</summary>
        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string oneLine = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            if (oneLine.Length <= SampleTextLimit)
            {
                return oneLine;
            }

            return oneLine.Substring(0, SampleTextLimit) + "...";
        }

        private static string LevelName(KeyLevel level)
        {
            switch (level)
            {
                case KeyLevel.Ctx:
                    return "ctx";
                case KeyLevel.Site:
                    return "site";
                case KeyLevel.Global:
                    return "global";
                case KeyLevel.Pattern:
                    return "pattern";
                default:
                    return "miss";
            }
        }
    }
}
