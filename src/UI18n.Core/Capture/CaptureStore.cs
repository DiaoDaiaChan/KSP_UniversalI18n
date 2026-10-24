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
        private const int IndexFiltered = -1;
        private const int IndexOverflow = -2;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly string[] NoLines = new string[0];

        private readonly CaptureOptions _options;
        private readonly Dictionary<ulong, int> _index;
        private readonly List<CaptureRecord> _records;

        // task-35：按 caller 聚合（只统计 distinct 记录，重复 miss 不会增加权重 —— 权重就是「有多少条不同文本来自它」）
        private readonly Dictionary<string, int> _callers;
        private int _unknownCallers;

        private long _filtered;
        private long _overflow;
        private int _candidates;
        private int _templates;
        private int _never;
        private bool _capReached;
        private bool _capNoticePending;
        private int _written;
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
            if (_options.MaxEntries < 0)
            {
                _options.MaxEntries = 0;
            }

            if (_options.MaxTextLength <= 0)
            {
                _options.MaxTextLength = 400;
            }

            if (_options.MinTextLength < 0)
            {
                _options.MinTextLength = 0;
            }

            int capacity = _options.MaxEntries > 0 ? Math.Min(_options.MaxEntries, 1024) : 16;
            _index = new Dictionary<ulong, int>(capacity);
            _records = new List<CaptureRecord>(capacity);
            _callers = new Dictionary<string, int>(16, StringComparer.Ordinal);
        }

        /// <summary>生效的选项（只读快照，改它不会影响已建实例的判定）。</summary>
        public CaptureOptions Options
        {
            get { return _options; }
        }

        /// <summary>已记录的 distinct 条数。</summary>
        public int Distinct
        {
            get { return _records.Count; }
        }

        /// <summary>已成功写盘的条数。</summary>
        public int Written
        {
            get { return _written; }
        }

        /// <summary>还没写盘的条数。</summary>
        public int Pending
        {
            get { return _records.Count - _written; }
        }

        /// <summary>被过滤规则拒掉的次数（含重复被拒）。</summary>
        public long Filtered
        {
            get { return _filtered; }
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

        /// <summary>超过 <see cref="CaptureOptions.MaxEntries"/> 后被丢弃的次数。</summary>
        public long Overflow
        {
            get { return _overflow; }
        }

        /// <summary>是否已触顶（触顶后新原文一律丢弃，只计 <see cref="Overflow"/>）。</summary>
        public bool CapReached
        {
            get { return _capReached; }
        }

        /// <summary>最近一次写盘失败的原因（成功后清空）。</summary>
        public string LastWriteError
        {
            get { return _lastWriteError; }
        }

        /// <summary>
        /// 记一次未命中：首次出现时建记录并取调用栈，重复出现时只累加计数。
        /// 返回 true 表示新建了一条 distinct 记录。
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
        /// 重复未命中计数（缓存里已知的 miss 走这条，零分配）：不认识或已被过滤/丢弃的 hash 一律忽略。
        /// </summary>
        public void CountMiss(ulong hash)
        {
            int index;
            if (!_index.TryGetValue(hash, out index))
            {
                return;
            }

            if (index >= 0)
            {
                _records[index].Count++;
                return;
            }

            if (index == IndexOverflow)
            {
                _overflow++;
            }
            else
            {
                _filtered++;
            }
        }

        /// <summary>
        /// 触顶后只允许日志打一次：第一次调用返回 true，之后返回 false。
        /// </summary>
        public bool ConsumeCapNotice()
        {
            if (!_capNoticePending)
            {
                return false;
            }

            _capNoticePending = false;
            return true;
        }

        /// <summary>把还没写过的记录序列化成 JSONL 行（不含换行；供单测与写盘共用）。</summary>
        public string[] PendingJsonLines()
        {
            int pending = _records.Count - _written;
            if (pending <= 0)
            {
                return NoLines;
            }

            string[] lines = new string[pending];
            for (int i = 0; i < pending; i++)
            {
                lines[i] = CaptureJson.ToJsonLine(_records[_written + i]);
            }

            return lines;
        }

        /// <summary>
        /// 把还没写过的记录追加到 <paramref name="path"/>（LF 结尾、无 BOM；目录不存在会建）。
        /// 返回写入的条数；失败返回 0 并记 <see cref="LastWriteError"/>，<b>绝不抛</b>。
        /// 只有真正写成功才推进 <see cref="Written"/>，所以失败后下次会重试同一批。
        /// </summary>
        public int WritePending(string path)
        {
            int pending = _records.Count - _written;
            if (pending <= 0)
            {
                return 0;
            }

            if (string.IsNullOrEmpty(path))
            {
                _lastWriteError = "empty path";
                return 0;
            }

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                StringBuilder sb = new StringBuilder(pending * 192);
                for (int i = 0; i < pending; i++)
                {
                    sb.Append(CaptureJson.ToJsonLine(_records[_written + i])).Append('\n');
                }

                File.AppendAllText(path, sb.ToString(), Utf8NoBom);
                _written = _records.Count;
                _lastWriteError = null;
                return pending;
            }
            catch (Exception ex)
            {
                _lastWriteError = ex.GetType().Name + ": " + ex.Message;
                return 0;
            }
        }

        /// <summary>已记录的副本（单测用；按首次出现顺序）。</summary>
        public CaptureRecord[] ToArray()
        {
            return _records.ToArray();
        }

        /// <summary>统计行，形如 <c>capture: distinct=812, written=812</c>（有过滤/丢弃/触顶时追加后缀）。</summary>
        public string Describe()
        {
            StringBuilder sb = new StringBuilder(64);
            sb.Append("capture: distinct=").Append(_records.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(", written=").Append(_written.ToString(CultureInfo.InvariantCulture));

            if (_records.Count > 0)
            {
                sb.Append(", candidate=").Append(_candidates.ToString(CultureInfo.InvariantCulture));
                sb.Append(", template=").Append(_templates.ToString(CultureInfo.InvariantCulture));
                sb.Append(", never=").Append(_never.ToString(CultureInfo.InvariantCulture));
            }

            if (_filtered > 0)
            {
                sb.Append(", filtered=").Append(_filtered.ToString(CultureInfo.InvariantCulture));
            }

            if (_overflow > 0)
            {
                sb.Append(", overflow=").Append(_overflow.ToString(CultureInfo.InvariantCulture));
            }

            if (_capReached)
            {
                sb.Append(", cap=reached");
            }

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

        private bool RecordCore(ulong hash, string source, string normalized, string sink, string caller, string firstSeen, bool captureCaller)
        {
            if (!_options.Enabled)
            {
                return false;
            }

            int index;
            if (_index.TryGetValue(hash, out index))
            {
                if (index >= 0)
                {
                    _records[index].Count++;
                }
                else if (index == IndexOverflow)
                {
                    _overflow++;
                }
                else
                {
                    _filtered++;
                }

                return false;
            }

            string reason = CaptureFilter.RejectReason(normalized, source, _options);
            if (reason != null)
            {
                _index[hash] = IndexFiltered;
                _filtered++;
                return false;
            }

            if (_options.MaxEntries > 0 && _records.Count >= _options.MaxEntries)
            {
                _index[hash] = IndexOverflow;
                _overflow++;
                if (!_capReached)
                {
                    _capReached = true;
                    _capNoticePending = true;
                }

                return false;
            }

            string callerValue = captureCaller ? CaptureCaller.FromStack(2) : (caller ?? string.Empty);
            string seen = firstSeen ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            CaptureRecord record = new CaptureRecord(hash, source, normalized, sink, callerValue, seen);
            _index[hash] = _records.Count;
            _records.Add(record);

            // task-35：caller 聚合（每个 distinct 文本只加一次，重复 miss 不重复计权）
            if (IsUnknownCaller(callerValue))
            {
                _unknownCallers++;
            }
            else
            {
                int times;
                _callers.TryGetValue(callerValue, out times);
                _callers[callerValue] = times + 1;
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
