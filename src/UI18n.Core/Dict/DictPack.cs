using System;
using System.Collections.Generic;

namespace UI18n.Core.Dict
{
    /// <summary>
    /// 字典包：JSONL 载入的条目集合 + 按 64 位哈希分桶的查找索引（决策 D5 / D11 / D25）。
    /// <para>
    /// <b>查找语义（00 §4.1）</b>：同一哈希下可能有多个条目（不同层、或碰撞），桶内按
    /// <see cref="KeyLevel"/> 升序（Miss &lt; Ctx &lt; Site &lt; Global，即 ctx 最优先）稳定排序，
    /// 逐个用源文 Ordinal 比对消歧 —— 于是 ctx 命中就绝不会掉到 site / global。
    /// 「译文 == 源文」的条目是 keep（不可译项，04 §6.3 门禁 5），命中它算 miss 并记 <see cref="Keeps"/>。
    /// </para>
    /// <para>
    /// <b>线程模型</b>：非线程安全。<see cref="LoadJsonLines"/> / <see cref="AddJsonLine"/> 只在主线程装配期调用，
    /// 索引懒构建一次后只读；游戏内命中路径（主线程）只读不写，符合 05 §6.2 的零锁要求。
    /// </para>
    /// </summary>
    public sealed class DictPack
    {
        private readonly List<DictEntry> _entries = new List<DictEntry>();
        private Dictionary<ulong, int[]> _index;
        private Dictionary<ulong, int[]> _patternIndex;
        private int _patternEntries;
        private int _patternHits;
        private int _hits;
        private int _misses;
        private int _keeps;
        private int _parseErrors;

        /// <summary>已载入条目数。</summary>
        public int Entries
        {
            get { return _entries.Count; }
        }

        /// <summary>命中（返回了非 keep 译文）次数。</summary>
        public int Hits
        {
            get { return _hits; }
        }

        /// <summary>未命中次数（含 keep：keep 视为无译文）。</summary>
        public int Misses
        {
            get { return _misses; }
        }

        /// <summary>命中「译文 == 源文」（不可译项）的次数，单独统计以便和真 miss 区分。</summary>
        public int Keeps
        {
            get { return _keeps; }
        }

        /// <summary>坏行计数（JSON 语法错 / h 缺失或非法 / t 缺失或空）。</summary>
        public int ParseErrors
        {
            get { return _parseErrors; }
        }

        /// <summary>
        /// 骨架行条数（v1.3 pattern 通道，JSONL 带 <c>"p":1</c> 的行）。
        /// <see cref="Entries"/> 是<b>全部</b>条目数（精确 + 骨架），本属性只是其中的骨架部分。
        /// </summary>
        public int PatternEntries
        {
            get { return _patternEntries; }
        }

        /// <summary>骨架查表命中次数（<see cref="TryGetPattern"/> 成功返回译文的次数）。</summary>
        public int PatternHits
        {
            get { return _patternHits; }
        }

        /// <summary>没有任何条目。</summary>
        public bool IsEmpty
        {
            get { return _entries.Count == 0; }
        }

        /// <summary>把计数归零（载入新语言包后重新统计命中率用）。</summary>
        public void ResetCounters()
        {
            _hits = 0;
            _misses = 0;
            _keeps = 0;
            _parseErrors = 0;
            _patternHits = 0;
        }

        /// <summary>载入若干 JSONL 行。<paramref name="lines"/> 为 null 时返回空包。</summary>
        public static DictPack LoadJsonLines(IEnumerable<string> lines)
        {
            var pack = new DictPack();
            if (lines == null)
            {
                return pack;
            }

            foreach (string line in lines)
            {
                pack.AddJsonLine(line);
            }

            return pack;
        }

        /// <summary>
        /// 追加一行 JSONL。空行静默跳过；坏行只计入 <see cref="ParseErrors"/> 并跳过，<b>不抛异常</b>。
        /// </summary>
        public void AddJsonLine(string line)
        {
            DictEntry entry;
            JsonLineReader.Status status = JsonLineReader.ParseLine(line, out entry);

            if (status == JsonLineReader.Status.Ok)
            {
                _entries.Add(entry);
                if (entry.IsPattern)
                {
                    _patternEntries++;
                }

                _index = null;                              // 精确索引失效
                _patternIndex = null;                       // 骨架索引一起失效（下次查找时重建）
            }
            else if (status == JsonLineReader.Status.Error)
            {
                _parseErrors++;
            }
        }

