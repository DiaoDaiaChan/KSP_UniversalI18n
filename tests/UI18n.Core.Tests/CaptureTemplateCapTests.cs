using System;
using UI18n.Core.Capture;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// v1.3：模板档单独限额的回归用例。
    /// <para>
    /// 缺陷由来（round 11 实测，<c>docs/03</c> 捕获分诊）：一局 5,000 条捕获里
    /// <b>4,323 条是模板档</b>（加载屏 <c>Loading sound asset 0/234</c> 这类计数器 / 进度串），
    /// 真正的 UI 文本只有 109 条 —— 总配额被噪声吃光，采集等于白跑。
    /// 修法：<c>bucket=template</c> 走独立额度 <see cref="CaptureOptions.MaxTemplateEntries"/>，
    /// 且判断在总配额之前（模板档不许挤占候选档）。
    /// </para>
    /// </summary>
    public class CaptureTemplateCapTests
    {
        private const string Seen = "2026-10-26T00:32:05Z";
        private const string Sink = "TMPro.TMP_Text.set_text";
        private const string Caller = "LoadingScreen.UpdateProgressBar";

        private static CaptureStore Store(int maxEntries, int maxTemplateEntries)
        {
            var options = CaptureOptions.Default;
            options.MaxEntries = maxEntries;
            options.MaxTemplateEntries = maxTemplateEntries;

            // 本文件只管「模板档额度」这一层：caller 额度（v1.3 新增，默认 60）先关掉，
            // 否则 4,000 条噪声会先被 caller 额度挡住，测不到模板档的行为。
            // caller 额度自己的用例在 CaptureCallerQuotaTests.cs。
            options.MaxPerCaller = 0;
            return new CaptureStore(options);
        }

        /// <summary>模板档到顶后：新模板被挡住并计入 tmplSkipped，候选档照收。</summary>
        [Fact]
        public void TemplateCap_DropsExtraTemplates_ButKeepsCandidates()
        {
            CaptureStore store = Store(5000, 2);

            Assert.True(store.Record(1UL, "Loading sound asset 0/234", "Loading sound asset 0/234", Sink, Caller, Seen));
            Assert.True(store.Record(2UL, "Loading sound asset 1/234", "Loading sound asset 1/234", Sink, Caller, Seen));
            Assert.False(store.Record(3UL, "Loading sound asset 2/234", "Loading sound asset 2/234", Sink, Caller, Seen));

            Assert.Equal(2, store.Templates);
            Assert.Equal(1L, store.TemplateSkipped);

            // 模板档满了也不影响候选档 —— 这正是旧行为做不到的（旧行为是总配额先满）。
            Assert.True(store.Record(4UL, "Vessel mass", "Vessel mass", "GUILayout.DoLabel", "A.B", Seen));
            Assert.Equal(1, store.Candidates);
            Assert.Equal(3, store.Distinct);
        }

        /// <summary><c>0</c> = 一条模板都不录（含数字的进度串全丢），候选档不受影响。</summary>
        [Fact]
        public void TemplateCap_Zero_DropsEveryTemplate()
        {
            CaptureStore store = Store(5000, 0);

            Assert.False(store.Record(1UL, "Loading sound asset 0/234", "Loading sound asset 0/234", Sink, Caller, Seen));
            Assert.Equal(0, store.Templates);
            Assert.Equal(1L, store.TemplateSkipped);

            Assert.True(store.Record(2UL, "Vessel mass", "Vessel mass", "GUILayout.DoLabel", "A.B", Seen));
            Assert.Equal(1, store.Candidates);
            Assert.Equal(1, store.Distinct);
        }

        /// <summary>负数 = 不限制（旧行为），且此时 tmplSkipped 恒为 0。</summary>
        [Fact]
        public void TemplateCap_Negative_IsUnlimited()
        {
            CaptureStore store = Store(5000, -1);

            for (ulong i = 1; i <= 50; i++)
            {
                string text = "Loading sound asset " + i + "/234";
                Assert.True(store.Record(i, text, text, Sink, Caller, Seen));
            }

            Assert.Equal(50, store.Templates);
            Assert.Equal(0L, store.TemplateSkipped);
            Assert.DoesNotContain("tmplSkipped", store.Describe(), StringComparison.Ordinal);
        }

        /// <summary>
        /// 这才是真实场景的回归：<b>先来 4,000 条噪声，再来真正的 UI 文本</b>。
        /// 旧行为下总配额（5000）会被噪声吃掉大半甚至吃满；现在噪声只占 200 条，候选档一条不丢。
        /// </summary>
        [Fact]
        public void RealWorld_NoiseBurst_DoesNotStarveCandidates()
        {
            CaptureStore store = Store(5000, 200);

            for (ulong i = 1; i <= 4000; i++)
            {
                string text = "Loading sound asset " + i + "/4000";
                store.Record(i, text, text, Sink, Caller, Seen);
            }

            // 候选档的文本必须<b>不含数字</b>：带数字的串会被 CaptureClassify 判成模板档（counter），
            // 那样这段用例就变成「模板档第二次触顶」，测不到「候选档不被噪声饿死」这件事。
            ulong hash = 100000;
            for (int i = 0; i < 30; i++)
            {
                string text = "Missing ui text " + (char)('a' + (i % 26)) + (char)('a' + (i / 26));
                Assert.True(store.Record(hash++, text, text, "GUILayout.DoLabel", "A.B", Seen));
            }

            Assert.Equal(200, store.Templates);
            Assert.Equal(3800L, store.TemplateSkipped);
            Assert.Equal(30, store.Candidates);
            Assert.Equal(230, store.Distinct);
            Assert.False(store.CapReached);

            string describe = store.Describe();
            Assert.Contains("template=200", describe, StringComparison.Ordinal);
            Assert.Contains("tmplSkipped=3800", describe, StringComparison.Ordinal);
            Assert.Contains("candidate=30", describe, StringComparison.Ordinal);
        }

        /// <summary>模板档被限额挡掉的 hash 记成 filtered：<c>CountMiss</c> 不会凭空造记录。</summary>
        [Fact]
        public void SkippedTemplate_IsCountedAsFiltered_NotRecorded()
        {
            CaptureStore store = Store(5000, 1);

            Assert.True(store.Record(1UL, "Loading 1/2", "Loading 1/2", Sink, Caller, Seen));
            Assert.False(store.Record(2UL, "Loading 2/2", "Loading 2/2", Sink, Caller, Seen));

            long filteredBefore = store.Filtered;
            store.CountMiss(2UL);
            store.CountMiss(2UL);

            Assert.Equal(1, store.Distinct);
            Assert.Equal(filteredBefore + 2, store.Filtered);
        }

        /// <summary>默认值 = 200（改这个数等于悄悄改变所有人的采集口径，钉住它）。</summary>
        [Fact]
        public void Default_IsTwoHundred()
        {
            Assert.Equal(200, CaptureOptions.Default.MaxTemplateEntries);
            Assert.Equal(200, new UI18n.Core.Config.UI18nConfig().CaptureMaxTemplateEntries);
            Assert.Equal(200, CaptureOptions.Default.Clone().MaxTemplateEntries);
        }
    }
}
