using System;

namespace UI18n.Core.Policy
{
    /// <summary>
    /// 语言门禁（决策 D24 / D28 / D32，05 §6.2）：非中文环境<b>一个 patch 都不装</b>，
    /// 因此这里是 Mod 启动时唯一的分岔点，任何后续逻辑都不该再重复判断语言。
    /// <para>
    /// 只做 zh（D28 说只做 zh-cn 但架构语言无关），<c>zh</c> / <c>zh-CN</c> / <c>zh-Hans</c> 一律算中文；
    /// 比较用 OrdinalIgnoreCase，不带 CultureInfo（避免 KSP 里 locale 状态影响判断）。
    /// </para>
    /// </summary>
    public static class LanguageGate
    {
        /// <summary>
        /// 当前是否该安装 / 启用翻译。<paramref name="forceOverride"/> 来自配置（默认 false，D32），
        /// 为 true 时无条件放行（用于强制覆盖英文界面做测试）。
        /// </summary>
        public static bool ShouldTranslate(string currentLanguage, bool forceOverride)
        {
            if (forceOverride)
            {
                return true;
            }

            return IsChinese(currentLanguage);
        }

        /// <summary>语言码是否中文（以 "zh" 开头，忽略大小写）。null / 空串 / 空白一律 false。</summary>
        public static bool IsChinese(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                return false;
            }

            string trimmed = language.Trim();
            if (trimmed.Length < 2)
            {
                return false;
            }

            return trimmed[0] == 'z' || trimmed[0] == 'Z'
                ? trimmed[1] == 'h' || trimmed[1] == 'H'
                : false;
        }
    }
}
