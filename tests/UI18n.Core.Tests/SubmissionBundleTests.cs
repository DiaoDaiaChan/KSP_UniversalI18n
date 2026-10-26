using System;
using System.Collections.Generic;
using UI18n.Core.Submission;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// ui18n-submission/1 的字节级契约（docs/10 §6.1，已冻结）。
    /// <para>
    /// 期望值全部来自 python 的 <c>json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False)</c>
    /// + <c>sha256</c> 前 16 位——不是「C# 自己和自己比」。两边序列化只要有一个字节不同，
    /// 服务端就 exit 5（submission-id-mismatch），玩家的投稿会白投。
    /// </para>
    /// </summary>
    public class SubmissionBundleTests
    {
        /// <summary>python 算出的 bundle id（_scratch/canon.py 的输出）。</summary>
        internal const string ExpectedId = "7f23d6a14ecd6a85";

        /// <summary>python 算出的 canonical body（去 id、键升序、无空格、中文原样）。</summary>
        internal const string ExpectedCanonical =
            "{\"actor\":{\"installId\":\"0123456789abcdef0123456789abcdef\",\"kind\":\"player\"," +
            "\"kspVersion\":\"1.12.5\",\"ui18nVersion\":\"1.6.0\"}," +
            "\"consent\":{\"granted\":true,\"grantedAt\":\"2026-10-26T18:20:00+08:00\",\"policyVersion\":1}," +
            "\"createdAt\":\"2026-10-26T18:20:00+08:00\"," +
            "\"entries\":[" +
            "{\"caller\":\"FerramAerospaceResearch.FARDebugAndSettings.Update\",\"channel\":\"capture\"," +
            "\"count\":37,\"h\":\"3ff885233413de88\",\"mod\":\"FerramAerospaceResearch\"," +
            "\"s\":\"Aerodynamic Failure\",\"sink\":\"TMPro.TMP_Text.set_text\"}," +
            "{\"channel\":\"capture\",\"count\":2,\"h\":\"0f1e2d3c4b5a6978\",\"mod\":\"Squad\"," +
            "\"s\":\"推力不足 · 剩余 42%\"}]," +
            "\"language\":\"zh-cn\"," +
            "\"mods\":[{\"fileName\":\"FerramAerospaceResearch.il.jsonl\",\"modId\":\"FerramAerospaceResearch\"}," +
            "{\"fileName\":\"Squad.jsonl\",\"modId\":\"Squad\"}]," +
            "\"note\":\"\"," +
            "\"redactions\":{\"absolutePaths\":0,\"paths\":2,\"urls\":0,\"userNames\":1}," +
            "\"schema\":\"ui18n-submission/1\"}";

        /// <summary>与 python 侧完全相同的固定 bundle。</summary>
        internal static SubmissionBundle Fixture()
        {
            SubmissionBundle bundle = new SubmissionBundle();
            bundle.CreatedAt = "2026-10-26T18:20:00+08:00";
            bundle.Language = "zh-cn";
            bundle.Actor = new SubmissionActor
            {
                Kind = SubmissionActor.KindPlayer,
                InstallId = "0123456789abcdef0123456789abcdef",
                Ui18nVersion = "1.6.0",
                KspVersion = "1.12.5",
            };
            bundle.Consent = new SubmissionConsent
            {
                Granted = true,
                PolicyVersion = 1,
                GrantedAt = "2026-10-26T18:20:00+08:00",
            };
            bundle.Mods.Add(new SubmissionMod
            {
                ModId = "FerramAerospaceResearch",
                FileName = "FerramAerospaceResearch.il.jsonl",
            });
            bundle.Mods.Add(new SubmissionMod { ModId = "Squad", FileName = "Squad.jsonl" });
            bundle.Entries.Add(new SubmissionEntry
            {
                H = "3ff885233413de88",
                S = "Aerodynamic Failure",
                Mod = "FerramAerospaceResearch",
                Channel = "capture",
                Sink = "TMPro.TMP_Text.set_text",
                Caller = "FerramAerospaceResearch.FARDebugAndSettings.Update",
                Count = 37,
            });
            bundle.Entries.Add(new SubmissionEntry
            {
                H = "0f1e2d3c4b5a6978",
                S = "推力不足 · 剩余 42%",
                Mod = "Squad",
                Channel = "capture",
                Count = 2,
            });
            bundle.Redactions = new SubmissionRedactions { Paths = 2, UserNames = 1 };
            bundle.Note = string.Empty;
            return bundle;
        }

        [Fact]
        public void CanonicalJson_MatchesPythonByteForByte()
        {
            SubmissionBundle bundle = Fixture();
            Assert.Equal(ExpectedCanonical, bundle.ToCanonicalJson(false));
        }

        [Fact]
        public void ComputeId_MatchesPython()
        {
            SubmissionBundle bundle = Fixture();
            Assert.Equal(ExpectedId, bundle.ComputeId());
        }

        [Fact]
        public void CanonicalBody_HasNoSpacesAndNoAsciiEscapes()
        {
            SubmissionBundle bundle = Fixture();
            string body = bundle.ToCanonicalJson(false);
            Assert.DoesNotContain("\n", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u", body, StringComparison.Ordinal);
            // 中文必须是原样 UTF-8，不是 \uXXXX
            Assert.Contains("推力不足", body, StringComparison.Ordinal);
        }

        // 第五个 redactions 键只在 >0 时写：没有私有值的 bundle 字节与 v1.7.0 之前完全一致。
        [Fact]
        public void Redactions_UserNamesDropped_IsWrittenOnlyWhenPositive()
        {
            SubmissionBundle zero = Fixture();
            Assert.DoesNotContain("userNamesDropped", zero.ToCanonicalJson(false), StringComparison.Ordinal);

            SubmissionBundle bundle = Fixture();
            bundle.Redactions.UserNamesDropped = 3;
            string body = bundle.ToCanonicalJson(false);
            Assert.Contains(
                "\"redactions\":{\"absolutePaths\":0,\"paths\":2,\"urls\":0,\"userNames\":1,\"userNamesDropped\":3}",
                body,
                StringComparison.Ordinal);

            // id 是内容派生：多一个键就必须重算，且能原样读回
            bundle.Id = bundle.ComputeId();
            SubmissionBundle parsed;
            string error;
            Assert.True(SubmissionBundle.TryParse(bundle.ToFileJson(), out parsed, out error), error);
            Assert.Equal(3, parsed.Redactions.UserNamesDropped);
            Assert.Equal(1, parsed.Redactions.UserNames);
            Assert.Equal(2, parsed.Redactions.Paths);
        }

        [Fact]
        public void CanonicalWithId_InsertsIdBeforeLanguage()
        {
            SubmissionBundle bundle = Fixture();
            bundle.Id = ExpectedId;
            string body = bundle.ToCanonicalJson(true);
            Assert.Contains("\"entries\":[", body, StringComparison.Ordinal);
            int idIndex = body.IndexOf("\"id\":\"" + ExpectedId + "\"", StringComparison.Ordinal);
            int langIndex = body.IndexOf("\"language\":", StringComparison.Ordinal);
            Assert.True(idIndex > 0 && idIndex < langIndex, "id 必须排在 entries 之后、language 之前");
        }

        [Fact]
        public void FileJson_EndsWithNewline_AndRoundTrips()
        {
            SubmissionBundle bundle = Fixture();
            bundle.Id = bundle.ComputeId();
            string json = bundle.ToFileJson();
            Assert.EndsWith("\n", json, StringComparison.Ordinal);

            SubmissionBundle parsed;
            string error;
            Assert.True(SubmissionBundle.TryParse(json, out parsed, out error), error);
            Assert.Equal(ExpectedId, parsed.Id);
            Assert.Equal(2, parsed.Entries.Count);
            Assert.Equal("推力不足 · 剩余 42%", parsed.Entries[1].S);
            Assert.Equal(ExpectedCanonical, parsed.ToCanonicalJson(false));
        }

        [Fact]
        public void SortDeterministic_SortsModsAndMergesDuplicateEntries()
        {
            SubmissionBundle bundle = new SubmissionBundle();
            bundle.CreatedAt = "2026-10-26T18:20:00+08:00";
            bundle.Language = "zh-cn";
            bundle.Actor.InstallId = "0123456789abcdef0123456789abcdef";
            bundle.Consent.Granted = true;
            bundle.Consent.PolicyVersion = 1;
            bundle.Consent.GrantedAt = bundle.CreatedAt;
            bundle.Mods.Add(new SubmissionMod { ModId = "Squad", FileName = "Squad.jsonl" });
            bundle.Mods.Add(new SubmissionMod { ModId = "FerramAerospaceResearch" });
            bundle.Mods.Add(new SubmissionMod { ModId = "Squad", FileName = "Squad.jsonl" });
            bundle.Entries.Add(new SubmissionEntry { H = "0f1e2d3c4b5a6978", S = "Wing stalled", Mod = "Squad", Count = 1 });
            bundle.Entries.Add(new SubmissionEntry { H = "3ff885233413de88", S = "Aerodynamic Failure", Mod = "FerramAerospaceResearch", Count = 2 });
            bundle.Entries.Add(new SubmissionEntry { H = "0f1e2d3c4b5a6978", S = "Wing stalled", Mod = "Squad", Count = 4 });

            bundle.SortDeterministic();

            Assert.Equal(2, bundle.Mods.Count);
            Assert.Equal("FerramAerospaceResearch", bundle.Mods[0].ModId);
            Assert.Equal("Squad", bundle.Mods[1].ModId);
            Assert.Equal(2, bundle.Entries.Count);
            Assert.Equal("FerramAerospaceResearch", bundle.Entries[0].Mod);
            Assert.Equal(5, bundle.Entries[1].Count);
        }

        [Fact]
        public void TryValidate_RejectsWrongSchema()
        {
            SubmissionBundle bundle = Fixture();
            bundle.Schema = "ui18n-submission/2";
            string error;
            Assert.False(SubmissionBundle.TryValidate(bundle, out error));
            Assert.Equal("submission-schema-invalid", error);
        }

        [Theory]
        [InlineData("")]
        [InlineData("3FF885233413DE88")]
        [InlineData("3ff885233413de8")]
        [InlineData("3ff885233413de88a")]
        public void IsHex16_RejectsWrongShapes(string value)
        {
            Assert.False(SubmissionBundle.IsHex16(value));
        }

        [Fact]
        public void TryValidate_RejectsEntryWithoutTextAndUndeclaredMod()
        {
            SubmissionBundle a = Fixture();
            a.Entries[0].S = string.Empty;
            string error;
            Assert.False(SubmissionBundle.TryValidate(a, out error));
            Assert.StartsWith("submission-field-invalid:entries[0]", error, StringComparison.Ordinal);

            SubmissionBundle b = Fixture();
            b.Entries[1].Mod = "NotDeclared";
            Assert.False(SubmissionBundle.TryValidate(b, out error));
            Assert.Equal("submission-mod-undeclared:NotDeclared", error);
        }

        [Fact]
        public void TryValidate_RejectsTextOverEightThousand()
        {
            SubmissionBundle bundle = Fixture();
            bundle.Entries[0].S = new string('a', SubmissionBundle.MaxTextLength + 1);
            string error;
            Assert.False(SubmissionBundle.TryValidate(bundle, out error));
            Assert.StartsWith("submission-text-too-long:", error, StringComparison.Ordinal);
        }

        [Fact]
        public void TryValidate_RequiresConsentAndInstallId()
        {
            SubmissionBundle a = Fixture();
            a.Consent.Granted = false;
            string error;
            Assert.False(SubmissionBundle.TryValidate(a, out error));
            Assert.Equal("submission-consent-missing", error);

            SubmissionBundle b = Fixture();
            b.Actor.InstallId = string.Empty;
            Assert.False(SubmissionBundle.TryValidate(b, out error));
            Assert.Equal("submission-field-invalid:actor.installId", error);
        }

        [Fact]
        public void TryValidate_RejectsEmptyEntries()
        {
            SubmissionBundle bundle = Fixture();
            bundle.Entries.Clear();
            string error;
            Assert.False(SubmissionBundle.TryValidate(bundle, out error));
            Assert.Equal("submission-empty-entries", error);
        }

        [Fact]
        public void CanonicalWithId_WritesDeclaredId()
        {
            SubmissionBundle bundle = Fixture();
            bundle.Id = "ffffffffffffffff";
            Assert.Contains("\"id\":\"ffffffffffffffff\"", bundle.ToCanonicalJson(true), StringComparison.Ordinal);
        }

        [Fact]
        public void TryParse_RejectsTamperedId()
        {
            SubmissionBundle bundle = Fixture();
            bundle.Id = "ffffffffffffffff";
            string json = bundle.ToFileJson();

            SubmissionBundle parsed;
            string error;
            Assert.False(SubmissionBundle.TryParse(json, out parsed, out error));
            Assert.StartsWith("submission-id-mismatch:", error, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("[]")]
        [InlineData("{\"schema\":\"ui18n-submission/1\"}")]
        public void TryParse_NeverThrowsOnGarbage(string json)
        {
            SubmissionBundle parsed;
            string error;
            Assert.False(SubmissionBundle.TryParse(json, out parsed, out error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void Sanitize_ReplacesLoneSurrogate()
        {
            string broken = "abc" + (char)0xD800 + "def";
            string fixedUp = SubmissionBundle.Sanitize(broken);
            Assert.Equal("abc\uFFFDdef", fixedUp);
            Assert.Equal(string.Empty, SubmissionBundle.Sanitize(null));
        }

        [Theory]
        [InlineData("FerramAerospaceResearch", true)]
        [InlineData("AtmosphereAutopilot.UI", true)]
        [InlineData("UI18n-Unattributed", true)]
        [InlineData(".hidden", false)]
        [InlineData("has space", false)]
        [InlineData("", false)]
        public void IsValidModId_MatchesServerRule(string modId, bool expected)
        {
            Assert.Equal(expected, SubmissionBundle.IsValidModId(modId));
        }
    }
}
