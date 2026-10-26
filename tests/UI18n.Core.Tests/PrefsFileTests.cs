using System;
using System.Collections.Generic;
using System.IO;
using UI18n.Core.Settings;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// v1.6 M2 的 prefs.json（按 mod 开关的持久化状态，task-71）。
    /// 这一层只测纯逻辑：默认值、往返、坏文件不抛、未知字段前向兼容、IsEnabled 默认开。
    /// 真正的写盘位置由 Runtime 的 PrefsRuntime 决定，Core 只负责「路径进来、状态出去」。
    /// </summary>
    public class PrefsFileTests
    {
        private static string TempPath(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ui18n-prefs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        [Fact]
        public void CreateDefault_MatchesContract()
        {
            Prefs prefs = Prefs.CreateDefault();
            Assert.Equal("ui18n-prefs/1", prefs.Schema);
            Assert.Equal("zh-cn", prefs.Language);
            Assert.Equal("translated", prefs.DefaultMode);
            Assert.Equal(0, prefs.ModCount);
            Assert.Equal(0, prefs.DisabledCount);
            Assert.NotNull(prefs.Submission);
            Assert.False(prefs.Submission.Granted);
            Assert.Equal(0, prefs.Submission.PolicyVersion);
            Assert.Equal(string.Empty, prefs.Submission.LastSubmittedAt);
        }

        // 没有记录 = 启用（缺省信任）。这是 docs/10 §10 的核心约定：
        // 玩家装了个新 mod，不需要去面板里打开它才生效。
        [Fact]
        public void IsEnabled_DefaultsTrueForUnknownMod()
        {
            Prefs prefs = Prefs.CreateDefault();
            Assert.True(prefs.IsEnabled("Squad"));
            Assert.True(prefs.IsEnabled("RealFuels"));
            Assert.True(prefs.IsEnabled(null));
            Assert.True(prefs.IsEnabled(string.Empty));
            Assert.False(prefs.Contains("Squad"));
            Assert.Null(prefs.GetExplicit("Squad"));
        }

        [Fact]
        public void SetEnabled_TogglesAndCounts()
        {
            Prefs prefs = Prefs.CreateDefault();
            prefs.SetEnabled("Squad", false);
            Assert.True(prefs.Contains("Squad"));
            Assert.False(prefs.IsEnabled("Squad"));
            Assert.Equal(false, prefs.GetExplicit("Squad"));
            Assert.Equal(1, prefs.ModCount);
            Assert.Equal(1, prefs.DisabledCount);
            Assert.NotNull(prefs.Get("Squad"));
            Assert.False(prefs.Get("Squad").Enabled);

            prefs.SetEnabled("Squad", true);
            Assert.True(prefs.IsEnabled("Squad"));
            Assert.Equal(0, prefs.DisabledCount);
            Assert.Equal(1, prefs.ModCount);
        }

        // modId 大小写不敏感：玩家手改 prefs.json 写 squad 也要生效，
        // 否则「关掉了却还在翻译」会成为最难查的一类 bug。
        [Fact]
        public void ModIds_AreCaseInsensitive()
        {
            Prefs prefs = Prefs.CreateDefault();
            prefs.SetEnabled("squad", false);
            Assert.False(prefs.IsEnabled("Squad"));
            Assert.False(prefs.IsEnabled("SQUAD"));
            Assert.Equal(1, prefs.ModCount);
            Assert.Equal("squad", prefs.GetModIds()[0]);
        }

        [Fact]
        public void GetModIds_And_DisabledModIds_AreOrdinalSorted()
        {
            Prefs prefs = Prefs.CreateDefault();
            prefs.SetEnabled("Squad", false);
            prefs.SetEnabled("000_TexturesUnlimited", false);
            prefs.SetEnabled("B9-PWings-Fork", true);
            prefs.SetEnabled("AtmosphereAutopilot.UI", false);

            Assert.Equal(
                new[] { "000_TexturesUnlimited", "AtmosphereAutopilot.UI", "B9-PWings-Fork", "Squad" },
                prefs.GetModIds());
            Assert.Equal(
                new[] { "000_TexturesUnlimited", "AtmosphereAutopilot.UI", "Squad" },
                prefs.DisabledModIds());
            Assert.Equal(3, prefs.DisabledCount);
        }

        [Fact]
        public void SetAll_AppliesToEveryGivenId()
        {
            Prefs prefs = Prefs.CreateDefault();
            string[] ids = { "Squad", "RealFuels", "MechJeb2" };
            prefs.SetAll(ids, false);
            Assert.Equal(3, prefs.DisabledCount);
            foreach (string id in ids)
            {
                Assert.False(prefs.IsEnabled(id));
            }

            prefs.SetAll(ids, true);
            Assert.Equal(0, prefs.DisabledCount);
        }

        [Fact]
        public void Clone_IsDeepEnough()
        {
            Prefs prefs = Prefs.CreateDefault();
            prefs.SetEnabled("Squad", false);
            prefs.Get("Squad").Unknown["note"] = "\"keep\"";
            prefs.Submission.Granted = true;
            prefs.Unknown["future"] = "1";

            Prefs copy = prefs.Clone();
            copy.SetEnabled("RealFuels", false);
            copy.Get("Squad").Unknown["extra"] = "2";
            copy.Submission.Granted = false;
            copy.Unknown["other"] = "2";

            Assert.False(copy.IsEnabled("RealFuels"));
            Assert.True(prefs.IsEnabled("RealFuels"));
            Assert.False(prefs.Get("Squad").Unknown.ContainsKey("extra"));
            Assert.True(prefs.Submission.Granted);
            Assert.False(prefs.Unknown.ContainsKey("other"));
            Assert.Equal(1, prefs.ModCount);
            Assert.Equal(2, copy.ModCount);
        }

        // 首次运行必然走这条路：文件不存在不是错误，返回默认值（全开）。
        [Fact]
        public void Load_MissingFile_ReturnsDefaultsAndReportsNotExists()
        {
            string path = TempPath("prefs.json");
            string error;
            bool exists;
            Prefs prefs = PrefsFile.Load(path, out error, out exists);

            Assert.Null(error);
            Assert.False(exists);
            Assert.NotNull(prefs);
            Assert.Equal(0, prefs.ModCount);
            Assert.True(prefs.IsEnabled("Squad"));
        }

        [Fact]
        public void Load_EmptyPath_ReturnsDefaults()
        {
            string error;
            Prefs prefs = PrefsFile.Load(null, out error);
            Assert.Null(error);
            Assert.Equal(0, prefs.ModCount);
        }

        // 坏 JSON 绝不抛（红线 6）：返回默认值 + 错误串，且原文件一个字节都不动
        // （玩家手改坏了，我们不能顺手把它覆盖成默认值，否则他要重新配一遍）。
        [Fact]
        public void Load_BadJson_ReturnsDefaultsAndLeavesFileUntouched()
        {
            string path = TempPath("prefs.json");
            string broken = "{ this is not json";
            File.WriteAllText(path, broken);

            string error;
            bool exists;
            Prefs prefs = PrefsFile.Load(path, out error, out exists);

            Assert.True(exists);
            Assert.NotNull(error);
            Assert.Contains("invalid-json", error);
            Assert.NotNull(prefs);
            Assert.Equal(0, prefs.ModCount);
            Assert.True(prefs.IsEnabled("Squad"));
            Assert.Equal(broken, File.ReadAllText(path));
        }

        [Fact]
        public void TryParse_ReadsContractShape()
        {
            string json = "{"
                + "\"schema\":\"ui18n-prefs/1\","
                + "\"language\":\"zh-cn\","
                + "\"defaultMode\":\"translated\","
                + "\"mods\":{\"Squad\":{\"enabled\":false},\"RealFuels\":{\"enabled\":true}},"
                + "\"submission\":{\"consent\":{\"granted\":true}}"
                + "}";

            Prefs prefs;
            string error;
            Assert.True(PrefsFile.TryParse(json, out prefs, out error), error);
            Assert.Equal("ui18n-prefs/1", prefs.Schema);
            Assert.Equal("zh-cn", prefs.Language);
            Assert.Equal("translated", prefs.DefaultMode);
            Assert.False(prefs.IsEnabled("Squad"));
            Assert.True(prefs.IsEnabled("RealFuels"));
            Assert.Equal(2, prefs.ModCount);
            Assert.True(prefs.Submission.Granted);
        }

        // docs/10 §10 的示例写的是 "schema": 1 与 "consent": "granted"，
        // 任务书写的是字符串 schema 与对象 consent —— 两种都要能读，只写任务书形态。
        [Fact]
        public void TryParse_AcceptsLegacySchemaAndStringConsent()
        {
            string json = "{\"schema\":1,\"submission\":{\"consent\":\"granted\",\"policyVersion\":4}}";

            Prefs prefs;
            string error;
            Assert.True(PrefsFile.TryParse(json, out prefs, out error), error);
            Assert.Equal("ui18n-prefs/1", prefs.Schema);
            Assert.True(prefs.Submission.Granted);
            Assert.Equal(4, prefs.Submission.PolicyVersion);
        }

        [Fact]
        public void TryParse_AcceptsLooseBooleanForms()
        {
            string json = "{\"mods\":{\"A\":{\"enabled\":\"false\"},\"B\":{\"enabled\":0},\"C\":{\"enabled\":\"true\"},\"D\":{\"enabled\":1}}}";

            Prefs prefs;
            string error;
            Assert.True(PrefsFile.TryParse(json, out prefs, out error), error);
            Assert.False(prefs.IsEnabled("A"));
            Assert.False(prefs.IsEnabled("B"));
            Assert.True(prefs.IsEnabled("C"));
            Assert.True(prefs.IsEnabled("D"));
            Assert.Equal(2, prefs.DisabledCount);
        }

        // 单条坏记录只进 Warnings，不整体失败：一个 mod 的字段写错不该让全部开关归零。
        [Fact]
        public void TryParse_BadModEntry_OnlyWarns()
        {
            string json = "{\"mods\":{\"Good\":{\"enabled\":false},\"Bad\":42}}";

            Prefs prefs;
            string error;
            Assert.True(PrefsFile.TryParse(json, out prefs, out error), error);
            Assert.False(prefs.IsEnabled("Good"));
            Assert.False(prefs.Contains("Bad"));
            Assert.True(prefs.Warnings.Count >= 1);
            Assert.Contains("Bad", prefs.Warnings[0]);
        }

        // 未知字段必须原样写回：升级一次不能把玩家的状态 / 未来版本的字段吃掉。
        [Fact]
        public void TryParse_PreservesUnknownFields_AndWritesThemBack()
        {
            string json = "{"
                + "\"schema\":\"ui18n-prefs/1\","
                + "\"mods\":{\"Squad\":{\"enabled\":false,\"note\":\"keep\"}},"
                + "\"submission\":{\"consent\":{\"granted\":false},\"channel\":\"beta\"},"
                + "\"future\":{\"x\":1}"
                + "}";

            Prefs prefs;
            string error;
            Assert.True(PrefsFile.TryParse(json, out prefs, out error), error);
            Assert.False(prefs.IsEnabled("Squad"));
            Assert.True(prefs.Unknown.ContainsKey("future"));
            Assert.Contains("x", prefs.Unknown["future"]);
            Assert.Contains("keep", prefs.Get("Squad").Unknown["note"]);
            Assert.Contains("beta", prefs.Submission.Unknown["channel"]);

            string written = PrefsFile.ToJson(prefs);
            Assert.Contains("\"future\"", written);
            Assert.Contains("\"note\"", written);
            Assert.Contains("\"channel\"", written);

            Prefs again;
            string againError;
            Assert.True(PrefsFile.TryParse(written, out again, out againError), againError);
            Assert.True(again.Unknown.ContainsKey("future"));
            Assert.False(again.IsEnabled("Squad"));
            Assert.Contains("keep", again.Get("Squad").Unknown["note"]);
        }

        [Theory]
        [InlineData(null, "empty-document")]
        [InlineData("", "empty-document")]
        [InlineData("{ this is not json", "invalid-json")]
        [InlineData("[]", "root-not-object")]
        [InlineData("42", "root-not-object")]
        [InlineData("{\"mods\":[]}", "mods-not-object")]
        [InlineData("{\"mods\":\"all\"}", "mods-not-object")]
        public void TryParse_RejectsStructuralGarbageWithoutThrowing(string json, string expected)
        {
            Prefs prefs;
            string error;
            Assert.False(PrefsFile.TryParse(json, out prefs, out error));
            Assert.Contains(expected, error);
        }

        [Fact]
        public void ToJson_IsDeterministic()
        {
            Prefs prefs = Prefs.CreateDefault();
            prefs.SetEnabled("Squad", false);
            prefs.SetEnabled("000_TexturesUnlimited", false);
            prefs.SetEnabled("B9-PWings-Fork", true);

            string first = PrefsFile.ToJson(prefs);
            string second = PrefsFile.ToJson(prefs);
            Assert.Equal(first, second);
            Assert.EndsWith("\n", first);
            Assert.True(first.IndexOf("000_TexturesUnlimited", StringComparison.Ordinal)
                < first.IndexOf("B9-PWings-Fork", StringComparison.Ordinal));
            Assert.Contains("\"schema\"", first);
            Assert.Contains("ui18n-prefs/1", first);
            Assert.Contains("\"granted\"", first);
        }

        [Fact]
        public void SaveThenLoad_RoundTrips()
        {
            string path = TempPath("prefs.json");
            Prefs prefs = Prefs.CreateDefault();
            prefs.SetEnabled("Squad", false);
            prefs.SetEnabled("RealFuels", true);
            prefs.SetEnabled("000_TexturesUnlimited", false);
            prefs.Get("Squad").Unknown["note"] = "\"keep\"";
            prefs.Submission.Granted = true;
            prefs.Submission.PolicyVersion = 2;
            prefs.Submission.LastSubmittedAt = "2026-11-02T00:00:00Z";
            prefs.Unknown["futureThing"] = "{\"a\":1}";

            string saveError;
            Assert.True(PrefsFile.TrySave(path, prefs, out saveError), saveError);
            Assert.True(File.Exists(path));

            string error;
            bool exists;
            Prefs loaded = PrefsFile.Load(path, out error, out exists);
            Assert.True(exists);
            Assert.Null(error);
            Assert.Equal(3, loaded.ModCount);
            Assert.Equal(2, loaded.DisabledCount);
            Assert.False(loaded.IsEnabled("Squad"));
            Assert.True(loaded.IsEnabled("RealFuels"));
            Assert.True(loaded.Submission.Granted);
            Assert.Equal(2, loaded.Submission.PolicyVersion);
            Assert.Equal("2026-11-02T00:00:00Z", loaded.Submission.LastSubmittedAt);
            Assert.True(loaded.Unknown.ContainsKey("futureThing"));
            Assert.Contains("keep", loaded.Get("Squad").Unknown["note"]);
        }

        // 第二次保存走 File.Replace 分支（文件已存在），必须真的覆盖掉旧内容。
        [Fact]
        public void TrySave_OverwritesExistingFile()
        {
            string path = TempPath("prefs.json");
            Prefs first = Prefs.CreateDefault();
            first.SetEnabled("Squad", false);
            string error;
            Assert.True(PrefsFile.TrySave(path, first, out error), error);

            Prefs second = Prefs.CreateDefault();
            second.SetEnabled("Squad", true);
            second.SetEnabled("RealFuels", false);
            Assert.True(PrefsFile.TrySave(path, second, out error), error);

            Prefs loaded = PrefsFile.Load(path);
            Assert.True(loaded.IsEnabled("Squad"));
            Assert.False(loaded.IsEnabled("RealFuels"));
            Assert.Equal(1, loaded.DisabledCount);
        }

        [Fact]
        public void TrySave_CreatesMissingDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ui18n-prefs-" + Guid.NewGuid().ToString("N"), "nested");
            string path = Path.Combine(dir, "prefs.json");
            string error;
            Assert.True(PrefsFile.TrySave(path, Prefs.CreateDefault(), out error), error);
            Assert.True(File.Exists(path));
        }

        [Fact]
        public void TrySave_EmptyPath_ReturnsError()
        {
            string error;
            Assert.False(PrefsFile.TrySave(null, Prefs.CreateDefault(), out error));
            Assert.Equal("empty-path", error);
            Assert.False(PrefsFile.TrySave(string.Empty, Prefs.CreateDefault(), out error));
            Assert.Equal("empty-path", error);
        }

        [Fact]
        public void FilePathIn_JoinsFileName()
        {
            Assert.Equal(PrefsFile.FileName, PrefsFile.FilePathIn(null));
            Assert.Equal(PrefsFile.FileName, PrefsFile.FilePathIn(string.Empty));
            Assert.Equal(Path.Combine("PluginData/UI18n", PrefsFile.FileName), PrefsFile.FilePathIn("PluginData/UI18n"));
            Assert.Equal("prefs.json", PrefsFile.FileName);
        }

        [Fact]
        public void Describe_MentionsCounts()
        {
            Prefs prefs = Prefs.CreateDefault();
            prefs.SetEnabled("Squad", false);
            prefs.SetEnabled("RealFuels", true);
            string text = prefs.Describe();
            Assert.Contains("ui18n-prefs/1", text);
            Assert.Contains("mods=2", text);
            Assert.Contains("disabled=1", text);
        }
    }
}
