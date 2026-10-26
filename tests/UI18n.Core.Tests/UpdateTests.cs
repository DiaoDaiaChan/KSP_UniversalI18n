using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using UI18n.Core.Update;
using Xunit;
using CoreEntry = UI18n.Core.Update.ZipEntry;
using CoreZip = UI18n.Core.Update.ZipArchive;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// v1.2 联网分发骨架的纯逻辑单测（task-40）。
    /// 这一层刻意不碰网络、不碰磁盘布局：版本比较、URL 门禁、zip-slip 白名单、sha256/bytes 校验、
    /// manifest 解析、update-state 读写、inflate + zip 容器解析，全部是可离线断言的数据变换。
    /// zip 相关用例用 net10 的 System.IO.Compression 当参照物造包并交叉验证，避免「拿自己证自己」。
    /// </summary>
    public class ReleaseVersionTests
    {
        [Theory]
        [InlineData("1.2.3", 1, 2, 3)]
        [InlineData("0.0.0", 0, 0, 0)]
        [InlineData(" 10.20.30 ", 10, 20, 30)]
        [InlineData("1.1", 1, 1, 0)]
        [InlineData("1.2.3.4", 1, 2, 3)]
        public void TryParse_AcceptsValidShapes(string text, int major, int minor, int patch)
        {
            ReleaseVersion version;
            Assert.True(ReleaseVersion.TryParse(text, out version));
            Assert.True(version.IsValid);
            Assert.Equal(major, version.Major);
            Assert.Equal(minor, version.Minor);
            Assert.Equal(patch, version.Patch);
        }

        // 运行时版本串是 1.1.0+29ffa5c...（AssemblyInformationalVersion 带 commit），必须能解析
        [Theory]
        [InlineData("1.1.0+29ffa5c0ffee")]
        [InlineData("1.1.0-beta.1")]
        [InlineData("1.1.0-beta+29ffa5c")]
        public void TryParse_StripsBuildAndPrereleaseSuffix(string text)
        {
            ReleaseVersion version;
            Assert.True(ReleaseVersion.TryParse(text, out version));
            Assert.Equal("1.1.0", version.ToString());
        }

        // 负向：非法输入不抛，只返回 false + None
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1")]
        [InlineData("v1.2.3")]
        [InlineData("1.2.x")]
        [InlineData("1..2")]
        [InlineData("1.2.3.4.5")]
        [InlineData("-1.2.3")]
        public void TryParse_RejectsGarbageWithoutThrowing(string text)
        {
            ReleaseVersion version;
            Assert.False(ReleaseVersion.TryParse(text, out version));
            Assert.False(version.IsValid);
            Assert.Equal(ReleaseVersion.None, version);
            Assert.Equal(0, version.Major);
        }

        [Fact]
        public void CompareTo_IsNumericNotLexicographic()
        {
            ReleaseVersion a = ReleaseVersion.ParseOrNone("1.9.9");
            ReleaseVersion b = ReleaseVersion.ParseOrNone("1.10.0");
            Assert.True(a < b);
            Assert.True(b > a);
            Assert.True(b.IsNewerThan(a));
            Assert.False(a.IsNewerThan(b));
            Assert.True(a.CompareTo(a) == 0);
            Assert.Equal(a, ReleaseVersion.Create(1, 9, 9));
            Assert.True(a == ReleaseVersion.Create(1, 9, 9));
            Assert.True(a != b);
        }

        [Fact]
        public void ParseOrNone_ReturnsNoneInsteadOfThrowing()
        {
            Assert.Equal(ReleaseVersion.None, ReleaseVersion.ParseOrNone("nonsense"));
            Assert.Equal(ReleaseVersion.None, ReleaseVersion.ParseOrNone(null));
            Assert.Equal("1.2.0", ReleaseVersion.ParseOrNone("1.2.0").ToString());
            Assert.Equal("0.0.0", ReleaseVersion.None.ToString());
        }
    }

    public class Sha256HexTests
    {
        private const string AbcSha = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

        [Fact]
        public void OfBytes_MatchesKnownVector()
        {
            byte[] data = Encoding.UTF8.GetBytes("abc");
            Assert.Equal(AbcSha, Sha256Hex.OfBytes(data));
            Assert.Equal(Sha256Hex.HexLength, Sha256Hex.OfBytes(data).Length);
            Assert.Equal(AbcSha, Sha256Hex.OfBytes(data, 0, data.Length));
        }

        [Fact]
        public void OfBytes_RangeMatchesSlice()
        {
            byte[] data = Encoding.UTF8.GetBytes("xxxabcxxx");
            Assert.Equal(AbcSha, Sha256Hex.OfBytes(data, 3, 3));
        }

        [Fact]
        public void OfBytes_OutOfRangeThrows()
        {
            byte[] data = Encoding.UTF8.GetBytes("abc");
            Assert.Throws<ArgumentOutOfRangeException>(() => Sha256Hex.OfBytes(data, 2, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => Sha256Hex.OfBytes(data, -1, 1));
        }

        [Fact]
        public void Normalize_AcceptsEitherCaseAndRejectsJunk()
        {
            Assert.Equal(AbcSha, Sha256Hex.Normalize(AbcSha));
            Assert.Equal(AbcSha, Sha256Hex.Normalize(AbcSha.ToLowerInvariant()));
            Assert.Equal(AbcSha, Sha256Hex.Normalize("  " + AbcSha.ToLowerInvariant() + "  "));
            Assert.Null(Sha256Hex.Normalize("not-a-hash"));
            Assert.Null(Sha256Hex.Normalize(AbcSha.Substring(1)));
            Assert.Null(Sha256Hex.Normalize(null));
            Assert.True(Sha256Hex.IsHex64(AbcSha.ToLowerInvariant()));
            Assert.False(Sha256Hex.IsHex64("zz"));
        }

        [Fact]
        public void EqualsIgnoreCase_And_DescribeBytes()
        {
            Assert.True(Sha256Hex.EqualsIgnoreCase(AbcSha, AbcSha.ToLowerInvariant()));
            Assert.False(Sha256Hex.EqualsIgnoreCase(AbcSha, "junk"));
            Assert.False(Sha256Hex.EqualsIgnoreCase(null, null));
            Assert.Equal("1,089,492 B", Sha256Hex.DescribeBytes(1089492));
        }
    }

    public class ZipSafetyTests
    {
        [Fact]
        public void IsSafeRelativePath_AcceptsPackEntries()
        {
            Assert.True(ZipSafety.IsSafeRelativePath("GameData/UI18n/Packs/zh-cn/Squad.jsonl"));
            Assert.True(ZipSafety.IsSafeRelativePath("./GameData/UI18n/Config.cfg"));
            Assert.False(ZipSafety.IsSafeRelativePath("../evil.jsonl"));
            Assert.False(ZipSafety.IsSafeRelativePath(null));
        }

        // 负向：zip-slip 的经典形态一个都不能过
        [Theory]
        [InlineData("../../etc/passwd")]
        [InlineData("Packs/../../evil.jsonl")]
        [InlineData("/etc/passwd")]
        [InlineData("C:/Windows/system32/x.dll")]
        [InlineData("\\\\server\\share\\x.jsonl")]
        [InlineData("GameData//UI18n/x.jsonl")]
        public void TryNormalize_RejectsUnsafeEntries(string entry)
        {
            string normalized;
            string reason;
            Assert.False(ZipSafety.TryNormalize(entry, out normalized, out reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void TryNormalize_RejectsEmptyAndControlChars()
        {
            string normalized;
            string reason;
            Assert.False(ZipSafety.TryNormalize("", out normalized, out reason));
            Assert.Equal(ZipSafety.ReasonEmpty, reason);
            Assert.False(ZipSafety.TryNormalize(null, out normalized, out reason));
            Assert.Equal(ZipSafety.ReasonEmpty, reason);
            Assert.False(ZipSafety.TryNormalize("GameData/UI18n/x\u0001.jsonl", out normalized, out reason));
            Assert.Equal(ZipSafety.ReasonControlChar, reason);
        }

        [Fact]
        public void TryNormalize_AcceptsAndNormalizesDotSegments()
        {
            string normalized;
            string reason;
            Assert.True(ZipSafety.TryNormalize("./GameData/UI18n/Packs/zh-cn/Squad.jsonl", out normalized, out reason));
            Assert.Equal("GameData/UI18n/Packs/zh-cn/Squad.jsonl", normalized);
            Assert.Null(reason);
        }

        [Fact]
        public void TryMapToModRelative_StripsModRootPrefix()
        {
            string modRelative;
            string reason;
            Assert.True(ZipSafety.TryMapToModRelative("GameData/UI18n/Packs/zh-cn/Squad.jsonl", out modRelative, out reason));
            Assert.Equal("Packs/zh-cn/Squad.jsonl", modRelative);

            // 不在 GameData/UI18n/ 之下的条目：剥前缀层不拦（它只做归一化），
            // 真正的白名单在 TryGetPackTarget —— 装机时只会认 Packs/<lang>/<file>.jsonl。
            Assert.True(ZipSafety.TryMapToModRelative("GameData/Squad/x.cfg", out modRelative, out reason));
            Assert.Equal("GameData/Squad/x.cfg", modRelative);
            string language;
            string fileName;
            Assert.False(ZipSafety.TryGetPackTarget(modRelative, out language, out fileName, out reason));
            Assert.Equal(ZipSafety.ReasonNotPack, reason);
        }

        [Fact]
        public void TryGetPackTarget_AcceptsOnlyPacksLanguageJsonl()
        {
            string language;
            string fileName;
            string reason;
            Assert.True(ZipSafety.TryGetPackTarget("Packs/zh-cn/Squad.jsonl", out language, out fileName, out reason));
            Assert.Equal("zh-cn", language);
            Assert.Equal("Squad.jsonl", fileName);
        }

        // 负向：白名单只有 Packs/<lang>/<file>.jsonl 三层，别的层级一律不装
        [Theory]
        [InlineData("Plugins/UI18n.Runtime.dll")]
        [InlineData("Config.cfg")]
        [InlineData("Packs/zh-cn/nested/Squad.jsonl")]
        [InlineData("Packs/zh-cn/Squad.json")]
        [InlineData("Packs/zh-cn/_smoke.jsonl")]
        [InlineData("Packs/z/Squad.jsonl")]
        [InlineData("Packs//Squad.jsonl")]
        [InlineData("Packs/zh-cn/Squad.jsonl.exe")]
        public void TryGetPackTarget_RejectsEverythingElse(string modRelative)
        {
            string language;
            string fileName;
            string reason;
            Assert.False(ZipSafety.TryGetPackTarget(modRelative, out language, out fileName, out reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void IsValidLanguageTag_And_IsValidPackFileName()
        {
            Assert.True(ZipSafety.IsValidLanguageTag("zh-cn"));
            Assert.True(ZipSafety.IsValidLanguageTag("zh"));
            Assert.True(ZipSafety.IsValidLanguageTag("pt-br"));
            Assert.False(ZipSafety.IsValidLanguageTag("z"));
            Assert.False(ZipSafety.IsValidLanguageTag("-zh"));
            Assert.False(ZipSafety.IsValidLanguageTag("zh-"));
            Assert.False(ZipSafety.IsValidLanguageTag("zh--cn"));
            Assert.False(ZipSafety.IsValidLanguageTag("this-language-tag-is-way-too-long"));

            Assert.True(ZipSafety.IsValidPackFileName("Squad.jsonl"));
            Assert.True(ZipSafety.IsValidPackFileName("000_TexturesUnlimited.jsonl"));
            Assert.False(ZipSafety.IsValidPackFileName(".hidden.jsonl"));
            Assert.False(ZipSafety.IsValidPackFileName("a b.jsonl"));
            Assert.False(ZipSafety.IsValidPackFileName("Squad.jsonl.bak"));
        }
    }

    public class UpdateUrlGateTests
    {
        [Theory]
        [InlineData("https://example.com/ui18n")]
        [InlineData("https://cdn.example.com/a/b/")]
        [InlineData("http://127.0.0.1:8099")]
        [InlineData("http://localhost:8099/")]
        // 官方通道的真实地址：v1.5 之前在 Mono 上被 Uri.TryCreate 判成 invalid-url，永久不联网
        [InlineData("https://raw.githubusercontent.com/DiaoDaiaChan/KSP_UniversalI18n/update")]
        [InlineData("HTTPS://EXAMPLE.COM/x")]
        [InlineData("http://LOCALHOST:8099")]
        [InlineData("http://[::1]:8099")]
        [InlineData("  https://example.com/x  ")]
        public void IsAllowedBaseUrl_AcceptsHttpsAndLoopback(string url)
        {
            string reason;
            Assert.True(UpdateUrlGate.IsAllowedBaseUrl(url, out reason));
            Assert.Null(reason);
        }

        // 负向：明文 http 只允许本机回环，避免把玩家流量暴露在公网明文上
        [Theory]
        [InlineData("http://example.com/ui18n", UpdateUrlGate.ReasonHost)]
        [InlineData("ftp://example.com/x", UpdateUrlGate.ReasonScheme)]
        [InlineData("file:///c:/x", UpdateUrlGate.ReasonScheme)]
        [InlineData("", UpdateUrlGate.ReasonEmpty)]
        [InlineData("   ", UpdateUrlGate.ReasonEmpty)]
        [InlineData("not a url", UpdateUrlGate.ReasonBadUrl)]
        [InlineData("https://", UpdateUrlGate.ReasonBadUrl)]
        [InlineData("https:///path", UpdateUrlGate.ReasonBadUrl)]
        [InlineData("//example.com/x", UpdateUrlGate.ReasonBadUrl)]
        [InlineData("mailto:someone@example.com", UpdateUrlGate.ReasonBadUrl)]
        [InlineData("http://192.168.1.5/ui18n", UpdateUrlGate.ReasonHost)]
        [InlineData("http://raw.githubusercontent.com/x", UpdateUrlGate.ReasonHost)]
        public void IsAllowedBaseUrl_RejectsEverythingElse(string url, string expectedReason)
        {
            string reason;
            Assert.False(UpdateUrlGate.IsAllowedBaseUrl(url, out reason));
            Assert.Equal(expectedReason, reason);
        }

        [Fact]
        public void BuildManifestUrl_NeverDoublesSlashes()
        {
            Assert.Equal("https://example.com/ui18n/stable/manifest.json", UpdateUrlGate.BuildManifestUrl("https://example.com/ui18n", "stable"));
            Assert.Equal("https://example.com/ui18n/stable/manifest.json", UpdateUrlGate.BuildManifestUrl("https://example.com/ui18n/", "stable"));
            Assert.Equal("http://127.0.0.1:8099/ok/stable/manifest.json", UpdateUrlGate.BuildManifestUrl("http://127.0.0.1:8099/ok", "stable"));
            Assert.Equal("https://example.com/a/stable/UI18n-1.2.0.zip", UpdateUrlGate.BuildAssetUrl("https://example.com/a", "stable", "UI18n-1.2.0.zip"));
            Assert.Equal("https://example.com/a", UpdateUrlGate.NormalizeBaseUrl("https://example.com/a/"));
        }

        [Theory]
        [InlineData("stable")]
        [InlineData("testing")]
        [InlineData("dev-2026")]
        public void IsValidChannel_AcceptsSlug(string channel)
        {
            Assert.True(UpdateUrlGate.IsValidChannel(channel));
        }

        [Theory]
        [InlineData("")]
        [InlineData("../stable")]
        [InlineData("a/b")]
        [InlineData("with space")]
        [InlineData("a\\b")]
        [InlineData("this-channel-name-is-far-too-long-for-the-limit")]
        public void IsValidChannel_RejectsPathLikeValues(string channel)
        {
            Assert.False(UpdateUrlGate.IsValidChannel(channel));
        }
    }

    public class UpdateJsonTests
    {
        [Fact]
        public void TryParse_ReadsObjectWithEscapes()
        {
            JsonValue root;
            string error;
            bool ok = Json.TryParse("{\"a\":\"line\\nbreak\",\"b\":[1,2,3],\"c\":true,\"d\":null}", out root, out error);
            Assert.True(ok, error);
            Assert.Equal(JsonKind.Object, root.Kind);
            Assert.Equal("line\nbreak", root.GetString("a", ""));
            Assert.Equal(3, root.Get("b").Count);
            Assert.True(root.Get("c").AsBool());
            Assert.True(root.Get("d").IsNull);
        }

        [Fact]
        public void TryParse_ToleratesBomAndWhitespace()
        {
            JsonValue root;
            string error;
            Assert.True(Json.TryParse("\uFEFF  \r\n {\"a\":1}  \r\n", out root, out error), error);
            Assert.Equal(1, root.Get("a").AsDouble());
        }

        // 负向：解析器只返回 false + 原因，永不抛
        [Theory]
        [InlineData("", "unexpected-end")]
        [InlineData("{", "expected-member-name")]
        [InlineData("{\"a\":1} trailing", "trailing-content")]
        [InlineData("{\"a\":1,\"a\":2}", "duplicate-key")]
        [InlineData("[1,2,]", "unexpected-char")]
        [InlineData("{'a':1}", "expected-member-name")]
        public void TryParse_RejectsMalformedJson(string text, string errorFragment)
        {
            JsonValue root;
            string error;
            Assert.False(Json.TryParse(text, out root, out error));
            Assert.Contains(errorFragment, error);
        }

        [Fact]
        public void TryParse_RejectsOverlyDeepDocuments()
        {
            string deep = new string('[', 80) + new string(']', 80);
            JsonValue root;
            string error;
            Assert.False(Json.TryParse(deep, out root, out error));
            Assert.Contains("too-deep", error);
        }

        [Fact]
        public void TryGetLong_RejectsFractionalNumbers()
        {
            JsonValue root;
            string error;
            Assert.True(Json.TryParse("{\"n\":3,\"m\":1.5,\"e\":1e3}", out root, out error), error);

            long value;
            Assert.True(root.Get("n").TryGetLong(out value));
            Assert.Equal(3L, value);

            // 浮点不能悄悄取整：manifest 里的 bytes 必须是整数
            Assert.False(root.Get("m").TryGetLong(out value));
            Assert.False(root.Get("e").TryGetLong(out value));
            Assert.False(root.TryGetLong("missing", out value));
        }

        [Fact]
        public void JsonWriter_EscapesControlCharacters()
        {
            Assert.Equal("\"a\\nb\"", JsonWriter.Escape("a\nb"));
            Assert.Equal("\"a\\tb\"", JsonWriter.Escape("a\tb"));
            Assert.Equal("\"a\\\"b\"", JsonWriter.Escape("a\"b"));
            Assert.Equal("\"a\\\\b\"", JsonWriter.Escape("a\\b"));
            Assert.Equal("\"a\\u0001b\"", JsonWriter.Escape("a\u0001b"));

            StringBuilder sb = new StringBuilder();
            JsonWriter.WriteString(sb, "x\ny");
            Assert.Equal("\"x\\ny\"", sb.ToString());
            Assert.Equal("null", JsonWriter.Escape(null));
        }
    }

    public class UpdateManifestTests
    {
        private static readonly string ContentSha = Sha256Hex.OfBytes(Encoding.UTF8.GetBytes("abc"));

        private static string Manifest(string version, string zipSha, string filesJson)
        {
            return "{\"version\":\"" + version + "\","
                + "\"zip\":{\"name\":\"UI18n-1.2.0.zip\",\"bytes\":123,\"sha256\":\"" + zipSha + "\"},"
                + "\"files\":" + filesJson + "}";
        }

        private static string FileEntry(string path, long bytes, string sha)
        {
            return "{\"path\":\"" + path + "\",\"bytes\":" + bytes.ToString(CultureInfo.InvariantCulture) + ",\"sha256\":\"" + sha + "\"}";
        }

        [Fact]
        public void TryParse_ReadsVersionZipAndFiles()
        {
            string json = Manifest("1.2.0", ContentSha,
                "[" + FileEntry("GameData/UI18n/Packs/zh-cn/Squad.jsonl", 3, ContentSha) + "]");

            UpdateManifest manifest;
            string error;
            Assert.True(UpdateManifest.TryParse(json, out manifest, out error), error);
            Assert.Equal("1.2.0", manifest.Version);
            Assert.Equal(ReleaseVersion.Create(1, 2, 0), manifest.ParsedVersion);
            Assert.Equal("UI18n-1.2.0.zip", manifest.ZipName);
            Assert.Equal(123L, manifest.ZipBytes);
            Assert.Equal(ContentSha, manifest.ZipSha256);
            Assert.Equal(1, manifest.FileCount);
            Assert.Equal("Packs/zh-cn/Squad.jsonl", manifest.Files[0].ModRelativePath);
            Assert.Equal(ContentSha, manifest.Files[0].Sha256);

            // 大小写不敏感查找，便于用磁盘上的真实大小写路径查表
            Assert.NotNull(manifest.Find("packs/ZH-CN/squad.jsonl"));
            Assert.Null(manifest.Find("Packs/zh-cn/Missing.jsonl"));
        }

        [Fact]
        public void TryParse_AllowsMissingFilesArray()
        {
            string json = "{\"version\":\"1.2.0\",\"zip\":{\"name\":\"a.zip\",\"bytes\":1,\"sha256\":\"" + ContentSha + "\"}}";
            UpdateManifest manifest;
            string error;
            Assert.True(UpdateManifest.TryParse(json, out manifest, out error), error);
            Assert.Equal(0, manifest.FileCount);
        }

        [Theory]
        [InlineData("{\"zip\":{\"name\":\"a.zip\",\"bytes\":1,\"sha256\":\"X\"}}", "bad-version")]
        [InlineData("{\"version\":\"nonsense\",\"zip\":{\"name\":\"a.zip\",\"bytes\":1,\"sha256\":\"X\"}}", "bad-version")]
        [InlineData("{\"version\":\"1.2.0\"}", "missing-zip")]
        [InlineData("{\"version\":\"1.2.0\",\"zip\":{\"name\":\"../a.zip\",\"bytes\":1,\"sha256\":\"X\"}}", "bad-zip-name")]
        [InlineData("{\"version\":\"1.2.0\",\"zip\":{\"name\":\"a/b.zip\",\"bytes\":1,\"sha256\":\"X\"}}", "bad-zip-name")]
        [InlineData("{\"version\":\"1.2.0\",\"zip\":{\"name\":\"a.zip\",\"bytes\":0,\"sha256\":\"X\"}}", "bad-zip-bytes")]
        [InlineData("{\"version\":\"1.2.0\",\"zip\":{\"name\":\"a.zip\",\"bytes\":1}}", "bad-zip-sha256")]
        public void TryParse_RejectsBadEnvelopes(string json, string errorFragment)
        {
            UpdateManifest manifest;
            string error;
            Assert.False(UpdateManifest.TryParse(json, out manifest, out error));
            Assert.Contains(errorFragment, error);
        }

        // files 存在但不是数组也必须拒绝（否则会静默当作空清单，导致「更新了什么都没装」）
        [Fact]
        public void TryParse_RejectsFilesNotArray()
        {
            string json = "{\"version\":\"1.2.0\",\"zip\":{\"name\":\"a.zip\",\"bytes\":1,\"sha256\":\"" + ContentSha + "\"},\"files\":{}}";
            UpdateManifest manifest;
            string error;
            Assert.False(UpdateManifest.TryParse(json, out manifest, out error));
            Assert.Contains("files-not-array", error);
        }

        [Fact]
        public void TryParse_RejectsUnsafeAndDuplicatePaths()
        {
            string unsafeJson = Manifest("1.2.0", ContentSha,
                "[" + FileEntry("GameData/UI18n/Packs/zh-cn/../../evil.jsonl", 3, ContentSha) + "]");
            UpdateManifest manifest;
            string error;
            Assert.False(UpdateManifest.TryParse(unsafeJson, out manifest, out error));
            Assert.Contains("unsafe-path", error);
            Assert.Contains("parent-dir", error);

            string duplicateJson = Manifest("1.2.0", ContentSha,
                "[" + FileEntry("GameData/UI18n/Packs/zh-cn/Squad.jsonl", 3, ContentSha) + ","
                + FileEntry("GameData/UI18n/Packs/zh-cn/Squad.jsonl", 3, ContentSha) + "]");
            Assert.False(UpdateManifest.TryParse(duplicateJson, out manifest, out error));
            Assert.Contains("duplicate-file-path", error);
        }
    }

    public class UpdateVerifyTests
    {
        private static readonly byte[] Content = Encoding.UTF8.GetBytes("abc");
        private static readonly string ContentSha = Sha256Hex.OfBytes(Content);

        [Fact]
        public void CheckBytes_AcceptsExactMatch()
        {
            string reason;
            Assert.True(UpdateVerify.CheckBytes(Content, Content.Length, ContentSha, out reason));
            Assert.Equal(UpdateVerify.ReasonNone, reason);
            Assert.True(UpdateVerify.CheckBytes(Content, Content.Length, ContentSha.ToLowerInvariant(), out reason));
        }

        [Fact]
        public void CheckBytes_ReportsSizeAndShaMismatch()
        {
            string reason;
            Assert.False(UpdateVerify.CheckBytes(Content, Content.Length + 1, ContentSha, out reason));
            Assert.Contains(UpdateVerify.ReasonSizeMismatch, reason);
            Assert.Contains("expected 4 B", reason);

            string other = Sha256Hex.OfBytes(Encoding.UTF8.GetBytes("abcd"));
            Assert.False(UpdateVerify.CheckBytes(Content, Content.Length, other, out reason));
            Assert.Contains(UpdateVerify.ReasonSha256Mismatch, reason);
        }

        [Fact]
        public void CheckFile_And_CheckEntry()
        {
            string path = Path.Combine(Path.GetTempPath(), "ui18n-verify-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                File.WriteAllBytes(path, Content);
                string reason;
                Assert.True(UpdateVerify.CheckFile(path, Content.Length, ContentSha, out reason));
                Assert.False(UpdateVerify.CheckFile(path + ".missing", Content.Length, ContentSha, out reason));
                Assert.Contains(UpdateVerify.ReasonUnreadable, reason);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            string manifestJson = "{\"version\":\"1.2.0\",\"zip\":{\"name\":\"a.zip\",\"bytes\":1,\"sha256\":\"" + ContentSha + "\"},"
                + "\"files\":[{\"path\":\"GameData/UI18n/Packs/zh-cn/Squad.jsonl\",\"bytes\":" + Content.Length.ToString(CultureInfo.InvariantCulture)
                + ",\"sha256\":\"" + ContentSha + "\"}]}";
            UpdateManifest manifest;
            string manifestError;
            Assert.True(UpdateManifest.TryParse(manifestJson, out manifest, out manifestError), manifestError);
            ManifestFile file = manifest.Files[0];
            Assert.Equal("Packs/zh-cn/Squad.jsonl", file.ModRelativePath);
            string entryReason;
            Assert.True(UpdateVerify.CheckEntry(Content, file, out entryReason));
            Assert.False(UpdateVerify.CheckEntry(Encoding.UTF8.GetBytes("abd"), file, out entryReason));
            Assert.False(UpdateVerify.CheckZip(null, null, out entryReason));
        }
    }

    public class UpdateStateTests
    {
        [Fact]
        public void Empty_HasNeverCheckedAndIsDue()
        {
            UpdateState state = UpdateState.Empty;
            Assert.True(state.HasNeverChecked);
            Assert.Equal(DateTime.MinValue, state.LastCheckUtc);
            Assert.True(state.IsCheckDue(DateTime.UtcNow, 7));
            Assert.True(state.IsCheckDue(DateTime.UtcNow, 0));
            Assert.NotSame(UpdateState.Empty, UpdateState.Empty);
        }

        [Fact]
        public void IsCheckDue_RespectsIntervalAndClockSkew()
        {
            DateTime now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
            UpdateState fresh = UpdateState.Empty.WithCheck(now.AddHours(-6), "1.1.0", UpdateState.ResultUpToDate);
            Assert.False(fresh.IsCheckDue(now, 7));
            Assert.True(fresh.IsCheckDue(now.AddDays(7), 7));
            Assert.True(fresh.IsCheckDue(now, 0));

            // 时钟回拨（上次检查时间在未来）必须当成到期，否则玩家永远不再检查
            UpdateState skewed = UpdateState.Empty.WithCheck(now.AddDays(3), "1.1.0", UpdateState.ResultUpToDate);
            Assert.True(skewed.IsCheckDue(now, 7));
        }

        [Fact]
        public void ToJson_RoundTrips()
        {
            DateTime when = new DateTime(2026, 10, 25, 11, 58, 47, DateTimeKind.Utc);
            UpdateState state = UpdateState.Empty.WithCheck(when, "1.2.0", UpdateState.ResultUpdateAvailable);
            string json = state.ToJson();
            Assert.Contains("lastCheckUtc", json);
            Assert.Contains("1.2.0", json);

            UpdateState parsed;
            string error;
            Assert.True(UpdateState.TryParse(json, out parsed, out error), error);
            Assert.Equal(when, parsed.LastCheckUtc);
            Assert.Equal("1.2.0", parsed.LastVersion);
            Assert.Equal(UpdateState.ResultUpdateAvailable, parsed.LastResult);
            Assert.Contains("1.2.0", parsed.ToString());

            UpdateState never = UpdateState.Empty;
            Assert.True(UpdateState.TryParse(never.ToJson(), out parsed, out error), error);
            Assert.True(parsed.HasNeverChecked);
        }

        // 负向：状态文件是玩家可编辑的，坏内容只返回 false，绝不抛
        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("{\"lastCheckUtc\":\"yesterday\"}")]
        public void TryParse_RejectsGarbageWithoutThrowing(string json)
        {
            UpdateState state;
            string error;
            Assert.False(UpdateState.TryParse(json, out state, out error));
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    /// <summary>
    /// inflate + zip 容器：造包用 net10 的 System.IO.Compression，读包用我们自己的实现，
    /// 逐字节比对。KSP 的运行环境没有 System.IO.Compression，所以这一段必须自己写、必须自己验。
    /// </summary>
    public class ZipReaderTests
    {
        private static byte[] BuildZip(CompressionLevel level, params string[] names)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (System.IO.Compression.ZipArchive zip = new System.IO.Compression.ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        ZipArchiveEntry entry = zip.CreateEntry(names[i], level);
                        byte[] payload = Encoding.UTF8.GetBytes("payload-" + i + "-" + new string('x', 200 + i));
                        using (Stream stream = entry.Open())
                        {
                            stream.Write(payload, 0, payload.Length);
                        }
                    }
                }

                return ms.ToArray();
            }
        }

        [Theory]
        [InlineData(CompressionLevel.Optimal)]
        [InlineData(CompressionLevel.NoCompression)]
        [InlineData(CompressionLevel.Fastest)]
        public void OwnReader_MatchesSystemZipArchive(CompressionLevel level)
        {
            string[] names =
            {
                "GameData/UI18n/Packs/zh-cn/Squad.jsonl",
                "GameData/UI18n/Packs/zh-cn/RealismOverhaul.jsonl",
                "GameData/UI18n/Config.cfg",
            };

            byte[] bytes = BuildZip(level, names);
            CoreZip archive;
            string error;
            Assert.True(ZipArchiveReader.TryRead(bytes, out archive, out error), error);
            Assert.Equal(names.Length, archive.Count);

            using (MemoryStream ms = new MemoryStream(bytes))
            using (System.IO.Compression.ZipArchive reference = new System.IO.Compression.ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (string name in names)
                {
                    System.IO.Compression.ZipArchiveEntry expected = reference.GetEntry(name);
                    Assert.NotNull(expected);

                    byte[] ours;
                    string extractError;
                    Assert.True(archive.TryExtract(name, out ours, out extractError), extractError);

                    byte[] theirs;
                    using (Stream stream = expected.Open())
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        theirs = buffer.ToArray();
                    }

                    Assert.Equal(theirs, ours);
                }
            }

            // 不存在的条目、目录条目、大小上限都必须安全拒绝
            byte[] none;
            string reason;
            Assert.False(archive.TryExtract("nope.jsonl", out none, out reason));
            Assert.False(archive.TryExtract(names[0], 8, out none, out reason));
            Assert.False(archive.Find("nope.jsonl") != null);
        }

        [Fact]
        public void OwnReader_RejectsTruncatedAndJunk()
        {
            byte[] bytes = BuildZip(CompressionLevel.NoCompression, "GameData/UI18n/Packs/zh-cn/Squad.jsonl");
            byte[] half = new byte[bytes.Length / 2];
            Array.Copy(bytes, half, half.Length);

            CoreZip archive;
            string error;
            Assert.False(ZipArchiveReader.TryRead(half, out archive, out error));
            Assert.Contains("end-of-central-directory", error);

            Assert.False(ZipArchiveReader.TryRead(Encoding.UTF8.GetBytes("not a zip at all"), out archive, out error));
            Assert.False(ZipArchiveReader.TryRead(new byte[0], out archive, out error));
            Assert.False(ZipArchiveReader.TryRead(null, out archive, out error));
        }

        [Fact]
        public void OwnReader_RejectsCorruptedCrc()
        {
            const string name = "GameData/UI18n/Packs/zh-cn/Squad.jsonl";
            byte[] bytes = BuildZip(CompressionLevel.NoCompression, name);

            // stored 条目：把正文里某个 payload 字节翻转，尺寸不变、CRC 必须对不上
            byte[] marker = Encoding.UTF8.GetBytes("payload-0-");
            int at = IndexOf(bytes, marker);
            Assert.True(at > 0);
            bytes[at + marker.Length] ^= 0xFF;

            CoreZip archive;
            string error;
            Assert.True(ZipArchiveReader.TryRead(bytes, out archive, out error), error);

            byte[] content;
            string extractError;
            Assert.False(archive.TryExtract(name, out content, out extractError));
            Assert.Contains("crc-mismatch", extractError);
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        hit = false;
                        break;
                    }
                }

                if (hit)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
