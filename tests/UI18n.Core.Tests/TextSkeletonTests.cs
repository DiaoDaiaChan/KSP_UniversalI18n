using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using UI18n.Core.Text;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 数字骨架（pattern 通道）的跨语言门禁。
    /// <para>
    /// 骨架是<b>查表键</b>的生产者，C# 运行期（<see cref="TextSkeleton.TryBuild"/>）与 python 侧
    /// <c>tools/i18n_tools/skeleton.py</c>（生成语言包里的 <c>p:1</c> 行）必须逐字同口径 ——
    /// 两边差一个字符，骨架行的 <c>h</c> 就永远对不上，症状是「翻译全都没生效」而不是报错。
    /// 所以这里拿 python 生成的 <c>Data/skeleton-golden.jsonl</c>（331 条，含 31 条手写边界）
    /// 逐条比对：(sk, nums) 与 FillBack 往返都要一致。
    /// </para>
    /// <para>
    /// 改骨化规则必须<b>同时</b>改 skeleton.py 并重新生成这份 goldens，否则两边自测会各绿各的。
    /// </para>
    /// </summary>
    public class TextSkeletonTests
    {
        /// <summary>goldens 路径（csproj 里 CopyToOutputDirectory=PreserveNewest 复制到输出的 Data 下）。</summary>
        private static string GoldenPath()
        {
            return Path.Combine(AppContext.BaseDirectory, "Data", "skeleton-golden.jsonl");
        }

        private sealed class GoldenRow
        {
            public string Text;
            public string Skeleton;
            public string[] Numbers;
        }

        private static List<GoldenRow> LoadGolden()
        {
            string path = GoldenPath();
            Assert.True(File.Exists(path), "缺少门禁语料：" + path + "（应从 tests/UI18n.Core.Tests/Data 复制到输出目录）");

            var rows = new List<GoldenRow>();
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                using (JsonDocument doc = JsonDocument.Parse(line))
                {
                    JsonElement root = doc.RootElement;
                    var row = new GoldenRow
                    {
                        Text = root.GetProperty("t").GetString(),
                        Skeleton = root.GetProperty("sk").ValueKind == JsonValueKind.Null
                            ? null
                            : root.GetProperty("sk").GetString(),
                        Numbers = new string[0],
                    };

                    var numbers = new List<string>();
                    foreach (JsonElement n in root.GetProperty("nums").EnumerateArray())
                    {
                        numbers.Add(n.GetString());
                    }

                    row.Numbers = numbers.ToArray();
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>331 条逐条：骨架文本、数字段、往返还原、占位符计数都必须与 python 一致。</summary>
        [Fact]
        public void Golden_SkeletonMatchesPython_AndRoundTrips()
        {
            List<GoldenRow> rows = LoadGolden();
            Assert.True(rows.Count >= 300, "goldens 条数异常：" + rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                GoldenRow row = rows[i];
                string where = "第 " + (i + 1) + " 行 t=\"" + row.Text + "\"";

                string skeleton;
                string[] numbers;
                bool built = TextSkeleton.TryBuild(row.Text, out skeleton, out numbers);

                if (row.Skeleton == null)
                {
                    Assert.False(built, where + "：python 判定无骨架，C# 却生成了 \"" + skeleton + "\"");
                    Assert.Empty(numbers);
                    continue;
                }

                Assert.True(built, where + "：python 给了骨架，C# 却返回 false");
                Assert.Equal(row.Skeleton, skeleton);
                Assert.Equal(row.Numbers, numbers);

                // 标签内的 '#' 是字面量（<color=#FF00AA>），所以填回不是简单 replace，必须走同一套扫描
                Assert.Equal(row.Text, TextSkeleton.FillBack(row.Skeleton, row.Numbers));
                Assert.Equal(row.Numbers.Length, TextSkeleton.CountPlaceholders(row.Skeleton));
            }
        }

        /// <summary>标签里的色值 <c>#</c> 不吃槽位；标签外的才是占位符（互逆性最容易坏的地方）。</summary>
        [Fact]
        public void TagInteriorHash_IsLiteral_NotASlot()
        {
            const string source = "<color=#FF00AA>12 ms</color>";

            string skeleton;
            string[] numbers;
            Assert.True(TextSkeleton.TryBuild(source, out skeleton, out numbers));

            Assert.Equal("<color=#FF00AA># ms</color>", skeleton);
            Assert.Equal(new[] { "12" }, numbers);
            Assert.Equal(1, TextSkeleton.CountPlaceholders(skeleton));
            Assert.Equal(source, TextSkeleton.FillBack(skeleton, numbers));

            // 译文侧同理：译文里的 # 只有一个吃数字，色值原样保留
            Assert.Equal("<color=#FF00AA>12 毫秒</color>",
                TextSkeleton.FillBack("<color=#FF00AA># 毫秒</color>", new[] { "12" }));
        }

        /// <summary>白名单外的尖括号不吞数字（<c>a &lt; b</c> 里的比较符），带空格的标签写法同样不吞。</summary>
        [Fact]
        public void UnknownAngleBrackets_DoNotSwallowNumbers()
        {
            string skeleton;
            string[] numbers;

            Assert.True(TextSkeleton.TryBuild("a < b and 7", out skeleton, out numbers));
            Assert.Equal("a < b and #", skeleton);
            Assert.Equal(new[] { "7" }, numbers);

            // <quad width=2> 带空格 → 不匹配白名单（与 python 实测值一致，别「顺手修」）
            Assert.True(TextSkeleton.TryBuild("<quad width=2>x</quad>", out skeleton, out numbers));
            Assert.Equal("<quad width=#>x</quad>", skeleton);
            Assert.Equal(new[] { "2" }, numbers);
        }

        /// <summary>只吃一层分隔符：<c>1.2.3</c> = "1.2" + "3"；末尾孤立分隔符不吃（<c>1.</c> 的 nums 只有 "1"）。</summary>
        [Fact]
        public void NumberSegment_EatsAtMostOneSeparator()
        {
            string skeleton;
            string[] numbers;

            Assert.True(TextSkeleton.TryBuild("1.2.3", out skeleton, out numbers));
            Assert.Equal("#.#", skeleton);
            Assert.Equal(new[] { "1.2", "3" }, numbers);

            Assert.True(TextSkeleton.TryBuild("Delta-v: 3,456.7 m/s", out skeleton, out numbers));
            Assert.Equal("Delta-v: #.# m/s", skeleton);
            Assert.Equal(new[] { "3,456", "7" }, numbers);

            Assert.True(TextSkeleton.TryBuild("1.", out skeleton, out numbers));
            Assert.Equal("#.", skeleton);
            Assert.Equal(new[] { "1" }, numbers);
        }

        /// <summary>槽位上限：8 段可以，9 段放弃（那多半是日志 / 序列化数据，不是 UI 文本）。</summary>
        [Fact]
        public void SlotLimit_IsExactlyEight()
        {
            string skeleton;
            string[] numbers;

            Assert.True(TextSkeleton.TryBuild("A 1 B 2 C 3 D 4 E 5 F 6 G 7 H 8", out skeleton, out numbers));
            Assert.Equal(8, numbers.Length);
            Assert.Equal("A # B # C # D # E # F # G # H #", skeleton);

            Assert.False(TextSkeleton.TryBuild("1 2 3 4 5 6 7 8 9", out skeleton, out numbers));
            Assert.Null(skeleton);
            Assert.Empty(numbers);
        }

        /// <summary>没有数字 / 空串 / null 都不是模板；一个字符都不许因此变形。</summary>
        [Fact]
        public void NonTemplates_ReturnFalse_WithoutTouchingText()
        {
            string skeleton;
            string[] numbers;

            Assert.False(TextSkeleton.TryBuild(null, out skeleton, out numbers));
            Assert.False(TextSkeleton.TryBuild(string.Empty, out skeleton, out numbers));
            Assert.False(TextSkeleton.TryBuild("no digits here", out skeleton, out numbers));
            Assert.False(TextSkeleton.TryBuild("   ", out skeleton, out numbers));

            // 空白与 <<1>> 原样保留（占比符号不是数字段）
            Assert.True(TextSkeleton.TryBuild("Toggle <<1>> on part 3", out skeleton, out numbers));
            Assert.Equal("Toggle <<#>> on part #", skeleton);
            Assert.Equal(new[] { "1", "3" }, numbers);
        }

        /// <summary>FillBack 的三种失败都必须返回 null：半成品译文绝不能显示出去。</summary>
        [Fact]
        public void FillBack_RejectsMismatchedSlots()
        {
            Assert.Null(TextSkeleton.FillBack(null, new[] { "1" }));
            Assert.Null(TextSkeleton.FillBack("#", null));
            Assert.Null(TextSkeleton.FillBack("# #", new[] { "1" }));          // 占位符比数字多
            Assert.Null(TextSkeleton.FillBack("#", new[] { "1", "2" }));       // 数字没吃完
            Assert.Null(TextSkeleton.FillBack("#", new string[] { null }));    // 半个数字段
            Assert.Equal("12 毫秒", TextSkeleton.FillBack("# 毫秒", new[] { "12" }));
        }

        /// <summary>CountPlaceholders 与 HasDigits 的边界口径（前者用 -1 区分 null 与「零槽位」）。</summary>
        [Fact]
        public void CountPlaceholders_And_HasDigits_Edges()
        {
            Assert.Equal(-1, TextSkeleton.CountPlaceholders(null));
            Assert.Equal(0, TextSkeleton.CountPlaceholders(string.Empty));
            Assert.Equal(0, TextSkeleton.CountPlaceholders("no slots"));
            Assert.Equal(2, TextSkeleton.CountPlaceholders("#/#"));
            Assert.Equal(0, TextSkeleton.CountPlaceholders("<color=#FF00AA>x</color>"));

            Assert.False(TextSkeleton.HasDigits(null));
            Assert.False(TextSkeleton.HasDigits(string.Empty));
            Assert.False(TextSkeleton.HasDigits("Mass Flow: ton/s"));
            Assert.True(TextSkeleton.HasDigits("Mass Flow: 2.20917 ton/s"));
            Assert.True(TextSkeleton.HasDigits("x0"));
        }
    }
}
