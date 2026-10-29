using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UI18n.Core.Dict;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 显示名数据表 <c>Packs/&lt;lang&gt;/display-names.json</c>（v1.7 去硬编码 Fix-2）：
    /// 以前 <see cref="PackFilter.DisplayName"/> 里写死了「UI18n-Unattributed → 未归属文本」
    /// 「Squad → 本体」三条 if，现在这些名字只从数据里来。
    /// <para>
    /// 每个用例自带临时目录；<c>_customDisplayNames</c> 是静态注册表，构造/析构都清一遍做隔离。
    /// </para>
    /// </summary>
    [Collection(PackFilterTests.RegistryCollection)]
    public class PackDisplayNamesTests : IDisposable
    {
        private readonly string _root;

        public PackDisplayNamesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ui18n-display-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "zh-cn"));
            PackFilter.ClearCustomDisplayNames();
        }

        public void Dispose()
        {
            PackFilter.ClearCustomDisplayNames();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private string LanguageDir
        {
            get { return Path.Combine(_root, "zh-cn"); }
        }

        private string WriteDisplayNames(string json)
        {
            string path = Path.Combine(LanguageDir, PackDisplayNames.FileName);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }

        // ------------------------------------------------------------------ 解析

        [Fact]
        public void LoadFromJson_FlatObject_RegistersNames()
        {
            int loaded = PackDisplayNames.LoadFromJson("{\"Squad\":\"本体\",\"RealFuels\":\"真实燃料\"}");

            Assert.Equal(2, loaded);
            Assert.Equal("本体", PackFilter.DisplayName("Squad"));
            // 注册表是 OrdinalIgnoreCase：大小写变体也命中
            Assert.Equal("真实燃料", PackFilter.DisplayName("realfuels"));
        }

        [Fact]
        public void LoadFromJson_NamesObject_SkipsNonStringAndInvalidKeys()
        {
            // "names" 这层是推荐写法；空值、非字符串、非法 modId 全部跳过，合法的那条生效
            int loaded = PackDisplayNames.LoadFromJson(
                "{\"schema\":\"ui18n-display-names/1\"," +
                "\"names\":{\"Squad\":\"\",\"UI18n-Unattributed\":\"未归属文本\",\"Bad Name\":\"x\",\"Count\":3}}");

            Assert.Equal(1, loaded);
            Assert.Equal("未归属文本", PackFilter.DisplayName("UI18n-Unattributed"));
            // 没注册的都回退 modId（不再有任何内置文案）
            Assert.Equal("Squad", PackFilter.DisplayName("Squad"));
            Assert.Equal("Bad Name", PackFilter.DisplayName("Bad Name"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{")]
        [InlineData("[]")]
        [InlineData("\"just a string\"")]
        [InlineData("{}")]
        [InlineData("{\"names\":{}}")]
        public void LoadFromJson_BadOrEmptyInput_ReturnsZeroWithoutThrowing(string json)
        {
            Assert.Equal(0, PackDisplayNames.LoadFromJson(json));
        }

        [Fact]
        public void LoadFromJson_DoesNotThrow_OnVeryLargeGarbage()
        {
            string junk = new string('{', 4096);
            Assert.Equal(0, PackDisplayNames.LoadFromJson(junk));
        }

        // ------------------------------------------------------------------ 文件

        [Fact]
        public void LoadFromDirectory_RegistersNamesFromPackDirectory()
        {
            WriteDisplayNames("{\"names\":{\"Squad\":\"本体\"}}");

            int loaded = PackDisplayNames.LoadFromDirectory(LanguageDir);

            Assert.Equal(1, loaded);
            Assert.Equal("本体", PackFilter.DisplayName("Squad"));
        }

        [Fact]
        public void LoadFromDirectory_WithBomFile_StillLoads()
        {
            string path = Path.Combine(LanguageDir, PackDisplayNames.FileName);
            File.WriteAllText(path, "{\"Squad\":\"本体\"}", new UTF8Encoding(true));

            Assert.Equal(1, PackDisplayNames.LoadFromDirectory(LanguageDir));
            Assert.Equal("本体", PackFilter.DisplayName("Squad"));
        }

        [Fact]
        public void LoadFromDirectory_MissingDirectoryOrFile_ReturnsZero()
        {
            Assert.Equal(0, PackDisplayNames.LoadFromDirectory(LanguageDir));
            Assert.Equal(0, PackDisplayNames.LoadFromDirectory(Path.Combine(_root, "no-such-lang")));
            Assert.Equal(0, PackDisplayNames.LoadFromDirectory(null));
            Assert.Equal(0, PackDisplayNames.LoadFromDirectory(string.Empty));
            Assert.Equal(0, PackDisplayNames.LoadFromFile(null));
            Assert.Equal(0, PackDisplayNames.LoadFromFile(string.Empty));
            Assert.Equal(0, PackDisplayNames.LoadFromFile(Path.Combine(LanguageDir, "nope.json")));
        }

        [Fact]
        public void PathFor_CombinesDirectoryAndFileName()
        {
            Assert.Equal(Path.Combine("P", PackDisplayNames.FileName), PackDisplayNames.PathFor("P"));
            Assert.Equal(string.Empty, PackDisplayNames.PathFor(null));
            Assert.Equal(string.Empty, PackDisplayNames.PathFor(string.Empty));
        }

        // ------------------------------------------------------------------ 与包枚举的隔离

        // 关键不变量：这张表是 .json（不是 .jsonl），所以包枚举永远看不到它 ——
        // 否则它会变成一个 modId 名下的空包，污染计数与面板列表。
        [Fact]
        public void ListPackFiles_IgnoresDisplayNamesFile()
        {
            string pack = Path.Combine(LanguageDir, "Squad.jsonl");
            File.WriteAllText(pack, "{\"k\":\"v\"}\n", new UTF8Encoding(false));
            WriteDisplayNames("{\"Squad\":\"本体\"}");

            IReadOnlyList<string> files = PackLocator.ListPackFiles(LanguageDir);

            Assert.Equal(new[] { pack }, files);
        }

        // 包从文件名派生 modId，数据表不该参与；顺手确认两者键空间一致（都用真实 modId 当键）
        [Fact]
        public void DisplayNames_KeyIsModIdDerivedFromFileName()
        {
            string fileName = "AtmosphereAutopilot.UI.il.jsonl";
            string modId = PackFilter.ModIdFromFileName(fileName);
            Assert.Equal("AtmosphereAutopilot.UI", modId);

            WriteDisplayNames("{\"names\":{\"AtmosphereAutopilot.UI\":\"大气自动驾驶\"}}");
            Assert.Equal(1, PackDisplayNames.LoadFromDirectory(LanguageDir));
            Assert.Equal("大气自动驾驶", PackFilter.DisplayName(modId));
        }
    }
}
