using System;
using UI18n.Core.Capture;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// v1.5（docs/08 D-T2）：<b>模板档限额退役</b>后的回归用例。
    /// <para>
    /// 缺陷由来（round 11 实测，<c>docs/03</c> 捕获分诊）：一局 5,000 条捕获里
    /// <b>4,323 条是模板档</b>（加载屏 <c>Loading sound asset 0/234</c> 这类计数器 / 进度串），
    /// 真正的 UI 文本只有 109 条。v1.3 的修法是给模板档单独限额
    /// （<see cref="CaptureOptions.MaxTemplateEntries"/>，默认 200），结果只是把「丢正文」换了个位置丢：
    /// 4,000 条噪声仍然只留下 200 条，谁也没法回答「那 3,800 条里有没有漏掉的界面文本」。
    /// </para>
    /// <para>
    /// v1.5 的口径：<b>一条都不丢</b>。<see cref="CaptureOptions.MaxTemplateEntries"/> /
    /// <see cref="CaptureOptions.MaxEntries"/> / <see cref="CaptureOptions.MaxPerCaller"/> 三个字段还在
    /// （老配置文件仍要能解析、老调用点仍在读），但<b>不再生效</b>；桶（candidate / template / never）
    /// 只表示优先级，不表示「要不要留档」。所以本文件的断言从「超额被丢」翻转成「超额照收」。
    /// </para>
    /// </summary>
    public class CaptureTemplateCapTests
    {
        private const string Seen = "2026-10-26T00:32:05Z";
        private const string Sink = "TMPro.TMP_Text.set_text";
        private const string Caller = "LoadingScreen.UpdateProgressBar";

        /// <summary>刻意把两个退役上限压到很小，用来证明它们<b>不再</b>影响取舍。</summary>
        private static CaptureStore Store(int maxEntries, int maxTemplateEntries)
        {
            var options = CaptureOptions.Default;
            options.MaxEntries = maxEntries;
            options.MaxTemplateEntries = maxTemplateEntries;

            // caller 额度（v1.3 新增，默认 60）同样退役，这里一并关掉：
            // 4,000 条噪声来自同一个 caller，若额度还在生效就测不到模板档这一层。
            options.MaxPerCaller = 0;
            return new CaptureStore(options);
        }

        /// <summary>模板档上限设成 2：第 3 条照样建记录，<c>TemplateSkipped</c> 恒 0。</summary>
        [Fact]
        public void TemplateCapRetired_ExtraTemplatesAreStillRecorded()
        {
            CaptureStore store = Store(5000, 2);

            Assert.True(store.Record(1UL, "Loading sound asset 0/234", "Loading sound asset 0/234", Sink, Caller, Seen));
            Assert.True(store.Record(2UL, "Loading sound asset 1/234", "Loading sound asset 1/234", Sink, Caller, Seen));

            // v1.3 这里返回 false（第 3 条被模板额度挡掉）；v1.5 必须收。
            Assert.True(store.Record(3UL, "Loading sound asset 2/234", "Loading sound asset 2/234", Sink, Caller, Seen));

            Assert.Equal(3, store.Templates);
            Assert.Equal(0L, store.TemplateSkipped);
            Assert.Equal(3, store.Distinct);

            // 噪声不再挤占候选档 —— 而且现在也不需要「挤占」这个概念了。
            Assert.True(store.Record(4UL, "Vessel mass", "Vessel mass", "GUILayout.DoLabel", "A.B", Seen));
            Assert.Equal(1, store.Candidates);
            Assert.Equal(4, store.Distinct);
        }

        /// <summary><c>0</c> 以前表示「一条模板都不录」；退役后 <c>0</c> 与 <c>-1</c> / <c>200</c> 完全等价。</summary>
        [Fact]
        public void TemplateCapZeroRetired_RecordsEveryTemplate()
        {
            CaptureStore store = Store(5000, 0);

            Assert.True(store.Record(1UL, "Loading sound asset 0/234", "Loading sound asset 0/234", Sink, Caller, Seen));
            Assert.Equal(1, store.Templates);
            Assert.Equal(0L, store.TemplateSkipped);

            Assert.True(store.Record(2UL, "Vessel mass", "Vessel mass", "GUILayout.DoLabel", "A.B", Seen));
            Assert.Equal(1, store.Candidates);
            Assert.Equal(2, store.Distinct);
        }

        /// <summary>真实场景的回归：<b>先来 4,000 条噪声，再来真正的 UI 文本</b> —— 4,030 条全部留档。</summary>
        [Fact]
        public void RealWorld_NoiseBurst_KeepsEveryRecord()
        {
            CaptureStore store = Store(5000, 200);

            for (ulong i = 1; i <= 4000; i++)
            {
                string text = "Loading sound asset " + i + "/4000";
                Assert.True(store.Record(i, text, text, Sink, Caller, Seen));
            }

            // 候选档的文本必须<b>不含数字</b>：带数字的串会被 CaptureClassify 判成模板档（counter），
            // 那样这段用例就变成「模板档再来一批」，测不到「候选档和噪声同时留档」这件事。
            ulong hash = 100000;
            for (int i = 0; i < 30; i++)
            {
                string text = "Missing ui text " + (char)('a' + (i % 26)) + (char)('a' + (i / 26));
                Assert.True(store.Record(hash++, text, text, "GUILayout.DoLabel", "A.B", Seen));
            }

            Assert.Equal(4000, store.Templates);
            Assert.Equal(30, store.Candidates);
            Assert.Equal(4030, store.Distinct);
            Assert.Equal(4030, store.ToArray().Length);
            Assert.Equal(0L, store.TemplateSkipped);
            Assert.Equal(0L, store.Filtered);
            Assert.False(store.CapReached);

            string describe = store.Describe();
            Assert.Contains("template=4000", describe, StringComparison.Ordinal);
            Assert.Contains("candidate=30", describe, StringComparison.Ordinal);
            Assert.DoesNotContain("tmplSkipped", describe, StringComparison.Ordinal);
        }

        /// <summary>同一条重复上报仍然只累加 count（「不再丢弃」不等于「重复建桶」）。</summary>
        [Fact]
        public void RepeatedTemplateMiss_OnlyIncrementsCount()
        {
            CaptureStore store = Store(5000, 1);

            Assert.True(store.Record(1UL, "Loading 1/2", "Loading 1/2", Sink, Caller, Seen));
            Assert.False(store.Record(1UL, "Loading 1/2", "Loading 1/2", Sink, Caller, Seen));

            store.CountMiss(1UL);

            Assert.Equal(1, store.Distinct);
            Assert.Equal(3, store.ToArray()[0].Count);
            Assert.Equal(0L, store.TemplateSkipped);
        }

        /// <summary>
        /// 字段仍在、默认值不变（配置兼容：老 <c>UI18n.cfg</c> 里的 <c>CaptureMaxTemplateEntries</c>
        /// 仍然解析得进来，只是不再生效）。
        /// </summary>
        [Fact]
        public void RetiredOptions_AreStillParsed_ButHaveNoEffect()
        {
            Assert.Equal(200, CaptureOptions.Default.MaxTemplateEntries);
            Assert.Equal(200, new UI18n.Core.Config.UI18nConfig().CaptureMaxTemplateEntries);
            Assert.Equal(200, CaptureOptions.Default.Clone().MaxTemplateEntries);
            Assert.Equal(5000, CaptureOptions.Default.MaxEntries);
        }

        /// <summary>四个退役计数在<u>任何</u>输入下都是 0，且不出现在统计行里。</summary>
        [Fact]
        public void RetiredCounters_AreAlwaysZero()
        {
            CaptureStore store = Store(1, 0);

            for (ulong i = 1; i <= 50; i++)
            {
                string text = "Loading sound asset " + i + "/50";
                Assert.True(store.Record(i, text, text, Sink, Caller, Seen));
            }

            Assert.Equal(50, store.Distinct);
            Assert.Equal(0L, store.Filtered);
            Assert.Equal(0L, store.Overflow);
            Assert.Equal(0L, store.TemplateSkipped);
            Assert.Equal(0L, store.CallerSkipped);
            Assert.False(store.ConsumeCapNotice());

            string describe = store.Describe();
            Assert.DoesNotContain("filtered", describe, StringComparison.Ordinal);
            Assert.DoesNotContain("overflow", describe, StringComparison.Ordinal);
            Assert.DoesNotContain("callerSkipped", describe, StringComparison.Ordinal);
            Assert.DoesNotContain("cap=reached", describe, StringComparison.Ordinal);
        }
    }
}
