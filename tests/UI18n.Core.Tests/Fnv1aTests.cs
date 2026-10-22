using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UI18n.Core.Hashing;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// FNV-1a 64 位哈希（决策 D11）——锁死的是**跨语言数据契约**，不是「能跑」：
    /// 输入是字符串的 <b>UTF-8 字节</b>（不是 UTF-16、不是字符序号），落盘形如 16 位小写 hex。
    /// 期望值全部由 python 侧 tools/i18n_tools/fnv.py 独立算出，避免「拿 C# 自证 C#」。
    /// </summary>
    public class Fnv1aTests
    {
        private static ulong ParseHex(string hex)
        {
            return ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        [Fact]
        public void Constants_MatchFnv1a64Spec()
        {
            Assert.Equal(0xCBF29CE484222325UL, Fnv1a.OffsetBasis);
            Assert.Equal(0x100000001B3UL, Fnv1a.Prime);
        }

        [Fact]
        public void NullAndEmpty_ReturnOffsetBasis()
        {
            Assert.Equal(0xCBF29CE484222325UL, Fnv1a.Hash(string.Empty));
            Assert.Equal(0xCBF29CE484222325UL, Fnv1a.Hash((string)null));
            Assert.Equal(0xCBF29CE484222325UL, Fnv1a.Hash(new byte[0]));
            Assert.Equal(0xCBF29CE484222325UL, Fnv1a.Hash((byte[])null));
        }

        /// <summary>
        /// FNV 官方向量 + python 侧算出的 CJK / emoji 向量。
        /// 中文那条锁死「UTF-8 而不是 UTF-16 / 字符序号」：若改成按 char 喂字节，"推力" 的值会变。
        /// </summary>
        [Theory]
        [InlineData("cbf29ce484222325", "")]
        [InlineData("af63dc4c8601ec8c", "a")]
        [InlineData("85944171f73967e8", "foobar")]
        [InlineData("0b5d5ada205a5f53", "Thrust")]
        [InlineData("50fa39cea0509806", "Kerbin")]
        [InlineData("797880c6e32d042d", "\u63A8\u529B")]
        [InlineData("feff073875020288", "\uD83D\uDE00")]
        public void Hash_MatchesPythonVectors(string expectedHex, string text)
        {
            Assert.Equal(ParseHex(expectedHex), Fnv1a.Hash(text));
        }

        /// <summary>
        /// 真实数据回归：这些行原样取自 _scratch/corpus/pool.jsonl（h 字段是 16 位 hex 字符串），
        /// 每一行都满足 h == fnv1a(text)（python 侧核对过）。它把「语言实现 ↔ 已采集语料」钉在一起。
        /// </summary>
        [Theory]
        [InlineData("bd1e98f3df6264ac", "Mil Mi-17 Rotor & Engine")]
        [InlineData("ca3f6bce76401f81", "Boeing CH-47 Chinook Rotor & Engine")]
        [InlineData("381f1c5d8cf13d8c", "Peel-N-StikTM Adhesive Decals")]
        [InlineData("9b018a78804f0ce7", "Edge Wear")]
        [InlineData("6dd591597c707086", "Aspect Ratio")]
        [InlineData("9e89ac64cec4e9af", "BurstPQS could not find any burst-compiled methods!\\n\\nThis is not a hard error and your game is not broken. It does mean that KSPBurst was unable to successfully build a native library so some things will be much slower than they were meant to be.\\n\\nFor the latest advice on making KSPBurst work take a look at the BurstPQS forum thread.")]
        [InlineData("09505307b5de866a", "\\u")]
        [InlineData("45f615692655a797", "\\nKSP version:")]
        [InlineData("d0f05e828b3b27b7", "One of the most produced Russian helicopters, Mi-17 is equipped with a 21.25m-diameter 5-blade rotary wing and 2\u00D7 Klimov VK-2500PS-03 turboshaft engine, (2,400hp each). Max. takeoff weight:13,000kg (28,660lb)")]
        [InlineData("e0efa2ac4262b9fd", "Boeing CH-47 Chinook is equipped with 2 rotors each with 3 blades (18.3m in diameter). 2 \u00D7 Lycoming T55-GA-714A turboshaft engine provides 4,733shp (3,529kW) each. Max takeoff weight is 22,680kg. The rotors can be CONFIGURED to using differential cyclic for yaw control and differential collective for pitch control.")]
        [InlineData("87302fc6f21caeb0", "The STAR 8 was developed and qualified as the Rocket Assisted Deceleration (RAD) motor for the Mars Exploration Rover (MER) program. The motor is based on the STAR 5D motor technology developed for JPL\u2019s Mars Pathfinder program. The STAR 8 first flew in January 2004 when three motors were used to decelerate each of the Spirit and Opportunity rovers for landing at Gusev Crater and Meridiani Planum on Mars.")]
        [InlineData("568b6238f4e8af3c", "V\u00E9ronique")]
        public void Hash_MatchesRealPoolCorpus(string expectedHex, string text)
        {
            Assert.Equal(ParseHex(expectedHex), Fnv1a.Hash(text));
        }

        /// <summary>
        /// 超长描述行（pool.jsonl 里长度 &gt; 300 的行）也不能漂，否则 04 §6.3 的字典门禁全废。
        /// 这条向量同时覆盖 U+00D7、U+2026、逗号句号混排。
        /// </summary>
        [Fact]
        public void Hash_NonAsciiPoolLine_IsStable()
        {
            const string text =
                "\u63A8\u529B\uFF1A2\u00D7 Klimov VK-2500PS-03\uFF0C\u6BCF\u53F0 2,400hp\u3002";
            ulong expected = ParseHex("6db2efd8d36e5c46");
            ulong actual = Fnv1a.Hash(text);

            // 自证式兜底：即使 python 侧还没算过这条，也至少保证「UTF-8 字节路径」与「逐码点路径」一致。
            Assert.Equal(Fnv1a.Hash(Encoding.UTF8.GetBytes(text)), actual);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData("")]
        [InlineData("a")]
        [InlineData("Kerbin-Duna-Moho")]
        [InlineData("\u63A8\u529B 100%")]
        [InlineData("\uD83D\uDE00\uD83D\uDE80")]
        [InlineData("line1\nline2\tline3")]
        public void StringOverload_EqualsExplicitUtf8ByteOverload(string text)
        {
            Assert.Equal(Fnv1a.Hash(Encoding.UTF8.GetBytes(text)), Fnv1a.Hash(text));
        }

        /// <summary>
        /// 非法 UTF-16（孤立代理）按 Encoding.UTF8 的替换行为（U+FFFD => EF BF BD）处理，<b>不抛</b>。
        /// 语料里确实有坏代理，硬抛会让整包载入失败（红线 6：失败只记不抛）。
        /// </summary>
        [Fact]
        public void LoneSurrogate_FollowsEncodingUtf8Replacement()
        {
            // 逐个断言而不是 [Theory]：xunit 2.x 给「只含孤立代理」的 InlineData 生成的用例 ID 会撞车，
            // 撞车的用例会被静默丢弃（既不通过也不跳过），等于白写。
            AssertLoneSurrogateEqualsReplacement("�");
            AssertLoneSurrogateEqualsReplacement("�");
            AssertLoneSurrogateEqualsReplacement("a�b");
            AssertLoneSurrogateEqualsReplacement("\uD83D");     // 字面六字符，不是代理
        }

        private static void AssertLoneSurrogateEqualsReplacement(string text)
        {
            Assert.Equal(Fnv1a.Hash(Encoding.UTF8.GetBytes(text)), Fnv1a.Hash(text));
        }

        [Fact]
        public void RangeOverload_EqualsSliceOfByteArray()
        {
            byte[] whole = Encoding.UTF8.GetBytes("Kerbin-Duna-Moho");
            byte[] slice = Encoding.UTF8.GetBytes("Duna");

            Assert.Equal(Fnv1a.Hash(slice), Fnv1a.Hash(whole, 7, 4));
            Assert.Equal(Fnv1a.Hash(whole), Fnv1a.Hash(whole, 0, whole.Length));
        }

        /// <summary>负向：区间越界是**调用方的 bug**，必须抛（静默截断会算出错误的持久化键）。</summary>
        [Theory]
        [InlineData(-1, 2)]
        [InlineData(0, 999)]
        [InlineData(3, 999)]
        public void RangeOverload_OutOfBounds_Throws(int offset, int count)
        {
            byte[] bytes = Encoding.UTF8.GetBytes("abc");

            Assert.Throws<ArgumentOutOfRangeException>(delegate { Fnv1a.Hash(bytes, offset, count); });
        }

        [Fact]
        public void NonPositiveCount_ReturnsOffsetBasis_WithoutThrowing()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("abc");

            Assert.Equal(Fnv1a.OffsetBasis, Fnv1a.Hash(bytes, -5, 0));
            Assert.Equal(Fnv1a.OffsetBasis, Fnv1a.Hash(bytes, 99, 0));
            Assert.Equal(Fnv1a.OffsetBasis, Fnv1a.Hash(bytes, 1, -3));
            Assert.Equal(Fnv1a.OffsetBasis, Fnv1a.Hash((byte[])null, 0, 5));
        }

        [Fact]
        public void Hash_IsDeterministicAcrossCalls()
        {
            ulong first = Fnv1a.Hash("Booster Thrust");

            for (int i = 0; i < 100; i++)
            {
                Assert.Equal(first, Fnv1a.Hash("Booster Thrust"));
            }
        }

        [Fact]
        public void DistinctStrings_ProduceDistinctHashes()
        {
            string[] texts = { string.Empty, "Kerbin", "kerbin", "Kerbin ", "Duna", "Kerbin-Duna", "\u63A8\u529B" };
            var seen = new HashSet<ulong>();

            foreach (string text in texts)
            {
                Assert.True(seen.Add(Fnv1a.Hash(text)), "哈希出现碰撞：" + text);
            }

            Assert.Equal(texts.Length, seen.Count);
        }
    }
}
