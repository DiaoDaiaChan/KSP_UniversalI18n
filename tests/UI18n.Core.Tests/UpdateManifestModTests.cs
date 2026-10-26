using System.Text;
using UI18n.Core.Update;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 清单 v2 的 <c>mods[]</c>（task-71）。三条要钉死的事：
    /// <list type="number">
    /// <item>v1 旧清单（只有 <c>files[]</c>）必须继续能用——不能因为加了字段就把老包拒了。</item>
    /// <item>每条记录的 <c>file</c> 必须是合法包路径（<c>Packs/&lt;lang&gt;/&lt;file&gt;.jsonl</c>），
    /// 否则「按 mod 关包」会被一个随便写的 file 字符串绕过。</item>
    /// <item>坏条目一律整份拒绝，错误串要能指到第几条的哪个字段（否则线上排查只能靠猜）。</item>
    /// </list>
    /// </summary>
    public class UpdateManifestModTests
    {
        // sha256 只校验形状（64 hex），这里用固定常量，避免测试之间互相依赖
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static string Zip(string tail)
        {
            return "{\"version\":\"1.2.0\","
                + "\"zip\":{\"name\":\"UI18n-1.2.0.zip\",\"bytes\":123,\"sha256\":\"" + Sha + "\"}"
                + tail + "}";
        }

        private static string WithMods(string modsJson)
        {
            return Zip(",\"mods\":" + modsJson);
        }

        private static string FileEntry(string path, long bytes)
        {
            return "{\"path\":\"" + path + "\",\"bytes\":" + bytes + ",\"sha256\":\"" + Sha + "\"}";
        }

        [Fact]
        public void V2_ParsesModsWithFiles()
        {
            string json = Zip(
                ",\"files\":["
                + FileEntry("GameData/UI18n/Packs/zh-cn/Squad.jsonl", 11) + ","
                + FileEntry("GameData/UI18n/Packs/zh-cn/RealFuels.il.jsonl", 12)
                + "],\"mods\":["
                + "{\"modId\":\"Squad\",\"displayName\":\"本体\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\","
                + "\"bytes\":11,\"sha256\":\"" + Sha.ToUpperInvariant() + "\",\"rows\":900,\"updatedAt\":\"2026-11-01T00:00:00Z\"},"
                + "{\"modId\":\"RealFuels\",\"file\":\"GameData/UI18n/Packs/zh-cn/RealFuels.il.jsonl\",\"bytes\":12,\"sha256\":\"" + Sha + "\",\"rows\":7}"
                + "]");

            UpdateManifest manifest;
            string error;
            Assert.True(UpdateManifest.TryParse(json, out manifest, out error), error);

            Assert.True(manifest.HasMods);
            Assert.Equal(2, manifest.ModCount);
            Assert.Equal(2, manifest.FileCount);

            ManifestMod squad = manifest.Mods[0];
            Assert.Equal("Squad", squad.ModId);
            Assert.Equal("本体", squad.DisplayName);
            Assert.Equal("GameData/UI18n/Packs/zh-cn/Squad.jsonl", squad.File);
            Assert.Equal("Packs/zh-cn/Squad.jsonl", squad.ModRelativePath);
            Assert.Equal(11L, squad.Bytes);
            Assert.Equal(Sha.ToUpperInvariant(), squad.Sha256);
            Assert.Equal(900L, squad.Rows);
            Assert.Equal("2026-11-01T00:00:00Z", squad.UpdatedAt);

            // displayName 缺省回 modId；rows / updatedAt 缺省为 0 / 空串
            ManifestMod fuels = manifest.Mods[1];
            Assert.Equal("RealFuels", fuels.DisplayName);
            Assert.Equal(7L, fuels.Rows);
            Assert.Equal(string.Empty, fuels.UpdatedAt);

            Assert.NotNull(manifest.FindMod("squad"));
            Assert.Null(manifest.FindMod("Missing"));
            Assert.Null(manifest.FindMod(null));
            Assert.Equal("RealFuels", manifest.ModIdOfFile("packs/zh-cn/realfuels.il.jsonl"));
            Assert.Null(manifest.ModIdOfFile("Packs/zh-cn/Unknown.jsonl"));
            Assert.Contains("mods=2", manifest.Describe());
        }

        // v1 旧清单（没有 mods[]）必须原样可用：安装阶段退回「全装」。
        [Fact]
        public void V1_ManifestWithoutMods_StillParses()
        {
            string json = Zip(",\"files\":[" + FileEntry("GameData/UI18n/Packs/zh-cn/Squad.jsonl", 11) + "]");

            UpdateManifest manifest;
            string error;
            Assert.True(UpdateManifest.TryParse(json, out manifest, out error), error);

            Assert.False(manifest.HasMods);
            Assert.Equal(0, manifest.ModCount);
            Assert.Equal(1, manifest.FileCount);
            Assert.Null(manifest.ModIdOfFile("Packs/zh-cn/Squad.jsonl"));
            Assert.DoesNotContain("mods=", manifest.Describe());
        }

        [Fact]
        public void Mods_EmptyArray_IsAllowedButNotHasMods()
        {
            UpdateManifest manifest;
            string error;
            Assert.True(UpdateManifest.TryParse(WithMods("[]"), out manifest, out error), error);
            Assert.False(manifest.HasMods);
            Assert.Equal(0, manifest.ModCount);
        }

        [Fact]
        public void Mods_RejectsNotArray()
        {
            UpdateManifest manifest;
            string error;
            Assert.False(UpdateManifest.TryParse(WithMods("{}"), out manifest, out error));
            Assert.Equal("mods-not-array", error);

            Assert.False(UpdateManifest.TryParse(WithMods("\"Squad\""), out manifest, out error));
            Assert.Equal("mods-not-array", error);
        }

        [Fact]
        public void Mods_RejectsDuplicateModId()
        {
            string json = WithMods("["
                + "{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\",\"bytes\":1,\"sha256\":\"" + Sha + "\"},"
                + "{\"modId\":\"squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.il.jsonl\",\"bytes\":1,\"sha256\":\"" + Sha + "\"}"
                + "]");

            UpdateManifest manifest;
            string error;
            Assert.False(UpdateManifest.TryParse(json, out manifest, out error));
            Assert.Contains("duplicate-mod-id", error);
        }

        [Fact]
        public void Mods_RejectsDuplicateFile()
        {
            string json = WithMods("["
                + "{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\",\"bytes\":1,\"sha256\":\"" + Sha + "\"},"
                + "{\"modId\":\"Other\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\",\"bytes\":1,\"sha256\":\"" + Sha + "\"}"
                + "]");

            UpdateManifest manifest;
            string error;
            Assert.False(UpdateManifest.TryParse(json, out manifest, out error));
            Assert.Contains("duplicate-mod-file", error);
        }

        [Theory]
        [InlineData("[{}]", "bad-mod-entry:0")]
        [InlineData("[0]", "bad-mod-entry:0")]
        [InlineData("[\"Squad\"]", "bad-mod-entry:0")]
        [InlineData("[{\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\"}]", "missing-mod-id")]
        [InlineData("[{\"modId\":\"\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\"}]", "missing-mod-id")]
        [InlineData("[{\"modId\":\"a..b\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\"}]", "bad-mod-id")]
        [InlineData("[{\"modId\":\"Foo Bar\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\"}]", "bad-mod-id")]
        [InlineData("[{\"modId\":\"Squad\"}]", "missing-file")]
        [InlineData("[{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/../../evil.jsonl\"}]", "unsafe-file")]
        [InlineData("[{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Plugins/UI18n.dll\"}]", "not-a-pack-file")]
        [InlineData("[{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/_smoke.jsonl\"}]", "not-a-pack-file")]
        [InlineData("[{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\",\"sha256\":\"" + Sha + "\"}]", "bad-bytes")]
        [InlineData("[{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\",\"bytes\":-1,\"sha256\":\"" + Sha + "\"}]", "bad-bytes")]
        [InlineData("[{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\",\"bytes\":10}]", "bad-sha256")]
        [InlineData("[{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\",\"bytes\":10,\"sha256\":\"X\"}]", "bad-sha256")]
        [InlineData("[{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\",\"bytes\":10,\"sha256\":\"" + Sha + "\",\"rows\":-1}]", "bad-rows")]
        [InlineData("[{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\",\"bytes\":10,\"sha256\":\"" + Sha + "\",\"rows\":\"7\"}]", "bad-rows")]
        public void Mods_RejectsBadEntries(string modsJson, string expected)
        {
            UpdateManifest manifest;
            string error;
            Assert.False(UpdateManifest.TryParse(WithMods(modsJson), out manifest, out error));
            Assert.Contains(expected, error);
        }

        // 索引必须指到出错那一条：第 2 条坏掉时报 bad-mod-entry:1，而不是笼统的 0。
        [Fact]
        public void Mods_ErrorIndexPointsAtFailingEntry()
        {
            string json = WithMods("["
                + "{\"modId\":\"Good\",\"file\":\"GameData/UI18n/Packs/zh-cn/Good.jsonl\",\"bytes\":1,\"sha256\":\"" + Sha + "\"},"
                + "{\"modId\":\"\",\"file\":\"GameData/UI18n/Packs/zh-cn/Bad.jsonl\"}"
                + "]");

            UpdateManifest manifest;
            string error;
            Assert.False(UpdateManifest.TryParse(json, out manifest, out error));
            Assert.Contains("bad-mod-entry:1", error);
        }

        [Fact]
        public void ManifestMod_ToString_MentionsPathAndRows()
        {
            string json = WithMods("[{\"modId\":\"Squad\",\"file\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\","
                + "\"bytes\":11,\"sha256\":\"" + Sha + "\",\"rows\":900}]");

            UpdateManifest manifest;
            string error;
            Assert.True(UpdateManifest.TryParse(json, out manifest, out error), error);
            string text = manifest.Mods[0].ToString();
            Assert.Contains("Squad", text);
            Assert.Contains("Packs/zh-cn/Squad.jsonl", text);
            Assert.Contains("rows=900", text);
        }
    }
}
