using System;
using TMPro;
using UI18n.Runtime.Fonts;

namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// 文本层两个 patch（05 §2 的 C 类：IL 硬编码字面量必须 hook）。
    /// <list type="bullet">
    /// <item>UnityEngine.UI.Text.set_text(string value) —— 文本参数位 #0，参数名 value</item>
    /// <item>TMPro.TMP_Text.set_text(string value) —— 文本参数位 #0，参数名 value</item>
    /// </list>
    /// 两个目标各自一个前缀方法（P2 的 per-target 计数要求），共享的只有查表逻辑。
    /// <para>
    /// <b>字体门禁在替换之前</b>：命中译文后先问 <see cref="FontResolver"/> 这个组件能不能真显示中文，
    /// 只有 <see cref="FontCheck.Available"/> 才换文本；否则保留原文并计入 <c>KeptSource</c>
    /// （P2 的「可判定降级」：宁可显示英文也不显示 □□□）。
    /// </para>
    /// <para>
    /// 注意：TMPro 只能打声明方法 <c>TMP_Text.set_text</c>；打 <c>TextMeshProUGUI</c> 会抛
    /// "You can only patch implemented methods"。两个 patch 都只改传入值、只改这一个组件的 font 引用，
    /// 不碰任何静态状态、不调玩法 API（红线 3）。
    /// </para>
    /// </summary>
    internal static class TextPatches
    {
        /// <summary>UnityEngine.UI.Text.set_text 的前缀（uGUI：字体可以真修）。</summary>
        private static void UnityTextPrefix(UnityEngine.UI.Text __instance, ref string value)
        {
            PatchCounter counter = Counters.UnityText;
            counter.Touch();

            if (!Translator.HasAnyTranslation)
            {
                return;
            }

            try
            {
                string translated = Translator.Translate(value, PatchNames.UnityText);
                if (translated == null)
                {
                    return;
                }

                if (FontResolver.EnsureLegacyFont(__instance) == FontCheck.Available)
                {
                    value = translated;
                    counter.Hits++;
                }
                else
                {
                    counter.KeptSource++;
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure(PatchNames.UnityText, ex);
            }
        }

        /// <summary>TMPro.TMP_Text.set_text 的前缀（TMP：要么追加 fallback 资产，要么保留原文）。</summary>
        private static void TmpTextPrefix(TMP_Text __instance, ref string value)
        {
            PatchCounter counter = Counters.TmpText;
            counter.Touch();

            if (!Translator.HasAnyTranslation)
            {
                return;
            }

            try
            {
                string translated = Translator.Translate(value, PatchNames.TmpText);
                if (translated == null)
                {
                    return;
                }

                if (FontResolver.EnsureTmpFallback(__instance) == FontCheck.Available)
                {
                    value = translated;
                    counter.Hits++;
                }
                else
                {
                    counter.KeptSource++;
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure(PatchNames.TmpText, ex);
            }
        }
    }
}
