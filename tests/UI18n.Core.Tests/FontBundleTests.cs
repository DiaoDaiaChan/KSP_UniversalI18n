using System;
using System.IO;
using System.Text;
using UI18n.Core.Config;
using UI18n.Core.Update;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// <see cref="FontBundle"/>：随包字体的清单解析与完整性校验。
    /// 关注点是「验不过就回落」，所以失败路径比成功路径多 —— 每条失败都钉住原因码，
    /// 因为 Runtime 只按原因码决定要不要继续载入 AssetBundle。
    /// </summary>
    public sealed class FontBundleTests
    {
        private sealed class TempDirectory : IDisposable
        {
            internal TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ui18n-fontbundle-" + Guid.NewGuid().ToString("n"));
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; private set; }

            internal string PathOf(string name)
            {
                return System.IO.Path.Combine(Path, name);
            }

            internal void Write(string name, byte[] bytes)
            {
                System.IO.File.WriteAllBytes(PathOf(name), bytes);
            }

            internal void WriteText(string name, string text)
            {
                System.IO.File.WriteAllText(PathOf(name), text, new UTF8Encoding(false));
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Path))
                    {
                        Directory.Delete(Path, true);
                    }
                }
                catch (Exception)
                {
                    // 临时目录删不掉不影响断言。
                }
            }
        }

        private static byte[] Payload(int size)
        {
            var bytes = new byte[size];
            for (int i = 0; i < size; i++)
            {
                bytes[i] = (byte)(i * 31 + 7);
            }

            return bytes;
        }

        private static string ManifestJson(string file, long bytes, string sha, string license, string licenseFile, string schema = FontBundle.SchemaId)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"schema\": ").Append(schema == null ? "null" : "\"" + schema + "\"").Append(", ");
            // JsonWriter.Escape 自带两端引号，不要再套一层。
            sb.Append("\"file\": ").Append(file == null ? "null" : UI18n.Core.Update.JsonWriter.Escape(file)).Append(", ");
            sb.Append("\"sha256\": ").Append(sha == null ? "null" : "\"" + sha + "\"").Append(", ");
            sb.Append("\"bytes\": ").Append(bytes).Append(", ");
            sb.Append("\"fontName\": \"Noto Sans SC\", ");
            sb.Append("\"license\": ").Append(license == null ? "null" : "\"" + license + "\"").Append(", ");
            sb.Append("\"licenseFile\": ").Append(licenseFile == null ? "null" : "\"" + licenseFile + "\"").Append(", ");
            sb.Append("\"source\": \"https://example.invalid/noto\", ");
            sb.Append("\"subset\": \"CJK 常用 + 拉丁 + 标点\"");
            sb.Append("}");
            return sb.ToString();
        }

        private static string GoodBundle(TempDirectory dir, int size = 128)
        {
            byte[] bytes = Payload(size);
            dir.Write("ui18n-cjk.bundle", bytes);
            dir.WriteText("OFL.txt", "SIL OPEN FONT LICENSE Version 1.1\n");
            string json = ManifestJson("ui18n-cjk.bundle", bytes.Length, Sha256Hex.OfBytes(bytes), "OFL-1.1", "OFL.txt");
            dir.WriteText(FontBundle.ManifestFileName, json);
            return json;
        }

        [Fact]
        public void Check_ValidBundle_IsOk()
        {
            using (var dir = new TempDirectory())
            {
                GoodBundle(dir);

                FontBundleCheck check = FontBundle.Check(dir.Path, false);

                Assert.True(check.Ok, check.Describe());
                Assert.Equal(FontBundle.ReasonNone, check.Reason);
                Assert.Equal(128, check.ActualBytes);
                Assert.NotNull(check.Manifest);
                Assert.Equal("Noto Sans SC", check.Manifest.FontName);
                Assert.Equal(dir.PathOf("OFL.txt"), check.LicensePath);
                Assert.Contains("font bundle ok", check.Describe(), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Check_NoManifest_Fails()
        {
            using (var dir = new TempDirectory())
            {
                FontBundleCheck check = FontBundle.Check(dir.Path, false);

                Assert.False(check.Ok);
                Assert.Equal(FontBundle.ReasonNoManifest, check.Reason);
                Assert.Null(check.Manifest);
                Assert.Contains("no-manifest", check.Describe(), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Check_EmptyDirectory_DoesNotThrow()
        {
            FontBundleCheck check = FontBundle.Check(string.Empty, false);
            Assert.False(check.Ok);
            Assert.Equal(FontBundle.ReasonNoManifest, check.Reason);

            Assert.False(FontBundle.Check(null, false).Ok);
        }

        [Fact]
        public void Check_BrokenJson_ReportsBadManifest()
        {
            using (var dir = new TempDirectory())
            {
                dir.WriteText(FontBundle.ManifestFileName, "{ this is not json");

                FontBundleCheck check = FontBundle.Check(dir.Path, false);

                Assert.False(check.Ok);
                Assert.Equal(FontBundle.ReasonBadManifest, check.Reason);
            }
        }

        [Fact]
        public void ParseManifest_MissingSchema_IsRejected()
        {
            string error;
            FontBundleManifest manifest = FontBundle.ParseManifest(
                ManifestJson("a.bundle", 1, new string('a', 64), "OFL-1.1", "OFL.txt", null), out error);

            Assert.Null(manifest);
            Assert.StartsWith(FontBundle.ReasonBadManifest, error, StringComparison.Ordinal);
        }

        [Fact]
        public void ParseManifest_WrongSchema_IsRejected()
        {
            string error;
            Assert.Null(FontBundle.ParseManifest("{\"schema\":\"ui18n-font-bundle/2\",\"file\":\"a\",\"sha256\":\"" + new string('a', 64) + "\",\"bytes\":1}", out error));
            Assert.Contains("ui18n-font-bundle/1", error, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("../evil.bundle")]
        [InlineData("sub/evil.bundle")]
        [InlineData("sub\\evil.bundle")]
        [InlineData("C:evil.bundle")]
        [InlineData("")]
        [InlineData(".")]
        [InlineData("..")]
        public void ParseManifest_UnsafeFileName_IsRejected(string name)
        {
            string error;
            FontBundleManifest manifest = FontBundle.ParseManifest(
                ManifestJson(name, 1, new string('a', 64), "OFL-1.1", "OFL.txt"), out error);

            Assert.Null(manifest);
            Assert.StartsWith(FontBundle.ReasonUnsafeName, error, StringComparison.Ordinal);
        }

        [Fact]
        public void ParseManifest_BadSha256_IsRejected()
        {
            string error;
            Assert.Null(FontBundle.ParseManifest(ManifestJson("a.bundle", 1, "xyz", "OFL-1.1", "OFL.txt"), out error));
            Assert.StartsWith(FontBundle.ReasonBadManifest, error, StringComparison.Ordinal);
        }

        [Fact]
        public void ParseManifest_NonPositiveBytes_IsRejected()
        {
            string error;
            Assert.Null(FontBundle.ParseManifest(ManifestJson("a.bundle", 0, new string('a', 64), "OFL-1.1", "OFL.txt"), out error));
            Assert.Contains("bytes", error, StringComparison.Ordinal);
        }

        [Fact]
        public void ParseManifest_AcceptsFontsArrayForm()
        {
            string json = "{\"fonts\":[" + ManifestJson("a.bundle", 4, new string('a', 64), "OFL-1.1", "OFL.txt") + "]}";

            string error;
            FontBundleManifest manifest = FontBundle.ParseManifest(json, out error);

            Assert.Null(error);
            Assert.NotNull(manifest);
            Assert.Equal("a.bundle", manifest.FileName);
        }

        [Fact]
        public void ParseManifest_NormalizesShaToUpperCase()
        {
            string lower = new string('a', 64);

            string error;
            FontBundleManifest manifest = FontBundle.ParseManifest(ManifestJson("a.bundle", 4, lower, "OFL-1.1", "OFL.txt"), out error);

            Assert.Null(error);
            // Sha256Hex.Normalize 的既有口径是「转大写」，比对一律走 EqualsIgnoreCase，所以这里钉住大写形态。
            Assert.Equal(lower.ToUpperInvariant(), manifest.Sha256);
        }

        [Fact]
        public void Check_MissingFile_IsReported()
        {
            using (var dir = new TempDirectory())
            {
                dir.WriteText(FontBundle.ManifestFileName,
                    ManifestJson("absent.bundle", 16, Sha256Hex.OfBytes(Payload(16)), "OFL-1.1", "OFL.txt"));

                FontBundleCheck check = FontBundle.Check(dir.Path, false);

                Assert.False(check.Ok);
                Assert.Equal(FontBundle.ReasonMissing, check.Reason);
                Assert.Contains("absent.bundle", check.FilePath, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Check_SizeMismatch_IsReported()
        {
            using (var dir = new TempDirectory())
            {
                byte[] bytes = Payload(64);
                dir.Write("ui18n-cjk.bundle", bytes);
                dir.WriteText(FontBundle.ManifestFileName,
                    ManifestJson("ui18n-cjk.bundle", 63, Sha256Hex.OfBytes(bytes), "OFL-1.1", "OFL.txt"));

                FontBundleCheck check = FontBundle.Check(dir.Path, false);

                Assert.False(check.Ok);
                Assert.Equal(FontBundle.ReasonSizeMismatch, check.Reason);
                Assert.Equal(64, check.ActualBytes);
            }
        }

        [Fact]
        public void Check_Sha256Mismatch_IsReported()
        {
            using (var dir = new TempDirectory())
            {
                dir.Write("ui18n-cjk.bundle", Payload(64));
                dir.WriteText(FontBundle.ManifestFileName,
                    ManifestJson("ui18n-cjk.bundle", 64, Sha256Hex.OfBytes(Payload(65)), "OFL-1.1", "OFL.txt"));

                FontBundleCheck check = FontBundle.Check(dir.Path, false);

                Assert.False(check.Ok);
                Assert.Equal(FontBundle.ReasonSha256Mismatch, check.Reason);
                Assert.Equal(Sha256Hex.OfBytes(Payload(64)), check.ActualSha256);
                Assert.Contains("sha256-mismatch", check.Describe(), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Check_OverSizeBudget_IsReported()
        {
            using (var dir = new TempDirectory())
            {
                // 只写 6 MB + 1 的稀疏内容即可触发上限，不用真造那么多有效数据。
                byte[] bytes = new byte[FontBundle.SizeBudget + 1];
                dir.Write("ui18n-cjk.bundle", bytes);
                dir.WriteText(FontBundle.ManifestFileName,
                    ManifestJson("ui18n-cjk.bundle", bytes.Length, Sha256Hex.OfBytes(bytes), "OFL-1.1", "OFL.txt"));

                FontBundleCheck check = FontBundle.Check(dir.Path, false);

                Assert.False(check.Ok);
                Assert.Equal(FontBundle.ReasonTooLarge, check.Reason);
            }
        }

        [Fact]
        public void Check_RequireLicense_WithoutLicenseFields_IsRejected()
        {
            using (var dir = new TempDirectory())
            {
                byte[] bytes = Payload(32);
                dir.Write("ui18n-cjk.bundle", bytes);
                dir.WriteText(FontBundle.ManifestFileName,
                    ManifestJson("ui18n-cjk.bundle", bytes.Length, Sha256Hex.OfBytes(bytes), null, null));

                FontBundleCheck lax = FontBundle.Check(dir.Path, false);
                FontBundleCheck strict = FontBundle.Check(dir.Path, true);

                Assert.True(lax.Ok, lax.Describe());
                Assert.Null(lax.LicensePath);
                Assert.False(strict.Ok);
                Assert.Equal(FontBundle.ReasonNoLicense, strict.Reason);
            }
        }

        [Fact]
        public void Check_RequireLicense_WithMissingLicenseFile_IsRejected()
        {
            using (var dir = new TempDirectory())
            {
                byte[] bytes = Payload(32);
                dir.Write("ui18n-cjk.bundle", bytes);
                dir.WriteText(FontBundle.ManifestFileName,
                    ManifestJson("ui18n-cjk.bundle", bytes.Length, Sha256Hex.OfBytes(bytes), "OFL-1.1", "OFL.txt"));

                FontBundleCheck check = FontBundle.Check(dir.Path, true);

                Assert.False(check.Ok);
                Assert.Equal(FontBundle.ReasonNoLicense, check.Reason);
                Assert.Equal(dir.PathOf("OFL.txt"), check.LicensePath);
            }
        }

        [Fact]
        public void Check_RequireLicense_WithLicenseFile_IsOk()
        {
            using (var dir = new TempDirectory())
            {
                GoodBundle(dir);

                FontBundleCheck check = FontBundle.Check(dir.Path, true);

                Assert.True(check.Ok, check.Describe());
                Assert.Equal("OFL-1.1", check.Manifest.License);
                Assert.NotNull(check.LicensePath);
            }
        }

        [Fact]
        public void Check_UnsafeLicenseFileName_IsRejected()
        {
            using (var dir = new TempDirectory())
            {
                byte[] bytes = Payload(32);
                dir.Write("ui18n-cjk.bundle", bytes);
                dir.WriteText(FontBundle.ManifestFileName,
                    ManifestJson("ui18n-cjk.bundle", bytes.Length, Sha256Hex.OfBytes(bytes), "OFL-1.1", "../OFL.txt"));

                FontBundleCheck check = FontBundle.Check(dir.Path, true);

                Assert.False(check.Ok);
                Assert.Equal(FontBundle.ReasonUnsafeName, check.Reason);
            }
        }

        [Fact]
        public void IsSafeFileName_Matrix()
        {
            Assert.True(FontBundle.IsSafeFileName("ui18n-cjk.bundle"));
            Assert.True(FontBundle.IsSafeFileName("a b.txt"));
            Assert.False(FontBundle.IsSafeFileName(null));
            Assert.False(FontBundle.IsSafeFileName(string.Empty));
            Assert.False(FontBundle.IsSafeFileName(new string('x', 201)));
            Assert.False(FontBundle.IsSafeFileName("a/b"));
            Assert.False(FontBundle.IsSafeFileName("a:b"));
            Assert.False(FontBundle.IsSafeFileName(".."));
        }

        [Fact]
        public void Sha256OfFile_MatchesOfBytes()
        {
            using (var dir = new TempDirectory())
            {
                byte[] bytes = Payload(4096);
                dir.Write("blob.bin", bytes);

                Assert.Equal(Sha256Hex.OfBytes(bytes), FontBundle.Sha256OfFile(dir.PathOf("blob.bin")));
                Assert.Null(FontBundle.Sha256OfFile(dir.PathOf("absent.bin")));
            }
        }

        [Fact]
        public void SizeBudget_IsSixMegabytes()
        {
            Assert.Equal(6L * 1024 * 1024, FontBundle.SizeBudget);
            Assert.Equal("fonts.json", FontBundle.ManifestFileName);
            Assert.Equal("ui18n-font-bundle/1", FontBundle.SchemaId);
        }
    }
}
