using System;
using UnityEngine;

namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// 文本层两个 patch（05 §2 的 C 类：IL 硬编码字面量必须 hook）。
    /// <list type="bullet">
    /// <item>UnityEngine.UI.Text.set_text(string value) —— 文本参数位 #0，参数名 value</item>
    /// <item>TMPro.TMP_Text.set_text(string value) —— 文本参数位 #0，参数名 value</item>
    /// </list>
    /// 注意：TMPro 只能打声明方法 TMP_Text.set_text；打 TextMeshProUGUI 会抛
    /// "You can only patch implemented methods"。两个 patch 都只改传入值，不碰任何静态状态。
    /// </summary>
    internal static class TextPatches
    {
        /// <summary>UnityEngine.UI.Text.set_text 的前缀。</summary>
        private static void UnityTextPrefix(ref string value)
        {
            if (!Translator.HasAnyTranslation)
            {
                return;
            }

            try
            {
                string translated = Translator.Translate(value);
                if (translated != null)
                {
                    value = translated;
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure("UnityEngine.UI.Text.set_text", ex);
            }
        }

        /// <summary>TMPro.TMP_Text.set_text 的前缀。</summary>
        private static void TmpTextPrefix(ref string value)
        {
            if (!Translator.HasAnyTranslation)
            {
                return;
            }

            try
            {
                string translated = Translator.Translate(value);
                if (translated != null)
                {
                    value = translated;
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure("TMPro.TMP_Text.set_text", ex);
            }
        }
    }
}
