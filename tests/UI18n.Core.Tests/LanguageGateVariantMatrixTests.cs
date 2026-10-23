using System;
using System.Globalization;
using UI18n.Core.Policy;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 语言变体矩阵（T17）。
    /// <para>
    /// **本机真实取值 = "zh-cn"**，证据（四条互相独立，全部在仓库外的 KSP 安装目录 / 游戏程序集里）：
    /// <list type="number">
    /// <item>settings.cfg：`LANGUAGE = zh-cn`（KSP 设置界面写回的原始值，小写 + 连字符）</item>
    /// <item>buildID.txt：`language = zh-cn`（安装时的语言，同一种写法）</item>
    /// <item>KSP.log：`[UI18n] language=zh-cn, enabled=True, forceOverride=False` —— 这是
    /// UI18nAddon 把 KSP.Localization.Localizer.CurrentLanguage 原样打出来的值（UI18nAddon.cs:275 返回它）</item>
    /// <item>Assembly-CSharp.dll：扫 ASCII 字符串能看到 `CurrentLanguage` / `get_CurrentLanguage` /
    /// `_currentLanguage` 三个符号 —— 它是**存下来的字段**而不是每次现算，所以 settings.cfg 里写什么，
    /// 门禁拿到的就是什么；KSP 不会替我们归一化成 `zh_CN` 或 `zh`</item>
    /// </list>
    /// 也就是说：KSP 自己从不产生 `zh_CN` / `ZH-CN` / `zh-Hans` 这类变体，除非玩家手改 settings.cfg。
    /// 下面的矩阵把「实现到底放行哪些变体」钉死，顺便把两类断言分开写：
    /// </para>
    /// <para>
    /// **设计承诺（DesignPromise）**：只有 `zh` 与 `zh-cn` 必须命中（D24/D28/00 §5.5/05 §6.2：只做 zh，
    /// 但架构语言无关）；语言非中文时**一个 patch 都不装**（红线 5）。
    /// </para>
    /// <para>
    /// **现状（CurrentBehaviour，不是承诺）**：实现只校验前两位 `zh`（LanguageGate.cs:43-45），
    /// 所以 `zh-tw` / `zh-hant` / `zh-hk` / `zh-mo` / `zh_CN` / `zhx` / `zho` 一律命中。
    /// 我们没有繁体包，也没有计划做；锁住现状是为了「将来谁改了这个判断，测试必须一起改」，
    /// 而不是宣称支持这些变体。
    /// </para>
    /// </summary>
    public class LanguageGateVariantMatrixTests
    {
        /// <summary>把 null / 空白 / 控制字符渲染成可读形式，失败信息里能看出到底是哪一行。</summary>
        private static string Show(string language)
        {
            if (language == null)
            {
                return "<null>";
            }

            if (language.Length == 0)
            {
                return "<empty>";
            }

            var sb = new System.Text.StringBuilder();
            foreach (char c in language)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 设计承诺的最小集合：`zh`（主语言）与 `zh-cn`（唯一在产的语言包）必须命中。
        /// 这两行是整个 Mod 的启动分岔点，判断反了就是「玩家英文界面被中文糊一脸」。
        /// </summary>
        [Theory]
        [InlineData("zh-cn")]
        [InlineData("zh")]
        public void DesignPromise_ZhAndZhCn_MustBeRecognised(string language)
        {
            Assert.True(LanguageGate.IsChinese(language), "设计承诺：必须判为中文 —— " + Show(language));
            Assert.True(LanguageGate.ShouldTranslate(language, false), "设计承诺：必须翻译 —— " + Show(language));
        }

        /// <summary>
        /// 13 个真实会出现的语言字符串，逐个写死期望值。第三列是「为什么」，会进失败信息。
        /// 前 3 行是承诺，其余是现状（见类注释）。
        /// </summary>
        [Theory]
        // --- 承诺：zh-cn / zh ---
        [InlineData("zh-cn", true, "本机真实取值（settings.cfg LANGUAGE = zh-cn）；设计承诺")]
        [InlineData("zh", true, "主语言；设计承诺（D28：只做 zh，但门禁语言无关）")]
        [InlineData("zh-CN", true, "大小写变体：实现按 Ordinal 比首两位、不经过 CultureInfo，故命中；非本机真实取值")]
        // --- 现状：前缀语义放行的其它中文变体（不是承诺） ---
        [InlineData("zh-hans", true, "现状：前缀语义放行；简体脚本标签，我们只发 zh-cn，不是承诺")]
        [InlineData("zh-tw", true, "现状：前缀语义放行；繁体包不存在，将来要做必须显式改这条断言")]
        [InlineData("zh-hant", true, "现状：前缀语义放行；繁体脚本标签，同上")]
        [InlineData("ZH-CN", true, "全大写变体：OrdinalIgnoreCase 语义，命中")]
        [InlineData("zh_CN", true, "下划线变体：BCP-47 非法，但门禁只看前两位，不看分隔符")]
        [InlineData(" zh-cn ", true, "前后空格：实现先 Trim（LanguageGate.cs:37）")]
        // --- 不命中：非中文 / 空 / null ---
        [InlineData("en-us", false, "KSP 英文默认取值；红线 5：非中文一个 patch 都不装")]
        [InlineData("en", false, "英文主语言")]
        [InlineData("", false, "空串：启动期 Localizer 未初始化时可能是它")]
        [InlineData(null, false, "null：同上，绝不抛异常")]
        public void VariantMatrix_MatchesPrefixSemantics(string language, bool expected, string why)
        {
            Assert.True(expected == LanguageGate.IsChinese(language),
                        "IsChinese(" + Show(language) + ") 期望 " + expected + " —— " + why);
            Assert.True(expected == LanguageGate.ShouldTranslate(language, false),
                        "ShouldTranslate(" + Show(language) + ", false) 期望 " + expected + " —— " + why);
        }

        /// <summary>
        /// 现状锁定：繁体（tw/hk/mo/hant）与其它中文变体都命中，因为我们只看前两位。
        /// **这不是设计承诺** —— 我们只发 zh-cn 包；将来真要支持繁体，得先有包、再改这里的断言。
        /// </summary>
        [Theory]
        [InlineData("zh-tw")]
        [InlineData("zh-hk")]
        [InlineData("zh-mo")]
        [InlineData("zh-hant")]
        public void CurrentBehaviour_TraditionalVariants_AreAccepted_ButNotAPromise(string language)
        {
            Assert.True(LanguageGate.IsChinese(language),
                        "现状（非承诺）：前缀语义会放行 " + Show(language) + "；若要收窄，必须同时改这条断言");
        }

        /// <summary>
        /// 下划线 / 大小写混写都不影响：门禁不做归一化，只 Trim 后看前两位。
        /// 这一点很关键 —— 归一化（`_`→`-`、小写）发生在 PackLocator / deploy 那一侧，不在门禁里。
        /// </summary>
        [Theory]
        [InlineData("zh_CN")]
        [InlineData("ZH_CN")]
        [InlineData("Zh_cn")]
        [InlineData("zh CN")]
        [InlineData("zh~cn")]
        [InlineData("zhcn")]
        public void SeparatorAndCase_DoNotMatter_PrefixOnly(string language)
        {
            Assert.True(LanguageGate.IsChinese(language), "前缀语义应放行 " + Show(language));
        }

        /// <summary>只看前两位：`z` 不够、`z h` 第二位是空格也不行、`zh` 正好够。</summary>
        [Theory]
        [InlineData("z", false)]
        [InlineData("Z", false)]
        [InlineData("zh", true)]
        [InlineData("z h", false)]
        [InlineData("zh\t", true)]
        [InlineData("\tzh", true)]
        public void PrefixSemantics_IsExactlyTwoCharacters(string language, bool expected)
        {
            Assert.True(expected == LanguageGate.IsChinese(language),
                        "IsChinese(" + Show(language) + ") 期望 " + expected + "（先 Trim，再看前两位）");
        }

        /// <summary>
        /// 纯空白（含 NBSP U+00A0、全角空格 U+3000、换行）一律不命中：
        /// Trim 之后为空 → false。这类值来自手改坏的 settings.cfg，不能当成中文放行。
        /// </summary>
        [Theory]
        [InlineData(" ")]
        [InlineData("\t")]
        [InlineData("\n")]
        [InlineData("\r\n")]
        [InlineData("\u00a0")]
        [InlineData("\u3000")]
        [InlineData("  \t  ")]
        public void WhitespaceOnly_IsNeverChinese(string language)
        {
            Assert.False(LanguageGate.IsChinese(language), "纯空白不应判为中文：" + Show(language));
            Assert.False(LanguageGate.ShouldTranslate(language, false));
        }

        /// <summary>
        /// 门禁必须与当前区域/区域设置无关（KSP 会切 locale，05 §6.2 要求不用 CultureInfo）。
        /// 反例：若有人把实现改成 `language.ToLower().StartsWith("zh")`，在 tr-TR 下 `ZH-CN`
        /// 的 `I`→`ı` 规则会出偏差；这里用两个「有坑」的区域把行为钉死。
        /// </summary>
        [Fact]
        public void Gate_IsIndependentOfCurrentCulture()
        {
            CultureInfo oldCulture = CultureInfo.CurrentCulture;
            CultureInfo oldUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                string[] cultures = { "tr-TR", "ar-SA", "de-DE" };
                foreach (string name in cultures)
                {
                    var culture = new CultureInfo(name);
                    CultureInfo.CurrentCulture = culture;
                    CultureInfo.CurrentUICulture = culture;

                    Assert.True(LanguageGate.IsChinese("ZH-CN"), name + " 下 ZH-CN 仍应命中");
                    Assert.True(LanguageGate.IsChinese("zh-cn"), name + " 下 zh-cn 仍应命中");
                    Assert.False(LanguageGate.IsChinese("EN-US"), name + " 下 EN-US 不应命中");
                    Assert.False(LanguageGate.ShouldTranslate("en-us", false), name + " 下英文不翻译");
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = oldCulture;
                CultureInfo.CurrentUICulture = oldUiCulture;
            }
        }

        /// <summary>D32：ForceOverride=true 时矩阵里每一行都必须放行（包括 null 与空白）。</summary>
        [Theory]
        [InlineData("zh-cn")]
        [InlineData("zh")]
        [InlineData("zh-hant")]
        [InlineData("zh_CN")]
        [InlineData(" ZH-CN ")]
        [InlineData("en-us")]
        [InlineData("en")]
        [InlineData("de")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ForceOverride_AcceptsEveryVariant(string language)
        {
            Assert.True(LanguageGate.ShouldTranslate(language, true),
                        "ForceOverride=true 必须无条件放行：" + Show(language));
        }

        /// <summary>
        /// 整个矩阵都不许抛（启动期调用方是 Mod 初始化，抛了就整局游戏起不来）：
        /// 这是「非法输入不抛」的负向契约。
        /// </summary>
        [Fact]
        public void WholeMatrix_NeverThrows()
        {
            string[] inputs =
            {
                null, "", " ", "\t", "\u00a0", "z", "zh", "zh-cn", "zh_CN", "ZH-CN", "zh-hant",
                "en-us", "en", "de", "zh\u0000cn", "\uD83D\uDE00", "\uD83D",
            };

            foreach (string input in inputs)
            {
                string label = Show(input);
                Assert.Null(Record.Exception(delegate { LanguageGate.IsChinese(input); }));
                Assert.Null(Record.Exception(delegate { LanguageGate.ShouldTranslate(input, false); }));
                Assert.Null(Record.Exception(delegate { LanguageGate.ShouldTranslate(input, true); }));
                // 空白/控制字符/孤立代理都不该被当成中文放行
                if (input == null || input.Trim().Length < 2 || !(input.Trim()[0] == 'z' || input.Trim()[0] == 'Z'))
                {
                    Assert.False(LanguageGate.IsChinese(input), "不应判为中文：" + label);
                }
            }
        }
    }
}
