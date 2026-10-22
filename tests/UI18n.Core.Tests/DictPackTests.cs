using System;
using System.Collections.Generic;
using System.Globalization;
using UI18n.Core.Dict;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 查找语义（00 §4.1 三层键 + 决策 D5）：
    /// <b>ctx &gt; site &gt; global</b>，同层先载入者赢；<b>任何一层命中 keep 就终止查找并算 miss</b>。
    /// 这些语义错一条，游戏里就会「该翻的不翻 / 不该翻的翻了」，所以逐条钉死。
    /// </summary>
    public class DictPackTests
    {
        private const ulong Kerbin = 0x50FA39CEA0509806UL;      // fnv1a("Kerbin")

        private static string Line(ulong hash, string fields)
        {
            return "{\"h\":" + hash.ToString(CultureInfo.InvariantCulture) + "," + fields + "}";
        }

        private static DictPack Pack(params string[] lines)
        {
            return DictPack.LoadJsonLines(lines);
        }

        /// <summary>带上下文的查询（Core 的 5 参重载同时要 out KeyLevel，这里只关心译文）。</summary>
        private static bool TryGet(DictPack pack, ulong hash, string source, string context, out string translation)
        {
            KeyLevel level;
            return pack.TryGet(hash, source, context, out translation, out level);
        }

        // ---------- 空包 / 载入 ----------

        [Fact]
        public void EmptyPack_IsEmptyAndEveryLookupMisses()
        {
            var pack = new DictPack();
            string translation;

            Assert.True(pack.IsEmpty);
            Assert.Equal(0, pack.Entries);
            Assert.False(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Null(translation);
            Assert.Equal(1, pack.Misses);
            Assert.Equal(0, pack.Hits);
        }

        [Fact]
        public void LoadJsonLines_Null_ReturnsEmptyPack()
        {
            DictPack pack = DictPack.LoadJsonLines(null);

            Assert.NotNull(pack);
            Assert.True(pack.IsEmpty);
        }

        [Fact]
        public void LoadJsonLines_SkipsBlankAndBadLinesButKeepsGoodOnes()
        {
            DictPack pack = Pack(
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"\u51EF\u5C14\u5BBE\""),
                "",
                "   ",
                "not json",
                Line(2UL, "\"s\":\"B\",\"t\":\"\u4e59\""),
                null,
                Line(3UL, "\"s\":\"C\",\"t\":\"\u4e19\""));

            Assert.Equal(3, pack.Entries);
            Assert.Equal(1, pack.ParseErrors);
            Assert.False(pack.IsEmpty);
            Assert.Equal(3, pack.GetEntries().Count);
        }

        [Fact]
        public void OnlyBadLines_LeavePackEmptyButCounted()
        {
            DictPack pack = Pack("nope", "{\"t\":\"x\"}");

            Assert.True(pack.IsEmpty);
            Assert.Equal(2, pack.ParseErrors);
        }

        // ---------- 三层优先级 ----------

        private static DictPack ThreeLevels()
        {
            return Pack(
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"G\""),
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"S\",\"mod\":\"AJE\""),
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"C\",\"ctx\":\"AJE|AJE.AtTools|UpdateInfo|0\""));
        }

        [Fact]
        public void Ctx_BeatsSiteAndGlobal_WhenContextMatches()
        {
            DictPack pack = ThreeLevels();
            string translation;
            KeyLevel level;

            Assert.True(pack.TryGet(Kerbin, "Kerbin", "AJE|AJE.AtTools|UpdateInfo|0", out translation, out level));
            Assert.Equal("C", translation);
            Assert.Equal(KeyLevel.Ctx, level);
        }

        [Fact]
        public void Site_BeatsGlobal_WhenContextIsUnknown()
        {
            DictPack pack = ThreeLevels();
            string translation;
            KeyLevel level;

            Assert.True(pack.TryGet(Kerbin, "Kerbin", "OTHER|Ctx|Here|0", out translation, out level));
            Assert.Equal("S", translation);
            Assert.Equal(KeyLevel.Site, level);
        }

        [Fact]
        public void Site_BeatsGlobal_WhenNoCtxEntryExistsForThisHash()
        {
            DictPack pack = Pack(
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"G\""),
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"S\",\"mod\":\"AJE\""));
            string translation;
            KeyLevel level;

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Equal("S", translation);

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation, out level));
            Assert.Equal(KeyLevel.Site, level);

            Assert.True(pack.TryGet(Kerbin, "Kerbin", string.Empty, out translation, out level));
            Assert.Equal("S", translation);
        }

        /// <summary>
        /// T8 修复 #2：不带 context 的查询（2 参 / 4 参重载，以及显式传 null、空串）必须跳过<b>全部</b> ctx 级条目 ——
        /// 泛指查询没有位置信息，不该拿到某个具体位置的译文，而应下沉到 site（再不行才 global）。
        /// </summary>
        [Fact]
        public void NoContextQuery_SkipsCtxLevelEntries()
        {
            DictPack pack = ThreeLevels();
            string translation;
            KeyLevel level;

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Equal("S", translation);                       // ctx 级被跳过 → site 级

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation, out level));
            Assert.Equal("S", translation);
            Assert.Equal(KeyLevel.Site, level);

            // 显式空串 / null 等价于「没有位置信息」
            Assert.True(pack.TryGet(Kerbin, "Kerbin", string.Empty, out translation, out level));
            Assert.Equal("S", translation);
            Assert.Equal(KeyLevel.Site, level);

            Assert.True(pack.TryGet(Kerbin, "Kerbin", null, out translation, out level));
            Assert.Equal("S", translation);
            Assert.Equal(KeyLevel.Site, level);
        }

        /// <summary>
        /// 只有 ctx 级条目时，无 context 的查询是<b>未命中</b>（不能"退而求其次"把 ctx 译文当通用译文用），
        /// 并且这次未命中要计入 Misses。
        /// </summary>
        [Fact]
        public void NoContextQuery_IsMiss_WhenOnlyCtxEntriesExist()
        {
            DictPack pack = Pack(Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"C\",\"ctx\":\"AJE|AJE.AtTools|UpdateInfo|0\""));
            string translation;
            KeyLevel level;

            Assert.False(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Null(translation);
            Assert.Equal(1, pack.Misses);
            Assert.Equal(0, pack.Hits);

            Assert.False(pack.TryGet(Kerbin, "Kerbin", string.Empty, out translation, out level));
            Assert.Equal(2, pack.Misses);
            Assert.Equal(0, pack.Hits);

            // 反过来：带对上下文就能命中（证明上面的 miss 是"跳过 ctx"而不是"条目坏了"）
            Assert.True(pack.TryGet(Kerbin, "Kerbin", "AJE|AJE.AtTools|UpdateInfo|0", out translation, out level));
            Assert.Equal("C", translation);
            Assert.Equal(KeyLevel.Ctx, level);
        }

        [Fact]
        public void Global_IsUsed_WhenNothingMoreSpecificExists()
        {
            DictPack pack = Pack(Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"G\""));
            string translation;
            KeyLevel level;

            Assert.True(pack.TryGet(Kerbin, "Kerbin", "AJE|AJE.AtTools|UpdateInfo|0", out translation, out level));
            Assert.Equal("G", translation);
            Assert.Equal(KeyLevel.Global, level);
        }

        /// <summary>优先级不能依赖载入顺序：反序载入必须给出同样的结果。</summary>
        [Fact]
        public void Priority_IsIndependentOfLoadOrder()
        {
            DictPack pack = Pack(
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"C\",\"ctx\":\"AJE|AJE.AtTools|UpdateInfo|0\""),
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"S\",\"mod\":\"AJE\""),
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"G\""));
            string translation;

            Assert.True(TryGet(pack, Kerbin, "Kerbin", "AJE|AJE.AtTools|UpdateInfo|0", out translation));
            Assert.Equal("C", translation);

            Assert.True(TryGet(pack, Kerbin, "Kerbin", "OTHER|Ctx|Here|0", out translation));
            Assert.Equal("S", translation);
        }

        /// <summary>ctx 级条目只对它自己的上下文生效；上下文不符时按 site/global 继续。</summary>
        [Fact]
        public void CtxEntry_OnlyMatchesItsOwnContext()
        {
            DictPack pack = Pack(
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"C1\",\"ctx\":\"C1\""),
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"C2\",\"ctx\":\"C2\""));
            string translation;

            Assert.True(TryGet(pack, Kerbin, "Kerbin", "C2", out translation));
            Assert.Equal("C2", translation);

            Assert.False(TryGet(pack, Kerbin, "Kerbin", "C3", out translation));
            Assert.Null(translation);
        }

        // ---------- keep：终止查找 ----------

        [Fact]
        public void Keep_AtCtxLevel_TerminatesLookup_AndCountsAsMiss()
        {
            DictPack pack = Pack(
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"Kerbin\",\"ctx\":\"AJE|AJE.AtTools|UpdateInfo|0\""),
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"S\",\"mod\":\"AJE\""),
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"G\""));
            string translation;
            KeyLevel level;

            Assert.False(pack.TryGet(Kerbin, "Kerbin", "AJE|AJE.AtTools|UpdateInfo|0", out translation, out level));
            Assert.Null(translation);
            Assert.Equal(KeyLevel.Miss, level);
            Assert.Equal(1, pack.Keeps);
            Assert.Equal(1, pack.Misses);
            Assert.Equal(0, pack.Hits);
        }

        [Fact]
        public void Keep_AtSiteLevel_TerminatesLookup()
        {
            DictPack pack = Pack(
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"Kerbin\",\"mod\":\"AJE\""),
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"G\""));
            string translation;

            Assert.False(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Null(translation);
            Assert.Equal(1, pack.Keeps);
        }

        [Fact]
        public void Keep_AtGlobalLevel_TerminatesLookup()
        {
            DictPack pack = Pack(Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"Kerbin\""));
            string translation;

            Assert.False(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Equal(1, pack.Keeps);
            Assert.Equal(1, pack.Misses);
        }

        /// <summary>keep 只终止「被走到的那一层」：上下文不符的 ctx keep 不应拦住更具体的 site 译文。</summary>
        [Fact]
        public void Keep_AtForeignCtx_DoesNotBlockSiteHit()
        {
            DictPack pack = Pack(
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"Kerbin\",\"ctx\":\"C1\""),
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"S\",\"mod\":\"AJE\""));
            string translation;

            Assert.True(TryGet(pack, Kerbin, "Kerbin", "C2", out translation));
            Assert.Equal("S", translation);
            Assert.Equal(0, pack.Keeps);
            Assert.Equal(1, pack.Hits);
        }

        /// <summary>keep 也可以用 n（归一化文本）判定：s 为空时拿 n 比。</summary>
        [Fact]
        public void Keep_IsDetectedViaNormalizedWhenSourceIsEmpty()
        {
            DictPack pack = Pack(Line(Kerbin, "\"n\":\"Kerbin\",\"t\":\"Kerbin\""));
            string translation;

            Assert.False(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Equal(1, pack.Keeps);
        }

        // ---------- 同哈希消歧（Ordinal 兜底） ----------

        [Fact]
        public void SameHash_DifferentSources_AreDisambiguatedBySource()
        {
            DictPack pack = Pack(
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"TX\""),
                Line(Kerbin, "\"s\":\"Kerbin II\",\"t\":\"TY\""));
            string translation;

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Equal("TX", translation);

            Assert.True(pack.TryGet(Kerbin, "Kerbin II", out translation));
            Assert.Equal("TY", translation);
        }

        [Fact]
        public void SourceMismatch_IsMiss_NotFirstEntryFallback()
        {
            DictPack pack = Pack(Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"TX\""));
            string translation;

            Assert.False(pack.TryGet(Kerbin, "Something else", out translation));
            Assert.Null(translation);
            Assert.Equal(1, pack.Misses);
        }

        /// <summary>条目带 s 时必须拿到源文才能匹配（不做「随便给一个」的通配）。</summary>
        [Fact]
        public void EntryWithSource_RequiresSourceArgument()
        {
            DictPack pack = Pack(Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"TX\""));
            string translation;

            Assert.False(pack.TryGet(Kerbin, null, out translation));
        }

        [Fact]
        public void SourceFallback_ToNormalized_WhenEntrySourceIsEmpty()
        {
            DictPack pack = Pack(Line(Kerbin, "\"n\":\"Kerbin\",\"t\":\"TX\""));
            string translation;

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Equal("TX", translation);

            Assert.False(pack.TryGet(Kerbin, "Other", out translation));
        }

        [Fact]
        public void SourceMismatchBeatsNormalizedMatch_WhenBothPresent()
        {
            // s 与 n 都是 Kerbin：用 n 也能命中
            DictPack pack = Pack(Line(Kerbin, "\"s\":\"Kerbin\",\"n\":\"Kerbin\",\"t\":\"TX\""));
            string translation;

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Equal("TX", translation);
        }

        // ---------- 计数 ----------

        [Fact]
        public void Counters_CountHitsMissesAndKeeps()
        {
            DictPack pack = Pack(
                Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"TX\""),
                Line(7UL, "\"s\":\"Kerbin\",\"t\":\"Kerbin\""));
            string translation;

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.False(pack.TryGet(Kerbin, "Nope", out translation));
            Assert.False(pack.TryGet(999UL, "Nope", out translation));
            Assert.False(pack.TryGet(7UL, "Kerbin", out translation));      // keep

            Assert.Equal(2, pack.Hits);
            Assert.Equal(3, pack.Misses);
            Assert.Equal(1, pack.Keeps);
        }

        [Fact]
        public void ResetCounters_ZeroesCountersButKeepsEntries()
        {
            DictPack pack = ThreeLevels();
            string translation;

            pack.TryGet(Kerbin, "Kerbin", out translation);
            pack.ResetCounters();

            Assert.Equal(0, pack.Hits);
            Assert.Equal(0, pack.Misses);
            Assert.Equal(0, pack.Keeps);
            Assert.Equal(0, pack.ParseErrors);
            Assert.Equal(3, pack.Entries);

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Equal(1, pack.Hits);
        }

        // ---------- 快照 / 索引失效 ----------

        [Fact]
        public void GetEntries_PreservesLoadOrder_AndIsReadOnly()
        {
            DictPack pack = Pack(
                Line(1UL, "\"s\":\"A\",\"t\":\"\u7532\""),
                Line(2UL, "\"s\":\"B\",\"t\":\"\u4e59\""),
                Line(3UL, "\"s\":\"C\",\"t\":\"\u4e19\""));
            IList<DictEntry> entries = pack.GetEntries();

            Assert.Equal(3, entries.Count);
            Assert.Equal("A", entries[0].Source);
            Assert.Equal("B", entries[1].Source);
            Assert.Equal("C", entries[2].Source);

            var extra = new DictEntry(4UL, "D", "D", "\u4e01", "", "");
            Assert.Throws<NotSupportedException>(delegate { entries.Add(extra); });
        }

        /// <summary>查到一半再追加条目：索引必须失效重建，否则新条目永远查不到。</summary>
        [Fact]
        public void AddAfterLookup_InvalidatesIndex()
        {
            DictPack pack = Pack(Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"G\""));
            string translation;

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Equal("G", translation);

            pack.AddJsonLine(Line(Kerbin, "\"s\":\"Kerbin\",\"t\":\"S\",\"mod\":\"AJE\""));

            Assert.True(pack.TryGet(Kerbin, "Kerbin", out translation));
            Assert.Equal("S", translation);
        }

        [Fact]
        public void KeyLevel_NumericOrder_IsMostSpecificFirst()
        {
            Assert.Equal(0, (int)KeyLevel.Miss);
            Assert.Equal(1, (int)KeyLevel.Ctx);
            Assert.Equal(2, (int)KeyLevel.Site);
            Assert.Equal(3, (int)KeyLevel.Global);
            Assert.True((int)KeyLevel.Ctx < (int)KeyLevel.Site);
            Assert.True((int)KeyLevel.Site < (int)KeyLevel.Global);
        }
    }
}
