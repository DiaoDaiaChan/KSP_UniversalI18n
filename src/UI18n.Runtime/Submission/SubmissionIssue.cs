using System;
using UI18n.Core.Submission;
using UI18n.Runtime.Diagnostics;
using UnityEngine;

namespace UI18n.Runtime
{
    /// <summary>
    /// M4-b：把已导出的投稿 bundle 变成一条 GitHub Issue（标题 / 正文 / 预填 URL）。
    /// <para>
    /// 仍然<b>绝不联网</b>（§6.9）：这里只算字符串，URL 交给系统浏览器，由玩家自己点「Submit」。
    /// 正文只带计数与文件名，不带任何原文或私有值 —— 脱敏与 id 计算全在 Core 的
    /// <see cref="SubmissionFilter"/> / <see cref="SubmissionBundle"/> 里，这里不重复实现。
    /// </para>
    /// <para>
    /// 传输层是<b>文件附件</b>（tools/accept-submission-issue.ps1 只认 user-attachments 链接），
    /// 所以正文里绝不内联 JSON：文件由 <see cref="SubmissionExporter"/> 落盘，玩家拖进表单即可。
    /// </para>
    /// </summary>
    internal static class SubmissionIssue
    {
        /// <summary>投稿页面（与面板里的 SubmissionPageUrl 同一个入口，只是这里要带 title/body）。</summary>
        internal const string Repo = SubmissionReport.DefaultRepo;

        /// <summary>玩家看到的英文文案（收件人是维护者，所以不做本地化；按钮文案才走 T()）。</summary>
        internal static SubmissionReportText Text()
        {
            return new SubmissionReportText
            {
                TitleFormat = "capture: {0} untranslated strings",
                BodyIntro = "UI18n could not translate the strings below. The attached json is the machine-readable bundle",
                BodyFileLabel = "bundle file",
                BodyCountFormat = "candidates: {0} / {1}",
                BodyTruncatedFormat = "{0} more strings were not included (limit {1}); please submit again after this one is merged.",
                BodyVersionLabel = "ui18n",
                BodyEnvironmentLabel = "environment",
                BodySteps = "1. drag the json file into the **bundle** field above (keep it attached)\n"
                    + "2. check the preview shows the same candidate count as below\n"
                    + "3. submit — no account data, save files or paths are included",
            };
        }

        /// <summary>组装一次上报（不可上报时 <see cref="SubmissionReport.CanReport"/> 为 false，不抛）。</summary>
        internal static SubmissionReport Build(SubmissionBundle bundle)
        {
            SubmissionReportOptions options = new SubmissionReportOptions();
            options.Repo = Repo;
            options.Version = SafeVersion();
            options.Environment = SafeEnvironment();
            return SubmissionReport.Compose(bundle, Text(), options);
        }

        /// <summary>
        /// 把 URL 交给系统浏览器。<b>不发起任何请求</b>，也不检查返回结果（浏览器可能被拦截）。
        /// </summary>
        /// <returns>URL 是否成功交给系统。</returns>
        internal static bool Open(SubmissionReport report)
        {
            if (report == null || !report.CanReport || string.IsNullOrEmpty(report.Url))
            {
                return false;
            }

            try
            {
                Application.OpenURL(report.Url);
                Log.Info("submission: issue url opened (" + report.Describe() + ")");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("submission: open issue url failed (" + ex.GetType().Name + ": " + ex.Message + ")");
                return false;
            }
        }

        private static string SafeVersion()
        {
            try
            {
                string version = UI18nAddon.Version;
                return string.IsNullOrEmpty(version) ? "unknown" : version;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static string SafeEnvironment()
        {
            string ksp = "unknown";
            try
            {
                ksp = KspVersionInfo.Authoritative();
                if (string.IsNullOrEmpty(ksp))
                {
                    ksp = "unknown";
                }
            }
            catch (Exception)
            {
            }

            string os = "unknown";
            try
            {
                os = Environment.OSVersion.ToString();
            }
            catch (Exception)
            {
            }

            return "KSP " + ksp + ", " + os;
        }
    }
}