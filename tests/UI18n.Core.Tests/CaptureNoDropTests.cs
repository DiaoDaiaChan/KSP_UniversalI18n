using System;
using System.Globalization;
using System.IO;
using System.Text;
using UI18n.Core.Capture;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// v1.5（docs/08 D-T2「捕获端不丢正文、过滤只决定优先级」）的核心证据用例。
    /// <para>
    /// 允许的丢弃<b>只有两种</b>，且都必须留痕：
    /// ①文本超过 <see cref="CaptureOptions.MaxTextLength"/>（默认 8000）→ 截断写入并标
    /// <c>"truncated": true</c>；②本会话已写入字节超过 <see cref="CaptureOptions.MaxSessionBytes"/>
    /// （默认 64 MiB）→ 停止写正文，只累加 <see cref="CaptureStore.BudgetSkipped"/> 并让日志通报一次。
    /// 除此之外，<b>任何输入都必须建记录</b>。
    /// </para>
    /// <para>
    /// 老的四道闸门（<c>CaptureFilter</c> 拒收 / 模板档限额 / 总条数上限 / caller 配额）已退役，
    /// <see cref="CaptureStore.Filtered"/>、<see cref="CaptureStore.Overflow"/>、
    /// <see cref="CaptureStore.TemplateSkipped"/>、<see cref="CaptureStore.CallerSkipped"/>
    /// 在 v1.5 里恒为 0 —— 本文件的端到端用例就是钉这个恒等式。
    /// </para>
    /// </summary>
    public class CaptureNoDropTests
    {
        private const string Seen = "2026-10-26T00:32:05Z";
        private const string Sink = "TMPro.TMP_Text.set_text";

        /// <summary>
        /// 端到端核心证据：20,000 条互不相同的假 miss（含 path 形态、含一条 3,000 字符长文本、
        /// 含同一 caller 5,000 条）——<b>全部</b>被记录，四个退役计数全 0。
        /// <para>
        /// 若还留着 v1.3 的任一道闸门，这条用例会立刻红：模板档 4,999 条会在 200 条处截断、
        /// 单 caller 那 5,000 条会在 60 条处截断、path 形态会被 <c>CaptureFilter</c> 整批拒收。
        /// </para>
        /// </summary>
        [Fact]
        public void EndToEnd_TwentyThousandDistinctMisses_AreAllRecorded()
        {
            const string BurstCaller = "ProfilerUI.DrawPluginsContent";
            const ulong LongHash = 20000UL;
            const int Count = 20000;

            var store = new CaptureStore(CaptureOptions.Default);

            int accepted = 0;
            for (ulong i = 1; i <= Count - 1; i++)
            {
                string text;
                string caller;
                switch (i % 4UL)
                {
                    case 0UL:   // path 形态：v1.3 会被 CaptureFilter 判 identifier/path 直接拒收
                        text = "ReStockPlus/Parts/Control/RCS/panel" + i.ToString(CultureInfo.InvariantCulture);
                        caller = "PartListTooltip.Setup";
                        break;

                    case 1UL:   // 同一个 caller 连报 5,000 条：v1.3 会在第 61 条开始丢
                        text = "Profiler row label " + Letters(i);
                        caller = BurstCaller;
                        break;

                    case 2UL:   // 普通候选档
                        text = "Vessel mass " + Letters(i);
                        caller = "EditorUI.Update";
                        break;

                    default:    // 不带数字的散文，同样是候选档
                        text = "Science report body " + Letters(i);
                        caller = "RDTreeWidget.DrawNode";
                        break;
                }

                if (store.Record(i, text, text, Sink, caller, Seen))
                {
                    accepted++;
                }
            }

            // 第 20,000 条：3,000 字符的长正文 —— 未超 MaxTextLength(8000)，所以原文照收。
            string longText = "Long capture probe " + new string('x', 3000);
            if (store.Record(LongHash, longText, longText, Sink, "RDTechTree.DrawNode", Seen))
            {
                accepted++;
            }

            Assert.Equal(Count, accepted);
            Assert.Equal(Count, store.Distinct);
            Assert.Equal(Count, store.Pending);
            Assert.Equal(Count, store.ToArray().Length);

            // ① 四个退役计数必须全 0
            Assert.Equal(0L, store.Filtered);
            Assert.Equal(0L, store.Overflow);
            Assert.Equal(0L, store.TemplateSkipped);
            Assert.Equal(0L, store.CallerSkipped);
            Assert.False(store.CapReached);

            // ② 本批没有超长串，所以一条 truncate 都没有；也没有预算丢弃
            Assert.Equal(0, store.TruncatedCount);
            Assert.Equal(0L, store.BudgetSkipped);
            Assert.Equal(0L, store.LateRepeats);

            // ③ 3,000 字符那条原样留着（没被截断）
            CaptureRecord longRecord = Find(store, LongHash);
            Assert.False(longRecord.Truncated);
            Assert.Equal(longText.Length, longRecord.Normalized.Length);

            // ④ 5,000 条同 caller 的串确实都在（bucket 只影响优先级）
            string describe = store.Describe();
            Assert.Contains("distinct=20000", describe, StringComparison.Ordinal);
            Assert.DoesNotContain("filtered", describe, StringComparison.Ordinal);
            Assert.DoesNotContain("callerSkipped", describe, StringComparison.Ordinal);
            Assert.DoesNotContain("tmplSkipped", describe, StringComparison.Ordinal);

            // ⑤ 真写盘也是 20,000 行（一条都不许在落盘这一层消失）
            string path = TempFile();
            try
            {
                Assert.Equal(Count, store.WritePending(path));
                Assert.Equal(Count, store.Written);
                Assert.Equal(0, store.Pending);
                Assert.Equal(Count, File.ReadAllLines(path).Length);
            }
            finally
            {
                Delete(path);
            }

            // 诊断行（跑 `dotnet test -l "console;verbosity=detailed"` 时能看到；也便于回归时对照）
            Console.WriteLine(
                "[no-drop] records=" + store.Distinct.ToString(CultureInfo.InvariantCulture)
                + " written=" + store.Written.ToString(CultureInfo.InvariantCulture)
                + " truncated=" + store.TruncatedCount.ToString(CultureInfo.InvariantCulture)
                + " filtered=" + store.Filtered.ToString(CultureInfo.InvariantCulture)
                + " overflow=" + store.Overflow.ToString(CultureInfo.InvariantCulture)
                + " tmplSkipped=" + store.TemplateSkipped.ToString(CultureInfo.InvariantCulture)
                + " callerSkipped=" + store.CallerSkipped.ToString(CultureInfo.InvariantCulture)
                + " budgetSkipped=" + store.BudgetSkipped.ToString(CultureInfo.InvariantCulture)
                + " laterRepeats=" + store.LateRepeats.ToString(CultureInfo.InvariantCulture)
                + " bytes=" + store.SessionBytes.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>超长文本：截断到 <see cref="CaptureOptions.MaxTextLength"/>，标 <c>Truncated</c>，JSON 末尾带 <c>truncated</c>。</summary>
        [Fact]
        public void LongText_IsTruncatedAndFlagged()
        {
            var store = new CaptureStore();
            string text = new string('a', 9000);

            Assert.True(store.Record(1UL, text, text, Sink, "A.B", Seen));

            CaptureRecord record = store.ToArray()[0];
            Assert.True(record.Truncated);
            Assert.Equal(8000, record.Normalized.Length);
            Assert.Equal(8000, record.Source.Length);
            Assert.Equal(1, store.TruncatedCount);

            string line = CaptureJson.ToJsonLine(record);
            Assert.Contains("\"truncated\":true", line, StringComparison.Ordinal);
            Assert.EndsWith("}", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// 不截断的行必须与 v1.4 的字段顺序<b>逐字节相同</b>（<c>truncated</c> 只在 true 时追加）。
        /// 这条钉住 <c>packs/capture-candidates.jsonl</c> 的下游解析器不被格式变动打红。
        /// </summary>
        [Fact]
        public void UntruncatedRow_IsByteIdenticalToLegacyFormat()
        {
            var record = new CaptureRecord(1UL, "Vessel mass", "Vessel mass", Sink, "A.B", Seen);
            string fromRecord = CaptureJson.ToJsonLine(record);
            string legacy = CaptureJson.ToJsonLine(1UL, "Vessel mass", "Vessel mass", Sink, "A.B", 1, Seen);

            Assert.Equal(legacy, fromRecord);
            Assert.DoesNotContain("truncated", fromRecord, StringComparison.Ordinal);
            Assert.False(record.Truncated);
        }

        /// <summary>截断不许切在代理对中间（会写出半个 emoji）。</summary>
        [Fact]
        public void Truncation_NeverSplitsSurrogatePair()
        {
            var store = new CaptureStore();
            string text = new string('a', 7999) + "\uD83D\uDE00" + new string('b', 10);

            Assert.True(store.Record(1UL, text, text, Sink, "A.B", Seen));

            CaptureRecord record = store.ToArray()[0];
            Assert.True(record.Truncated);
            Assert.Equal(7999, record.Normalized.Length);
            Assert.False(char.IsHighSurrogate(record.Normalized[record.Normalized.Length - 1]));
        }

        /// <summary>
        /// 会话字节预算用尽：停止写正文但<b>继续计数</b>，并且日志只通报一次
        /// （<see cref="CaptureStore.ConsumeBudgetNotice"/> 第一次 true、第二次 false）。
        /// </summary>
        [Fact]
        public void SessionBudget_StopsBody_KeepsCounting_AndNotifiesOnce()
        {
            string text = "Budget probe A";
            var probe = new CaptureStore();
            probe.Record(1UL, text, text, Sink, "A.B", Seen);
            long lineBytes = Encoding.UTF8.GetByteCount(CaptureJson.ToJsonLine(probe.ToArray()[0])) + 1;

            var options = CaptureOptions.Default;
            options.MaxSessionBytes = lineBytes;   // 刚好放得下一行
            var store = new CaptureStore(options);

            Assert.True(store.Record(1UL, text, text, Sink, "A.B", Seen));
            Assert.True(store.Record(2UL, "Budget probe B", "Budget probe B", Sink, "A.B", Seen));
            Assert.True(store.Record(3UL, "Budget probe C", "Budget probe C", Sink, "A.B", Seen));

            string path = TempFile();
            try
            {
                // 一行写出，另外两行因预算被丢（D-T2 允许的第二类丢弃，必须留痕）
                Assert.Equal(3, store.WritePending(path));
                Assert.Equal(1, store.Written);
                Assert.Equal(2L, store.BudgetSkipped);
                Assert.Equal(lineBytes, store.SessionBytes);
                Assert.Single(File.ReadAllLines(path));

                // 预算已用尽：新串不再留正文，但计入 Distinct 与 BudgetSkipped
                Assert.False(store.Record(4UL, "Budget probe D", "Budget probe D", Sink, "A.B", Seen));
                Assert.Equal(4, store.Distinct);
                Assert.Equal(3L, store.BudgetSkipped);

                Assert.True(store.ConsumeBudgetNotice());
                Assert.False(store.ConsumeBudgetNotice());

                string describe = store.Describe();
                Assert.Contains("budgetSkipped=3", describe, StringComparison.Ordinal);
                Assert.Contains("bytes=", describe, StringComparison.Ordinal);

                // 仍为零的只有四个退役计数：预算丢弃是另一类，故意不混进它们
                Assert.Equal(0L, store.Filtered);
                Assert.Equal(0L, store.CallerSkipped);
            }
            finally
            {
                Delete(path);
            }
        }

        /// <summary>落盘之后再来的重复 miss 只累加 <see cref="CaptureStore.LateRepeats"/>，不重建记录。</summary>
        [Fact]
        public void LateRepeat_AfterFlush_IsCountedNotRecorded()
        {
            var store = new CaptureStore();
            Assert.True(store.Record(1UL, "Pre patch init", "Pre patch init", Sink, "A.B", Seen));

            string path = TempFile();
            try
            {
                Assert.Equal(1, store.WritePending(path));
            }
            finally
            {
                Delete(path);
            }

            store.CountMiss(1UL);
            store.CountMiss(1UL);

            Assert.Equal(1, store.Distinct);
            Assert.Equal(2L, store.LateRepeats);
            Assert.Equal(0, store.Pending);
        }

        /// <summary>写盘前后重复上报都只累加 count（<c>CountMiss</c> 不会凭空造记录）。</summary>
        [Fact]
        public void CountMiss_NeverCreatesRecords()
        {
            var store = new CaptureStore();

            // 没见过这个 hash：不计记录，也不计入 laterRepeats（那一条从未入过索引，谈不上「迟到」）
            store.CountMiss(42UL);
            Assert.Equal(0, store.Distinct);
            Assert.Equal(0L, store.LateRepeats);

            Assert.True(store.Record(1UL, "Vessel mass", "Vessel mass", Sink, "A.B", Seen));
            store.CountMiss(1UL);
            store.CountMiss(1UL);

            Assert.Equal(1, store.Distinct);
            Assert.Equal(3, store.ToArray()[0].Count);
        }

        /// <summary>
        /// <c>CaptureFilter</c> 的判据还在、还能算（下游统计仍用它的 reason 词表），
        /// 但<b>不再拦任何串</b>：判成 already-cjk / identifier 的照样建记录。
        /// </summary>
        [Fact]
        public void CaptureFilter_IsAdvisoryOnly()
        {
            var options = CaptureOptions.Default;

            Assert.Equal("already-cjk", CaptureFilter.RejectReason("设置", "设置", options));
            Assert.Equal("identifier", CaptureFilter.RejectReason("abc", "abc", options));

            var store = new CaptureStore(options);
            Assert.True(store.Record(1UL, "设置", "设置", Sink, "A.B", Seen));
            Assert.True(store.Record(2UL, "abc", "abc", Sink, "A.B", Seen));
            Assert.Equal(2, store.Distinct);
            Assert.Equal(0L, store.Filtered);
        }

        /// <summary>v1.5 的两个新参数：默认值 + <c>Clone()</c> 复制 + 配置文件模型同步。</summary>
        [Fact]
        public void NewOptions_HaveDefaults_AndCloneCopies()
        {
            Assert.Equal(8000, CaptureOptions.Default.MaxTextLength);
            Assert.Equal(64L * 1024 * 1024, CaptureOptions.Default.MaxSessionBytes);

            var options = CaptureOptions.Default;
            options.MaxTextLength = 1234;
            options.MaxSessionBytes = 5678L;

            CaptureOptions copy = options.Clone();
            Assert.Equal(1234, copy.MaxTextLength);
            Assert.Equal(5678L, copy.MaxSessionBytes);
            Assert.NotSame(options, copy);

            var config = new UI18n.Core.Config.UI18nConfig();
            Assert.Equal(8000, config.CaptureMaxTextLength);
            Assert.Equal(64L * 1024 * 1024, config.CaptureMaxSessionBytes);
        }

        // ---------------- helpers ----------------

        /// <summary>1 -> A、26 -> Z、27 -> AA：只含字母，确保被判成 candidate 而不是模板档。</summary>
        private static string Letters(ulong value)
        {
            var sb = new StringBuilder();
            ulong n = value;
            while (n > 0)
            {
                ulong r = (n - 1) % 26;
                sb.Insert(0, (char)('A' + (int)r));
                n = (n - 1) / 26;
            }

            return sb.Length == 0 ? "A" : sb.ToString();
        }

        private static CaptureRecord Find(CaptureStore store, ulong hash)
        {
            CaptureRecord[] all = store.ToArray();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Hash == hash)
                {
                    return all[i];
                }
            }

            throw new InvalidOperationException("record not found: " + hash.ToString("x16", CultureInfo.InvariantCulture));
        }

        private static string TempFile()
        {
            return Path.Combine(Path.GetTempPath(), "ui18n-capture-" + Guid.NewGuid().ToString("n") + ".jsonl");
        }

        private static void Delete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 临时文件清理失败不该让用例红；上层目录会被系统回收。
            }
        }
    }
}
