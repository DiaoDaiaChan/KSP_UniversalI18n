using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UI18n.Core.Dict;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// T19：<see cref="PackLocator"/> 的单元测试（D35 契约 <c>Packs/&lt;lang&gt;/&lt;modId&gt;.jsonl</c>）。
    /// <para>
    /// 为什么值得单独测：旧 Runtime 的包发现逻辑（<c>TopDirectoryOnly</c> 平铺读 <c>Packs/*.jsonl</c>）
    /// 与契约不符，导致按契约放的包一条都读不到、字典恒空、patch 恒 0。这个 bug 只在装进真游戏后
    /// 才暴露 —— 因为那一层依赖游戏侧程序集，本机没法单测。现在逻辑搬进了 Core，用**真实临时目录**
    /// （不 mock 文件系统）把优先级、回退、平铺文件被忽略全部钉死。
    /// </para>
    /// <para>
    /// 每个测试实例一个独立临时根目录（名字带 Guid），并行跑不会互相踩；<see cref="Dispose"/> 删树。
    /// </para>
    /// </summary>
    public sealed class PackLocatorTests : IDisposable
    {
        private readonly string _root;

        public PackLocatorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ui18n-packlocator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不该让测试变红。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // ------------------------------------------------------------------ 归一化

        [Theory]
        [InlineData("zh-cn", "zh-cn")]
        [InlineData("zh-CN", "zh-cn")]
        [InlineData("ZH_CN", "zh-cn")]
        [InlineData("  zh-cn  ", "zh-cn")]
        [InlineData("zh\t", "zh")]
        [InlineData("zh-cn\u00a0", "zh-cn")]
        [InlineData("zh_cn_x", "zh-cn-x")]
        [InlineData("EN-US", "en-us")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        [InlineData(null, "")]
        public void NormalizeLanguage_TrimsLowercasesAndSwapsUnderscores(string input, string expected)
        {
            Assert.Equal(expected, PackLocator.NormalizeLanguage(input));
        }

        [Fact]
        public void NormalizeLanguage_ReturnsEmptyStringNeverNull()
        {
            Assert.NotNull(PackLocator.NormalizeLanguage(null));
            Assert.NotNull(PackLocator.NormalizeLanguage("   "));
        }

        [Fact]
        public void NormalizeLanguage_IsCultureInvariant()
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                // 土耳其语下 ToLower() 会把 I 变成 noktalı ı；必须走 Invariant。
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                Assert.Equal("zh-cn", PackLocator.NormalizeLanguage("ZH-CN"));
                Assert.Equal("i", PackLocator.NormalizeLanguage("I"));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        // ------------------------------------------------------------------ 候选

        [Theory]
        [InlineData("zh-cn", "zh-cn|zh")]
        [InlineData("zh-CN", "zh-cn|zh")]
        [InlineData("zh_CN", "zh-cn|zh")]
        [InlineData("zh-hans", "zh-hans|zh")]
        [InlineData("zh-cn-x-foo", "zh-cn-x-foo|zh")]
        [InlineData("zh", "zh")]
        [InlineData("en-us", "en-us|en")]
        [InlineData("-zh", "-zh")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        [InlineData(null, "")]
        public void LanguageCandidates_CompleteTagFirstThenPrimaryLanguage(string language, string expectedJoined)
        {
            IReadOnlyList<string> candidates = PackLocator.LanguageCandidates(language);
            Assert.Equal(expectedJoined, string.Join("|", candidates));
        }

        [Fact]
        public void LanguageCandidates_ReturnsTwoCandidatesForFullTags()
        {
            Assert.Equal(2, PackLocator.LanguageCandidates("zh-cn").Count);
            Assert.Single(PackLocator.LanguageCandidates("zh"));
            Assert.Empty(PackLocator.LanguageCandidates(null));
        }

        // ------------------------------------------------------------------ 纯函数选目录

        [Theory]
        [InlineData("zh-cn,zh", "zh-cn", "zh-cn")]
        [InlineData("zh", "zh-cn", "zh")]
        [InlineData("en-us", "zh-cn", null)]
        [InlineData("zh-cn", "zh", null)]
        [InlineData("zh-cn", "zh_CN", "zh-cn")]
        [InlineData("zh-cn", "ZH-CN", "zh-cn")]
        [InlineData("ZH-CN", "zh-cn", "ZH-CN")]
        [InlineData("zh-hans,zh", "zh-hans", "zh-hans")]
        [InlineData("zh-cn-x-foo,zh", "zh-cn-x-foo", "zh-cn-x-foo")]
        [InlineData("en-us,en", "en-us", "en-us")]
        [InlineData("ZH-CN,zh", "zh-cn", "ZH-CN")]
        [InlineData("zh,ZH-CN", "zh-cn", "ZH-CN")]
        [InlineData("", "zh-cn", null)]
        [InlineData(null, "zh-cn", null)]
        [InlineData("zh-cn", "", null)]
        [InlineData("zh-cn", null, null)]
        [InlineData("zh-cn", "   ", null)]
        public void SelectLanguageDir_PicksByPriority_AndReturnsRealCase(string existingCsv, string language, string expected)
        {
            string[] existing = SplitCsv(existingCsv);
            Assert.Equal(expected, PackLocator.SelectLanguageDir(existing, language));
        }

        // ------------------------------------------------------------------ I/O 版选目录

        [Fact]
        public void SelectLanguageDir_Io_FindsDirectoryAndKeepsDiskCase()
        {
            Dir("ZH-CN");
            Dir("zh");

            string selected;
            PackDirStatus status = PackLocator.SelectLanguageDir(_root, new[] { "zh-cn", "zh" }, out selected);

            Assert.Equal(PackDirStatus.Found, status);
            Assert.Equal(Path.Combine(_root, "ZH-CN"), selected);
        }

        [Fact]
        public void SelectLanguageDir_Io_ReturnsPacksRootMissing_WhenRootAbsent()
        {
            string selected;
            PackDirStatus status = PackLocator.SelectLanguageDir(Path.Combine(_root, "no-such-root"), new[] { "zh-cn" }, out selected);

            Assert.Equal(PackDirStatus.PacksRootMissing, status);
            Assert.Null(selected);
        }

        [Fact]
        public void SelectLanguageDir_Io_ReturnsLanguageDirMissing_WhenNothingMatches()
        {
            Dir("en-us");

            string selected;
            PackDirStatus status = PackLocator.SelectLanguageDir(_root, new[] { "zh-cn", "zh" }, out selected);

            Assert.Equal(PackDirStatus.LanguageDirMissing, status);
            Assert.Null(selected);
        }

        [Fact]
        public void SelectLanguageDir_Io_ReturnsLanguageDirMissing_ForNullOrEmptyCandidates()
        {
            Dir("zh-cn");

            string selected;
            Assert.Equal(PackDirStatus.LanguageDirMissing, PackLocator.SelectLanguageDir(_root, null, out selected));
            Assert.Null(selected);

            Assert.Equal(PackDirStatus.LanguageDirMissing, PackLocator.SelectLanguageDir(_root, new string[0], out selected));
            Assert.Null(selected);

            Assert.Equal(PackDirStatus.LanguageDirMissing, PackLocator.SelectLanguageDir(_root, new string[] { null, string.Empty }, out selected));
            Assert.Null(selected);
        }

        // ------------------------------------------------------------------ 列文件

        [Fact]
        public void ListPackFiles_ReturnsOnlyNonEmptyJsonl_InOrdinalNameOrder()
        {
            string dir = Dir("zh-cn");
            string a = WriteFile("zh-cn/a.jsonl", "a");
            string b = WriteFile("zh-cn/b.jsonl", "b");
            string c = WriteFile("zh-cn/C.jsonl", "c");
            WriteFile("zh-cn/notes.txt", "x");
            WriteFile("zh-cn/x.jsonl.bak", "x");
            WriteFile("zh-cn/data.json", "{}");
            WriteEmptyFile("zh-cn/empty.jsonl");
            Dir("zh-cn/trap.jsonl");

            IReadOnlyList<string> files = PackLocator.ListPackFiles(dir);

            // ordinal：'C'(0x43) < 'a'(0x61) < 'b'(0x62)
            Assert.Equal(new[] { c, a, b }, files);
            Assert.True(Path.IsPathRooted(files[0]));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("no-such-dir")]
        public void ListPackFiles_ReturnsEmpty_ForUnusableDirectory(string languageDir)
        {
            Assert.Empty(PackLocator.ListPackFiles(languageDir));
        }

        [Fact]
        public void ListPackFiles_TwoArgOverload_EqualsResolveFiles()
        {
            Dir("zh-cn");
            string a = WriteFile("zh-cn/a.jsonl", "a");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(res.Files, PackLocator.ListPackFiles(_root, "zh-cn"));
            Assert.Equal(new[] { a }, res.Files);
        }

        // ------------------------------------------------------------------ Resolve：命中与优先级

        [Fact]
        public void Resolve_PrefersExactLanguageDir_WhenBothZhAndZhCnExist()
        {
            Dir("zh-cn");
            Dir("zh");
            string exact = WriteFile("zh-cn/Exact.jsonl", "x");
            WriteFile("zh/Primary.jsonl", "y");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(PackDirStatus.Found, res.Status);
            Assert.Equal("zh-cn", res.SelectedLanguage);
            Assert.Equal(Path.Combine(_root, "zh-cn"), res.Directory);
            Assert.Equal(new[] { exact }, res.Files);
            Assert.Equal(new[] { "zh", "zh-cn" }, res.AvailableLanguageDirs);
            Assert.Equal(0, res.FlatFileCount);
        }

        [Fact]
        public void Resolve_FallsBackToPrimaryLanguage_WhenOnlyZhExists()
        {
            Dir("zh");
            string file = WriteFile("zh/Primary.jsonl", "y");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(PackDirStatus.Found, res.Status);
            Assert.Equal("zh", res.SelectedLanguage);
            Assert.Equal(new[] { file }, res.Files);
        }

        [Fact]
        public void Resolve_DoesNotPromotePrimaryLanguageToFullTag()
        {
            // 单向回退：zh 不许升到 zh-cn（否则中文玩家会被塞一个更具体的包，语义倒挂）。
            Dir("zh-cn");

            PackResolution res = PackLocator.Resolve(_root, "zh");

            Assert.Equal(PackDirStatus.LanguageDirMissing, res.Status);
            Assert.Null(res.Directory);
            Assert.Empty(res.Files);
        }

        [Fact]
        public void Resolve_NormalizesUnderscoreAndCase()
        {
            Dir("zh-cn");
            string file = WriteFile("zh-cn/a.jsonl", "a");

            PackResolution res = PackLocator.Resolve(_root, "  ZH_CN  ");

            Assert.Equal(PackDirStatus.Found, res.Status);
            Assert.Equal("zh-cn", res.NormalizedLanguage);
            Assert.Equal("ZH_CN", res.Language.Trim());
            Assert.Equal(new[] { "zh-cn", "zh" }, res.Candidates);
            Assert.Equal(new[] { file }, res.Files);
        }

        [Fact]
        public void Resolve_ReturnsDiskCase_ForUppercaseDirectoryOnDisk()
        {
            Dir("ZH-CN");
            string file = WriteFile("ZH-CN/a.jsonl", "a");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(PackDirStatus.Found, res.Status);
            Assert.Equal("ZH-CN", res.SelectedLanguage);
            Assert.Equal(Path.Combine(_root, "ZH-CN"), res.Directory);
            Assert.Equal(new[] { file }, res.Files);
        }

        [Fact]
        public void Resolve_HandlesTrailingSeparatorOnRoot()
        {
            Dir("zh-cn");
            WriteFile("zh-cn/a.jsonl", "a");

            PackResolution res = PackLocator.Resolve(_root + Path.DirectorySeparatorChar, "zh-cn");

            Assert.Equal(PackDirStatus.Found, res.Status);
            Assert.True(res.HasPacks);
        }

        [Fact]
        public void Resolve_EmptyLanguageDirectory_IsFoundButHasNoPacks()
        {
            Dir("zh-cn");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(PackDirStatus.Found, res.Status);
            Assert.False(res.HasPacks);
            Assert.Empty(res.Files);
        }

        [Fact]
        public void Resolve_DoesNotRecurseIntoSubdirectories()
        {
            Dir("zh-cn/sub");
            WriteFile("zh-cn/sub/nested.jsonl", "n");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(PackDirStatus.Found, res.Status);
            Assert.Empty(res.Files);
            // 根下只有 zh-cn 一个目录，sub 是它的子目录，不该出现在诊断里。
            Assert.Equal(new[] { "zh-cn" }, res.AvailableLanguageDirs);
        }

        // ------------------------------------------------------------------ Resolve：事故回归锁

        [Fact]
        public void Resolve_FlatPacksUnderRoot_AreReportedNotSilentlyLoaded()
        {
            // 事故回归锁：deploy.ps1 旧版把 pool.jsonl / glossary.jsonl 平铺拷进 Packs 根。
            // 正确的行为是「一条都别读」，并且把平铺文件数量报出来（FlatFileCount > 0 就该 Warn），
            // 绝不能出现「字典看起来加载了」的假成功。
            WriteFile("pool.jsonl", "{}");
            WriteFile("glossary.jsonl", "{}");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(PackDirStatus.LanguageDirMissing, res.Status);
            Assert.Null(res.Directory);
            Assert.Null(res.SelectedLanguage);
            Assert.False(res.HasPacks);
            Assert.Empty(res.Files);
            Assert.Equal(2, res.FlatFileCount);
            Assert.Empty(res.AvailableLanguageDirs);
        }

        [Fact]
        public void Resolve_FlatPacksDoNotHideRealLanguageDirectory()
        {
            WriteFile("pool.jsonl", "{}");
            WriteFile("glossary.jsonl", "{}");
            Dir("zh-cn");
            string file = WriteFile("zh-cn/Real.jsonl", "x");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(PackDirStatus.Found, res.Status);
            Assert.Equal(new[] { file }, res.Files);
            Assert.Equal(2, res.FlatFileCount);
        }

        // ------------------------------------------------------------------ Resolve：失败与异常输入

        [Fact]
        public void Resolve_ReturnsPacksRootMissing_WhenRootDoesNotExist()
        {
            PackResolution res = PackLocator.Resolve(Path.Combine(_root, "not-installed"), "zh-cn");

            Assert.Equal(PackDirStatus.PacksRootMissing, res.Status);
            Assert.Null(res.Directory);
            Assert.Empty(res.Files);
            Assert.Empty(res.AvailableLanguageDirs);
            Assert.Equal(0, res.FlatFileCount);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Resolve_ReturnsPacksRootMissing_ForNullOrBlankRoot(string packsRoot)
        {
            PackResolution res = PackLocator.Resolve(packsRoot, "zh-cn");

            Assert.Equal(PackDirStatus.PacksRootMissing, res.Status);
            Assert.Empty(res.Files);
        }

        [Fact]
        public void Resolve_ReturnsLanguageDirMissing_WhenOnlyOtherLanguagesExist()
        {
            Dir("en-us");
            WriteFile("en-us/a.jsonl", "a");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(PackDirStatus.LanguageDirMissing, res.Status);
            Assert.Null(res.Directory);
            Assert.Empty(res.Files);
            Assert.Equal(new[] { "en-us" }, res.AvailableLanguageDirs);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Resolve_WithEmptyLanguage_DoesNotGuess(string language)
        {
            Dir("zh-cn");
            WriteFile("zh-cn/a.jsonl", "a");

            PackResolution res = PackLocator.Resolve(_root, language);

            Assert.Equal(PackDirStatus.LanguageDirMissing, res.Status);
            Assert.Equal(string.Empty, res.NormalizedLanguage);
            Assert.Empty(res.Candidates);
            Assert.Empty(res.Files);
            Assert.Equal(new[] { "zh-cn" }, res.AvailableLanguageDirs);
        }

        [Theory]
        [InlineData("\0")]
        [InlineData("::::")]
        [InlineData("\\\\?\\nosuchdevice")]
        [InlineData("a|b")]
        [InlineData("C:\\nope\\nope\\nope")]
        public void Resolve_WithHostileRoot_NeverThrows(string packsRoot)
        {
            PackResolution res = null;
            Exception thrown = Record.Exception(() => { res = PackLocator.Resolve(packsRoot, "zh-cn"); });

            Assert.Null(thrown);
            Assert.NotNull(res);
            Assert.NotEqual(PackDirStatus.Found, res.Status);
            Assert.Empty(res.Files);
        }

        [Fact]
        public void Resolve_WithVeryLongRoot_NeverThrows()
        {
            string longRoot = Path.Combine(_root, new string('x', 300));

            PackResolution res = null;
            Exception thrown = Record.Exception(() => { res = PackLocator.Resolve(longRoot, "zh-cn"); });

            Assert.Null(thrown);
            Assert.NotNull(res);
            Assert.Empty(res.Files);
        }

        // ------------------------------------------------------------------ Resolve：诊断与不变式

        [Fact]
        public void Resolve_AvailableLanguageDirs_AreOrdinalSortedAndCappedAtEight()
        {
            for (int i = 0; i < 10; i++)
            {
                Dir("d" + i.ToString(CultureInfo.InvariantCulture));
            }

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(PackDirStatus.LanguageDirMissing, res.Status);
            Assert.Equal(new[] { "d0", "d1", "d2", "d3", "d4", "d5", "d6", "d7" }, res.AvailableLanguageDirs);
        }

        [Fact]
        public void Resolve_Describe_IsOneDeterministicLineWithDiagnostics()
        {
            Dir("zh-cn");
            WriteFile("zh-cn/a.jsonl", "a");
            WriteFile("zh-cn/b.jsonl", "b");
            WriteFile("pool.jsonl", "{}");

            PackResolution found = PackLocator.Resolve(_root, "zh-cn");
            string line = found.Describe();

            Assert.Equal(line, found.ToString());
            Assert.Contains("status=Found", line);
            Assert.Contains("language=zh-cn", line);
            Assert.Contains("normalized=zh-cn", line);
            Assert.Contains("candidates=[zh-cn,zh]", line);
            Assert.Contains("files=2", line);
            Assert.Contains("flat=1", line);
            Assert.Contains("available=[zh-cn]", line);

            PackResolution missing = PackLocator.Resolve(Path.Combine(_root, "gone"), null);
            string missingLine = missing.Describe();
            Assert.Contains("status=PacksRootMissing", missingLine);
            Assert.Contains("language=(none)", missingLine);
            Assert.Contains("dir=(none)", missingLine);
            Assert.Contains("files=0", missingLine);
        }

        [Fact]
        public void Resolve_AliasProperties_MatchCanonicalOnes()
        {
            Dir("zh-cn");
            WriteFile("zh-cn/a.jsonl", "a");

            PackResolution res = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(res.Directory, res.ChosenDir);
            Assert.Equal(res.Files, res.PackFiles);
            Assert.Same(res.Files, res.PackFiles);
        }

        [Fact]
        public void Resolve_IsIdempotent_AndHasNoSideEffects()
        {
            Dir("zh-cn");
            WriteFile("zh-cn/a.jsonl", "a");
            string before = SnapshotTree();

            PackResolution first = PackLocator.Resolve(_root, "zh-cn");
            PackResolution second = PackLocator.Resolve(_root, "zh-cn");

            Assert.Equal(first.Status, second.Status);
            Assert.Equal(first.Directory, second.Directory);
            Assert.Equal(first.Files, second.Files);
            Assert.Equal(before, SnapshotTree());
        }

        [Fact]
        public void Resolve_IsCultureInvariant()
        {
            Dir("ZH-CN");
            string file = WriteFile("ZH-CN/a.jsonl", "a");

            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                PackResolution res = PackLocator.Resolve(_root, "ZH-CN");

                Assert.Equal(PackDirStatus.Found, res.Status);
                Assert.Equal("ZH-CN", res.SelectedLanguage);
                Assert.Equal(new[] { file }, res.Files);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        // ------------------------------------------------------------------ 助手

        /// <summary>把 "a/b/c.jsonl" 这种相对路径拼到临时根下，统一成本机分隔符（'/' 也接受）。</summary>
        private string Full(string relative)
        {
            string[] parts = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string path = _root;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
            }

            return path;
        }

        private static string[] SplitCsv(string csv)
        {
            if (csv == null)
            {
                return null;
            }

            return csv.Length == 0 ? new string[0] : csv.Split(',');
        }

        private string Dir(string relative)
        {
            string path = Full(relative);
            Directory.CreateDirectory(path);
            return path;
        }

        private string WriteFile(string relative, string content)
        {
            string path = Full(relative);
            string parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(path, content);
            return path;
        }

        private string WriteEmptyFile(string relative)
        {
            string path = Full(relative);
            string parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(path, new byte[0]);
            return path;
        }

        private string SnapshotTree()
        {
            string[] entries = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories);
            Array.Sort(entries, StringComparer.Ordinal);
            return string.Join("\n", entries.Select(e => e + "|" + (File.Exists(e) ? new FileInfo(e).Length.ToString(CultureInfo.InvariantCulture) : "dir")));
        }
    }
}
