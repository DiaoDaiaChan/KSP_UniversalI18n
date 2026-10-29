using System;
using UI18n.Core.Dict;
using UI18n.Core.Settings;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 包文件名 → modId 的派生规则（task-71）。这条规则必须与 Lead 实测的 97 个包逐字一致：
    /// 去掉末尾 <c>.jsonl</c>，再只去掉一个已知 channel 后缀 <c>.il</c> / <c>.cfg</c>，其余原样。
    /// 陷阱：<c>AtmosphereAutopilot.UI.il.jsonl</c> 的 modId 是 <c>AtmosphereAutopilot.UI</c> ——
    /// modId 可以含点号，所以「剥掉最后一个点号段」是错的。
    /// </summary>
    // 与 PackDisplayNamesTests 共用静态注册表 _customDisplayNames，串行跑（同一个 collection）
    [Collection(PackFilterTests.RegistryCollection)]
    public class PackFilterTests
    {
        /// <summary>显示名注册表相关用例的 collection 名（与 PackDisplayNamesTests 共用）。</summary>
        public const string RegistryCollection = "PackFilterDisplayNames";

        [Theory]
        [InlineData("Squad.jsonl", "Squad")]
        [InlineData("Squad.il.jsonl", "Squad")]
        [InlineData("Squad.cfg.jsonl", "Squad")]
        [InlineData("BurstPQS.il.jsonl", "BurstPQS")]
        [InlineData("BurstPQS.jsonl", "BurstPQS")]
        [InlineData("AtmosphereAutopilot.UI.il.jsonl", "AtmosphereAutopilot.UI")]
        [InlineData("UI18n-ConfigCache.il.jsonl", "UI18n-ConfigCache")]
        [InlineData("UI18n-Unattributed.il.jsonl", "UI18n-Unattributed")]
        [InlineData("000_TexturesUnlimited.jsonl", "000_TexturesUnlimited")]
        [InlineData("B9-PWings-Fork.il.jsonl", "B9-PWings-Fork")]
        [InlineData("Foo.IL.JSONL", "Foo")]
        [InlineData("Foo.Jsonl", "Foo")]
        public void ModIdFromFileName_StripsKnownSuffixes(string fileName, string expected)
        {
            Assert.Equal(expected, PackFilter.ModIdFromFileName(fileName));
        }

        // 只剥一层：多写一层后缀不是我们的命名约定，剥多会撞名；
        // 不认识的尾巴一律不动（宁可 id 丑，也不要猜错把两个 mod 合并）。
        [Theory]
        [InlineData("Foo.il.cfg.jsonl", "Foo.il")]
        [InlineData("Bar.cfgx.jsonl", "Bar.cfgx")]
        [InlineData("Plain", "Plain")]
        [InlineData(".il.jsonl", ".il")]
        public void ModIdFromFileName_StripsAtMostOneChannelSuffix(string fileName, string expected)
        {
            Assert.Equal(expected, PackFilter.ModIdFromFileName(fileName));
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        public void ModIdFromFileName_EmptyInputs(string fileName, string expected)
        {
            Assert.Equal(expected, PackFilter.ModIdFromFileName(fileName));
        }

        [Theory]
        [InlineData("Packs/zh-cn/Squad.il.jsonl", "Squad")]
        [InlineData("Packs/zh-cn/Squad.jsonl", "Squad")]
        [InlineData("GameData/UI18n/Packs/zh-cn/AtmosphereAutopilot.UI.il.jsonl", "AtmosphereAutopilot.UI")]
        public void ModIdFromPath_StripsDirectories(string path, string expected)
        {
            Assert.Equal(expected, PackFilter.ModIdFromPath(path));
        }

        [Fact]
        public void ModIdFromPath_HandlesBackslashesAndEmpty()
        {
            Assert.Equal("Squad", PackFilter.ModIdFromPath(@"Packs\zh-cn\Squad.il.jsonl"));
            Assert.Equal(string.Empty, PackFilter.ModIdFromPath(null));
            Assert.Equal(string.Empty, PackFilter.ModIdFromPath(string.Empty));
        }

        [Theory]
        [InlineData("Squad")]
        [InlineData("000_TexturesUnlimited")]
        [InlineData("B9-PWings-Fork")]
        [InlineData("AtmosphereAutopilot.UI")]
        [InlineData("UI18n-Unattributed")]
        public void IsValidModId_AcceptsRealIds(string modId)
        {
            Assert.True(PackFilter.IsValidModId(modId));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(".hidden")]
        [InlineData("Squad.")]
        [InlineData("a..b")]
        [InlineData("Foo Bar")]
        [InlineData("Foo/Bar")]
        [InlineData("Foo:Bar")]
        public void IsValidModId_RejectsBadIds(string modId)
        {
            Assert.False(PackFilter.IsValidModId(modId));
        }

        // 显示名只有数据来源（Packs/<lang>/display-names.json 或 RegisterDisplayName）：
        // Core 里不许内置 mod 名、也不许内置界面文案，没登记就原样回 modId（连大小写都不动）。
        // 这三行正是 v1.7 之前被写死成「未归属文本」/「配置缓存」/「本体」的三个 id。
        [Theory]
        [InlineData("UI18n-Unattributed")]
        [InlineData("UI18n-ConfigCache")]
        [InlineData("Squad")]
        [InlineData("RealFuels")]
        public void DisplayName_FallsBackToModIdWithoutData(string modId)
        {
            Assert.Equal(modId, PackFilter.DisplayName(modId));
            Assert.Equal(modId.ToLowerInvariant(), PackFilter.DisplayName(modId.ToLowerInvariant()));
            Assert.Equal(modId.ToUpperInvariant(), PackFilter.DisplayName(modId.ToUpperInvariant()));
        }

        [Fact]
        public void DisplayName_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, PackFilter.DisplayName(null));
            Assert.Equal(string.Empty, PackFilter.DisplayName(string.Empty));
        }

        [Theory]
        [InlineData("UI18n-Unattributed", true)]
        [InlineData("ui18n-configcache", true)]
        [InlineData("UI18n-Foo", true)]
        [InlineData("UI18n-", false)]
        [InlineData("Squad", false)]
        [InlineData("UI18n", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void IsInternalPack_MatchesOnlyProductOwnPacks(string modId, bool expected)
        {
            Assert.Equal(expected, PackFilter.IsInternalPack(modId));
        }

        // 同一个 mod 的两个 channel（BurstPQS.il.jsonl + BurstPQS.jsonl）必须共享一个开关，
        // 否则面板上会出现两行、玩家关掉一行另一行还在翻译。
        [Fact]
        public void RegisterDisplayName_OverridesOrProvidesCustomName()
        {
            try
            {
                PackFilter.RegisterDisplayName("MechJeb2", "机巧杰布2");
                Assert.Equal("机巧杰布2", PackFilter.DisplayName("MechJeb2"));
                Assert.Equal("机巧杰布2", PackFilter.DisplayName("mechjeb2"));
            }
            finally
            {
                PackFilter.ClearCustomDisplayNames();
            }
            Assert.Equal("MechJeb2", PackFilter.DisplayName("MechJeb2"));
        }

        [Fact]
        public void TwoChannelsOfSameMod_ShareOneSwitch()
        {
            string[] files = { "BurstPQS.il.jsonl", "BurstPQS.jsonl", "Squad.jsonl" };
            Prefs prefs = Prefs.CreateDefault();
            prefs.SetEnabled("BurstPQS", false);

            int disabledCount;
            string[] kept = PackFilter.SelectEnabled(files, prefs, out disabledCount);

            Assert.Single(kept);
            Assert.Equal("Squad.jsonl", kept[0]);
            Assert.Equal(2, disabledCount);
        }

        [Fact]
        public void SelectEnabled_NullPrefs_KeepsEverything()
        {
            string[] files = { "B.jsonl", "A.il.jsonl", "C.jsonl" };
            int disabledCount;
            string[] kept = PackFilter.SelectEnabled(files, null, out disabledCount);
            Assert.Equal(files, kept);
            Assert.Equal(0, disabledCount);
        }

        [Fact]
        public void SelectEnabled_KeepsInputOrder()
        {
            string[] files = { "Squad.jsonl", "RealFuels.il.jsonl", "MechJeb2.jsonl", "Waterfall.jsonl" };
            Prefs prefs = Prefs.CreateDefault();
            prefs.SetEnabled("RealFuels", false);

            int disabledCount;
            string[] kept = PackFilter.SelectEnabled(files, prefs, out disabledCount);

            Assert.Equal(new[] { "Squad.jsonl", "MechJeb2.jsonl", "Waterfall.jsonl" }, kept);
            Assert.Equal(1, disabledCount);
        }

        [Fact]
        public void SelectEnabled_MatchesCaseInsensitively()
        {
            string[] files = { "Squad.jsonl", "RealFuels.jsonl" };
            Prefs prefs = Prefs.CreateDefault();
            prefs.SetEnabled("squad", false);

            int disabledCount;
            string[] kept = PackFilter.SelectEnabled(files, prefs, out disabledCount);
            Assert.Equal(new[] { "RealFuels.jsonl" }, kept);
            Assert.Equal(1, disabledCount);
        }

        [Fact]
        public void SelectEnabled_EmptyInput()
        {
            int disabledCount;
            string[] kept = PackFilter.SelectEnabled(new string[0], Prefs.CreateDefault(), out disabledCount);
            Assert.Empty(kept);
            Assert.Equal(0, disabledCount);

            kept = PackFilter.SelectEnabled(null, Prefs.CreateDefault(), out disabledCount);
            Assert.Empty(kept);
            Assert.Equal(0, disabledCount);
        }

        [Fact]
        public void DistinctModIds_DedupesChannelsAndSorts()
        {
            string[] files =
            {
                "Squad.jsonl",
                "BurstPQS.il.jsonl",
                "AtmosphereAutopilot.UI.il.jsonl",
                "BurstPQS.jsonl",
            };

            string[] ids = PackFilter.DistinctModIds(files);
            Assert.Equal(new[] { "AtmosphereAutopilot.UI", "BurstPQS", "Squad" }, ids);
            Assert.Empty(PackFilter.DistinctModIds(null));
            Assert.Empty(PackFilter.DistinctModIds(new string[0]));
        }

        [Fact]
        public void FileStemOf_ReturnsModIdUnchanged()
        {
            Assert.Equal("Squad", PackFilter.FileStemOf("Squad"));
            Assert.Equal(string.Empty, PackFilter.FileStemOf(null));
        }

        [Fact]
        public void PackExtension_IsJsonl()
        {
            Assert.Equal(".jsonl", PackFilter.PackExtension);
        }
    }
}
