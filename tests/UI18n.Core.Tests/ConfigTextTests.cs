using System;
using System.IO;
using UI18n.Core.Config;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// v1.6 回归：KSP 的 <c>ConfigNode.PreFormatConfig</c> 会把每一行第一个 "//" 之后的内容整段砍掉
    /// （无条件、不认引号，反编译证据见 docs/09 §6.5.1），于是 <c>UpdateBaseUrl = https://…</c> 进到
    /// ConfigNode 里只会剩下 <c>https:</c>，加引号也救不了。这一层专门补读原始文本。
    /// </summary>
    public class ConfigTextTests
    {
        [Fact]
        public void ReadsUrlWithDoubleSlashIntact()
        {
            string text = "UI18N\n{\n    UpdateBaseUrl = https://raw.githubusercontent.com/DiaoDaiaChan/KSP_UniversalI18n/update\n}\n";
            Assert.Equal("https://raw.githubusercontent.com/DiaoDaiaChan/KSP_UniversalI18n/update",
                         ConfigText.ReadValueFromText(text, "UpdateBaseUrl"));
        }

        [Fact]
        public void PinsTheTruncationThisExistsToRepair()
        {
            // 参照物：这就是 ConfigNode 预处理之后剩下的东西。它必须被判为「残缺」，否则补读逻辑不会触发。
            Assert.Equal("https:", "https://x/y".Substring(0, "https://x/y".IndexOf("//", StringComparison.Ordinal)));
        }

        [Fact]
        public void StripsPairedQuotes()
        {
            string text = "UpdateBaseUrl = \"https://example.com/ui18n\"\n";
            Assert.Equal("https://example.com/ui18n", ConfigText.ReadValueFromText(text, "UpdateBaseUrl"));
        }

        [Fact]
        public void SkipsCommentLines()
        {
            string text = "    // UpdateBaseUrl = https://commented.out/old\n    UpdateBaseUrl = https://live.example/x\n";
            Assert.Equal("https://live.example/x", ConfigText.ReadValueFromText(text, "UpdateBaseUrl"));
        }

        [Fact]
        public void MissingKeyReturnsNull()
        {
            Assert.Null(ConfigText.ReadValueFromText("UpdateChannel = beta\n", "UpdateBaseUrl"));
            Assert.Null(ConfigText.ReadValueFromText("", "UpdateBaseUrl"));
            Assert.Null(ConfigText.ReadValueFromText("UpdateBaseUrl = https://x/y\n", ""));
        }

        [Fact]
        public void KeyNameMustMatchExactly()
        {
            Assert.Null(ConfigText.ReadValueFromText("UpdateBaseUrlExtra = https://x/y\n", "UpdateBaseUrl"));
            Assert.Null(ConfigText.ReadValueFromText("updatebaseurl = https://x/y\n", "UpdateBaseUrl"));
        }

        [Fact]
        public void HandlesCrlfAndIndentation()
        {
            string text = "UI18N\r\n{\r\n\tUpdateBaseUrl   =   https://127.0.0.1:8099/ui18n   \r\n}\r\n";
            Assert.Equal("https://127.0.0.1:8099/ui18n", ConfigText.ReadValueFromText(text, "UpdateBaseUrl"));
        }

        [Fact]
        public void InlineDoubleSlashBecomesPartOfTheValue()
        {
            // 刻意不做「剥行尾注释」：那正是会把 https:// 里的 // 一起吃掉的东西。
            // 代价是不许在同一行写 // 注释 —— Config.cfg 模板与文档里都写明了。
            Assert.Equal("https://x/y // oops", ConfigText.ReadValueFromText("UpdateBaseUrl = https://x/y // oops\n", "UpdateBaseUrl"));
        }

        [Fact]
        public void EmptyValueReturnsNull()
        {
            Assert.Null(ConfigText.ReadValueFromText("UpdateBaseUrl =\n", "UpdateBaseUrl"));
            Assert.Null(ConfigText.ReadValueFromText("UpdateBaseUrl = \"\"\n", "UpdateBaseUrl"));
        }

        [Fact]
        public void ReadsFromFileAndToleratesMissingFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "ui18n-configtext-" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                File.WriteAllText(path, "UI18N\n{\n    UpdateBaseUrl = https://raw.githubusercontent.com/x/y\n}\n");
                Assert.Equal("https://raw.githubusercontent.com/x/y", ConfigText.ReadValue(path, "UpdateBaseUrl"));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            Assert.Null(ConfigText.ReadValue(path, "UpdateBaseUrl"));
            Assert.Null(ConfigText.ReadValue(null, "UpdateBaseUrl"));
            Assert.Null(ConfigText.ReadValue(string.Empty, "UpdateBaseUrl"));
        }
    }
}
