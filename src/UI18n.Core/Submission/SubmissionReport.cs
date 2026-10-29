using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UI18n.Core.Submission
{
    /// <summary>
    /// 上报正文里所有**面向玩家**的文案（由 Runtime 用既有 <c>T()</c> 机制灌进来）。
    /// <para>
    /// Core 契约：这里**没有任何默认中文**——文案属于语言包，不属于代码（与 Fix-2「显示名去常量」同一口径）。
    /// 字段为 null / 空串时对应行直接不写，正文仍然合法。
    /// </para>
    /// </summary>
    public sealed class SubmissionReportText
    {
        /// <summary>Issue 标题格式，<c>{0}</c> = 本次携带的条目数（例如 <c>"[投稿] {0} 条候选"</c>）。</summary>
        public string TitleFormat { get; set; } = "{0} entries";

        /// <summary>正文开头一段（怎么来的、会不会泄露隐私）。</summary>
        public string BodyIntro { get; set; } = string.Empty;

        /// <summary>「把文件拖进下面这个框」那一行的标签。</summary>
        public string BodyFileLabel { get; set; } = "file";

        /// <summary>条数那一行，<c>{0}</c> = 携带条数、<c>{1}</c> = 总条数。</summary>
        public string BodyCountFormat { get; set; } = "candidates: {0} / {1}";

        /// <summary>被 400 条上限挡下时补的一句，<c>{0}</c> = 剩余条数、<c>{1}</c> = 上限。</summary>
        public string BodyTruncatedFormat { get; set; } = "{0} more were not included (limit {1}); submit again after this one is merged.";

        /// <summary>版本行标签。</summary>
        public string BodyVersionLabel { get; set; } = "version";

        /// <summary>环境行标签。</summary>
        public string BodyEnvironmentLabel { get; set; } = "environment";

        /// <summary>收尾的步骤说明（可为空）。</summary>
        public string BodySteps { get; set; } = string.Empty;
    }

    /// <summary>上报的可调项（默认值即 docs/12 §12.4 的契约值）。</summary>
    public sealed class SubmissionReportOptions
    {
        /// <summary>目标仓库（owner/name）。</summary>
        public string Repo { get; set; } = SubmissionReport.DefaultRepo;

        /// <summary>Issue 表单模板文件名。</summary>
        public string Template { get; set; } = SubmissionReport.DefaultTemplate;

        /// <summary>标签（逗号分隔）。</summary>
        public string Labels { get; set; } = SubmissionReport.DefaultLabels;

        /// <summary>单次上报条目上限（&gt; 0 才生效；0 表示不限，只用于自检）。</summary>
        public int MaxEntries { get; set; } = SubmissionReport.LimitEntries;

        /// <summary>正文进 URL 的字符上限（超过就退回「写文件 + 拖拽」）。</summary>
        public int MaxUrlChars { get; set; } = SubmissionReport.LimitUrlChars;

        /// <summary>Issue 正文里要带的版本串（UI18n 版本，可空）。</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Issue 正文里要带的游戏环境串（KSP 版本 / 语言，可空）。</summary>
        public string Environment { get; set; } = string.Empty;
    }

    /// <summary>
    /// 一次上报的完整结果：要落盘的 bundle、标题、正文、Issue URL，以及「为什么不能上报」。
    /// <para>
    /// 本类**不做 IO、不弹浏览器、不依赖 UnityEngine**（Core 契约）：
    /// Runtime 只负责把 <see cref="PayloadJson"/> 写到 <see cref="FileName"/>、
    /// 再按 <see cref="NeedsFileFallback"/> 选择「开浏览器」还是「提示拖文件」。
    /// </para>
    /// </summary>
    public sealed class SubmissionReport
    {
        /// <summary>默认仓库（与 tools/accept-submission-issue.ps1 的 <c>$Repo</c> 一致）。</summary>
        public const string DefaultRepo = "DiaoDaiaChan/KSP_UniversalI18n";

        /// <summary>Issue 表单模板名（.github/ISSUE_TEMPLATE/submit-texts.yml）。</summary>
        public const string DefaultTemplate = "submit-texts.yml";

        /// <summary>默认标签。</summary>
        public const string DefaultLabels = "capture";

        /// <summary>单次上报条目上限（docs/12 §12.4）。</summary>
        public const int LimitEntries = 400;

        /// <summary>正文进 URL 的字符上限；超过改走文件拖拽（docs/12 §12.5 约束 2）。</summary>
        public const int LimitUrlChars = 7000;

        /// <summary>不能上报时的原因之一：没有候选。</summary>
        public const string ReasonNoEntries = "no-entries";

        /// <summary>文件名前缀（与 tools/accept-submission-issue.ps1 认的形态一致）。</summary>
        public const string FileNamePrefix = "ui18n-submission-";

        /// <summary>能否上报（false 时看 <see cref="Reason"/>）。</summary>
        public bool CanReport { get; private set; }

        /// <summary>不能上报的原因（能上报时为空串）。</summary>
        public string Reason { get; private set; } = string.Empty;

        /// <summary>本次携带的 bundle（已排序、已按上限截断、id 已重算）。</summary>
        public SubmissionBundle Payload { get; private set; }

        /// <summary>落盘用的 JSON（canonical + id + 行尾换行）。</summary>
        public string PayloadJson { get; private set; } = string.Empty;

        /// <summary>建议文件名（<c>ui18n-submission-&lt;id&gt;.json</c>）。</summary>
        public string FileName { get; private set; } = string.Empty;

        /// <summary>本次携带条数。</summary>
        public int EntryCount { get; private set; }

        /// <summary>截断前的总条数。</summary>
        public int TotalEntries { get; private set; }

        /// <summary>是否被上限挡掉了一部分。</summary>
        public bool Truncated { get; private set; }

        /// <summary>被挡掉的条数。</summary>
        public int Remaining { get; private set; }

        /// <summary>Issue 标题。</summary>
        public string Title { get; private set; } = string.Empty;

        /// <summary>Issue 正文。</summary>
        public string Body { get; private set; } = string.Empty;

        /// <summary>预填 Issue 的 URL（<b>不含</b>正文时也要能单独使用）。</summary>
        public string Url { get; private set; } = string.Empty;

        /// <summary>正文是否被塞进了 URL（false = 玩家要把文件拖进 Issue 输入框）。</summary>
        public bool BodyInUrl { get; private set; }

        /// <summary>URL 长度（日志用，docs/12 §12.5 约束 4）。</summary>
        public int UrlLength { get; private set; }

        /// <summary>
        /// 组装一次上报。
        /// </summary>
        /// <param name="bundle">导出器产出的 bundle（本方法**不改**它，内部深拷贝）。</param>
        /// <param name="text">面向玩家的文案（Runtime 用 T() 灌）。</param>
        /// <param name="options">可调项；null = 契约默认值。</param>
        public static SubmissionReport Compose(SubmissionBundle bundle, SubmissionReportText text, SubmissionReportOptions options)
        {
            SubmissionReportOptions opt = options ?? new SubmissionReportOptions();
            SubmissionReportText copy = text ?? new SubmissionReportText();

            SubmissionBundle payload = Copy(bundle);
            if (payload == null || payload.Entries == null || payload.Entries.Count == 0)
            {
                return new SubmissionReport { CanReport = false, Reason = ReasonNoEntries };
            }

            payload.SortDeterministic();
            int total = payload.Entries.Count;
            int limit = opt.MaxEntries;
            bool truncated = limit > 0 && total > limit;
            if (truncated)
            {
                payload.Entries = payload.Entries.GetRange(0, limit);
            }

            payload.Id = payload.ComputeId();

            SubmissionReport report = new SubmissionReport();
            report.CanReport = true;
            report.Payload = payload;
            report.PayloadJson = payload.ToFileJson();
            report.EntryCount = payload.Entries.Count;
            report.TotalEntries = total;
            report.Truncated = truncated;
            report.Remaining = truncated ? total - limit : 0;
            report.FileName = FileNamePrefix + payload.Id + ".json";
            report.Title = Format(copy.TitleFormat, report.EntryCount.ToString(CultureInfo.InvariantCulture));
            report.Body = BuildBody(report, copy, opt);

            string url = BuildUrl(opt, report.Title, report.Body);
            if (url.Length > opt.MaxUrlChars && opt.MaxUrlChars > 0)
            {
                report.BodyInUrl = false;
                report.Url = BuildUrl(opt, report.Title, string.Empty);
            }
            else
            {
                report.BodyInUrl = true;
                report.Url = url;
            }

            report.UrlLength = report.Url.Length;
            return report;
        }

        /// <summary>正文是否需要用「写文件 + 拖拽」的兜底路径。</summary>
        public bool NeedsFileFallback
        {
            get { return CanReport && !BodyInUrl; }
        }

        /// <summary>日志用一行摘要（docs/12 §12.5 约束 4）。</summary>
        public string Describe()
        {
            if (!CanReport)
            {
                return "report: skipped (" + Reason + ")";
            }

            return "report: entries=" + EntryCount.ToString(CultureInfo.InvariantCulture)
                + "/" + TotalEntries.ToString(CultureInfo.InvariantCulture)
                + ", bodyChars=" + Body.Length.ToString(CultureInfo.InvariantCulture)
                + ", urlChars=" + UrlLength.ToString(CultureInfo.InvariantCulture)
                + ", bodyInUrl=" + (BodyInUrl ? "true" : "false");
        }

        /// <summary>
        /// 拼 Issue URL：<c>https://github.com/{repo}/issues/new?template=..&amp;title=..&amp;body=..</c>。
        /// <para>预填字段用 <see cref="Uri.EscapeDataString(string)"/>，与浏览器行为一致；不引任何网络栈。</para>
        /// </summary>
        public static string BuildUrl(SubmissionReportOptions options, string title, string body)
        {
            SubmissionReportOptions opt = options ?? new SubmissionReportOptions();
            StringBuilder sb = new StringBuilder(256);
            sb.Append("https://github.com/").Append(string.IsNullOrEmpty(opt.Repo) ? DefaultRepo : opt.Repo).Append("/issues/new");
            sb.Append("?template=").Append(Escape(string.IsNullOrEmpty(opt.Template) ? DefaultTemplate : opt.Template));
            if (!string.IsNullOrEmpty(opt.Labels))
            {
                sb.Append("&labels=").Append(Escape(opt.Labels));
            }

            if (!string.IsNullOrEmpty(title))
            {
                sb.Append("&title=").Append(Escape(title));
            }

            if (!string.IsNullOrEmpty(body))
            {
                sb.Append("&body=").Append(Escape(body));
            }

            if (!string.IsNullOrEmpty(opt.Version))
            {
                sb.Append("&version=").Append(Escape(opt.Version));
            }

            if (!string.IsNullOrEmpty(opt.Environment))
            {
                sb.Append("&environment=").Append(Escape(opt.Environment));
            }

            return sb.ToString();
        }

        private static string BuildBody(SubmissionReport report, SubmissionReportText text, SubmissionReportOptions options)
        {
            StringBuilder sb = new StringBuilder(512);
            if (!string.IsNullOrEmpty(text.BodyIntro))
            {
                sb.Append(text.BodyIntro).Append("\n\n");
            }

            sb.Append("- ").Append(text.BodyFileLabel).Append(": `").Append(report.FileName).Append("`\n");
            sb.Append("- ").Append(Format(text.BodyCountFormat,
                report.EntryCount.ToString(CultureInfo.InvariantCulture),
                report.TotalEntries.ToString(CultureInfo.InvariantCulture)));
            if (report.Truncated)
            {
                sb.Append(" · ").Append(Format(text.BodyTruncatedFormat,
                    report.Remaining.ToString(CultureInfo.InvariantCulture),
                    LimitEntries.ToString(CultureInfo.InvariantCulture)));
            }

            sb.Append("\n");
            if (!string.IsNullOrEmpty(options.Version))
            {
                sb.Append("- ").Append(text.BodyVersionLabel).Append(": ").Append(options.Version).Append("\n");
            }

            if (!string.IsNullOrEmpty(options.Environment))
            {
                sb.Append("- ").Append(text.BodyEnvironmentLabel).Append(": ").Append(options.Environment).Append("\n");
            }

            if (!string.IsNullOrEmpty(text.BodySteps))
            {
                sb.Append("\n").Append(text.BodySteps).Append("\n");
            }

            return sb.ToString();
        }

        private static string Escape(string raw)
        {
            return Uri.EscapeDataString(raw ?? string.Empty);
        }

        private static string Format(string pattern, params string[] args)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return string.Empty;
            }

            try
            {
                return string.Format(CultureInfo.InvariantCulture, pattern, args);
            }
            catch (FormatException)
            {
                return pattern;   // 文案里写了非法占位符：原样吐出，不要让上报整个崩掉
            }
        }

        /// <summary>深拷贝（Core 契约：<see cref="Compose"/> 绝不改调用方的 bundle）。</summary>
        private static SubmissionBundle Copy(SubmissionBundle source)
        {
            if (source == null)
            {
                return null;
            }

            SubmissionBundle copy = new SubmissionBundle();
            copy.Schema = source.Schema;
            copy.Id = source.Id;
            copy.CreatedAt = source.CreatedAt;
            copy.Language = source.Language;
            copy.Actor = source.Actor != null ? source.Actor.Clone() : new SubmissionActor();
            copy.Consent = source.Consent != null ? source.Consent.Clone() : new SubmissionConsent();
            copy.Redactions = source.Redactions != null ? source.Redactions.Clone() : new SubmissionRedactions();
            copy.Note = source.Note;
            copy.Mods = new List<SubmissionMod>();
            if (source.Mods != null)
            {
                for (int i = 0; i < source.Mods.Count; i++)
                {
                    SubmissionMod mod = source.Mods[i];
                    if (mod != null)
                    {
                        copy.Mods.Add(mod.Clone());
                    }
                }
            }

            copy.Entries = new List<SubmissionEntry>();
            if (source.Entries != null)
            {
                for (int i = 0; i < source.Entries.Count; i++)
                {
                    SubmissionEntry entry = source.Entries[i];
                    if (entry != null)
                    {
                        copy.Entries.Add(entry.Clone());
                    }
                }
            }

            return copy;
        }
    }
}
