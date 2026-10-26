using System;
using System.Text;
using UI18n.Core.Capture;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// v1.3：<b>单个调用者</b>的捕获配额（<see cref="CaptureOptions.MaxPerCaller"/>）回归用例。
    /// <para>
    /// 缺陷由来（round 11 实测）：模板档单独限额之后，噪声从「进度条」挪到了「某个大窗口」——
    /// <c>ProfilerUI.DrawPluginsContent</c> 一个 caller 独占 130 条、
    /// <c>LoadingScreen</c> 系列合计 3,028 条，候选档 5,000 条额度被几个调用者吃光，
    /// 而采集本来要回答的是「<b>哪些界面</b>缺翻译」。所以按 caller 再分一层额度，
    /// 让每个界面都有份，单个界面不能垄断。
    /// </para>
    /// <para>
    /// 两条边界必须钉住：<b>取不到栈的 caller 豁免</b>（那些记录本来就少，且是唯一线索），
    /// 以及 <c>0</c> / 负数 = 不限制（关掉这层额度不用改代码）。
    /// </para>
    /// </summary>
    public class CaptureCallerQuotaTests
    {
        private const string Seen = "2026-10-26T00:32:05Z";
        private const string Sink = "TMPro.TMP_Text.set_text";
        private const string Caller = "LoadingScreen.UpdateProgressBar";
        private const string OtherCaller = "AJE.AJEAtmTools:UpdateInfo";

        private static CaptureStore Store(int maxPerCaller)
        {
            var options = CaptureOptions.Default;
            options.MaxEntries = 5000;
            options.MaxTemplateEntries = -1;                 // 本组用例只管 caller 额度：先关掉模板档
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

        /// <summary>到顶后同一 caller 的新文本被丢，计 callerSkipped；已有的 distinct 数不变。</summary>
        [Fact]
        public void Quota_DropsBeyondCap_AndCountsSkips()
        {
            CaptureStore store = Store(2);

            Assert.True(store.Record(1UL, Text(1), Text(1), Sink, Caller, Seen));
            Assert.True(store.Record(2UL, Text(2), Text(2), Sink, Caller, Seen));
            Assert.False(store.Record(3UL, Text(3), Text(3), Sink, Caller, Seen));

            Assert.Equal(2, store.Distinct);
            Assert.Equal(1L, store.CallerSkipped);
            Assert.Contains("callerSkipped=1", store.Describe(), StringComparison.Ordinal);

            // 被丢的那条不建记录：再报一次还是 false，但只有第一次计入 skipped
            Assert.False(store.Record(3UL, Text(3), Text(3), Sink, Caller, Seen));
            Assert.Equal(1L, store.CallerSkipped);
        }

        /// <summary>额度是<b>按 caller</b> 的：一个界面吃满不影响另一个界面。</summary>
        [Fact]
        public void Quota_IsPerCaller()
        {
            CaptureStore store = Store(2);

            Assert.True(store.Record(1UL, Text(1), Text(1), Sink, Caller, Seen));
            Assert.True(store.Record(2UL, Text(2), Text(2), Sink, Caller, Seen));
            Assert.False(store.Record(3UL, Text(3), Text(3), Sink, Caller, Seen));

            Assert.True(store.Record(4UL, Text(4), Text(4), Sink, OtherCaller, Seen));
            Assert.True(store.Record(5UL, Text(5), Text(5), Sink, OtherCaller, Seen));
            Assert.False(store.Record(6UL, Text(6), Text(6), Sink, OtherCaller, Seen));

            Assert.Equal(4, store.Distinct);
            Assert.Equal(2L, store.CallerSkipped);
            Assert.Equal(2, store.DistinctCallers);
        }

        /// <summary>取不到调用栈（<see cref="CaptureCaller.Unknown"/> / 空串）一律豁免 —— 不许因为限额丢掉唯一的线索。</summary>
        [Fact]
        public void UnknownCaller_IsExemptFromQuota()
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
            Assert.Equal(0L, store.CallerSkipped);
            Assert.DoesNotContain("callerSkipped", store.Describe(), StringComparison.Ordinal);
        }

        /// <summary><c>0</c> 与负数都表示「不限制」（口径与 <see cref="CaptureOptions.MaxEntries"/> 一致）。</summary>
        [Fact]
        public void ZeroAndNegativeMeanUnlimited()
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

        /// <summary>默认额度 60：第 61 条被挡（实测单 caller 上百条就已明显垄断采集结果）。</summary>
        [Fact]
        public void Default_IsSixty()
        {
            Assert.Equal(60, CaptureOptions.Default.MaxPerCaller);

            CaptureStore store = new CaptureStore(CaptureOptions.Default);

            for (int i = 1; i <= 60; i++)
            {
                Assert.True(store.Record((ulong)i, Text(i), Text(i), Sink, Caller, Seen));
            }

            Assert.False(store.Record(61UL, Text(61), Text(61), Sink, Caller, Seen));
            Assert.Equal(60, store.Distinct);
            Assert.Equal(1L, store.CallerSkipped);
        }

        /// <summary>Clone() 必须带上新字段（漏了会让「按界面调额度」静默失效）。</summary>
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
