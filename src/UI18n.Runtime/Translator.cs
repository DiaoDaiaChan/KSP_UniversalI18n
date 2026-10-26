using System;
using System.Collections.Generic;
using System.Text;
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
            RenderCoverage.Reset();

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

            // task-62 render 覆盖率（docs/08 D-T4）：只在「这个原文第一次进来」这一条路径上记一次。
            // 上面两条缓存命中分支（含每帧热路径）一个字节都不动 —— 命中路径零查询、零分配。
            // hit = 首次就拿到译文，miss = 首次没拿到；hash 是查表前算好的，直接复用。
            RenderCoverage.Record(hash, patch, translation != null);

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

    /// <summary>
    /// render 覆盖率（task-62 / docs/08 D-T4）：本会话「渲染出口出现过多少条不同的原文，其中多少条
    /// 被字典接住了」。这是对玩家承诺的覆盖率口径；pool 覆盖率（packs/pool.jsonl）只是预热指标。
    /// <para>
    /// 口径：每个不同的原文只在首次走进 <see cref="Translator.Translate(string, string)"/> 的首次路径时记一次，
    /// 重复渲染（命中 TextCache）不计数。于是 hit = 首次就变成中文的不同串数，miss = 首次没变的不同串数，
    /// ratio = hit / distinct，且恒有 hit + miss == distinct。
    /// </para>
    /// <para>
    /// 为什么计数点放在首次路径：缓存命中分支（return cached.Translation 那一行）是每帧热路径，这里一个字都不改，
    /// 命中路径新增 0 次字典查询、0 字节分配。计数量级从「每帧」降到「每个不同串一次」（一局几千次），
    /// 所以一次 HashSet&lt;long&gt;.Add、以及 miss 时取一次调用栈（约 2 微秒）都可以接受。
    /// </para>
    /// <para>
    /// 为什么要自己去重：TextCache 满 20000 条会整体清空，清空后同一原文会再次走首次路径；这里用
    /// HashSet&lt;long&gt; 挡住重复，hash 是查表前已经算好的，直接复用，不重算、不分配字符串。
    /// </para>
    /// <para>
    /// 上限：distinct 与 byCaller 都有硬上限，防长会话内存无界增长；到顶时 log 一次（红线 6：说明原因，不抛）。
    /// </para>
    /// </summary>
    internal static class RenderCoverage
    {
        /// <summary>周期落盘间隔（秒）。默认 300 = 5 分钟；配置接线由集成时补。</summary>
        internal const int DefaultIntervalSeconds = 300;

        /// <summary>distinct 去重集合上限（65536 条约 1.5 MB 封顶）。</summary>
        private const int DistinctLimit = 65536;

        /// <summary>byCaller 上限：超出的未命中统一归入 CallerOther。</summary>
        private const int CallerLimit = 200;

        private const string CallerOther = "(other)";
        private const string SinkNone = "(none)";
        private const string RatioZero = "0.0000";

        private struct SinkCounter
        {
            internal long Hit;
            internal long Miss;
        }

        private static readonly HashSet<long> Distinct = new HashSet<long>();
        private static readonly Dictionary<string, SinkCounter> Sinks = new Dictionary<string, SinkCounter>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> CallerMiss = new Dictionary<string, long>(StringComparer.Ordinal);
        private static long _hit;
        private static long _miss;
        private static bool _capped;
        private static bool _callerCapped;

        internal static long Hit
        {
            get { return _hit; }
        }

        internal static long Miss
        {
            get { return _miss; }
        }

        /// <summary>本会话见过的不同原文条数 = hit + miss。</summary>
        internal static long DistinctCount
        {
            get { return _hit + _miss; }
        }

        /// <summary>hit / distinct，固定 4 位小数（写进 json 的 ratio 与日志行，格式固定便于 grep）。</summary>
        internal static string Ratio
        {
            get
            {
                long distinct = _hit + _miss;
                if (distinct <= 0)
                {
                    return RatioZero;
                }

                return ((double)_hit / distinct).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        /// <summary>由 Translator.Initialize 调用：换包/换语言 = 新的一局统计。</summary>
        internal static void Reset()
        {
            Distinct.Clear();
            Sinks.Clear();
            CallerMiss.Clear();
            _hit = 0;
            _miss = 0;
            _capped = false;
            _callerCapped = false;
        }

        /// <summary>
        /// 记一条首次见到的原文。只允许从 Translator.Translate 的首次路径调用（每个不同串一辈子只走到这里一次，
        /// 缓存被清空导致的重现由内部分去重挡住）。
        /// </summary>
        internal static void Record(ulong hash, string sink, bool hit)
        {
            if (hash == 0UL)
            {
                return;
            }

            if (Distinct.Count >= DistinctLimit)
            {
                if (!_capped)
                {
                    _capped = true;
                    Log.Warn("render coverage: distinct 已达上限 " + DistinctLimit + " 条，之后新出现的串不再计入（已计入的读数仍然有效）");
                }

                return;
            }

            if (!Distinct.Add(unchecked((long)hash)))
            {
                return;
            }

            string key = string.IsNullOrEmpty(sink) ? SinkNone : sink;
            SinkCounter counter;
            if (!Sinks.TryGetValue(key, out counter))
            {
                counter = new SinkCounter();
            }

            if (hit)
            {
                counter.Hit++;
                _hit++;
            }
            else
            {
                counter.Miss++;
                _miss++;
            }

            Sinks[key] = counter;

            if (!hit)
            {
                CountCaller();
            }
        }

        /// <summary>
        /// byCaller：只在「miss 且该原文首次出现」时取一次调用栈（约 2 微秒，每个不同串一辈子一次）。
        /// 之后的重复未命中只走 CaptureStore.CountMiss，绝不进这里。
        /// </summary>
        private static void CountCaller()
        {
            string caller = CaptureCaller.FromStack(1);

            long count;
            if (CallerMiss.TryGetValue(caller, out count))
            {
                CallerMiss[caller] = count + 1;
                return;
            }

            if (CallerMiss.Count >= CallerLimit)
            {
                if (!_callerCapped)
                {
                    _callerCapped = true;
                    Log.Warn("render coverage: byCaller 已达上限 " + CallerLimit + " 个，其余未命中归入 " + CallerOther);
                }

                if (CallerMiss.TryGetValue(CallerOther, out count))
                {
                    CallerMiss[CallerOther] = count + 1;
                }
                else
                {
                    CallerMiss[CallerOther] = 1;
                }

                return;
            }

            CallerMiss[caller] = 1;
        }

        /// <summary>一行摘要（不带 [UI18n] 前缀，前缀由 Log 加）。</summary>
        internal static string BuildLogLine()
        {
            long distinct = _hit + _miss;
            return "render coverage: hit=" + _hit + " miss=" + _miss
                   + " distinct=" + distinct + " ratio=" + Ratio;
        }

        /// <summary>
        /// 落盘用的 JSON（schema 见 docs/08）：stamp / version / hit / miss / distinct / ratio / bySink / byCaller。
        /// 键一律排好序，同一会话重复写盘逐字节相同（幂等，方便 diff 与工具去重）。
        /// </summary>
        internal static string BuildJson(string stamp, string version)
        {
            System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"stamp\":\"").Append(CaptureJson.Escape(stamp ?? string.Empty));
            sb.Append("\",\"version\":\"").Append(CaptureJson.Escape(version ?? string.Empty));
            sb.Append("\",\"hit\":").Append(_hit.ToString(invariant));
            sb.Append(",\"miss\":").Append(_miss.ToString(invariant));
            sb.Append(",\"distinct\":").Append((_hit + _miss).ToString(invariant));
            sb.Append(",\"ratio\":").Append(Ratio);

            sb.Append(",\"bySink\":{");
            List<string> sinks = new List<string>(Sinks.Keys);
            sinks.Sort(StringComparer.Ordinal);
            for (int i = 0; i < sinks.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                string sink = sinks[i];
                SinkCounter counter = Sinks[sink];
                sb.Append('"').Append(CaptureJson.Escape(sink)).Append("\":{\"hit\":").Append(counter.Hit.ToString(invariant));
                sb.Append(",\"miss\":").Append(counter.Miss.ToString(invariant)).Append('}');
            }

            sb.Append("},\"byCaller\":{");
            List<string> callers = new List<string>(CallerMiss.Keys);
            callers.Sort(delegate(string left, string right)
            {
                long a = CallerMiss[left];
                long b = CallerMiss[right];
                if (a != b)
                {
                    return a > b ? -1 : 1;
                }

                return string.CompareOrdinal(left, right);
            });

            for (int i = 0; i < callers.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                string caller = callers[i];
                sb.Append('"').Append(CaptureJson.Escape(caller)).Append("\":{\"miss\":").Append(CallerMiss[caller].ToString(invariant)).Append('}');
            }

            sb.Append("}}");
            return sb.ToString();
        }
    }
}
