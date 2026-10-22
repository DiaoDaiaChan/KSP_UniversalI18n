using System;
using System.Globalization;
using UI18n.Core.Hashing;
using UI18n.Core.Keys;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 三层键格式（决策 D5，00 §4.1）：
    /// ctx = {asm}|{type}|{member}|{ordinal}|{hash:x16}，site = {asm}|{hash:x16}，global = {hash:x16}。
    /// 键要落盘做持久化，所以「16 位小写 hex」与「同一输入恒等」是硬契约。
    /// </summary>
    public class TextKeyTests
    {
        [Fact]
        public void Separator_IsPipe()
        {
            Assert.Equal('|', TextKey.Separator);
        }

        [Theory]
        [InlineData(0UL, "0000000000000000")]
        [InlineData(0xAUL, "000000000000000a")]
        [InlineData(0xDEADBEEFUL, "00000000deadbeef")]
        [InlineData(0xFFFFFFFFFFFFFFFFUL, "ffffffffffffffff")]
        [InlineData(0x50FA39CEA0509806UL, "50fa39cea0509806")]
        public void Hex_IsAlwaysSixteenLowercaseDigits(ulong hash, string expected)
        {
            Assert.Equal(expected, TextKey.Hex(hash));
            Assert.Equal(16, TextKey.Hex(hash).Length);
        }

        [Fact]
        public void Hex_NeverEmitsUppercase()
        {
            for (int i = 0; i < 64; i++)
            {
                string hex = TextKey.Hex(((ulong)i * 0x9E3779B97F4A7C15UL) ^ 0xFFFFFFFFFFFFFFFFUL);

                Assert.Equal(16, hex.Length);
                Assert.Matches("^[0-9a-f]{16}$", hex);
            }
        }

        [Fact]
        public void Ctx_HasFivePipeSeparatedSegments()
        {
            Assert.Equal("AJE|AJE.AtTools|UpdateInfo|3|000000000000000a", TextKey.Ctx("AJE", "AJE.AtTools", "UpdateInfo", 3, 10UL));
        }

        [Fact]
        public void Site_HasTwoSegments()
        {
            Assert.Equal("AJE|000000000000000a", TextKey.Site("AJE", 10UL));
        }

        [Fact]
        public void Global_IsBareHash()
        {
            Assert.Equal("000000000000000a", TextKey.Global(10UL));
        }

        [Fact]
        public void TextOverloads_AgreeWithHashOverloads()
        {
            ulong h = Fnv1a.Hash("Thrust");

            Assert.Equal(TextKey.Ctx("AJE", "AJE.AtTools", "UpdateInfo", 3, h), TextKey.Ctx("AJE", "AJE.AtTools", "UpdateInfo", 3, "Thrust"));
            Assert.Equal(TextKey.Site("AJE", h), TextKey.Site("AJE", "Thrust"));
            Assert.Equal(TextKey.Global(h), TextKey.Global("Thrust"));
        }

        [Fact]
        public void TextOverloads_HashRawTextNotNormalizedText()
        {
            // 键的哈希输入是「调用方给什么就哈希什么」；归一化是调用方（Runtime / Normalizer）的职责。
            Assert.NotEqual(TextKey.Site("AJE", "Thrust "), TextKey.Site("AJE", "Thrust"));
            Assert.Equal(TextKey.Site("AJE", Fnv1a.Hash("Thrust ")), TextKey.Site("AJE", "Thrust "));
        }

        /// <summary>null 段按空串处理，永不抛（采集器里 typeFullName 可能是空）。</summary>
        [Fact]
        public void NullSegments_AreTreatedAsEmpty()
        {
            Assert.Equal("|||0|0000000000000000", TextKey.Ctx(null, null, null, 0, 0UL));
            Assert.Equal("|0000000000000000", TextKey.Site(null, 0UL));
            Assert.Null(Record.Exception(delegate { TextKey.Ctx(null, null, null, 0, (string)null); }));
        }

        [Fact]
        public void Ctx_NegativeOrdinal_RendersMinusSign()
        {
            Assert.Equal("a|t|m|-1|0000000000000000", TextKey.Ctx("a", "t", "m", -1, 0UL));
        }

        [Fact]
        public void SameInput_IsIdempotent()
        {
            string first = TextKey.Ctx("A", "T", "M", 7, 42UL);

            for (int i = 0; i < 100; i++)
            {
                Assert.Equal(first, TextKey.Ctx("A", "T", "M", 7, 42UL));
            }
        }

        /// <summary>
        /// 同一哈希的三种键必须互不相同，否则「ctx 命中就不会下沉到 site」的查找语义会被键格式本身破坏。
        /// </summary>
        [Fact]
        public void ThreeLevels_NeverCollideForSameHash()
        {
            const ulong h = 0x50FA39CEA0509806UL;
            string ctx = TextKey.Ctx("AJE", "AJE.AtTools", "UpdateInfo", 3, h);
            string site = TextKey.Site("AJE", h);
            string global = TextKey.Global(h);

            Assert.NotEqual(ctx, site);
            Assert.NotEqual(site, global);
            Assert.NotEqual(ctx, global);
        }

        /// <summary>ordinal 必须用 InvariantCulture 格式化：在阿拉伯语区域里 int.ToString() 可能给出本地数字。</summary>
        [Fact]
        public void Ordinal_IsFormattedWithInvariantCulture()
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
                Assert.Equal("A|T|M|1234|000000000000000a", TextKey.Ctx("A", "T", "M", 1234, 10UL));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }
    }
}
