using System;
using System.Collections.Generic;
using UI18n.Core.Submission;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// docs/10 §6.3 的三步脱敏。这里钉的是「什么绝对不能投出去」：
    /// 中文（我们自己翻过的串）、路径、URL、绝对路径、带数字的模板串、玩家私有标识。
    /// </summary>
    public class SubmissionFilterTests
    {
        private static SubmissionEntry Entry(string text, int count = 1)
        {
            return new SubmissionEntry
            {
                H = "3ff885233413de88",
                S = text,
                Mod = "Squad",
                Channel = "capture",
                Count = count,
            };
        }

        private static SubmissionFilterResult Run(bool allowLongSingletons, params SubmissionEntry[] entries)
        {
            SubmissionFilter filter = new SubmissionFilter(new List<SubmissionPrivateValue>());
            return filter.Filter(new List<SubmissionEntry>(entries), allowLongSingletons);
        }

        [Fact]
        public void PlainProse_IsKept()
        {
            SubmissionFilterResult result = Run(false, Entry("Aerodynamic Failure"));
            Assert.Single(result.Entries);
            Assert.Equal("Aerodynamic Failure", result.Entries[0].S);
            Assert.Equal(0, result.DroppedTotal);
        }

        // capture 行的 s 是**渲染后**文本：我们已经翻过的串在这里是中文。
        // 这条规则如果失效，投出去的全是中文当英文原文（服务端只能 warning，救不回来）。
        [Theory]
        [InlineData("设置")]
        [InlineData("发动机熄火")]
        [InlineData("推力不足，剩余二成")]
        public void AlreadyCjk_IsDropped(string text)
        {
            SubmissionFilterResult result = Run(false, Entry(text));
            Assert.Empty(result.Entries);
            Assert.Equal(1, result.DroppedAlreadyCjk);
            Assert.Equal(1, result.DroppedNonProse);
        }

        [Fact]
        public void PathLike_IsDropped_AndCountedAsPathRedaction()
        {
            SubmissionFilterResult result = Run(false, Entry("GameData/Squad/Parts/engine.cfg"));
            Assert.Empty(result.Entries);
            Assert.Equal(1, result.DroppedPath);
            Assert.Equal(1, result.Redactions.Paths);
        }

        [Fact]
        public void Url_IsDropped_AndCounted()
        {
            SubmissionFilterResult result = Run(false, Entry("Visit https://example.com/foo for details"));
            Assert.Empty(result.Entries);
            Assert.Equal(1, result.Redactions.Urls);
            Assert.Equal(1, result.DroppedPrivate);
        }

        // 注意：整串无空白的 "C:/Games/saves.sfs" 会先被第 1 步判成 path（三段式），
        // 所以这里用带空白的句子，才走得到第 2 步的绝对路径判据。
        [Fact]
        public void AbsolutePath_IsDropped_AndCounted()
        {
            SubmissionFilterResult result = Run(false, Entry("Installed at C:/Games"));
            Assert.Empty(result.Entries);
            Assert.Equal(1, result.Redactions.AbsolutePaths);
            Assert.Equal(1, result.DroppedPrivate);
        }

        // 带数字的串一律是渲染模板（"Alt: 1,234 m"），原文在字典里另有条目，投出去只会污染候选池。
        [Theory]
        [InlineData("Alt: 1,234 m")]
        [InlineData("Throttle 42%")]
        [InlineData("Part 3 of 7")] 
        public void DigitTemplate_IsDroppedAsNonProse(string text)
        {
            SubmissionFilterResult result = Run(false, Entry(text));
            Assert.Empty(result.Entries);
            Assert.Equal(1, result.DroppedNonProse);
        }

        [Fact]
        public void PrivateVesselName_IsReplacedWithPlaceholder()
        {
            SubmissionFilter filter = new SubmissionFilter(new List<SubmissionPrivateValue>
            {
                SubmissionPrivateValue.Of(SubmissionPrivateKind.Vessel, "Falcon"),
            });

            SubmissionFilterResult result = filter.Filter(
                new List<SubmissionEntry> { Entry("Duna Falcon landed on the ridge") }, false);

            Assert.Single(result.Entries);
            Assert.Equal("Duna <vessel> landed on the ridge", result.Entries[0].S);
            Assert.Equal(1, result.RedactedPrivate);
            Assert.Equal(1, result.Redactions.UserNames);
        }

        // ---- 私有标识的两类计数（Lead 裁决：redactions 必须分清「替换」与「丢弃」）----

        // 整串就是一个飞行器名 → 替换后只剩 <vessel>，没有正文可译 → 整条丢，记「丢弃」一类。
        [Fact]
        public void PrivateOnlyEntry_IsDropped_AndCountedAsDroppedClass()
        {
            SubmissionFilter filter = new SubmissionFilter(new List<SubmissionPrivateValue>
            {
                SubmissionPrivateValue.Of(SubmissionPrivateKind.Vessel, "Falcon"),
            });

            SubmissionFilterResult result = filter.Filter(
                new List<SubmissionEntry> { Entry("Falcon") }, false);

            Assert.Empty(result.Entries);
            Assert.Equal(1, result.DroppedPrivate);
            Assert.Equal(1, result.Redactions.UserNamesDropped);
            Assert.Equal(0, result.Redactions.UserNames);
        }

        // 只剩占位符 + 标点也不算正文（标点不是可译内容）。
        // 注意：带数字的串（"Duna Falcon 42"）在第 1 步就按 counter 桶丢掉了（DigitsAreTemplate），
        // 根本走不到这里，所以这里用纯标点把「占位符之外没有正文」这条判据单独钉住。
        [Fact]
        public void PrivateValuePlusPunctuationOnly_IsDropped()
        {
            SubmissionFilter filter = new SubmissionFilter(new List<SubmissionPrivateValue>
            {
                SubmissionPrivateValue.Of(SubmissionPrivateKind.Vessel, "Duna Falcon"),
            });

            SubmissionFilterResult result = filter.Filter(
                new List<SubmissionEntry> { Entry("Duna Falcon !!!") }, false);

            Assert.Empty(result.Entries);
            Assert.Equal(1, result.Redactions.UserNamesDropped);
            Assert.Equal(0, result.Redactions.UserNames);
        }

        // 替换后仍留有英文 → 保留，记「替换」一类；<vessel> 的字母不能算成正文，也不能算成丢。
        [Fact]
        public void ReplacedEntry_WithRemainingProse_IsKept_AndCountedAsReplacedClass()
        {
            SubmissionFilter filter = new SubmissionFilter(new List<SubmissionPrivateValue>
            {
                SubmissionPrivateValue.Of(SubmissionPrivateKind.Vessel, "Falcon"),
            });

            SubmissionFilterResult result = filter.Filter(
                new List<SubmissionEntry> { Entry("Falcon Science Bay") }, false);

            Assert.Single(result.Entries);
            Assert.Equal("<vessel> Science Bay", result.Entries[0].S);
            Assert.Equal(1, result.RedactedPrivate);
            Assert.Equal(1, result.Redactions.UserNames);
            Assert.Equal(0, result.Redactions.UserNamesDropped);
        }

        // 玩家正文里本来就写了 <vessel> 字面量：没有私有值命中，绝不能因此被当成「无正文」丢掉。
        // （屏蔽占位符只用于「还有没有可见字母」这一步，且必须是精确字面量、不是正则白名单。）
        [Fact]
        public void LiteralPlaceholderInPlayerText_IsKept()
        {
            SubmissionFilterResult result = Run(false, Entry("<vessel> Science Bay"));

            Assert.Single(result.Entries);
            Assert.Equal("<vessel> Science Bay", result.Entries[0].S);
            Assert.Equal(0, result.Redactions.UserNames);
            Assert.Equal(0, result.Redactions.UserNamesDropped);
        }

        [Fact]
        public void PrivateKerbalName_IsReplacedCaseInsensitively()
        {
            SubmissionFilter filter = new SubmissionFilter(new List<SubmissionPrivateValue>
            {
                SubmissionPrivateValue.Of(SubmissionPrivateKind.Kerbal, "Jebediah"),
            });

            SubmissionFilterResult result = filter.Filter(
                new List<SubmissionEntry> { Entry("jebediah is not impressed") }, false);

            Assert.Single(result.Entries);
            Assert.Equal("<kerbal> is not impressed", result.Entries[0].S);
        }

        [Fact]
        public void ShortPrivateValues_AreIgnored()
        {
            SubmissionFilter filter = new SubmissionFilter(new List<SubmissionPrivateValue>
            {
                SubmissionPrivateValue.Of(SubmissionPrivateKind.Vessel, "a"),
                SubmissionPrivateValue.Of(SubmissionPrivateKind.Vessel, "ab"),
            });

            Assert.Equal(2, filter.IgnoredValues);

            SubmissionFilterResult result = filter.Filter(
                new List<SubmissionEntry> { Entry("A part detached") }, false);

            Assert.Single(result.Entries);
            Assert.Equal("A part detached", result.Entries[0].S);
            Assert.Equal(2, result.IgnoredShortPrivateValues);
        }

        [Fact]
        public void LongSingletonText_IsDropped_UnlessAllowed()
        {
            string longText = new string('a', SubmissionFilter.LongSingletonThreshold + 10);

            SubmissionFilterResult dropped = Run(false, Entry(longText));
            Assert.Empty(dropped.Entries);
            Assert.Equal(1, dropped.DroppedSingleton);

            SubmissionFilterResult allowed = Run(true, Entry(longText));
            Assert.Single(allowed.Entries);
            Assert.Equal(0, allowed.DroppedSingleton);
        }

        [Fact]
        public void LongTextSeenMoreThanOnce_IsKept()
        {
            string longText = new string('a', SubmissionFilter.LongSingletonThreshold + 10);
            SubmissionFilterResult result = Run(false, Entry(longText, 5));
            Assert.Single(result.Entries);
        }

        [Fact]
        public void Filter_DoesNotMutateInput()
        {
            SubmissionEntry entry = Entry("Duna Falcon landed");
            SubmissionFilter filter = new SubmissionFilter(new List<SubmissionPrivateValue>
            {
                SubmissionPrivateValue.Of(SubmissionPrivateKind.Vessel, "Falcon"),
            });

            SubmissionFilterResult result = filter.Filter(new List<SubmissionEntry> { entry }, false);

            Assert.Equal("Duna Falcon landed", entry.S);
            Assert.Equal("Duna <vessel> landed", result.Entries[0].S);
        }

        [Fact]
        public void PlaceholderOf_CoversEveryKind()
        {
            Assert.Equal("<vessel>", SubmissionFilter.PlaceholderOf(SubmissionPrivateKind.Vessel));
            Assert.Equal("<kerbal>", SubmissionFilter.PlaceholderOf(SubmissionPrivateKind.Kerbal));
            Assert.Equal("<save>", SubmissionFilter.PlaceholderOf(SubmissionPrivateKind.Save));
            Assert.Equal("<user>", SubmissionFilter.PlaceholderOf(SubmissionPrivateKind.UserName));
        }

        [Fact]
        public void Filter_NullInput_ReturnsEmpty()
        {
            SubmissionFilter filter = new SubmissionFilter(null);
            SubmissionFilterResult result = filter.Filter(null, false);
            Assert.Empty(result.Entries);
            Assert.Equal(0, result.Input);
        }
    }
}
