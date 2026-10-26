using System;
using System.Collections.Generic;
using System.Text;
using UI18n.Core.Dict;
using UI18n.Core.Update;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// packs-only 更新包（v1.6 M3）里的 <c>mods[]</c>：一个 mod 可以有多条包（<c>Foo.jsonl</c> + <c>Foo.il.jsonl</c>
    /// 这类双 channel 的真实例子有 15 个），所以聚合形态用 <c>files[]</c> 而不是单个 <c>file</c>。
    /// 这里钉死四件事：
    /// <list type="number">
    /// <item>聚合形态能解析，<c>File</c> / <c>ModRelativePath</c> 取第一条（老代码只读这两处，不能崩）。</item>
    /// <item>两种形态同时出现 = 歧义，整份拒绝（绝不猜哪个是真的）。</item>
    /// <item>mods[] 一旦存在就是权威表：<c>ResolveModId</c> 必须按清单走，不能偷偷退回文件名派生
    /// （真实场景里文件名与 modId 不一致时，玩家开关会默默失效）。</item>
    /// <item>坏条目一律整份拒绝，错误串要能指到第几条的哪个字段。</item>
    /// </list>
    /// </summary>
    public class UpdateManifestFilesAggregationTests
    {
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string Squad = "GameData/UI18n/Packs/zh-cn/Squad.jsonl";
        private const string SquadIl = "GameData/UI18n/Packs/zh-cn/Squad.il.jsonl";

        private static string FileEntry(string path, long bytes)
        {
            return "{\"path\":\"" + path + "\",\"bytes\":" + bytes + ",\"sha256\":\"" + Sha + "\"}";
        }

        private static string Manifest(string filesJson, string modsJson)
        {
            string json = "{\"version\":\"1.2.3\","
                + "\"zip\":{\"name\":\"UI18n-1.2.3-zh-cn-packs.zip\",\"bytes\":123,\"sha256\":\"" + Sha + "\"}";
            if (!string.IsNullOrEmpty(filesJson))
            {
                json += ",\"files\":[" + filesJson + "]";
            }

            if (!string.IsNullOrEmpty(modsJson))
            {
                json += ",\"mods\":[" + modsJson + "]";
            }

            return json + "}";
        }

        private static UpdateManifest Parse(string filesJson, string modsJson)
        {
            UpdateManifest manifest;
            string error;
            Assert.True(
                UpdateManifest.TryParse(Manifest(filesJson, modsJson), out manifest, out error),
                "这份清单应当解析成功，实际 error=" + error);
            Assert.True(string.IsNullOrEmpty(error));
            return manifest;
        }

        private static string ParseError(string filesJson, string modsJson)
        {
            UpdateManifest manifest;
            string error;
            bool ok = UpdateManifest.TryParse(Manifest(filesJson, modsJson), out manifest, out error);
            Assert.False(ok, "这份清单必须被拒绝，实际解析成功了");
            Assert.Null(manifest);
            Assert.False(string.IsNullOrEmpty(error), "拒绝时必须给出错误串");
            return error;
        }

        // ------------------------------------------------------------ 正向

        [Fact]
        public void AggregatedModWithFiles_ParsesAndKeepsEveryPath()
        {
            UpdateManifest manifest = Parse(
                FileEntry(Squad, 11) + "," + FileEntry(SquadIl, 12),
                "{\"modId\":\"Squad\",\"files\":[\"" + Squad + "\",\"" + SquadIl + "\"],"
                + "\"rows\":95,\"bytes\":23,\"updatedAt\":\"2026-11-01T00:00:00Z\"}");

            Assert.True(manifest.HasMods);
            Assert.Equal(1, manifest.ModCount);

            ManifestMod mod = manifest.Mods[0];
            Assert.Equal("Squad", mod.ModId);
            Assert.Equal(2, mod.Files.Count);
            Assert.Equal(2, mod.FileCount);
            Assert.Equal(2, mod.ModRelativePaths.Count);
            Assert.Equal(Squad, mod.Files[0]);
            Assert.Equal(SquadIl, mod.Files[1]);
            Assert.Equal("Packs/zh-cn/Squad.jsonl", mod.ModRelativePaths[0]);
            Assert.Equal("Packs/zh-cn/Squad.il.jsonl", mod.ModRelativePaths[1]);

            // 老字段只读第一处：不能因为聚合就变成 null / 空串
            Assert.Equal(Squad, mod.File);
            Assert.Equal("Packs/zh-cn/Squad.jsonl", mod.ModRelativePath);
            Assert.Equal(23L, mod.Bytes);
            Assert.Equal(95L, mod.Rows);
            Assert.Equal("2026-11-01T00:00:00Z", mod.UpdatedAt);

            // 聚合形态的 sha256 是"合计摘要"，可缺失——缺了就不许假装有
            Assert.False(mod.HasSha256);
            Assert.Equal(string.Empty, mod.Sha256);

            // 两条路径都要进 file → modId 表（少一条 = 那个包按 mod 开关失效）
            Assert.Equal("Squad", manifest.ModIdOfFile("Packs/zh-cn/Squad.jsonl"));
            Assert.Equal("Squad", manifest.ModIdOfFile("Packs/zh-cn/Squad.il.jsonl"));

            // ToString 要提示还有别的文件，否则日志里看着像少了一半
            Assert.Contains("(+1 more)", mod.ToString(), StringComparison.Ordinal);
            Assert.Contains("mods=1", manifest.Describe(), StringComparison.Ordinal);
        }

        [Fact]
        public void AggregatedModWithSha256_IsAccepted()
        {
            UpdateManifest manifest = Parse(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"files\":[\"" + Squad + "\"],\"bytes\":11,"
                + "\"sha256\":\"" + Sha.ToUpperInvariant() + "\"}");

            ManifestMod mod = manifest.Mods[0];
            Assert.True(mod.HasSha256);
            Assert.Equal(Sha.ToUpperInvariant(), mod.Sha256);
            Assert.Equal(1, mod.FileCount);
        }

        [Fact]
        public void DisplayNameFallsBackToModId_ForAggregatedMod()
        {
            UpdateManifest manifest = Parse(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"files\":[\"" + Squad + "\"],\"bytes\":11}");

            Assert.Equal("Squad", manifest.Mods[0].DisplayName);
            Assert.Equal(0L, manifest.Mods[0].Rows);
            Assert.Equal(string.Empty, manifest.Mods[0].UpdatedAt);
        }

        /// <summary>
        /// 核心用例：清单声明 modId=RealFuels，可文件名明显是 Squad 的包。
        /// 只要 mods[] 在，ResolveModId 必须返回 RealFuels；按 Squad 关包不能误伤它。
        /// </summary>
        [Fact]
        public void ModsArrayIsAuthoritative_ResolveModIdIgnoresFileName()
        {
            UpdateManifest manifest = Parse(
                FileEntry(Squad, 11),
                "{\"modId\":\"RealFuels\",\"files\":[\"" + Squad + "\"],\"bytes\":11}");

            ManifestFile file = manifest.Find("Packs/zh-cn/Squad.jsonl");
            Assert.NotNull(file);

            // 两条兜底路径都只会说 Squad——这正是不能信任文件名派生的证据
            Assert.Equal("Squad", PackFilter.ModIdFromPath(file.ModRelativePath));
            Assert.Equal("Squad", PackInstallFilter.ModIdOfPackPath(file.ModRelativePath));

            Assert.Equal("RealFuels", manifest.ModIdOfFile(file.ModRelativePath));
            Assert.Equal("RealFuels", PackInstallFilter.ResolveModId(manifest, file));

            List<ManifestFile> enabled;
            List<ManifestFile> disabled;

            // 按清单里的真名关 → 装不上
            PackInstallFilter.Partition(
                manifest,
                manifest.Files,
                new List<string> { "RealFuels" },
                out enabled,
                out disabled);
            Assert.Empty(enabled);
            Assert.Single(disabled);

            // 按文件名派生的名字关 → 不该误伤（玩家关的是 Squad 本体，不是 RealFuels）
            PackInstallFilter.Partition(
                manifest,
                manifest.Files,
                new List<string> { "Squad" },
                out enabled,
                out disabled);
            Assert.Single(enabled);
            Assert.Empty(disabled);
        }

        [Fact]
        public void NoModsArray_ResolveModIdFallsBackToFileName()
        {
            // v1 清单（只有 files[]）必须继续能装
            UpdateManifest manifest = Parse(FileEntry(Squad, 11), null);
            Assert.False(manifest.HasMods);
            Assert.Equal(0, manifest.ModCount);

            ManifestFile file = manifest.Find("Packs/zh-cn/Squad.jsonl");
            Assert.NotNull(file);
            Assert.Equal("Squad", PackInstallFilter.ResolveModId(manifest, file));
        }

        // ------------------------------------------------------------ 反向：形状错误

        [Fact]
        public void BothFileAndFiles_IsRejected()
        {
            string error = ParseError(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"file\":\"" + Squad + "\",\"files\":[\"" + Squad + "\"],\"bytes\":11}");

            Assert.Equal("bad-mod-entry:0: both-file-and-files", error);
        }

        [Fact]
        public void NeitherFileNorFiles_IsRejected()
        {
            string error = ParseError(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"bytes\":11}");

            Assert.Equal("bad-mod-entry:0: missing-file", error);
        }

        [Fact]
        public void FilesMustBeAnArray()
        {
            string error = ParseError(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"files\":\"" + Squad + "\",\"bytes\":11}");

            Assert.Equal("bad-mod-entry:0: bad-files", error);
        }

        [Fact]
        public void EmptyFilesArray_IsRejected()
        {
            string error = ParseError(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"files\":[],\"bytes\":11}");

            Assert.Equal("bad-mod-entry:0: empty-files", error);
        }

        [Fact]
        public void NonStringElementInFiles_IsRejected()
        {
            string error = ParseError(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"files\":[123],\"bytes\":11}");

            Assert.Equal("bad-mod-entry:0: bad-files", error);
        }

        [Fact]
        public void DuplicatePathInsideOneMod_IsRejected()
        {
            string error = ParseError(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"files\":[\"" + Squad + "\",\"Packs/zh-cn/Squad.jsonl\"],\"bytes\":11}");

            Assert.Equal("bad-mod-entry:0: duplicate-file:Packs/zh-cn/Squad.jsonl", error);
        }

        [Fact]
        public void SameFileClaimedByTwoMods_IsRejected()
        {
            string error = ParseError(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"files\":[\"" + Squad + "\"],\"bytes\":11},"
                + "{\"modId\":\"RealFuels\",\"files\":[\"" + Squad + "\"],\"bytes\":11}");

            Assert.Equal("duplicate-mod-file: Packs/zh-cn/Squad.jsonl", error);
        }

        [Fact]
        public void NonPackFileInMods_IsRejected()
        {
            // mods[] 只登记语言包；DLL / cfg 混进来要么是构建脚本分组算错了，要么是有人手改清单
            string error = ParseError(
                null,
                "{\"modId\":\"Squad\",\"files\":[\"GameData/UI18n/Plugins/UI18n.dll\"],\"bytes\":11}");

            Assert.Equal("bad-mod-entry:0: not-a-pack-file", error);
        }

        [Fact]
        public void UnmappablePathInMods_IsRejected()
        {
            string error = ParseError(
                null,
                "{\"modId\":\"Squad\",\"files\":[\"GameData/UI18n/Packs/../../evil.jsonl\"],\"bytes\":11}");

            Assert.Contains("bad-mod-entry:0: unsafe-file:", error, StringComparison.Ordinal);
        }

        [Fact]
        public void BadSha256InAggregatedMod_IsRejected()
        {
            string error = ParseError(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"files\":[\"" + Squad + "\"],\"bytes\":11,\"sha256\":\"too-short\"}");

            Assert.Equal("bad-mod-entry:0: bad-sha256", error);
        }

        [Fact]
        public void NegativeRowsInAggregatedMod_IsRejected()
        {
            string error = ParseError(
                FileEntry(Squad, 11),
                "{\"modId\":\"Squad\",\"files\":[\"" + Squad + "\"],\"bytes\":11,\"rows\":-1}");

            Assert.Equal("bad-mod-entry:0: bad-rows", error);
        }

        [Fact]
        public void BadModIdInAggregatedMod_IsRejected()
        {
            string error = ParseError(
                FileEntry(Squad, 11),
                "{\"modId\":\"..\",\"files\":[\"" + Squad + "\"],\"bytes\":11}");

            Assert.Equal("bad-mod-entry:0: bad-mod-id", error);
        }
    }
}
