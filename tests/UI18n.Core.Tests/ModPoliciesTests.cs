using System;
using System.Collections.Generic;
using UI18n.Core.Config;
using UI18n.Core.Dict;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// <see cref="ModPolicies"/> 与 <see cref="PackFilter.SelectByPolicy"/>：策略表是「优化项」，
    /// 所以每个失败路径都必须退化成「照旧全部加载」，绝不能因为数据坏了少加载包。
    /// </summary>
    public class ModPoliciesTests
    {
        private static string Json(string modsBody, string schema = ModPolicies.SchemaId, string lang = "zh-cn")
        {
            return "{\"schema\":\"" + schema + "\",\"lang\":\"" + lang + "\",\"mods\":{" + modsBody + "}}";
        }

        private static ModPolicies Parse(string json)
        {
            string error;
            ModPolicies policies = ModPolicies.Parse(json, out error);
            Assert.Equal(ModPolicies.ErrorNone, error);
            return policies;
        }

        [Fact]
        public void Empty_IsNeverNative_AndHasZeroCount()
        {
            Assert.Equal(0, ModPolicies.Empty.Count);
            Assert.False(ModPolicies.Empty.IsNative("Anything"));
            Assert.Equal(string.Empty, ModPolicies.Empty.Language);
            Assert.Equal(new string[0], ModPolicies.Empty.ModIds());
        }

        [Fact]
        public void Empty_TryGetReturnsFalse()
        {
            ModPolicyEntry entry;
            Assert.False(ModPolicies.Empty.TryGet("Anything", out entry));
            Assert.Null(entry);
        }

        [Fact]
        public void Parse_ObjectForm_ReadsEveryField()
        {
            ModPolicies p = Parse(Json(
                "\"AJE\":{\"policy\":\"NATIVE\",\"locKeys\":42,\"source\":\"gamedata\",\"languages\":[\"en-us\",\"zh-cn\"],\"folders\":[\"000_AJE\"]}," +
                "\"TUFX\":{\"policy\":\"LEGACY\",\"locKeys\":0,\"source\":\"pool\"}"));

            Assert.Equal(2, p.Count);
            Assert.Equal("zh-cn", p.Language);

            ModPolicyEntry aje;
            Assert.True(p.TryGet("AJE", out aje));
            Assert.Equal("AJE", aje.ModId);
            Assert.Equal("NATIVE", aje.Policy);
            Assert.Equal(42L, aje.LocKeys);
            Assert.Equal("gamedata", aje.Source);
            Assert.Equal(new string[] { "en-us", "zh-cn" }, aje.Languages);
            Assert.Equal(new string[] { "000_AJE" }, aje.Folders);
            Assert.True(aje.IsNative);

            ModPolicyEntry tufx;
            Assert.True(p.TryGet("TUFX", out tufx));
            Assert.Equal(0L, tufx.LocKeys);
            Assert.Equal("pool", tufx.Source);
            Assert.False(tufx.IsNative);
            Assert.Equal(new string[0], tufx.Languages);
        }

        [Fact]
        public void Parse_ArrayForm_IsAccepted()
        {
            ModPolicies p = Parse(
                "{\"schema\":\"" + ModPolicies.SchemaId + "\",\"lang\":\"zh-cn\",\"mods\":[" +
                "{\"modId\":\"AJE\",\"policy\":\"NATIVE\"}," +
                "{\"modId\":\"TUFX\",\"policy\":\"PARTIAL\"}]}");

            Assert.Equal(2, p.Count);
            Assert.True(p.IsNative("AJE"));
            Assert.False(p.IsNative("TUFX"));
        }

        [Fact]
        public void IsNative_IsCaseInsensitiveOnBothSides()
        {
            ModPolicies p = Parse(Json("\"AJE\":{\"policy\":\"native\"}"));

            Assert.True(p.IsNative("AJE"));
            Assert.True(p.IsNative("aje"));
            Assert.False(p.IsNative("Aje2"));
            Assert.False(p.IsNative(null));
            Assert.False(p.IsNative(string.Empty));
        }

        [Fact]
        public void Parse_UnknownPolicyIsKeptButNotNative()
        {
            ModPolicies p = Parse(Json("\"X\":{\"policy\":\"SOMETHING_NEW\"}"));

            ModPolicyEntry entry;
            Assert.True(p.TryGet("X", out entry));
            Assert.Equal("SOMETHING_NEW", entry.Policy);
            Assert.False(p.IsNative("X"));
            Assert.Equal(0, p.CountByPolicy(ModPolicies.PolicyLegacy));
        }

        [Fact]
        public void Parse_MissingPolicyFallsBackToLegacy()
        {
            ModPolicies p = Parse(Json("\"X\":{\"source\":\"pool\"}"));

            ModPolicyEntry entry;
            Assert.True(p.TryGet("X", out entry));
            Assert.Equal(ModPolicies.PolicyLegacy, entry.Policy);
        }

        [Fact]
        public void Parse_ModsWithoutSchema_IsRejected()
        {
            string error;
            ModPolicies p = ModPolicies.Parse("{\"mods\":{\"AJE\":{\"policy\":\"NATIVE\"}}}", out error);

            Assert.Equal(0, p.Count);
            Assert.StartsWith(ModPolicies.ErrorBadSchema, error);
        }

        [Fact]
        public void Parse_WrongSchema_IsRejected()
        {
            string error;
            ModPolicies p = ModPolicies.Parse(Json("\"AJE\":{\"policy\":\"NATIVE\"}", "ui18n-policies/9"), out error);

            Assert.Equal(0, p.Count);
            Assert.StartsWith(ModPolicies.ErrorBadSchema, error);
        }

        [Fact]
        public void Parse_EmptyString_IsBadJson()
        {
            string error;
            ModPolicies p = ModPolicies.Parse(string.Empty, out error);

            Assert.Equal(0, p.Count);
            Assert.Equal(ModPolicies.ErrorBadJson, error);
        }

        [Fact]
        public void Parse_Null_IsBadJson()
        {
            string error;
            ModPolicies p = ModPolicies.Parse(null, out error);

            Assert.Equal(0, p.Count);
            Assert.Equal(ModPolicies.ErrorBadJson, error);
        }

        [Fact]
        public void Parse_BrokenJson_IsBadJson()
        {
            string error;
            ModPolicies p = ModPolicies.Parse("{\"schema\":", out error);

            Assert.Equal(0, p.Count);
            Assert.StartsWith(ModPolicies.ErrorBadJson, error);
        }

        [Fact]
        public void Parse_NonObjectRoot_IsBadJson()
        {
            string error;
            ModPolicies p = ModPolicies.Parse("[]", out error);

            Assert.Equal(0, p.Count);
            Assert.StartsWith(ModPolicies.ErrorBadJson, error);
        }

        [Fact]
        public void Parse_EmptyMods_IsNoMods()
        {
            string error;
            ModPolicies p = ModPolicies.Parse(Json(string.Empty), out error);

            Assert.Equal(0, p.Count);
            Assert.Equal(ModPolicies.ErrorNoMods, error);
        }

        [Fact]
        public void Parse_EntriesWithoutUsableModId_AreSkipped()
        {
            string error;
            ModPolicies p = ModPolicies.Parse(Json("\"\":{\"policy\":\"NATIVE\"}, \"Bad\":\"not-an-object\""), out error);

            Assert.Equal(0, p.Count);
            Assert.Equal(ModPolicies.ErrorNoMods, error);
        }

        [Fact]
        public void Parse_DuplicateModId_LastOneWins()
        {
            ModPolicies p = Parse(Json(
                "\"AJE\":{\"policy\":\"LEGACY\"},\"aje\":{\"policy\":\"NATIVE\"}"));

            Assert.Equal(1, p.Count);
            Assert.True(p.IsNative("AJE"));
        }

        [Fact]
        public void ModIds_AreSortedOrdinal()
        {
            // 表是 case-insensitive 的：A 与 a 算同一个 mod（同一包不同 channel 的大小写差异也归并），
            // 所以这里是 2 条记录而不是 3 条。
            ModPolicies p = Parse(Json(
                "\"b\":{\"policy\":\"LEGACY\"},\"A\":{\"policy\":\"LEGACY\"},\"a\":{\"policy\":\"LEGACY\"}"));

            Assert.Equal(2, p.Count);
            Assert.Equal(new string[] { "A", "b" }, p.ModIds());
        }

        [Fact]
        public void CountByPolicy_AndDescribe()
        {
            ModPolicies p = Parse(Json(
                "\"A\":{\"policy\":\"NATIVE\"},\"B\":{\"policy\":\"NATIVE_PARTIAL\"}," +
                "\"C\":{\"policy\":\"PARTIAL\"},\"D\":{\"policy\":\"LEGACY\"},\"E\":{\"policy\":\"legacy\"}"));

            Assert.Equal(1, p.CountByPolicy(ModPolicies.PolicyNative));
            Assert.Equal(1, p.CountByPolicy("native_partial"));
            Assert.Equal(2, p.CountByPolicy(ModPolicies.PolicyLegacy));
            Assert.Equal(0, p.CountByPolicy(null));
            Assert.Contains("mods=5", p.Describe());
            Assert.Contains("legacy=2", p.Describe());
        }

        [Fact]
        public void LoadFromDirectory_MissingFile_IsNoFile()
        {
            string error;
            ModPolicies p = ModPolicies.LoadFromDirectory(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ui18n-no-such-dir-" + Guid.NewGuid().ToString("N")),
                out error);

            Assert.Equal(0, p.Count);
            Assert.StartsWith(ModPolicies.ErrorNoFile, error);
        }

        [Fact]
        public void LoadFromDirectory_NullOrEmpty_IsNoFile()
        {
            string error;
            Assert.Equal(0, ModPolicies.LoadFromDirectory(null, out error).Count);
            Assert.StartsWith(ModPolicies.ErrorNoFile, error);
            Assert.Equal(0, ModPolicies.LoadFromDirectory(string.Empty, out error).Count);
            Assert.StartsWith(ModPolicies.ErrorNoFile, error);
        }

        [Fact]
        public void LoadFromFile_ReadsUtf8AndTrimmedBom()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ui18n-pol-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string path = System.IO.Path.Combine(dir, ModPolicies.FileName);
                System.IO.File.WriteAllText(path, Json("\"AJE\":{\"policy\":\"NATIVE\"}"), new System.Text.UTF8Encoding(false));

                string error;
                ModPolicies p = ModPolicies.LoadFromFile(path, out error);

                Assert.Equal(ModPolicies.ErrorNone, error);
                Assert.True(p.IsNative("AJE"));
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Fact]
        public void SelectByPolicy_DropsNativeOnly_AndKeepsOrder()
        {
            ModPolicies p = Parse(Json("\"AJE\":{\"policy\":\"NATIVE\"},\"TUFX\":{\"policy\":\"LEGACY\"}"));
            string[] files = new string[]
            {
                @"C:\Packs\zh-cn\AJE.jsonl",
                @"C:\Packs\zh-cn\TUFX.jsonl",
                @"C:\Packs\zh-cn\AJE.il.jsonl",
                @"C:\Packs\zh-cn\NoPolicy.jsonl",
            };

            int native;
            string[] kept = PackFilter.SelectByPolicy(files, p, out native);

            Assert.Equal(2, native);
            Assert.Equal(new string[] { files[1], files[3] }, kept);
        }

        [Fact]
        public void SelectByPolicy_NullOrEmptyPolicies_KeepsEverything()
        {
            string[] files = new string[] { @"Packs\zh-cn\AJE.jsonl", @"Packs\zh-cn\TUFX.jsonl" };

            int native;
            string[] withNull = PackFilter.SelectByPolicy(files, null, out native);
            Assert.Equal(0, native);
            Assert.Equal(files, withNull);

            string[] withEmpty = PackFilter.SelectByPolicy(files, ModPolicies.Empty, out native);
            Assert.Equal(0, native);
            Assert.Equal(files, withEmpty);

            Assert.NotSame(files, withNull);
        }

        [Fact]
        public void SelectByPolicy_EmptyInput_IsEmpty()
        {
            int native;
            Assert.Empty(PackFilter.SelectByPolicy(null, ModPolicies.Empty, out native));
            Assert.Equal(0, native);
            Assert.Empty(PackFilter.SelectByPolicy(new string[0], ModPolicies.Empty, out native));
            Assert.Equal(0, native);
        }

        [Fact]
        public void SelectByPolicy_AllNative_LeavesNothing()
        {
            ModPolicies p = Parse(Json("\"AJE\":{\"policy\":\"NATIVE\"}"));

            int native;
            string[] kept = PackFilter.SelectByPolicy(new string[] { @"Packs\zh-cn\AJE.jsonl" }, p, out native);

            Assert.Empty(kept);
            Assert.Equal(1, native);
        }

        [Fact]
        public void SelectByPolicy_IgnoresPseudoPacks()
        {
            ModPolicies p = Parse(Json("\"AJE\":{\"policy\":\"NATIVE\"}"));
            string[] files = new string[] { @"Packs\zh-cn\UI18n-Unattributed.il.jsonl", @"Packs\zh-cn\AJE.cfg.jsonl" };

            int native;
            string[] kept = PackFilter.SelectByPolicy(files, p, out native);

            Assert.Equal(1, native);
            Assert.Equal(new string[] { files[0] }, kept);
        }
    }
}
