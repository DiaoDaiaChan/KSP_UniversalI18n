using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UI18n.Core.Capture
{
    /// <summary>
    /// 「缺命中」聚合器：按 FNV-1a 64 去重，重复未命中只累加 <see cref="CaptureRecord.Count"/>，
    /// 落盘时把还没写过的记录按 JSONL 追加到文件（见 <see cref="CaptureJson"/>）。
    /// <para>
    /// <b>v1.5 起不丢正文</b>（docs/08 D-T2）：本类只在两种情况下不留正文，两种都必须留痕 ——
    /// ① 文本长度超 <see cref="CaptureOptions.MaxTextLength"/> 时截断写入并给记录打 <see cref="CaptureRecord.Truncated"/>；
    /// ② 本会话写入字节超 <see cref="CaptureOptions.MaxSessionBytes"/> 后停止写正文、只累加计数，
    /// 日志由 <see cref="ConsumeBudgetNotice"/> 保证只打一次。
    /// <see cref="CaptureOptions.MaxEntries"/> / <see cref="CaptureOptions.MaxTemplateEntries"/> /
    /// <see cref="CaptureOptions.MaxPerCaller"/> 已退役（字段保留只为配置解析不炸），
    /// <see cref="Filtered"/> / <see cref="Overflow"/> / <see cref="TemplateSkipped"/> / <see cref="CallerSkipped"/>
    /// 四个计数在 v1.5 恒为 0。
    /// </para>
    /// <para>
    /// <b>内存有界</b>：<see cref="_index"/> 只留「hash → 待写缓冲下标 / 已落盘」的索引，正文只活在
    /// <see cref="_pending"/> 里，<see cref="WritePending"/> 成功后立即清空 —— 会话越长不会把全部正文压在内存里。
    /// </para>
    /// <para>
    /// <b>性能契约</b>（task-30）：重复未命中的热路径只有「一次字典查表 + 一次整数自增」，
    /// 绝不在热路径上做归一化、算哈希、取调用栈或分配字符串。取调用栈（约 2 µs）只发生在
    /// 某条原文<b>首次</b>进入捕获时；重复命中走 <see cref="CountMiss"/>，O(1) 且零分配。
    /// </para>
    /// <para>
    /// <b>线程</b>：不是线程安全的。设计前提是只在 Unity 主线程调用（补丁前缀 + Update + 退出钩子）。
    /// </para>
    /// <para>
    /// <b>失败静默</b>：<see cref="WritePending"/> 吞掉所有 IO 异常，只把原因记在 <see cref="LastWriteError"/>
    /// 并返回 0；翻译主路径永远不受影响，也绝不抛。
    /// </para>
    /// </summary>
    public sealed class CaptureStore
    {
        /// <summary><see cref="_index"/> 里表示「这条的正文已经落盘」的值（&gt;= 0 表示待写缓冲下标）。</summary>
        private const int IndexWritten = -1;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly string[] NoLines = new string[0];

        private readonly CaptureOptions _options;

        // hash → (待写缓冲下标, >= 0) 或 IndexWritten（正文已落盘）。这是去重索引，条目数 = distinct 条数。
        private readonly Dictionary<ulong, int> _index;

        // 还没写盘的记录；WritePending 成功后清空，所以正文内存只跟「两次写盘之间的新增」成正比。
        private readonly List<CaptureRecord> _pending;

        // task-35：按 caller 聚合（只统计 distinct 记录，重复 miss 不会增加权重 —— 权重就是「有多少条不同文本来自它」）
        private readonly Dictionary<string, int> _callers;
        private int _unknownCallers;

        // v1.5：Filtered / Overflow / TemplateSkipped / CallerSkipped 四个计数已退役（恒 0），
        // 不再是字段 —— 见下面四个属性的注释，保留它们只为外部调用点（MissCapture 的日志与老测试）不炸。

        private long _budgetSkipped;    // 字节预算用尽后只计数、不留正文的条数（D-T2 允许的唯一「不留正文」之一）
        private long _lateRepeats;      // 正文已落盘的 hash 之后又 miss 的次数（只累加，不改已写出的行）
        private long _sessionBytes;     // 本会话已写出的字节数（按 UTF-8 行字节 + 换行计）
        private int _truncated;         // 截断写入的条数
        private int _candidates;
        private int _templates;
        private int _never;
        private int _written;           // 正文已落盘的 distinct 条数
        private bool _budgetNoticePending;
        private string _lastWriteError;

        /// <summary>用 <see cref="CaptureOptions.Default"/> 建一个聚合器。</summary>
        public CaptureStore()
            : this(null)
        {
        }

        /// <summary>用给定选项建一个聚合器（<paramref name="options"/> 为 null 时用默认值；内部会 Clone，外部改动不影响已建实例）。</summary>
        public CaptureStore(CaptureOptions options)
        {
            _options = (options ?? CaptureOptions.Default).Clone();
            if (_options.MaxTextLength <= 0)
            {
                _options.MaxTextLength = 8000;
            }

            if (_options.MinTextLength < 0)
            {
                _options.MinTextLength = 0;
            }

            if (_options.MaxSessionBytes < 0)
            {
                _options.MaxSessionBytes = 0;
            }

            // v1.5：MaxEntries 已退役（不再丢弃任何内容），初值只用来给字典一个不太离谱的起始容量。
            _index = new Dictionary<ulong, int>(256);
            _pending = new List<CaptureRecord>(256);
            _callers = new Dictionary<string, int>(16, StringComparer.Ordinal);
        }

        /// <summary>生效的选项（只读快照，改它不会影响已建实例的判定）。</summary>
        public CaptureOptions Options
        {
            get { return _options; }
        }

        /// <summary>见过的 distinct 条数（= 去重索引条目数；含正文因预算用尽而没留的条目）。</summary>
        public int Distinct
        {
            get { return _index.Count; }
        }

        /// <summary>正文已成功落盘的 distinct 条数。</summary>
        public int Written
        {
            get { return _written; }
        }

        /// <summary>还没写盘、正压在内存缓冲里的条数。</summary>
        public int Pending
        {
            get { return _pending.Count; }
        }

        /// <summary>本会话已写出的字节数（UTF-8，含换行）。</summary>
        public long SessionBytes
        {
            get { return _sessionBytes; }
        }

        /// <summary>
        /// v1.5 退役：恒为 0（以前是「被过滤规则拒掉的次数」）。
        /// 过滤器（<see cref="CaptureFilter"/>）仍然保留，但只作为分流/统计口径，不再拒绝任何串。
        /// </summary>
        public long Filtered
        {
            get { return 0L; }
        }

        /// <summary>已记录的条数里，<c>candidate</c> 桶（静态文本，可直接翻）的条数。</summary>
        public int Candidates
        {
            get { return _candidates; }
        }

        /// <summary>已记录的条数里，<c>template</c> 桶（含数字，需模板化）的条数。</summary>
        public int Templates
        {
            get { return _templates; }
        }

        /// <summary>已记录的条数里，<c>never</c> 桶（资源路径 / URL，永不翻）的条数。</summary>
        public int Never
        {
            get { return _never; }
        }

        /// <summary>v1.5 退役：恒为 0（以前是「模板档限额用尽被丢弃」的条数）。</summary>
        public long TemplateSkipped
        {
            get { return 0L; }
        }

        /// <summary>v1.5 退役：恒为 0（以前是「超过 MaxEntries 被丢弃」的次数）。</summary>
        public long Overflow
        {
            get { return 0L; }
        }

        /// <summary>v1.5 退役：恒为 0（以前是「单个 caller 配额用尽被丢弃」的条数）。</summary>
        public long CallerSkipped
        {
            get { return 0L; }
        }

        /// <summary>正文因会话字节预算用尽而没留的条数（D-T2 允许的第二类丢弃，会被日志通报一次）。</summary>
        public long BudgetSkipped
        {
            get { return _budgetSkipped; }
        }

        /// <summary>正文已落盘的 hash 之后又被 miss 的次数（只累加计数；不改已写出的行）。</summary>
        public long LateRepeats
        {
            get { return _lateRepeats; }
        }

        /// <summary>截断写入（<see cref="CaptureRecord.Truncated"/>）的条数。</summary>
        public int TruncatedCount
        {
            get { return _truncated; }
        }

        /// <summary>v1.5 退役：恒为 false（以前表示「已触顶，新原文一律丢弃」）。</summary>
        public bool CapReached
        {
            get { return false; }
        }

        /// <summary>最近一次写盘失败的原因（成功后清空）。</summary>
        public string LastWriteError
        {
            get { return _lastWriteError; }
        }

        /// <summary>
        /// 记一次未命中：首次出现时建记录并取调用栈，重复出现时只累加计数。
        /// 返回 true 表示新建了一条带正文的 distinct 记录；<b>只有</b>
        /// <see cref="CaptureOptions.Enabled"/> 关掉或正文因字节预算用尽而没留时才返回 false。
        /// </summary>
        public bool Record(ulong hash, string source, string normalized, string sink)
        {
            return RecordCore(hash, source, normalized, sink, null, null, true);
        }

        /// <summary>
        /// 记一次未命中，<paramref name="caller"/> / <paramref name="firstSeen"/> 由调用方给定（单测用，避免依赖真实栈与时钟）。
        /// </summary>
        public bool Record(ulong hash, string source, string normalized, string sink, string caller, string firstSeen)
        {
            return RecordCore(hash, source, normalized, sink, caller, firstSeen, false);
        }

        /// <summary>
        /// 重复未命中计数（缓存里已知的 miss 走这条，零分配）：不认识的 hash 一律忽略。
        /// 正文还在待写缓冲里的，直接改记录的 <see cref="CaptureRecord.Count"/>；
        /// 正文已经落盘的，只累加 <see cref="LateRepeats"/>（不回改文件）。
        /// </summary>
        public void CountMiss(ulong hash)
        {
            int slot;
            if (!_index.TryGetValue(hash, out slot))
            {
                return;
            }

            if (slot >= 0)
            {
                _pending[slot].Count++;
                return;
            }

            _lateRepeats++;
        }

        /// <summary>v1.5 退役：恒返回 false（不再有「触顶」这回事）。保留方法只为外部调用点不炸。</summary>
        public bool ConsumeCapNotice()
        {
            return false;
        }

        /// <summary>
        /// 字节预算用尽后只允许日志打一次：第一次调用返回 true，之后返回 false。
        /// </summary>
        public bool ConsumeBudgetNotice()
        {
            if (!_budgetNoticePending)
            {
                return false;
            }

            _budgetNoticePending = false;
            return true;
        }

        /// <summary>把还没写过的记录序列化成 JSONL 行（不含换行；供单测与写盘共用）。</summary>
        public string[] PendingJsonLines()
        {
            if (_pending.Count == 0)
            {
                return NoLines;
            }

            string[] lines = new string[_pending.Count];
            for (int i = 0; i < _pending.Count; i++)
            {
                lines[i] = CaptureJson.ToJsonLine(_pending[i]);
            }

            return lines;
        }

        /// <summary>
        /// 把还没写过的记录追加到 <paramref name="path"/>（LF 结尾、无 BOM；目录不存在会建）。
        /// 返回本批「消化掉」的条数（写出的 + 因字节预算丢弃的）；失败返回 0 并记 <see cref="LastWriteError"/>，<b>绝不抛</b>。
        /// 只有真正写成功才清空待写缓冲，所以失败后下次会重试同一批。
        /// </summary>
        public int WritePending(string path)
        {
            if (_pending.Count == 0)
            {
                return 0;
            }

            if (string.IsNullOrEmpty(path))
            {
                _lastWriteError = "empty path";
                return 0;
            }

            long budget = _options.MaxSessionBytes;
            int writtenNow = 0;
            int droppedByBudget = 0;
            long bytesNow = 0;

            try
            {
                StringBuilder sb = new StringBuilder(_pending.Count * 192);
                var flushed = new List<ulong>(_pending.Count);
                for (int i = 0; i < _pending.Count; i++)
                {
                    CaptureRecord record = _pending[i];
                    string line = CaptureJson.ToJsonLine(record);
                    int bytes = Utf8NoBom.GetByteCount(line) + 1;

                    if (budget > 0 && _sessionBytes + bytesNow + bytes > budget)
                    {
                        // D-T2 允许的第二类丢弃：停止写正文，只留计数 + 一次性日志。
                        _index[record.Hash] = IndexWritten;
                        droppedByBudget++;
                        continue;
                    }

                    sb.Append(line).Append('\n');
                    flushed.Add(record.Hash);
                    bytesNow += bytes;
                    writtenNow++;
                }

                if (writtenNow > 0)
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.AppendAllText(path, sb.ToString(), Utf8NoBom);
                }

                // 写出去的 hash 必须改标记成「正文已落盘」：否则 _pending.Clear() 之后
                // 旧下标还留在 _index 里，下一次 CountMiss 会拿越界下标去索引新的缓冲。
                for (int i = 0; i < flushed.Count; i++)
                {
                    _index[flushed[i]] = IndexWritten;
                }

                _sessionBytes += bytesNow;
                _written += writtenNow;
                if (droppedByBudget > 0)
                {
                    _budgetSkipped += droppedByBudget;
                    _budgetNoticePending = true;
                }

                _pending.Clear();
                _lastWriteError = null;
                return writtenNow + droppedByBudget;
            }
            catch (Exception ex)
            {
                _lastWriteError = ex.GetType().Name + ": " + ex.Message;
                return 0;
            }
        }

        /// <summary>待写缓冲的副本（单测用；按首次出现顺序）。</summary>
        public CaptureRecord[] ToArray()
        {
            return _pending.ToArray();
        }

        /// <summary>
        /// 统计行，形如 <c>capture: distinct=812, written=812</c>。
        /// v1.5 里 filtered / tmplSkipped / callerSkipped / overflow 恒为 0，所以不会出现；
        /// 有截断、预算丢弃、落盘后重复 miss 或已写出字节时追加对应后缀。
        /// </summary>
        public string Describe()
        {
            StringBuilder sb = new StringBuilder(64);
            sb.Append("capture: distinct=").Append(_index.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(", written=").Append(_written.ToString(CultureInfo.InvariantCulture));

            if (_index.Count > 0)
            {
                sb.Append(", candidate=").Append(_candidates.ToString(CultureInfo.InvariantCulture));
                sb.Append(", template=").Append(_templates.ToString(CultureInfo.InvariantCulture));
                sb.Append(", never=").Append(_never.ToString(CultureInfo.InvariantCulture));
            }

            if (_truncated > 0)
            {
                sb.Append(", truncated=").Append(_truncated.ToString(CultureInfo.InvariantCulture));
            }

            if (_budgetSkipped > 0)
            {
                sb.Append(", budgetSkipped=").Append(_budgetSkipped.ToString(CultureInfo.InvariantCulture));
            }

            if (_lateRepeats > 0)
            {
                sb.Append(", laterRepeats=").Append(_lateRepeats.ToString(CultureInfo.InvariantCulture));
            }

            if (_sessionBytes > 0)
            {
                sb.Append(", bytes=").Append(_sessionBytes.ToString(CultureInfo.InvariantCulture));
            }

            // v1.5：filtered / tmplSkipped / callerSkipped / overflow 四个计数已退役（恒 0），
            // 所以这一行里永远不会出现这四个后缀（老日志解析器按「有就显示」的写法仍然兼容）。
            return sb.ToString();
        }

        /// <summary>
        /// 不同的真实调用者个数（<b>不含</b>取不到栈的那些）。用来一眼判断「caller 修复是否生效」：
        /// 修好之后这个数应该是几十到几百，修坏（记成我们自己的补丁）时只会是 1~2。
        /// </summary>
        public int DistinctCallers
        {
            get { return _callers.Count; }
        }

        /// <summary>调用者取不到（空串 / <see cref="CaptureCaller.Unknown"/>）的记录条数。</summary>
        public int UnknownCallers
        {
            get { return _unknownCallers; }
        }

        /// <summary>
        /// 按「来自它的 distinct 文本条数」降序取前 <paramref name="count"/> 个调用者，
        /// 每项形如 <c>类型.方法 x12</c>；同条数按 <see cref="StringComparer.Ordinal"/> 升序（保证输出可复现）。
        /// </summary>
        public string[] TopCallers(int count)
        {
            if (count <= 0 || _callers.Count == 0)
            {
                return NoLines;
            }

            List<KeyValuePair<string, int>> items = new List<KeyValuePair<string, int>>(_callers);
            items.Sort(CompareCallers);

            int take = items.Count < count ? items.Count : count;
            string[] result = new string[take];
            for (int i = 0; i < take; i++)
            {
                result[i] = items[i].Key + " x" + items[i].Value.ToString(CultureInfo.InvariantCulture);
            }

            return result;
        }

        /// <summary>
        /// caller 聚合行，形如
        /// <c>capture callers: distinct=37, top5=[KSPWheel.KSPWheelGUI.OnWindow x9, …], unknown=2</c>。
        /// 退出 dump 里打一行就够判断 caller 修复是否生效，不必解 JSONL（栈落盘后无法回溯）。
        /// </summary>
        public string DescribeCallers()
        {
            string[] top = TopCallers(5);
            StringBuilder sb = new StringBuilder(96);
            sb.Append("capture callers: distinct=").Append(_callers.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(", top5=[");
            for (int i = 0; i < top.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(top[i]);
            }

            sb.Append("], unknown=").Append(_unknownCallers.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>捕获文件名：<c>capture-yyyyMMdd-HHmmss.jsonl</c>（传 <see cref="DateTime.UtcNow"/> 保证跨机器可比）。</summary>
        public static string BuildFileName(DateTime timestampUtc)
        {
            return "capture-" + timestampUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl";
        }

        private static int CompareCallers(KeyValuePair<string, int> left, KeyValuePair<string, int> right)
        {
            int byCount = right.Value.CompareTo(left.Value);
            if (byCount != 0)
            {
                return byCount;
            }

            return string.CompareOrdinal(left.Key, right.Key);
        }

        private static bool IsUnknownCaller(string caller)
        {
            if (string.IsNullOrEmpty(caller))
            {
                return true;
            }

            return string.Equals(caller, CaptureCaller.Unknown, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsBudgetExhausted()
        {
            long budget = _options.MaxSessionBytes;
            return budget > 0 && _sessionBytes >= budget;
        }

        /// <summary>
        /// 按 <see cref="CaptureOptions.MaxTextLength"/> 截断；<paramref name="truncated"/> 标明是否真的截了。
        /// 不切在代理对中间（否则写出去是半个字符，下游 JSON 还能圆回来但文本已经废了）。
        /// </summary>
        private string Truncate(string text, out bool truncated)
        {
            truncated = false;
            if (text == null)
            {
                return string.Empty;
            }

            int max = _options.MaxTextLength;
            if (max <= 0 || text.Length <= max)
            {
                return text;
            }

            int cut = max;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            {
                cut--;
            }

            truncated = true;
            return cut <= 0 ? string.Empty : text.Substring(0, cut);
        }

        private void TrackCaller(string callerValue)
        {
            if (IsUnknownCaller(callerValue))
            {
                _unknownCallers++;
                return;
            }

            int times;
            _callers.TryGetValue(callerValue, out times);
            _callers[callerValue] = times + 1;
        }

        private bool RecordCore(ulong hash, string source, string normalized, string sink, string caller, string firstSeen, bool captureCaller)
        {
            if (!_options.Enabled)
            {
                return false;
            }

            int slot;
            if (_index.TryGetValue(hash, out slot))
            {
                if (slot >= 0)
                {
                    _pending[slot].Count++;
                }
                else
                {
                    _lateRepeats++;
                }

                return false;
            }

            string callerValue = captureCaller ? CaptureCaller.FromStack(2) : (caller ?? string.Empty);
            string seen = firstSeen ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            bool truncatedSource;
            bool truncatedNormalized;
            string keptSource = Truncate(source, out truncatedSource);
            string keptNormalized = Truncate(normalized, out truncatedNormalized);
            bool truncated = truncatedSource || truncatedNormalized;

            // D-T2 的第二类丢弃：预算用尽后不再留正文，只记「见过这条」+ 计数，日志由 ConsumeBudgetNotice 通报一次。
            if (IsBudgetExhausted())
            {
                _index[hash] = IndexWritten;
                _budgetSkipped++;
                _budgetNoticePending = true;
                TrackCaller(callerValue);
                return false;
            }

            CaptureRecord record = new CaptureRecord(hash, keptSource, keptNormalized, sink, callerValue, seen, truncated);
            _index[hash] = _pending.Count;
            _pending.Add(record);

            // task-35：caller 聚合（每个 distinct 文本只加一次，重复 miss 不重复计权）
            TrackCaller(callerValue);

            if (truncated)
            {
                _truncated++;
            }

            // task-34 缺陷 3：分桶计数（数据一条不丢，只是给下游一个可读的分布）
            if (record.Bucket == CaptureClassify.BucketNever)
            {
                _never++;
            }
            else if (record.Bucket == CaptureClassify.BucketTemplate)
            {
                _templates++;
            }
            else
            {
                _candidates++;
            }

            return true;
        }
    }
}