        /// <summary>
        /// 不带位置信息的查译文：<paramref name="hash"/> 是源文（原文或归一化文本）的 FNV-1a 64；
        /// <paramref name="source"/> 用于同哈希多条目时消歧（先比 <c>s</c> 再比 <c>n</c>；条目 <c>s</c> 为空视为通配）。
        /// <para>
        /// <b>本重载永远不会返回 ctx 级条目</b>（调用方没有位置信息，见
        /// <see cref="TryGet(ulong, string, string, out string, out KeyLevel)"/> 的 context 语义），
        /// 实际命中层级只会是 Site 或 Global。需要 ctx 精度请用带 context 的重载。
        /// </para>
        /// </summary>
        public bool TryGet(ulong hash, string source, out string translation)
        {
            KeyLevel level;
            return TryGet(hash, source, null, out translation, out level);
        }

        /// <summary>
        /// 同 <see cref="TryGet(ulong, string, out string)"/>，并返回命中的层级（供 hitLevel 统计；本重载只会给出 Site 或 Global）。
        /// </summary>
        public bool TryGet(ulong hash, string source, out string translation, out KeyLevel level)
        {
            return TryGet(hash, source, null, out translation, out level);
        }

        /// <summary>
        /// 三层查找的<b>唯一实现</b>，另外两个重载都传 <c>context = null</c> 转调这里。
        /// <para>
        /// <b>context 语义（T8 修的缺陷，别再改回去）</b>：ctx 级条目表达的是「某个具体调用位置上的译文」，
        /// 只有在查询方<b>知道自己在什么位置</b>时才有资格命中。因此：
        /// <list type="bullet">
        /// <item><paramref name="context"/> 为 null 或空 → <b>跳过全部 ctx 级条目</b>，从 site 级开始找。
        /// 否则一个没有位置信息的查询会命中某个具体位置的译文（示例：三层包里 <c>"Run"</c> 有
        /// site 级「执行」和 ctx 级「跑步」，无 context 的查询必须拿到 site 级的「执行」）。</item>
        /// <item><paramref name="context"/> 非空 → 只接受 ctx 与之 <see cref="StringComparison.Ordinal"/> 相等的 ctx 级条目；
        /// site / global 级条目不受 context 限制。</item>
        /// </list>
        /// 两种情况都保持 ctx → site → global 的优先级（桶内已按 <see cref="KeyLevel"/> 升序排好），
        /// 命中即返回并回填 <paramref name="level"/> 供 hitLevel 统计。
        /// </para>
        /// </summary>
        public bool TryGet(ulong hash, string source, string context, out string translation, out KeyLevel level)
        {
            translation = null;
            level = KeyLevel.Miss;

            int[] bucket;
            if (!TryGetBucket(hash, out bucket))
            {
                _misses++;
                return false;
            }

            bool hasContext = !string.IsNullOrEmpty(context);

            for (int i = 0; i < bucket.Length; i++)
            {
                DictEntry entry = _entries[bucket[i]];

                if (entry.Level == KeyLevel.Ctx)
                {
                    // 没有位置信息就一律不碰 ctx 级（见方法注释里的契约）
                    if (!hasContext)
                    {
                        continue;
                    }

                    if (!string.Equals(entry.Context, context, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                if (!SourceMatches(entry, source))
                {
                    continue;
                }

                if (entry.IsKeep)
                {
                    // keep = 明确「不要翻」。它是最具体层级给出的结论，因此终止查找而不是继续下沉。
                    _keeps++;
                    _misses++;
                    return false;
                }

                translation = entry.Translation;
                level = entry.Level;
                _hits++;
                return true;
            }

            _misses++;
            return false;
        }

        /// <summary>
        /// 骨架查表（v1.3 pattern 通道的<b>唯一</b>入口）：<paramref name="skeletonHash"/> 是骨架文本的 FNV-1a 64，
        /// <paramref name="skeleton"/> 用于同哈希消歧（与条目的 <c>s</c> 做 Ordinal 比对）。
        /// <para>
        /// <b>与精确查表彻底隔离</b>：骨架行只进独立索引，所以
        /// <see cref="TryGet(ulong, string, out string)"/> 永远不会命中骨架行（同名文本 + <c>p:1</c> 时精确查表必须 miss），
        /// 这里也永远不会命中精确行。两条索引互不可见 —— 靠类型判断在装配期分流，不靠运行时猜。
        /// </para>
        /// <para>
        /// 命中计 <see cref="PatternHits"/>，<b>不</b>动精确通道的 <see cref="Hits"/> / <see cref="Misses"/>
        /// （那两个数的分母是精确查表的次数，混进来命中率就没意义了）。keep（译文 == 骨架）按未命中处理。
        /// </para>
        /// </summary>
        public bool TryGetPattern(ulong skeletonHash, string skeleton, out string translation)
        {
            translation = null;

            if (_patternIndex == null)
            {
                BuildIndex();
            }

            int[] bucket;
            if (!_patternIndex.TryGetValue(skeletonHash, out bucket))
            {
                return false;
            }

            for (int i = 0; i < bucket.Length; i++)
            {
                DictEntry entry = _entries[bucket[i]];
                if (!string.Equals(entry.Source, skeleton, StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.IsKeep)
                {
                    return false;                           // 明确「不要翻」：与精确通道同口径
                }

                translation = entry.Translation;
                _patternHits++;
                return true;
            }

            return false;
        }

        /// <summary>条目只读快照（调试 / dump 用，顺序即载入顺序；含骨架行）。</summary>
        public IList<DictEntry> GetEntries()
        {
            return _entries.AsReadOnly();
        }

        private bool TryGetBucket(ulong hash, out int[] bucket)
        {
            if (_index == null)
            {
                BuildIndex();
            }

            return _index.TryGetValue(hash, out bucket);
        }

        /// <summary>
        /// 建<b>两条</b>索引：精确行 → <c>_index</c>，骨架行 → <c>_patternIndex</c>（v1.3 起分流，两条索引互不可见）。
        /// 桶内顺序：Level 升序（ctx 优先），同层保持载入顺序（稳定插入排序；桶一般只有 1–2 个元素，
        /// 比 List.Sort + 比较器分配更省，符合 05 §4「未命中 0 B 分配」的约束）。
        /// </summary>
        private void BuildIndex()
        {
            var buckets = new Dictionary<ulong, List<int>>(_entries.Count);
            Dictionary<ulong, List<int>> patternBuckets = _patternEntries > 0
                ? new Dictionary<ulong, List<int>>(_patternEntries)
                : null;

            for (int i = 0; i < _entries.Count; i++)
            {
                DictEntry entry = _entries[i];
                Dictionary<ulong, List<int>> target = entry.IsPattern ? patternBuckets : buckets;

                List<int> ids;
                if (!target.TryGetValue(entry.Hash, out ids))
                {
                    ids = new List<int>(1);
                    target[entry.Hash] = ids;
                }

                ids.Add(i);
            }

            _index = BuildBuckets(buckets);
            _patternIndex = patternBuckets != null ? BuildBuckets(patternBuckets) : EmptyIndex;
        }

        /// <summary>骨架索引为空时的共享实例（避免每次为「包里没有骨架行」分配一个字典）。</summary>
        private static readonly Dictionary<ulong, int[]> EmptyIndex = new Dictionary<ulong, int[]>(0);

        /// <summary>把「hash → 插入序列表」排成「hash → 桶内按 Level 升序的索引数组」。</summary>
        private Dictionary<ulong, int[]> BuildBuckets(Dictionary<ulong, List<int>> buckets)
        {
            var index = new Dictionary<ulong, int[]>(buckets.Count);
            foreach (KeyValuePair<ulong, List<int>> pair in buckets)
            {
                int[] ids = pair.Value.ToArray();
                for (int i = 1; i < ids.Length; i++)
                {
                    int current = ids[i];
                    int level = (int)_entries[current].Level;
                    int j = i - 1;
                    while (j >= 0 && (int)_entries[ids[j]].Level > level)
                    {
                        ids[j + 1] = ids[j];
                        j--;
                    }

                    ids[j + 1] = current;
                }

                index[pair.Key] = ids;
            }

            return index;
        }

        private static bool SourceMatches(DictEntry entry, string source)
        {
            if (entry.Source.Length == 0)
            {
                return entry.Normalized.Length == 0 || string.IsNullOrEmpty(source) || string.Equals(entry.Normalized, source, StringComparison.Ordinal);
            }

            if (string.IsNullOrEmpty(source))
            {
                return false;
            }

            if (string.Equals(entry.Source, source, StringComparison.Ordinal))
            {
                return true;
            }

            return entry.Normalized.Length > 0 && string.Equals(entry.Normalized, source, StringComparison.Ordinal);
        }
    }
}
