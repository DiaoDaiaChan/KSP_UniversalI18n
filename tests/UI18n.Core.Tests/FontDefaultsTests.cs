using System;
using UI18n.Core.Config;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 字体候选单源表 <see cref="FontDefaults"/>（v1.7 去硬编码）：这些名字以前有两份副本
    /// （Runtime 的 <c>FontResolver.DefaultOsFontCandidates</c> 与 <c>Config.cfg</c> 注释里的散文列表），
    /// 现在只在 Core 定义一次，Runtime 通过 <see cref="UI18nConfig.OsFonts"/> 读它。
    /// <para>
    /// 这些用例锁的是「表本身的自洽性」而不是具体某个字体名：换个默认字体不该让单测红，
    /// 但「首选不在表里」「表里有重复/空项」「配置默认值不来自这张表」必须是红的。
    /// </para>
    /// </summary>
    public class FontDefaultsTests
    {
        [Fact]
        public void OsFontName_IsFirstCandidate()
        {
            Assert.False(string.IsNullOrEmpty(FontDefaults.OsFontName));
            Assert.Equal(FontDefaults.OsFontName, FontDefaults.OsFontCandidates[0]);
        }

        [Fact]
        public void OsFontCandidates_AreUsableNames()
        {
            Assert.NotEmpty(FontDefaults.OsFontCandidates);

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < FontDefaults.OsFontCandidates.Length; i++)
            {
                string name = FontDefaults.OsFontCandidates[i];
                Assert.False(string.IsNullOrWhiteSpace(name), "候选名不能为空（第 " + i + " 项）");
                Assert.Equal(name.Trim(), name);
                Assert.True(seen.Add(name), "候选名重复：" + name);
            }
        }

        [Fact]
        public void NewOsFontCandidates_ReturnsIndependentCopy()
        {
            string[] first = FontDefaults.NewOsFontCandidates();
            Assert.Equal(FontDefaults.OsFontCandidates, first);

            first[0] = "Mutated Sans";
            Assert.Equal(FontDefaults.OsFontName, FontDefaults.OsFontCandidates[0]);

            string[] second = FontDefaults.NewOsFontCandidates();
            Assert.Equal(FontDefaults.OsFontName, second[0]);
            Assert.NotSame(first, second);
        }

        [Fact]
        public void Config_DefaultsComeFromFontDefaults()
        {
            UI18nConfig config = UI18nConfig.Default;
            Assert.Equal(FontDefaults.OsFontName, config.OsFontName);
            Assert.Equal(FontDefaults.OsFontCandidates, config.OsFonts);

            // 每个实例拿到的是自己的副本：改一个实例不该影响另一个实例（配置是可变的）
            config.OsFonts[0] = "Mutated Sans";
            UI18nConfig fresh = UI18nConfig.Default;
            Assert.Equal(FontDefaults.OsFontName, fresh.OsFonts[0]);
        }

        [Theory]
        [InlineData(" A , B ,, ", new[] { "A", "B" })]
        [InlineData("SimHei, simhei, SIMHEI", new[] { "SimHei" })]
        [InlineData("A;B", new[] { "A", "B" })]
        [InlineData("  Noto Sans CJK SC  ", new[] { "Noto Sans CJK SC" })]
        [InlineData("A, B, C", new[] { "A", "B", "C" })]
        public void ParseFontList_TrimsDropsEmptiesAndDedupes(string raw, string[] expected)
        {
            Assert.Equal(expected, FontDefaults.ParseFontList(raw));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(",")]
        [InlineData(" ; , ")]
        public void ParseFontList_EmptyInput_ReturnsEmpty(string raw)
        {
            Assert.Empty(FontDefaults.ParseFontList(raw));
        }

        [Fact]
        public void ParseFontList_PreservesConfiguredOrder()
        {
            string[] parsed = FontDefaults.ParseFontList("Zeta, Alpha, Beta");
            Assert.Equal(new[] { "Zeta", "Alpha", "Beta" }, parsed);
        }
    }
}
