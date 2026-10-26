using System;
using System.Text;
using UI18n.Core.Capture;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// v1.5（docs/08 D-T2）：<b>单调用者配额退役</b>后的回归用例。
    /// <para>
    /// 缺陷由来（round 11 实测）：模板档单独限额之后，噪声从「进度条」挪到了「某个大窗口」——
    /// <c>ProfilerUI.DrawPluginsContent</c> 一个 caller 独占 130 条、<c>LoadingScreen</c> 系列合计 3,028 条。
    /// v1.3 的修法是按 caller 再分一层额度（<see cref="CaptureOptions.MaxPerCaller"/>，默认 60），
    /// 但那是同一种病：为了「公平」把正文丢掉，等于主动放弃「还剩多少英文」这个唯一要回答的问题。
    /// </para>
    /// <para>
    /// v1.5 的口径：<b>任何 caller 都一条不丢</b>（<c>CallerSkipped</c> 恒 0），
    /// 强弱只体现在 candidates.jsonl 的 bucket 与 count 上，不体现在「录不录」上。
    /// 字段本身保留，只为老配置仍能解析。
    /// </para>
    /// </summary>
    public class CaptureCallerQuotaTests
    {
        private const string Seen = "2026-10-26T00:32:05Z";
        private const string Sink = "TMPro.TMP_Text.set_text";
        private const string Caller = "LoadingScreen.UpdateProgressBar";
        private const string OtherCaller = "AJE.AJEAtmTools:UpdateInfo";

        /// <summary>刻意把三个退役上限压到很小，用来证明它们<b>不再</b>影响取舍。</summary>
        private static CaptureStore Store(int maxPerCaller)
        {
            var options = CaptureOptions.Default;
            options.MaxEntries = 5000;
            options.MaxTemplateEntries = -1;
            options.MaxPerCaller = maxPerCaller;
            return new CaptureStore(options);
        }

        /// <summary>不带数字的候选文本（避免被分到模板档），<c>1 -> A</c>、<c>27 -> AA</c>：保证 hash 互不相同。</summary>
        private static string Text(int i)
        {
            var sb = new StringBuilder("Vessel mass ");
            int n = i;
            var letters = new StringBuilder();
            while (n > 0)
            {
                int r = (n - 1) % 26;
                letters.Insert(0, (char)('A' + r));
                n = (n - 1) / 26;
            }

            return sb.Append(letters).ToString();
        }

        /// <summary>配额设成 2：同一 caller 的第 3 条照样留档，<c>CallerSkipped</c> 恒 0。</summary>
        [Fact]
        public void QuotaRetired_SameCallerIsNotThrottled()
        {
            CaptureStore store = Store(2);

            Assert.True(store.Record(1UL, Text(1), Text(1), Sink, Caller, Seen));
            Assert.True(store.Record(2UL, Text(2), Text(2), Sink, Caller, Seen));

            // v1.3 这里返回 false；v1.5 必须收。
            Assert.True(store.Record(3UL, Text(3), Text(3), Sink, Caller, Seen));

            Assert.Equal(3, store.Distinct);
            Assert.Equal(0L, store.CallerSkipped);
            Assert.DoesNotContain("callerSkipped", store.Describe(), StringComparison.Ordinal);

            // 重复上报仍然只累加 count（不是新建桶），也不许把 skipped 拉起来。
            Assert.False(store.Record(3UL, Text(3), Text(3), Sink, Caller, Seen));
            Assert.Equal(3, store.Distinct);
            Assert.Equal(2, store.ToArray()[2].Count);
            Assert.Equal(0L, store.CallerSkipped);
        }

        /// <summary>多个 caller 之间也不再互相影响：两个界面各 3 条，全部留档。</summary>
        [Fact]
        public void QuotaRetired_EveryCallerIsRecorded()
        {
            CaptureStore store = Store(2);

            Assert.True(store.Record(1UL, Text(1), Text(1), Sink, Caller, Seen));
            Assert.True(store.Record(2UL, Text(2), Text(2), Sink, Caller, Seen));
            Assert.True(store.Record(3UL, Text(3), Text(3), Sink, Caller, Seen));

            Assert.True(store.Record(4UL, Text(4), Text(4), Sink, OtherCaller, Seen));
            Assert.True(store.Record(5UL, Text(5), Text(5), Sink, OtherCaller, Seen));
            Assert.True(store.Record(6UL, Text(6), Text(6), Sink, OtherCaller, Seen));

            Assert.Equal(6, store.Distinct);
            Assert.Equal(0L, store.CallerSkipped);
            Assert.Equal(2, store.DistinctCallers);
        }

        /// <summary>取不到调用栈（<see cref="CaptureCaller.Unknown"/> / 空串）的串照样留档，计入 unknown。</summary>
        [Fact]
        public void UnknownCaller_IsRecordedToo()
        {
            CaptureStore store = Store(1);

            for (int i = 1; i <= 5; i++)
            {
                Assert.True(store.Record((ulong)i, Text(i), Text(i), Sink, CaptureCaller.Unknown, Seen));
            }

            for (int i = 6; i <= 10; i++)
            {
                Assert.True(store.Record((ulong)i, Text(i), Text(i), Sink, string.Empty, Seen));
            }

            Assert.Equal(10, store.Distinct);
            Assert.Equal(10, store.UnknownCallers);
            Assert.Equal(0L, store.CallerSkipped);
            Assert.DoesNotContain("callerSkipped", store.Describe(), StringComparison.Ordinal);
        }

        /// <summary><c>0</c> 与负数在退役后没有语义差别：都只是「字段值」，怎么设都全收。</summary>
        [Fact]
        public void ZeroAndNegative_HaveNoEffectAnyMore()
        {
            CaptureStore zero = Store(0);
            CaptureStore negative = Store(-1);

            for (int i = 1; i <= 70; i++)
            {
                Assert.True(zero.Record((ulong)i, Text(i), Text(i), Sink, Caller, Seen));
                Assert.True(negative.Record((ulong)i, Text(i), Text(i), Sink, Caller, Seen));
            }

            Assert.Equal(70, zero.Distinct);
            Assert.Equal(70, negative.Distinct);
            Assert.Equal(0L, zero.CallerSkipped);
            Assert.Equal(0L, negative.CallerSkipped);
        }

        /// <summary>默认值 60 仍然钉住（配置兼容），但第 61 条照收。</summary>
        [Fact]
        public void Default_IsSixty_ButNoLongerThrottles()
        {
            Assert.Equal(60, CaptureOptions.Default.MaxPerCaller);

            CaptureStore store = new CaptureStore(CaptureOptions.Default);

            for (int i = 1; i <= 61; i++)
            {
                Assert.True(store.Record((ulong)i, Text(i), Text(i), Sink, Caller, Seen));
            }

            Assert.Equal(61, store.Distinct);
            Assert.Equal(0L, store.CallerSkipped);
        }

        /// <summary>实测形态：一个窗口一次吐出 5,000 条，照样一条不丢。</summary>
        [Fact]
        public void SingleCallerBurst_IsFullyRecorded()
        {
            CaptureStore store = Store(2);

            for (int i = 1; i <= 5000; i++)
            {
                Assert.True(store.Record((ulong)i, Text(i), Text(i), Sink, Caller, Seen));
            }

            Assert.Equal(5000, store.Distinct);
            Assert.Equal(0L, store.CallerSkipped);

            // 5,000 条全挂在同一个 caller 上：caller 聚合照旧工作（配额退役不影响统计）。
            Assert.Equal("LoadingScreen.UpdateProgressBar x5000", store.TopCallers(1)[0]);
        }

        /// <summary>Clone() 必须带上字段（老调用点仍按它调参，漏了会静默改变配置语义）。</summary>
        [Fact]
        public void Clone_CopiesMaxPerCaller()
        {
            var options = CaptureOptions.Default;
            options.MaxPerCaller = 7;

            CaptureOptions copy = options.Clone();

            Assert.Equal(7, copy.MaxPerCaller);
            Assert.NotSame(options, copy);
        }
    }
}
