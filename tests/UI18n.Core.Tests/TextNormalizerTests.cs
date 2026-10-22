using System;
using UI18n.Core.Text;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 文本归一化（04 §4.1 五步）。字典源的 n 字段与本类输出必须逐字符一致，否则哈希对不上、全盘 miss，
    /// 所以每条断言都钉死一个具体字符结果。
    /// <para>顺序是 NFKC → 换行/空白折叠 → trim；<b>不</b>折叠大小写、<b>不</b>统一引号。</para>
    /// </summary>
    public class TextNormalizerTests
    {
        [Fact]
        public void NullAndEmpty_BecomeEmpty()
        {
            Assert.Equal(string.Empty, TextNormalizer.Normalize(null));
            Assert.Equal(string.Empty, TextNormalizer.Normalize(string.Empty));
        }

        /// <summary>第 1 步：Unicode NFKC —— 全角字母/数字/标点/全角空格一律折成半角。</summary>
        [Theory]
        [InlineData("\uFF21\uFF22\uFF23", "ABC")]
        [InlineData("\uFF11\uFF12\uFF13", "123")]
        [InlineData("\uFF01\uFF1F", "!?")]
        [InlineData("\uFF32\uFF55\uFF4E", "Run")]
        [InlineData("\uFF21\uFF22\uFF23\u3000\uFF11\uFF12\uFF13\u3000\uFF01\uFF1F", "ABC 123 !?")]
        public void Nfkc_FoldsFullwidthToHalfwidth(string raw, string expected)
        {
            Assert.Equal(expected, TextNormalizer.Normalize(raw));
        }

        /// <summary>第 2 步：CRLF / CR / LF 全部变半角空格（含连续空行）。</summary>
        [Theory]
        [InlineData("a\r\nb", "a b")]
        [InlineData("a\rb", "a b")]
        [InlineData("a\nb", "a b")]
        [InlineData("a\r\n\r\nb", "a b")]
        [InlineData("a\n\n\nb", "a b")]
        public void LineEndings_CollapseToSingleSpace(string raw, string expected)
        {
            Assert.Equal(expected, TextNormalizer.Normalize(raw));
        }

        /// <summary>第 3–4 步：连续空白折叠为一个空格并 trim（含 TAB、NBSP、NEL、全角空格）。</summary>
        [Theory]
        [InlineData("  a   b  ", "a b")]
        [InlineData("a\t\tb", "a b")]
        [InlineData("a\u00A0b", "a b")]
        [InlineData("a\u0085b", "a b")]
        [InlineData("  a  ", "a")]
        [InlineData("\u3000", "")]
        [InlineData("  ", "")]
        [InlineData("\t\r\n", "")]
        public void Whitespace_CollapsesAndTrims(string raw, string expected)
        {
            Assert.Equal(expected, TextNormalizer.Normalize(raw));
        }

        /// <summary>第 5 步（反例）：大小写有意义，绝不能折叠。</summary>
        [Fact]
        public void DoesNotFoldCase()
        {
            Assert.Equal("Run run RUN", TextNormalizer.Normalize("Run run RUN"));
            Assert.Equal("ABCdef", TextNormalizer.Normalize("ABCdef"));
        }

        /// <summary>第 5 步（反例）：弯引号与直引号字形不同，绝不能统一。</summary>
        [Fact]
        public void DoesNotUnifyQuotes()
        {
            const string curly = "\u201Cx\u201D \u2018y\u2019";

            Assert.Equal(curly, TextNormalizer.Normalize(curly));
            Assert.Equal("\"x\" 'y'", TextNormalizer.Normalize("\"x\" 'y'"));
            Assert.NotEqual(TextNormalizer.Normalize("\"x\""), TextNormalizer.Normalize("\u201Cx\u201D"));
        }

        /// <summary>05 §4.3⑥ 零分配：已归一的串必须原引用返回，否则每次查表都多一次分配。</summary>
        [Theory]
        [InlineData("Booster Thrust")]
        [InlineData("a b")]
        [InlineData("\u63A8\u529B")]
        [InlineData("<<1>> {0}")]
        public void AlreadyNormalized_ReturnsSameInstance(string already)
        {
            Assert.Same(already, TextNormalizer.Normalize(already));
        }

        [Theory]
        [InlineData("\uFF21\uFF22\uFF23\u3000\uFF11\uFF12\uFF13")]
        [InlineData("  a \r\n\r\n b\t\tc  ")]
        [InlineData("Run run RUN \u201Cx\u201D")]
        [InlineData("\u63A8\u529B\uFF1A2\u00D7 Klimov")]
        public void Normalize_IsIdempotent(string raw)
        {
            string once = TextNormalizer.Normalize(raw);

            Assert.Equal(once, TextNormalizer.Normalize(once));
            Assert.Same(once, TextNormalizer.Normalize(once));
        }

        /// <summary>模板占位符与富文本标签必须原样保留（遮蔽它们在 04 §5 里是另一层的事）。</summary>
        [Fact]
        public void TemplateTokens_ArePreserved()
        {
            Assert.Equal(
                "<<1>> {0} %s <color=#fff>",
                TextNormalizer.Normalize("  <<1>>   {0}  %s   <color=#fff>  "));
        }

        /// <summary>负向：孤立代理（非法 UTF-16）会让 string.Normalize 抛 ArgumentException，本方法必须降级不抛。</summary>
        [Fact]
        public void LoneSurrogate_DoesNotThrow_AndDegradesToRaw()
        {
            string normalized = TextNormalizer.Normalize("\uD83D");
            Assert.Equal("\uD83D", normalized);

            string mixed = TextNormalizer.Normalize("a\uD83Db");
            Assert.Equal("a\uD83Db", mixed);
        }

        [Theory]
        [InlineData("\u63A8\u529B")]
        [InlineData("Thrust \u63A8\u529B")]
        [InlineData("\u3002")]
        [InlineData("\uFF21")]
        [InlineData("\uFF71")]
        [InlineData("\uAC00")]
        [InlineData("\uD840\uDC00")]
        public void ContainsCjk_True(string text)
        {
            Assert.True(TextNormalizer.ContainsCjk(text), "应含 CJK：" + text);
        }

        [Theory]
        [InlineData("Thrust 100%")]
        [InlineData("")]
        [InlineData("\uD83D\uDE00")]
        [InlineData("\u2192")]
        [InlineData("V\u00E9ronique")]
        public void ContainsCjk_False(string text)
        {
            Assert.False(TextNormalizer.ContainsCjk(text), "不应含 CJK：" + text);
        }

        [Fact]
        public void ContainsCjk_NullIsFalse()
        {
            Assert.False(TextNormalizer.ContainsCjk(null));
        }

        /// <summary>IsCjk 的字块边界：下界外一位必须是 false，边界内一位必须是 true。</summary>
        [Theory]
        [InlineData('\u10FF', false)]
        [InlineData('\u1100', true)]
        [InlineData('\u4E00', true)]
        [InlineData('\u9FFF', true)]
        [InlineData('\uA000', false)]
        [InlineData('\uFF21', true)]
        [InlineData('\u00B7', false)]
        [InlineData('\u2192', false)]
        [InlineData('A', false)]
        [InlineData('\uD83D', false)]
        public void IsCjk_Boundaries(char c, bool expected)
        {
            Assert.Equal(expected, TextNormalizer.IsCjk(c));
        }

        /// <summary>emoji 不在 CJK 字块内（U+1F600 既不在 BMP 字块，也不在扩展 B 区）。</summary>
        [Fact]
        public void Emoji_IsNotCjk()
        {
            Assert.False(TextNormalizer.ContainsCjk("\uD83D\uDE00"));
            Assert.False(TextNormalizer.IsCjk('\uD83D'));
            Assert.False(TextNormalizer.IsCjk('\uDE00'));
        }
    }
}
