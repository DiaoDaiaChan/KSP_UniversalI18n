using System;
using UI18n.Core.Settings;

namespace UI18n.Core.Submission
{
    /// <summary>
    /// 投稿同意的判定口径（docs/10 §6.2）。
    /// <para>
    /// 两个开关并存，**prefs 优先**：
    /// <list type="number">
    /// <item><c>Config.cfg</c> 的 <c>SubmissionConsent = None|Granted</c>（安装级，默认 None）；</item>
    /// <item><c>PluginData/UI18n/prefs.json</c> 的 <c>submission.consent.granted</c>（玩家级）。</item>
    /// </list>
    /// 只要玩家在面板里点过一次（prefs 里有记录：granted 为真、或留下 <c>policyVersion</c>/<c>grantedAt</c>），
    /// 就以 prefs 为准 —— 否则「面板里关掉、Config.cfg 里还是 Granted」会退化成投不出去也关不掉的鬼状态。
    /// </para>
    /// <para>纯函数、不抛异常、不依赖 UnityEngine（Core 契约）。</para>
    /// </summary>
    public static class SubmissionPolicy
    {
        /// <summary>当前政策版本（写进 bundle 的 <c>consent.policyVersion</c>；改政策文案要加一）。</summary>
        public const int PolicyVersion = 1;

        /// <summary>Config.cfg 里表示「未同意」的值。</summary>
        public const string ConsentNone = "None";

        /// <summary>Config.cfg 里表示「已同意」的值。</summary>
        public const string ConsentGranted = "Granted";

        /// <summary>Config.cfg 的键名（<c>Control.Config</c> 段）。</summary>
        public const string ConfigKey = "SubmissionConsent";

        /// <summary>归一 Config.cfg 的取值：只认 Granted（不分大小写），其余一律 None。</summary>
        public static string NormalizeConsent(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return ConsentNone;
            }

            string trimmed = raw.Trim();
            return string.Equals(trimmed, ConsentGranted, StringComparison.OrdinalIgnoreCase)
                ? ConsentGranted
                : ConsentNone;
        }

        /// <summary>prefs 里是否留下过玩家的明确决定（用来判「prefs 优先」）。</summary>
        public static bool HasRecordedDecision(SubmissionPrefs submission)
        {
            if (submission == null)
            {
                return false;
            }

            return submission.Granted
                || submission.PolicyVersion > 0
                || !string.IsNullOrEmpty(submission.GrantedAt);
        }

        /// <summary>最终是否允许导出/投稿。</summary>
        public static bool IsGranted(SubmissionPrefs submission, string configConsent)
        {
            if (HasRecordedDecision(submission))
            {
                return submission.Granted;
            }

            return string.Equals(NormalizeConsent(configConsent), ConsentGranted, StringComparison.Ordinal);
        }
    }
}
