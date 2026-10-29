using System;
using System.Collections.Generic;
using UI18n.Core.Submission;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 上报组装（M4-b 的 Core 半边，docs/12 §12.4 / §12.5）。
    /// <para>
    /// 三个最容易错的地方：① 400 条上限**必须重算 bundle id**（否则落盘文件的 id 与正文对不上，
    /// <c>api submit accept</c> 会以 submission-id-mismatch 拒收）；② 深拷贝——调用方的 bundle 不能被改，
    /// 面板会拿同一份数据再导一次文件；③ 正文进 URL 前要看长度，超过就退回「写文件 + 拖拽」。
    /// </para>
    /// </summary>
    public class SubmissionReportTests
    {
        private const string FixedTime = "2026-10-29T12:00:00+08:00";

        private static SubmissionBundle Bundle(int count)
        {
            SubmissionBundle bundle = new SubmissionBundle();
            bundle.CreatedAt = FixedTime;
            bundle.Language = "zh-cn";
            bundle.Actor = new SubmissionActor
            {
                InstallId = "0123456789abcdef0123456789abcdef",
                Ui18nVersion = "1.7.0",
                KspVersion = "1.12.5"
            };
            bundle.Consent = new SubmissionConsent
            {
                Granted = true,
                PolicyVersion = SubmissionPolicy.PolicyVersion,
                GrantedAt = FixedTime
            };
            bundle.Mods.Add(new SubmissionMod { ModId = "Squad" });
            bundle.Mods.Add(new SubmissionMod { ModId = "FerramAerospaceResearch" });

            for (int i = 0; i < count; i++)
            {
                bundle.Entries.Add(new SubmissionEntry
                {
                    H = (0x1000 + i).ToString("x16"),
                    S = "text " + i,
                    Mod = i % 2 == 0 ? "Squad" : "FerramAerospaceResearch",
                    Channel = SubmissionBuilder.DefaultChannel,
                    Count = 1
                });
            }

            bundle.Id = bundle.ComputeId();
            return bundle;
        }

        private static SubmissionReportText Text()
        {
            SubmissionReportText text = new SubmissionReportText();
            text.TitleFormat = "[test] {0} entries";
            text.BodyIntro = "intro paragraph";
            text.BodyFileLabel = "file";
            text.BodyCountFormat = "candidates: {0} / {1}";
            text.BodyTruncatedFormat = "{0} more";
            text.BodySteps = "1. drag the file";
            return text;
        }

        [Fact]
        public void Compose_NoEntries_IsNotReportable()
        {
            SubmissionBundle empty = Bundle(0);
            SubmissionReport report = SubmissionReport.Compose(empty, Text(), null);

            Assert.False(report.CanReport);
            Assert.Equal(SubmissionReport.ReasonNoEntries, report.Reason);
            Assert.Equal(string.Empty, report.Url);
            Assert.Contains("skipped", report.Describe());
        }

        [Fact]
        public void Compose_NullBundle_DoesNotThrow()
        {
            SubmissionReport report = SubmissionReport.Compose(null, null, null);
            Assert.False(report.CanReport);
            Assert.False(report.NeedsFileFallback);
        }

        [Fact]
        public void Compose_TrimsToLimit_And_RecomputesId()
        {
            SubmissionBundle source = Bundle(500);
            string originalId = source.Id;

            SubmissionReport report = SubmissionReport.Compose(source, Text(), null);

            Assert.True(report.CanReport);
            Assert.Equal(SubmissionReport.LimitEntries, report.EntryCount);
            Assert.Equal(500, report.TotalEntries);
            Assert.True(report.Truncated);
            Assert.Equal(100, report.Remaining);
            Assert.Equal(SubmissionReport.LimitEntries, report.Payload.Entries.Count);
            Assert.Equal(report.Payload.ComputeId(), report.Payload.Id);
            Assert.NotEqual(originalId, report.Payload.Id);
            Assert.Equal(SubmissionReport.FileNamePrefix + report.Payload.Id + ".json", report.FileName);
        }

        [Fact]
        public void Compose_DoesNotMutateCallerBundle()
        {
            SubmissionBundle source = Bundle(500);
            string originalId = source.Id;

            SubmissionReport.Compose(source, Text(), null);

            Assert.Equal(500, source.Entries.Count);
            Assert.Equal(originalId, source.Id);
        }

        [Fact]
        public void Compose_SortsDeterministically_AcrossInputOrder()
        {
            SubmissionBundle forwards = Bundle(8);
            SubmissionBundle backwards = Bundle(8);
            backwards.Entries.Reverse();

            SubmissionReport a = SubmissionReport.Compose(forwards, Text(), null);
            SubmissionReport b = SubmissionReport.Compose(backwards, Text(), null);

            Assert.Equal(a.FileName, b.FileName);
            for (int i = 0; i < a.Payload.Entries.Count; i++)
            {
                Assert.Equal(a.Payload.Entries[i].H, b.Payload.Entries[i].H);
            }
        }

        [Fact]
        public void Compose_PayloadStayValid()
        {
            SubmissionReport report = SubmissionReport.Compose(Bundle(500), Text(), null);
            string error;
            Assert.True(SubmissionBundle.TryValidate(report.Payload, out error), error);
        }

        [Fact]
        public void Compose_PayloadJson_RoundTrips()
        {
            SubmissionReport report = SubmissionReport.Compose(Bundle(3), Text(), null);

            SubmissionBundle parsed;
            string error;
            Assert.True(SubmissionBundle.TryParse(report.PayloadJson, out parsed, out error), error);
            Assert.Equal(report.Payload.Id, parsed.Id);
            Assert.Equal(3, parsed.Entries.Count);
            Assert.EndsWith("\n", report.PayloadJson);
        }

        [Fact]
        public void Compose_Body_ListsFileAndCounts()
        {
            SubmissionReport report = SubmissionReport.Compose(Bundle(3), Text(), null);

            Assert.Contains(report.FileName, report.Body);
            Assert.Contains("candidates: 3 / 3", report.Body);
            Assert.Contains("intro paragraph", report.Body);
            Assert.Contains("1. drag the file", report.Body);
            Assert.DoesNotContain("more", report.Body);
            Assert.Equal("[test] 3 entries", report.Title);
        }

        [Fact]
        public void Compose_TruncatedBody_SaysHowManyLeft()
        {
            SubmissionReport report = SubmissionReport.Compose(Bundle(450), Text(), null);

            Assert.Contains("candidates: 400 / 450", report.Body);
            Assert.Contains("50 more", report.Body);
            Assert.Contains("400", report.Body);
        }

        [Fact]
        public void Compose_Url_CarriesTemplateTitleLabels()
        {
            SubmissionReport report = SubmissionReport.Compose(Bundle(2), Text(), null);

            Assert.StartsWith("https://github.com/" + SubmissionReport.DefaultRepo + "/issues/new?", report.Url);
            Assert.Contains("template=" + SubmissionReport.DefaultTemplate, report.Url);
            Assert.Contains("labels=" + SubmissionReport.DefaultLabels, report.Url);
            Assert.Contains("title=", report.Url);
            Assert.Contains("body=", report.Url);
            Assert.True(report.BodyInUrl);
            Assert.False(report.NeedsFileFallback);
            Assert.Equal(report.Url.Length, report.UrlLength);
        }

        [Fact]
        public void Compose_Url_EscapesSpacesAndCjk()
        {
            SubmissionReportText text = Text();
            text.TitleFormat = "上报 {0} 条 候选";
            SubmissionReportOptions options = new SubmissionReportOptions { Repo = "owner/name" };

            SubmissionReport report = SubmissionReport.Compose(Bundle(2), text, options);

            Assert.DoesNotContain(" ", report.Url);
            Assert.DoesNotContain("报", report.Url);
            Assert.Contains("%20", report.Url);
            Assert.Contains("owner/name", report.Url);
        }

        [Fact]
        public void Compose_LongBody_FallsBackToFile()
        {
            SubmissionReportOptions options = new SubmissionReportOptions { MaxUrlChars = 200 };
            SubmissionReport report = SubmissionReport.Compose(Bundle(3), Text(), options);

            Assert.False(report.BodyInUrl);
            Assert.True(report.NeedsFileFallback);
            Assert.DoesNotContain("body=", report.Url);
            Assert.Contains("title=", report.Url);
            Assert.True(report.Url.Length <= 200);
        }

        [Fact]
        public void Compose_VersionAndEnvironment_AppearWhenGiven()
        {
            SubmissionReportOptions options = new SubmissionReportOptions
            {
                Version = "1.7.0+b2a05809ea5f",
                Environment = "1.12.5 / zh-cn"
            };

            SubmissionReport report = SubmissionReport.Compose(Bundle(2), Text(), options);

            Assert.Contains("1.7.0+b2a05809ea5f", report.Body);
            Assert.Contains("1.12.5 / zh-cn", report.Body);
            Assert.Contains("version=", report.Url);
            Assert.Contains("environment=", report.Url);
        }

        [Fact]
        public void Compose_DefaultText_ContainsNoCjk()
        {
            // 文案属于语言包，不属于代码（Fix-2 同一口径）：Core 的默认值里不许出现中文。
            SubmissionReport report = SubmissionReport.Compose(Bundle(2), new SubmissionReportText(), null);

            Assert.False(ContainsCjk(report.Title));
            Assert.False(ContainsCjk(report.Body));
        }

        [Fact]
        public void Compose_BadPlaceholder_DoesNotThrow()
        {
            SubmissionReportText text = Text();
            text.TitleFormat = "{0} {1}";

            SubmissionReport report = SubmissionReport.Compose(Bundle(2), text, null);

            Assert.Equal("{0} {1}", report.Title);
            Assert.True(report.CanReport);
        }

        [Fact]
        public void Compose_MaxEntriesZero_DoesNotTrim()
        {
            SubmissionReportOptions options = new SubmissionReportOptions { MaxEntries = 0 };
            SubmissionReport report = SubmissionReport.Compose(Bundle(37), Text(), options);

            Assert.Equal(37, report.EntryCount);
            Assert.False(report.Truncated);
            Assert.Equal(0, report.Remaining);
        }

        [Fact]
        public void Describe_ReportsCounts()
        {
            SubmissionReport report = SubmissionReport.Compose(Bundle(2), Text(), null);
            string line = report.Describe();

            Assert.Contains("entries=2/2", line);
            Assert.Contains("bodyChars=", line);
            Assert.Contains("urlChars=", line);
            Assert.Contains("bodyInUrl=true", line);
        }

        private static bool ContainsCjk(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
