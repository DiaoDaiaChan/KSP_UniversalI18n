using System;
using UI18n.Core.Policy;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 语言门禁（决策 D24 / D28 / D32，05 §6.2）：非中文环境<b>一个 patch 都不装</b>。
    /// 这是 Mod 启动时唯一的分岔点，判断反了就是「玩家英文界面被中文糊一脸」，所以边界要钉死。
    /// </summary>
    public class LanguageGateTests
    {
        [Theory]
        [InlineData("zh")]
        [InlineData("zh-CN")]
        [InlineData("zh-cn")]
        [InlineData("zh-Hans")]
        [InlineData("ZH-CN")]
        [InlineData("Zh")]
        [InlineData("zH")]
        [InlineData("zhCN")]
        [InlineData(" zh-CN ")]
        public void ChineseLanguageCodes_AreRecognised(string language)
        {
            Assert.True(LanguageGate.IsChinese(language), "应判为中文：" + language);
            Assert.True(LanguageGate.ShouldTranslate(language, false));
        }

        [Theory]
        [InlineData("en")]
        [InlineData("en-US")]
        [InlineData("de")]
        [InlineData("fr-FR")]
        [InlineData("ja")]
        [InlineData("ru")]
        [InlineData("es-ES")]
        [InlineData("ko")]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("  \t ")]
        [InlineData("z")]
        [InlineData("Z")]
        public void NonChineseOrTooShortCodes_AreRejected(string language)
        {
            Assert.False(LanguageGate.IsChinese(language), "不应判为中文：" + language);
            Assert.False(LanguageGate.ShouldTranslate(language, false));
        }

        [Fact]
        public void NullLanguage_IsNotChinese_AndNeverThrows()
        {
            Assert.False(LanguageGate.IsChinese(null));
            Assert.False(LanguageGate.ShouldTranslate(null, false));
            Assert.Null(Record.Exception(delegate { LanguageGate.ShouldTranslate(null, true); }));
        }

        /// <summary>D32：ForceOverride=true 无条件放行 —— 任何语言、甚至 null 都要翻。</summary>
        [Theory]
        [InlineData("en-US")]
        [InlineData("de")]
        [InlineData("")]
        [InlineData(null)]
        public void ForceOverride_AlwaysTranslates(string language)
        {
            Assert.True(LanguageGate.ShouldTranslate(language, true));
        }

        /// <summary>ForceOverride 只放宽，不收紧：中文环境下即使不强制也照样翻。</summary>
        [Fact]
        public void ForceOverride_DoesNotSuppressChinese()
        {
            Assert.True(LanguageGate.ShouldTranslate("zh-CN", true));
        }

        /// <summary>
        /// 记录当前的前缀语义：只校验前两位 "zh"，不做 BCP-47 合法性检查，
        /// 所以 "zho" / "zhx" 也算中文。KSP 里 language 字段来自 Settings，不会出现这些值；
        /// 这里锁住行为，避免将来有人以为它是严格校验。
        /// </summary>
        [Theory]
        [InlineData("zho")]
        [InlineData("zhx")]
        [InlineData("zh-")]
        public void PrefixOnlyMatching_IsTheDocumentedBehaviour(string language)
        {
            Assert.True(LanguageGate.IsChinese(language));
        }
    }
}
