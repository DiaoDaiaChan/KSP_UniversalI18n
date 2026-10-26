using System;
using System.Collections.Generic;
using UI18n.Core.Submission;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 捕获行 → 投稿 bundle 的装配（docs/10 §6.2/§6.3 的客户端半边）。
    /// <para>
    /// 最容易搞错的一点：捕获行的 <c>s</c> 是<b>渲染后</b>的文本（我们翻过的串在这里是中文），
    /// <c>n</c> 才是与 <c>h</c> 对应的原文。所以 <c>n</c> 必须优先，否则投出去的全是中文。
    /// </para>
    /// </summary>
    public class SubmissionBuilderTests
    {
        private const string FixedTime = "2026-10-26T18:20:00+08:00";

        private const string LineOriginalAndRendered =
            "{\"h\":\"3ff885233413de88\",\"n\":\"Aerodynamic Failure\",\"s\":\"气动失效\"," +
            "\"sink\":\"TMPro.TMP_Text.set_text\",\"caller\":\"FerramAerospaceResearch.FARDebugAndSettings.Update\",\"count\":3}";

        private const string LineOnlyRendered =
            "{\"h\":\"3ff885233413de88\",\"s\":\"Aerodynamic Failure\"," +
            "\"caller\":\"FerramAerospaceResearch.FARDebugAndSettings.Update\",\"count\":1}";

        private static SubmissionBuildOptions Options()
        {
            SubmissionBuildOptions options = new SubmissionBuildOptions();
            options.InstallId = "0123456789abcdef0123456789abcdef";
            options.Ui18nVersion = "1.6.0";
            options.KspVersion = "1.12.5";
            options.Language = "zh-cn";
            options.CreatedAt = FixedTime;
            options.ModIds = new List<string> { "FerramAerospaceResearch", "Squad" };
            return options;
        }

        [Fact]
        public void Build_PrefersOriginalTextOverRenderedText()
        {
            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { LineOriginalAndRendered }, Options());

            Assert.True(result.Ok, result.Error);
            Assert.Equal(SubmissionBuilder.OutcomeOk, result.Outcome);
            Assert.Single(result.Bundle.Entries);
            Assert.Equal("Aerodynamic Failure", result.Bundle.Entries[0].S);
            Assert.Equal("FerramAerospaceResearch", result.Bundle.Entries[0].Mod);
            Assert.Equal(SubmissionBuilder.DefaultChannel, result.Bundle.Entries[0].Channel);
            Assert.Equal(3, result.Bundle.Entries[0].Count);
        }

        [Fact]
        public void Build_FallsBackToSourceWhenNormalizedMissing()
        {
            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { LineOnlyRendered }, Options());
            Assert.True(result.Ok, result.Error);
            Assert.Equal("Aerodynamic Failure", result.Bundle.Entries[0].S);
        }

        [Fact]
        public void Build_FillsConsentAndActorFromOptions()
        {
            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { LineOriginalAndRendered }, Options());
            Assert.True(result.Ok, result.Error);
            SubmissionBundle bundle = result.Bundle;
            Assert.True(bundle.Consent.Granted);
            Assert.Equal(SubmissionPolicy.PolicyVersion, bundle.Consent.PolicyVersion);
            // 没给 grantedAt 时沿用 createdAt，bundle 里不能出现空时间
            Assert.Equal(FixedTime, bundle.Consent.GrantedAt);
            Assert.Equal("player", bundle.Actor.Kind);
            Assert.Equal("0123456789abcdef0123456789abcdef", bundle.Actor.InstallId);
            Assert.Equal("1.6.0", bundle.Actor.Ui18nVersion);
            Assert.Equal("1.12.5", bundle.Actor.KspVersion);
            Assert.Equal(FixedTime, bundle.CreatedAt);
            Assert.Equal(SubmissionBundle.SchemaId, bundle.Schema);
        }

        [Fact]
        public void Build_AssignsInstallIdWhenMissing()
        {
            SubmissionBuildOptions options = Options();
            options.InstallId = string.Empty;

            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { LineOriginalAndRendered }, options);

            Assert.True(result.Ok, result.Error);
            Assert.True(InstallId.IsValid(result.Bundle.Actor.InstallId));
            Assert.Equal(32, result.Bundle.Actor.InstallId.Length);
        }

        [Fact]
        public void Build_SkipsTruncatedLines()
        {
            const string truncated =
                "{\"h\":\"3ff885233413de88\",\"n\":\"Aerodynamic Failure\",\"s\":\"Aerodynamic Failure\"," +
                "\"caller\":\"FerramAerospaceResearch.X.Y\",\"count\":1,\"truncated\":true}";

            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { truncated }, Options());

            Assert.Equal(SubmissionBuilder.OutcomeNoEntries, result.Outcome);
            Assert.Equal(1, result.SkippedTruncated);
            Assert.Equal(1, result.Input);
        }

        [Fact]
        public void Build_CountsInvalidLinesWithoutThrowing()
        {
            const string badHash =
                "{\"h\":\"XYZ\",\"n\":\"Aerodynamic Failure\",\"s\":\"Aerodynamic Failure\",\"count\":1}";
            const string notJson = "this is not json";
            const string empty = "   ";

            SubmissionBuildResult result = SubmissionBuilder.Build(
                new[] { badHash, notJson, empty, LineOriginalAndRendered }, Options());

            Assert.True(result.Ok, result.Error);
            Assert.Equal(2, result.SkippedInvalid);
            Assert.Equal(3, result.Input); // 空白行不计入 input
        }

        [Fact]
        public void Build_NoLines_ReportsNoLines()
        {
            SubmissionBuildResult result = SubmissionBuilder.Build(new string[0], Options());
            Assert.False(result.Ok);
            Assert.Equal(SubmissionBuilder.OutcomeNoLines, result.Outcome);
        }

        [Fact]
        public void Build_AllFilteredOut_ReportsNoEntries()
        {
            const string chinese =
                "{\"h\":\"3ff885233413de88\",\"n\":\"发动机熄火\",\"s\":\"发动机熄火\",\"count\":1}";

            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { chinese }, Options());

            Assert.False(result.Ok);
            Assert.Equal(SubmissionBuilder.OutcomeNoEntries, result.Outcome);
            Assert.NotNull(result.Filter);
            Assert.Equal(1, result.Filter.DroppedAlreadyCjk);
        }

        [Fact]
        public void Build_MergesDuplicateEntriesAndSortsMods()
        {
            const string second =
                "{\"h\":\"3ff885233413de88\",\"n\":\"Aerodynamic Failure\",\"s\":\"Aerodynamic Failure\"," +
                "\"caller\":\"FerramAerospaceResearch.X.Y\",\"count\":2}";

            SubmissionBuildResult result = SubmissionBuilder.Build(
                new[] { LineOriginalAndRendered, second }, Options());

            Assert.True(result.Ok, result.Error);
            Assert.Single(result.Bundle.Entries);
            Assert.Equal(5, result.Bundle.Entries[0].Count);
            Assert.Single(result.Bundle.Mods);
            Assert.Equal("FerramAerospaceResearch", result.Bundle.Mods[0].ModId);
            Assert.Equal("FerramAerospaceResearch.jsonl", result.Bundle.Mods[0].FileName);
        }

        [Fact]
        public void Build_UsesProvidedPackFileNames()
        {
            SubmissionBuildOptions options = Options();
            options.ModFileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "FerramAerospaceResearch", "FerramAerospaceResearch.il.jsonl" },
            };

            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { LineOriginalAndRendered }, options);

            Assert.True(result.Ok, result.Error);
            Assert.Equal("FerramAerospaceResearch.il.jsonl", result.Bundle.Mods[0].FileName);
        }

        [Fact]
        public void Build_SetsIdThatMatchesComputedValue()
        {
            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { LineOriginalAndRendered }, Options());
            Assert.True(result.Ok, result.Error);
            Assert.Equal(result.Bundle.ComputeId(), result.Bundle.Id);

            string error;
            Assert.True(SubmissionBundle.TryValidate(result.Bundle, out error), error);

            // 落盘形态能被自己读回来（= 服务端 TryParse 同构）
            SubmissionBundle parsed;
            Assert.True(SubmissionBundle.TryParse(result.Bundle.ToFileJson(), out parsed, out error), error);
            Assert.Equal(result.Bundle.Id, parsed.Id);
        }

        [Fact]
        public void Build_NullOptions_IsInvalid()
        {
            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { LineOriginalAndRendered }, null);
            Assert.False(result.Ok);
            Assert.Equal(SubmissionBuilder.OutcomeInvalid, result.Outcome);
            Assert.Equal("options-null", result.Error);
        }

        [Fact]
        public void Build_EmptyLanguage_IsInvalid()
        {
            SubmissionBuildOptions options = Options();
            options.Language = "   ";
            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { LineOriginalAndRendered }, options);
            Assert.Equal(SubmissionBuilder.OutcomeInvalid, result.Outcome);
            Assert.Equal("language-empty", result.Error);
        }

        [Fact]
        public void Build_UnattributedCaller_GoesToPseudoMod()
        {
            const string line =
                "{\"h\":\"3ff885233413de88\",\"n\":\"Aerodynamic Failure\",\"s\":\"Aerodynamic Failure\"," +
                "\"caller\":\"SomeUnknownAssembly.Type.Method\",\"count\":1}";

            SubmissionBuildResult result = SubmissionBuilder.Build(new[] { line }, Options());

            Assert.True(result.Ok, result.Error);
            Assert.Equal(ModResolver.Unattributed, result.Bundle.Entries[0].Mod);
            Assert.Equal(ModResolver.Unattributed, result.Bundle.Mods[0].ModId);
        }

        [Fact]
        public void FormatTimestamp_UsesContractShape()
        {
            string text = SubmissionBuilder.FormatTimestamp(
                new DateTimeOffset(2026, 10, 26, 18, 20, 0, TimeSpan.FromHours(8)));
            Assert.Equal("2026-10-26T18:20:00+08:00", text);
        }
    }
}
