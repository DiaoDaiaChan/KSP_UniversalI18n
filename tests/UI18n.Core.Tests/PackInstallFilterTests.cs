using System.Collections.Generic;
using System.Text;
using UI18n.Core.Update;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 更新链路的「只装启用的包」（task-71）。分区逻辑放在 Core 里，就是为了能在单测里
    /// 把「关掉的包不会被装回来」这件事断言死——这条如果错了，玩家在面板里关掉的 mod
    /// 会在下次联网更新后悄悄复活，比没做开关还糟。
    /// </summary>
    public class PackInstallFilterTests
    {
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static string ManifestJson(string filesJson, string modsJson)
        {
            string tail = modsJson == null ? string.Empty : ",\"mods\":" + modsJson;
            return "{\"version\":\"1.2.0\","
                + "\"zip\":{\"name\":\"UI18n-1.2.0.zip\",\"bytes\":123,\"sha256\":\"" + Sha + "\"},"
                + "\"files\":" + filesJson + tail + "}";
        }

        private static string FileEntry(string path, long bytes)
        {
            return "{\"path\":\"" + path + "\",\"bytes\":" + bytes + ",\"sha256\":\"" + Sha + "\"}";
        }

        private static UpdateManifest Parse(string filesJson, string modsJson)
        {
            UpdateManifest manifest;
            string error;
            Assert.True(UpdateManifest.TryParse(ManifestJson(filesJson, modsJson), out manifest, out error), error);
            return manifest;
        }

        [Theory]
        [InlineData("Packs/zh-cn/Squad.jsonl", true)]
        [InlineData("Packs/zh-cn/Squad.il.jsonl", true)]
        [InlineData("Packs/zh-cn/_smoke.jsonl", false)]
        [InlineData("Plugins/UI18n.dll", false)]
        [InlineData("Config.cfg", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsPackPath_OnlyAcceptsPacksLayout(string path, bool expected)
        {
            Assert.Equal(expected, PackInstallFilter.IsPackPath(path));
        }

        [Theory]
        [InlineData("Packs/zh-cn/Squad.il.jsonl", "Squad")]
        [InlineData("Packs/zh-cn/Squad.jsonl", "Squad")]
        [InlineData("Packs/zh-cn/AtmosphereAutopilot.UI.il.jsonl", "AtmosphereAutopilot.UI")]
        [InlineData(null, "")]
        [InlineData("", "")]
        public void ModIdOfPackPath_DerivesFromFileName(string path, string expected)
        {
            Assert.Equal(expected, PackInstallFilter.ModIdOfPackPath(path));
        }

        // 权威来源是清单的 mods[]：文件名只是兜底。这样即使以后包文件名换了写法，
        // 只要清单说这个文件属于 Squad，开关就照样命中。
        [Fact]
        public void ResolveModId_PrefersManifestMapping()
        {
            UpdateManifest manifest = Parse(
                "[" + FileEntry("GameData/UI18n/Packs/zh-cn/SomeFile.jsonl", 5) + "]",
                "[{\"modId\":\"RealModName\",\"file\":\"GameData/UI18n/Packs/zh-cn/SomeFile.jsonl\","
                + "\"bytes\":5,\"sha256\":\"" + Sha + "\"}]");

            ManifestFile file = manifest.Files[0];
            Assert.Equal("RealModName", PackInstallFilter.ResolveModId(manifest, file));
            Assert.Equal("SomeFile", PackInstallFilter.ModIdOfPackPath(file.ModRelativePath));
        }

        [Fact]
        public void ResolveModId_FallsBackToFileName()
        {
            UpdateManifest manifest = Parse(
                "[" + FileEntry("GameData/UI18n/Packs/zh-cn/Waterfall.il.jsonl", 5) + "]",
                null);

            Assert.Equal("Waterfall", PackInstallFilter.ResolveModId(manifest, manifest.Files[0]));
        }

        [Fact]
        public void ResolveModId_NonPackFileAndNullFile()
        {
            UpdateManifest manifest = Parse(
                "[" + FileEntry("GameData/UI18n/Plugins/UI18n.dll", 5) + "]",
                null);

            Assert.Equal(string.Empty, PackInstallFilter.ResolveModId(manifest, manifest.Files[0]));
            Assert.Equal(string.Empty, PackInstallFilter.ResolveModId(manifest, null));
        }

        [Fact]
        public void Partition_SplitsPacksAndKeepsNonPackFiles()
        {
            UpdateManifest manifest = Parse(
                "["
                + FileEntry("GameData/UI18n/Packs/zh-cn/Squad.jsonl", 1) + ","
                + FileEntry("GameData/UI18n/Packs/zh-cn/RealFuels.jsonl", 2) + ","
                + FileEntry("GameData/UI18n/Plugins/UI18n.dll", 3) + ","
                + FileEntry("GameData/UI18n/Config.cfg", 4)
                + "]",
                null);

            List<ManifestFile> enabled;
            List<ManifestFile> disabled;
            PackInstallFilter.Partition(manifest, manifest.Files, new[] { "Squad" }, out enabled, out disabled);

            Assert.Equal(3, enabled.Count);
            Assert.Equal("Packs/zh-cn/RealFuels.jsonl", enabled[0].ModRelativePath);
            Assert.Equal("Plugins/UI18n.dll", enabled[1].ModRelativePath);
            Assert.Equal("Config.cfg", enabled[2].ModRelativePath);
            Assert.Single(disabled);
            Assert.Equal("Packs/zh-cn/Squad.jsonl", disabled[0].ModRelativePath);
        }

        [Fact]
        public void Partition_NoDisabledIds_KeepsEverythingInOrder()
        {
            UpdateManifest manifest = Parse(
                "[" + FileEntry("GameData/UI18n/Packs/zh-cn/Squad.jsonl", 1) + ","
                + FileEntry("GameData/UI18n/Packs/zh-cn/RealFuels.jsonl", 2) + "]",
                null);

            List<ManifestFile> enabled;
            List<ManifestFile> disabled;
            PackInstallFilter.Partition(manifest, manifest.Files, null, out enabled, out disabled);

            Assert.Equal(2, enabled.Count);
            Assert.Empty(disabled);
            Assert.Equal("Packs/zh-cn/Squad.jsonl", enabled[0].ModRelativePath);
            Assert.Equal("Packs/zh-cn/RealFuels.jsonl", enabled[1].ModRelativePath);
        }

        [Fact]
        public void Partition_AllPacksDisabled_StillInstallsDllAndConfig()
        {
            UpdateManifest manifest = Parse(
                "[" + FileEntry("GameData/UI18n/Packs/zh-cn/Squad.jsonl", 1) + ","
                + FileEntry("GameData/UI18n/Packs/zh-cn/RealFuels.jsonl", 2) + ","
                + FileEntry("GameData/UI18n/Plugins/UI18n.dll", 3) + "]",
                null);

            List<ManifestFile> enabled;
            List<ManifestFile> disabled;
            PackInstallFilter.Partition(manifest, manifest.Files, new[] { "Squad", "RealFuels" }, out enabled, out disabled);

            Assert.Single(enabled);
            Assert.Equal("Plugins/UI18n.dll", enabled[0].ModRelativePath);
            Assert.Equal(2, disabled.Count);
        }

        [Fact]
        public void Partition_EmptyInput_YieldsEmptyLists()
        {
            UpdateManifest manifest = Parse("[]", null);

            List<ManifestFile> enabled;
            List<ManifestFile> disabled;
            PackInstallFilter.Partition(manifest, null, new[] { "Squad" }, out enabled, out disabled);
            Assert.Empty(enabled);
            Assert.Empty(disabled);

            PackInstallFilter.Partition(manifest, manifest.Files, new[] { "Squad" }, out enabled, out disabled);
            Assert.Empty(enabled);
            Assert.Empty(disabled);
        }

        // 大小写不能决定生死：玩家手改 prefs.json 写 squad 也要挡住更新。
        [Fact]
        public void Partition_MatchesModIdCaseInsensitively()
        {
            UpdateManifest manifest = Parse(
                "[" + FileEntry("GameData/UI18n/Packs/zh-cn/Squad.jsonl", 1) + "]",
                null);

            List<ManifestFile> enabled;
            List<ManifestFile> disabled;
            PackInstallFilter.Partition(manifest, manifest.Files, new[] { "sQuAd" }, out enabled, out disabled);

            Assert.Empty(enabled);
            Assert.Single(disabled);
        }

        [Fact]
        public void IsDisabledByPrefs_Matrix()
        {
            UpdateManifest manifest = Parse(
                "[" + FileEntry("GameData/UI18n/Packs/zh-cn/Squad.jsonl", 1) + ","
                + FileEntry("GameData/UI18n/Plugins/UI18n.dll", 2) + "]",
                null);

            ManifestFile pack = manifest.Files[0];
            ManifestFile dll = manifest.Files[1];

            Assert.True(PackInstallFilter.IsDisabledByPrefs(manifest, pack, new[] { "Squad" }));
            Assert.True(PackInstallFilter.IsDisabledByPrefs(manifest, pack, new[] { "squad" }));
            Assert.False(PackInstallFilter.IsDisabledByPrefs(manifest, pack, new[] { "RealFuels" }));
            Assert.False(PackInstallFilter.IsDisabledByPrefs(manifest, pack, null));
            Assert.False(PackInstallFilter.IsDisabledByPrefs(manifest, pack, new string[0]));
            // 非包文件恒 false：DLL 永远要装，否则关了任何 mod 就没法更新本体
            Assert.False(PackInstallFilter.IsDisabledByPrefs(manifest, dll, new[] { "UI18n" }));
            Assert.False(PackInstallFilter.IsDisabledByPrefs(manifest, dll, new[] { "Squad" }));
            Assert.False(PackInstallFilter.IsDisabledByPrefs(manifest, null, new[] { "Squad" }));
        }

        [Fact]
        public void DescribeInstall_MatchesLogContract()
        {
            Assert.Equal(
                "update: installing 93 pack(s), 4 disabled by prefs (UI18n packs skipped)",
                PackInstallFilter.DescribeInstall(93, 4));
        }
    }
}
