using System;
using UI18n.Core.Dict;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// JSONL 行解析（04 §6.3 门禁）：<b>坏行只能计数跳过，不能抛、不能静默当成好行</b>。
    /// JsonLineReader 是 internal，这里一律经 <see cref="DictPack.AddJsonLine"/> 间接验证。
    /// </summary>
    public class JsonLineReaderTests
    {
        private static DictPack Pack(string line)
        {
            var pack = new DictPack();
            pack.AddJsonLine(line);
            return pack;
        }

        /// <summary>必须是 Ok 行，且返回唯一条目。</summary>
        private static DictEntry Single(string line)
        {
            DictPack pack = Pack(line);
            Assert.Equal(0, pack.ParseErrors);
            Assert.Equal(1, pack.Entries);
            return pack.GetEntries()[0];
        }

        /// <summary>必须是坏行：不产生条目、计入 ParseErrors、且刚才那次调用没有抛。</summary>
        private static void AssertBad(string line)
        {
            DictPack pack = Pack(line);
            Assert.Equal(0, pack.Entries);
            Assert.Equal(1, pack.ParseErrors);
            Assert.True(pack.IsEmpty);
        }

        [Fact]
        public void Bom_IsAcceptedAndStripped()
        {
            DictEntry entry = Single("\uFEFF{\"h\":\"0x0a\",\"t\":\"x\"}");

            Assert.Equal(10UL, entry.Hash);
            Assert.Equal("x", entry.Translation);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("   \t\t  ")]
        [InlineData("\uFEFF")]
        [InlineData("\uFEFF   ")]
        public void BlankLines_AreSkippedWithoutParseErrors(string line)
        {
            DictPack pack = Pack(line);

            Assert.Equal(0, pack.Entries);
            Assert.Equal(0, pack.ParseErrors);      // 空行不是坏行：不计错误
        }

        /// <summary>负向：坏行一律 Error —— 计入 ParseErrors、不产生条目、永不抛。</summary>
        [Theory]
        [InlineData("[]")]                                                  // 不是对象
        [InlineData("not json")]                                            // 完全不是 JSON
        [InlineData("{}")]                                                  // 缺 h
        [InlineData("{\"h\":\"0x0a\"}")]                                    // 缺 t
        [InlineData("{\"h\":\"0x0a\",\"t\":\"\"}")]                         // 空译文（04 §6.3 门禁 7）
        [InlineData("{\"h\":\"\",\"t\":\"x\"}")]                             // h 空串
        [InlineData("{\"h\":\"-1\",\"t\":\"x\"}")]                           // h 负数
        [InlineData("{\"h\":\"zz\",\"t\":\"x\"}")]                           // h 非十进制非 hex
        [InlineData("{\"h\":\"123456789012345678901234567890\",\"t\":\"x\"}")] // h 超出 ulong
        [InlineData("{\"h\":12,\"t\":\"x\"")]                                // 未闭合的对象
        [InlineData("{\"h\":12,\"t\":\"x\"} trailing")]                      // 对象后面还有垃圾
        [InlineData("{\"h\":12,\"t\":\"x\",}")]                              // 尾逗号
        [InlineData("{\"h\":12.5,\"t\":\"x\"}")]                             // 小数
        [InlineData("{\"h\":12,\"t\":\"a\tb\"}")]                            // 字符串里裸 TAB（控制字符）
        [InlineData("{\"h\":12,\"t\":\"a\\qb\"}")]                           // 非法转义 \q
        [InlineData("{\"h\":12,\"t\":x}")]                                   // 值没加引号
        [InlineData("{h:12,\"t\":\"x\"}")]                                   // 键没加引号
        [InlineData("{\"h\":12 \"t\":\"x\"}")]                               // 缺逗号
        [InlineData("{\"h\":12,\"t\":\"abc}")]                               // 字符串没闭合
        [InlineData("{\"h\":\"1234567890abcdeg\",\"t\":\"x\"}")]             // 16 位但含非 hex 字符 g：十进制/hex 都不成立
        [InlineData("{\"h\":12,\"t\":null}")]                                // t 为 JSON null = 缺 t
        [InlineData("{\"h\":12,\"t\":true}")]                                // t 为 true（不能变成 "true"）
        [InlineData("{\"h\":12,\"t\":false}")]                               // t 为 false（同上）
        [InlineData("{\"h\":null,\"s\":\"Kerbin\",\"t\":\"x\"}")]            // h 为 JSON null = 缺 h
        public void BadLines_AreCountedAndSkipped(string line)
        {
            AssertBad(line);
        }

        [Fact]
        public void Escapes_AreDecoded()
        {
            string line = "{\"h\":12,\"t\":\"\\u4e2d\\u6587|\\n|\\t|\\r|\\\"|\\\\|\\/|\\b|\\f\"}";

            DictEntry entry = Single(line);

            Assert.Equal("\u4e2d\u6587|\n|\t|\r|\"|\\|/|\b|\f", entry.Translation);
        }

        [Fact]
        public void SurrogatePairEscape_BecomesTwoUtf16Chars()
        {
            DictEntry entry = Single("{\"h\":12,\"t\":\"\\uD83D\\uDE00\"}");

            Assert.Equal(2, entry.Translation.Length);
            Assert.Equal("\uD83D\uDE00", entry.Translation);
        }

        [Fact]
        public void UnknownKeys_AndNestedValues_AreIgnored()
        {
            const string line = "{\"note\":{\"a\":[1,{\"b\":\"}\"}]},\"h\":12,\"arr\":[1,2],\"t\":\"x\",\"zero\":0}";

            DictEntry entry = Single(line);

            Assert.Equal("x", entry.Translation);
        }

        [Fact]
        public void DuplicateKeys_LastOneWins()
        {
            DictEntry entry = Single("{\"h\":12,\"t\":\"first\",\"t\":\"second\"}");

            Assert.Equal("second", entry.Translation);
        }

        /// <summary>h 的四种写法都要能解析出同一个值（JSON number / 十进制串 / hex 串 / 0x 前缀）。</summary>
        [Theory]
        [InlineData("12")]
        [InlineData("\"12\"")]
        [InlineData("\"0x0c\"")]
        [InlineData("\"0X0C\"")]
        [InlineData("\"c\"")]
        [InlineData("\"000000000000000c\"")]
        public void HashFormats_AreAccepted(string hashField)
        {
            DictEntry entry = Single("{\"h\":" + hashField + ",\"t\":\"x\"}");

            Assert.Equal(12UL, entry.Hash);
        }

        [Fact]
        public void Fields_MapToDictEntry()
        {
            DictEntry entry = Single("{\"h\":12,\"n\":\"Kerbin\",\"s\":\"Kerbin \",\"t\":\"Kerbal\",\"mod\":\"Kopernicus\",\"ctx\":\"AJE|T|M|0\"}");

            Assert.Equal(12UL, entry.Hash);
            Assert.Equal("Kerbin", entry.Normalized);
            Assert.Equal("Kerbin ", entry.Source);
            Assert.Equal("Kerbal", entry.Translation);
            Assert.Equal("Kopernicus", entry.Mod);
            Assert.Equal("AJE|T|M|0", entry.Context);
            Assert.Equal(KeyLevel.Ctx, entry.Level);
            Assert.False(entry.IsKeep);
        }

        [Fact]
        public void MissingOptionalFields_BecomeEmptyStrings()
        {
            DictEntry entry = Single("{\"h\":12,\"t\":\"x\"}");

            Assert.Equal(string.Empty, entry.Normalized);
            Assert.Equal(string.Empty, entry.Source);
            Assert.Equal(string.Empty, entry.Mod);
            Assert.Equal(string.Empty, entry.Context);
            Assert.Equal(KeyLevel.Global, entry.Level);
        }

        [Theory]
        [InlineData("{\"h\":12,\"t\":\"x\"}", KeyLevel.Global)]
        [InlineData("{\"h\":12,\"t\":\"x\",\"mod\":\"AJE\"}", KeyLevel.Site)]
        [InlineData("{\"h\":12,\"t\":\"x\",\"ctx\":\"C\"}", KeyLevel.Ctx)]
        [InlineData("{\"h\":12,\"t\":\"x\",\"mod\":\"AJE\",\"ctx\":\"C\"}", KeyLevel.Ctx)]
        public void Level_IsInferredFromFields(string line, KeyLevel expected)
        {
            Assert.Equal(expected, Single(line).Level);
        }

        [Fact]
        public void IsKeep_WhenTranslationEqualsSource()
        {
            Assert.True(Single("{\"h\":12,\"s\":\"Kerbin\",\"t\":\"Kerbin\"}").IsKeep);
            Assert.False(Single("{\"h\":12,\"s\":\"Kerbin\",\"t\":\"Kerbal\"}").IsKeep);
        }

        /// <summary>
        /// T8 修复 #1：h 是「16 位且全是 hex 字符」时按 <b>hex</b> 解析 —— 即使 16 位全是数字，
        /// 也不能被十进制 TryParse 抢走（否则这批条目按真实 hash 永远查不到；
        /// pool.jsonl 12792 行里有 12 行踩中，例如 fnv1a("R-79 Turbofan") = 0x8400081244540915）。
        /// </summary>
        [Fact]
        public void SixteenCharHexHash_IsParsedAsHex_EvenWhenAllCharsAreDigits()
        {
            const ulong realHash = 0x8400081244540915UL;    // fnv1a("R-79 Turbofan")
            DictPack pack = Pack("{\"h\":\"8400081244540915\",\"s\":\"R-79 Turbofan\",\"t\":\"R-79 涡扇\"}");

            Assert.Equal(0, pack.ParseErrors);
            Assert.Equal(1, pack.Entries);
            DictEntry entry = pack.GetEntries()[0];
            Assert.Equal(realHash, entry.Hash);                     // 按 hex 解析
            Assert.NotEqual(8400081244540915UL, entry.Hash);        // 曾经被误读成的十进制值

            string translation;
            Assert.True(pack.TryGet(realHash, "R-79 Turbofan", out translation));
            Assert.Equal("R-79 涡扇", translation);
        }

        /// <summary>
        /// hex/十进制歧义的边界：只有「恰好 16 位且都是 hex 字符」才按 hex，15/17 位全数字仍按十进制
        /// （保持对旧数据与人类手写 h 的兼容）。
        /// </summary>
        [Theory]
        [InlineData("\"123456789012345\"", 123456789012345UL)]       // 15 位全数字 → 十进制
        [InlineData("\"12345678901234567\"", 12345678901234567UL)]   // 17 位全数字 → 十进制
        [InlineData("\"1234567890abcdef\"", 0x1234567890abcdefUL)]   // 16 位含 a-f → hex
        [InlineData("\"1234567890ABCDEF\"", 0x1234567890abcdefUL)]   // 16 位大写 hex → hex
        public void HashRadix_BoundaryIsExactlySixteenHexChars(string hashField, ulong expected)
        {
            DictEntry entry = Single("{\"h\":" + hashField + ",\"t\":\"x\"}");

            Assert.Equal(expected, entry.Hash);
        }

        /// <summary>
        /// T8 修复 #3：JSON 字面量真值的语义 —— null = 「字段缺失」，true / false = 非法值，
        /// 三者都<b>不能</b>退化成同名普通字符串（否则 "t":null 会变成译文 "null" 显示给玩家）。
        /// </summary>
        [Theory]
        [InlineData("{\"h\":12,\"t\":null}")]                            // t 为 null = 缺 t → Error
        [InlineData("{\"h\":12,\"t\":true}")]                            // 译文是 true → 非法值
        [InlineData("{\"h\":12,\"t\":false}")]                           // 译文是 false → 非法值
        [InlineData("{\"h\":null,\"s\":\"Kerbin\",\"t\":\"x\"}")]        // h 为 null = 缺 h → Error
        [InlineData("{\"h\":12,\"s\":true,\"t\":\"x\"}")]                // 可选字段也不能是 true
        public void JsonLiteralValues_AreNotAcceptedAsStrings(string line)
        {
            AssertBad(line);
        }

        /// <summary>null 值等价于「这个键没写」（而不是空字符串之外的任何东西）。</summary>
        [Fact]
        public void NullValue_EqualsMissingField()
        {
            DictEntry entry = Single("{\"h\":12,\"n\":null,\"s\":null,\"t\":\"x\",\"mod\":null,\"ctx\":null}");

            Assert.Equal(string.Empty, entry.Normalized);
            Assert.Equal(string.Empty, entry.Source);
            Assert.Equal(string.Empty, entry.Mod);
            Assert.Equal(string.Empty, entry.Context);
            Assert.Equal(KeyLevel.Global, entry.Level);
            Assert.False(entry.IsKeep);
        }
    }
}
