using System;
using System.Collections.Generic;
using System.Globalization;
using UI18n.Core.Dict;
using UI18n.Core.Hashing;
using UI18n.Core.Text;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 骨架行（<c>"p":1</c>）与精确行的<b>隔离</b>：两套索引必须互不可见。
    /// <para>
    /// 隔离坏掉的后果不是报错，而是更糟的静默错译 —— 骨架行的 <c>s</c> 是带 <c>#</c> 的模板，
    /// 精确查表拿它比对永远不相等，看起来「无害」；反过来精确行进骨架索引，就会把
    /// <c>Mass Flow: 2.20917 ton/s</c> 的译文填到任意数字上。所以两边各钉一条。
    /// </para>
    /// </summary>
    public class DictPackPatternTests
    {
        private const string Skeleton = "Mass Flow: # ton/s";
        private const string Exact = "Mass Flow: 2.20917 ton/s";
        private const string SkeletonZh = "\u8D28\u91CF\u6D41\u91CF\uFF1A# \u5428/\u79D2";      // 质量流量：# 吨/秒

        private static string Line(ulong hash, string fields)
        {
            return "{\"h\":" + hash.ToString(CultureInfo.InvariantCulture) + "," + fields + "}";
        }

        private static string PatternLine(string skeleton, string translation)
        {
            return Line(Fnv1a.Hash(skeleton),
                "\"n\":\"" + skeleton + "\",\"s\":\"" + skeleton + "\",\"t\":\"" + translation + "\",\"p\":1");
        }

        private static string ExactLine(string source, string translation)
        {
            return Line(Fnv1a.Hash(source),
                "\"n\":\"" + source + "\",\"s\":\"" + source + "\",\"t\":\"" + translation + "\"");
        }

        /// <summary>骨架行对精确查表不可见（不产生命中，也不产生 keep 干扰），只走自己的索引。</summary>
        [Fact]
        public void PatternRow_IsInvisibleToExactLookup()
        {
            DictPack pack = DictPack.LoadJsonLines(new[] { PatternLine(Skeleton, SkeletonZh) });

            Assert.Equal(1, pack.Entries);
            Assert.Equal(1, pack.PatternEntries);
            Assert.True(pack.GetEntries()[0].IsPattern);

            string exact;
            Assert.False(pack.TryGet(Fnv1a.Hash(Skeleton), Skeleton, out exact));
            Assert.Null(exact);
            Assert.Equal(1, pack.Misses);

            string pattern;
            Assert.True(pack.TryGetPattern(Fnv1a.Hash(Skeleton), Skeleton, out pattern));
            Assert.Equal(SkeletonZh, pattern);
            Assert.Equal(1, pack.PatternHits);
            Assert.Equal(0, pack.Hits);                     // 骨架命中不计入精确通道的 Hits
        }

        /// <summary>精确行对骨架查表不可见 —— 否则精确译文会被当成骨架去填数字。</summary>
        [Fact]
        public void ExactRow_IsInvisibleToPatternLookup()
        {
            DictPack pack = DictPack.LoadJsonLines(new[] { ExactLine(Exact, "\u8D28\u91CF\u6D41\u91CF\uFF1A2.20917 \u5428/\u79D2") });

            Assert.Equal(0, pack.PatternEntries);
            Assert.False(pack.GetEntries()[0].IsPattern);

            string pattern;
            Assert.False(pack.TryGetPattern(Fnv1a.Hash(Exact), Exact, out pattern));
            Assert.Null(pattern);
            Assert.Equal(0, pack.PatternHits);

            string exact;
            Assert.True(pack.TryGet(Fnv1a.Hash(Exact), Exact, out exact));
        }

        /// <summary>真实链路：精确未命中 → 骨化 → 骨架命中 → 数字填回（Translator.TryPattern 的 Core 侧等价）。</summary>
        [Fact]
        public void ExactMiss_FallsThroughToSkeleton_AndFillsNumbersBack()
        {
            DictPack pack = DictPack.LoadJsonLines(new[]
            {
                ExactLine("Vessels: 51", "\u98DE\u884C\u5668\uFF1A51"),
                PatternLine("Vessels: #", "\u98DE\u884C\u5668\uFF1A#"),
            });

            Assert.Equal(2, pack.Entries);
            Assert.Equal(1, pack.PatternEntries);

            // 精确命中优先
            string exact;
            Assert.True(pack.TryGet(Fnv1a.Hash("Vessels: 51"), "Vessels: 51", out exact));
            Assert.Equal("\u98DE\u884C\u5668\uFF1A51", exact);

            // 换个数就只剩骨架这条路
            const string runtime = "Vessels: 77";
            string normalized = TextNormalizer.Normalize(runtime);
            string translation;
            Assert.False(pack.TryGet(Fnv1a.Hash(normalized), normalized, out translation));

            string skeleton;
            string[] numbers;
            Assert.True(TextSkeleton.TryBuild(normalized, out skeleton, out numbers));
            Assert.Equal("Vessels: #", skeleton);

            string pattern;
            Assert.True(pack.TryGetPattern(Fnv1a.Hash(skeleton), skeleton, out pattern));
            Assert.Equal("\u98DE\u884C\u5668\uFF1A77", TextSkeleton.FillBack(pattern, numbers));
        }

        /// <summary>骨架文本按 Ordinal 比对（大小写不同就不是同一条：<c>Mass</c> 与 <c>mass</c> 是两个模板）。</summary>
        [Fact]
        public void PatternLookup_ComparesSkeletonOrdinal()
        {
            DictPack pack = DictPack.LoadJsonLines(new[] { PatternLine(Skeleton, SkeletonZh) });

            string translation;
            Assert.False(pack.TryGetPattern(Fnv1a.Hash("mass flow: # ton/s"), "mass flow: # ton/s", out translation));
            Assert.Null(translation);
            Assert.Equal(0, pack.PatternHits);              // 未命中不改计数
        }

        /// <summary>骨架行的译文等于源文本 = keep：与精确通道同口径，算未命中（04 §6.3 门禁 5）。</summary>
        [Fact]
        public void KeepPatternRow_CountsAsMiss()
        {
            DictPack pack = DictPack.LoadJsonLines(new[] { PatternLine(Skeleton, Skeleton) });

            string translation;
            Assert.False(pack.TryGetPattern(Fnv1a.Hash(Skeleton), Skeleton, out translation));
            Assert.Null(translation);
            Assert.Equal(0, pack.PatternHits);

            // keep 的判定在条目上（IsKeep），但<b>不动精确通道的 Keeps 计数</b>：
            // 那两个数的分母是精确查表次数，骨架路径混进来统计口径就废了。
            Assert.True(pack.GetEntries()[0].IsKeep);
            Assert.Equal(0, pack.Keeps);
        }

        /// <summary><c>p</c> 接受 <c>1</c> / <c>true</c>；缺失或 <c>0</c> 就是精确行；别的键写布尔仍然整行非法。</summary>
        [Fact]
        public void PatternFlag_ParsingVariants()
        {
            var lines = new List<string>
            {
                "{\"h\":" + Fnv1a.Hash("a #").ToString(CultureInfo.InvariantCulture) + ",\"s\":\"a #\",\"t\":\"A #\",\"p\":1}",
                "{\"h\":" + Fnv1a.Hash("b #").ToString(CultureInfo.InvariantCulture) + ",\"s\":\"b #\",\"t\":\"B #\",\"p\":true}",
                "{\"h\":" + Fnv1a.Hash("c #").ToString(CultureInfo.InvariantCulture) + ",\"s\":\"c #\",\"t\":\"C #\",\"p\":0}",
                "{\"h\":" + Fnv1a.Hash("d #").ToString(CultureInfo.InvariantCulture) + ",\"s\":\"d #\",\"t\":\"D #\"}",
                "{\"h\":" + Fnv1a.Hash("e #").ToString(CultureInfo.InvariantCulture) + ",\"s\":\"e #\",\"t\":\"E #\",\"x\":true}",
            };

            DictPack pack = DictPack.LoadJsonLines(lines);

            Assert.Equal(4, pack.Entries);                   // 第 5 行非法：p 之外的布尔位置仍然拒绝
            Assert.Equal(2, pack.PatternEntries);            // "p":1 与 "p":true
            Assert.Equal(1, pack.ParseErrors);

            string translation;
            Assert.True(pack.TryGetPattern(Fnv1a.Hash("a #"), "a #", out translation));
            Assert.Equal("A #", translation);
            Assert.True(pack.TryGetPattern(Fnv1a.Hash("b #"), "b #", out translation));
            Assert.False(pack.TryGetPattern(Fnv1a.Hash("c #"), "c #", out translation));
            Assert.False(pack.TryGetPattern(Fnv1a.Hash("d #"), "d #", out translation));
        }

        /// <summary>ResetCounters 清 patternHits 但不动条目（与精确通道一致）。</summary>
        [Fact]
        public void ResetCounters_ClearsPatternHits_KeepsEntries()
        {
            DictPack pack = DictPack.LoadJsonLines(new[] { PatternLine(Skeleton, SkeletonZh) });

            string translation;
            Assert.True(pack.TryGetPattern(Fnv1a.Hash(Skeleton), Skeleton, out translation));
            Assert.Equal(1, pack.PatternHits);

            pack.ResetCounters();

            Assert.Equal(0, pack.PatternHits);
            Assert.Equal(1, pack.PatternEntries);
            Assert.Equal(1, pack.Entries);
        }

        /// <summary>空包：骨架查表也不许抛。</summary>
        [Fact]
        public void EmptyPack_PatternLookupMisses()
        {
            var pack = new DictPack();

            string translation;
            Assert.False(pack.TryGetPattern(0UL, null, out translation));
            Assert.Null(translation);
            Assert.Equal(0, pack.PatternEntries);

            Assert.False(pack.TryGetPattern(123UL, "x #", out translation));
            Assert.Equal(0, pack.PatternHits);
        }
    }
}
