using System;
using UI18n.Core.Update;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 清单形状契约（<c>readerContract</c>）的协商规则。
    /// <para>
    /// 背景：1.6.0 事故 —— 出包脚本自 v1.2 起按 <c>files[]</c> 聚合形态写 <c>mods[]</c>，而当时的读取器只认
    /// 单文件 <c>file</c>，于是游戏里每份清单都被 <c>bad-mod-entry:0: missing-file</c> 整份拒掉，
    /// 联网更新从 v1.2 到 v1.6 一次都没生效。两边的断言都在各自语言里，谁也不校验对方。
    /// 现在清单带 <c>readerContract</c>，读取器只接受自己认识的那个值；出包脚本必须用这个读取器
    /// 真解一遍（tools/manifest-roundtrip），发布包还要核对 DLL 里有这条能力标记（release-checks 第 12 条）。
    /// </para>
    /// </summary>
    public class UpdateManifestContractTests
    {
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string Pack = "GameData/UI18n/Packs/zh-cn/Squad.jsonl";

        private static string Manifest(string contractLine)
        {
            return "{"
                + contractLine
                + "\"version\":\"1.2.3\","
                + "\"zip\":{\"name\":\"UI18n-1.2.3-zh-cn-packs.zip\",\"bytes\":123,\"sha256\":\"" + Sha + "\"},"
                + "\"files\":[{\"path\":\"" + Pack + "\",\"bytes\":10,\"sha256\":\"" + Sha + "\"}],"
                + "\"mods\":[{\"modId\":\"Squad\",\"files\":[\"" + Pack + "\"],\"bytes\":10}]"
                + "}";
        }

        [Fact]
        public void Manifest_WithCurrentContract_Parses()
        {
            UpdateManifest manifest;
            string error;
            string json = Manifest("\"readerContract\":\"" + UpdateManifest.ReaderContract + "\",");
            Assert.True(UpdateManifest.TryParse(json, out manifest, out error), "error=" + error);
            Assert.Equal(1, manifest.ModCount);
        }

        [Fact]
        public void Manifest_WithoutContract_StillParses_LegacyCompat()
        {
            // 旧清单没有这个键：必须继续能装，否则「读取器更新」会把上一版发出的清单全废掉
            UpdateManifest manifest;
            string error;
            Assert.True(UpdateManifest.TryParse(Manifest(string.Empty), out manifest, out error), "error=" + error);
            Assert.Equal(1, manifest.ModCount);
        }

        [Fact]
        public void Manifest_WithNewerContract_IsRejected_WithReadableReason()
        {
            UpdateManifest manifest;
            string error;
            bool ok = UpdateManifest.TryParse(Manifest("\"readerContract\":\"ui18n-manifest-reader/99\","), out manifest, out error);
            Assert.False(ok);
            Assert.Null(manifest);
            Assert.Contains("unsupported-reader-contract", error);
            // 报错里要同时给「对方声明的」与「本读取器认识的」，否则现场只能猜
            Assert.Contains("ui18n-manifest-reader/99", error);
            Assert.Contains(UpdateManifest.ReaderContract, error);
        }

        [Fact]
        public void Manifest_WithEmptyContract_FallsBackToLegacyParsing()
        {
            UpdateManifest manifest;
            string error;
            Assert.True(UpdateManifest.TryParse(Manifest("\"readerContract\":\"\","), out manifest, out error), "error=" + error);
        }

        [Fact]
        public void ReaderContract_ValueIsPinned()
        {
            // 值本身是契约：改它等于「换一种清单形状」，出包脚本会把它写进新清单、发布断言会核对包里的 DLL。
            // 这条断言只是让改动必须是有意的（改了这里就要在 docs/10 §9 与 build-update-package.ps1 同步）。
            Assert.Equal("ui18n-manifest-reader/2", UpdateManifest.ReaderContract);
        }
    }
}
