using System;
using UnityEngine;

namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// IMGUI 层 13 个 patch（05 §2 的 10 个 + T5b 补的 DoBox 已撤 + T5b2 补的 3 个：RepeatButton / DoWindow / DoModalWindow）。
    /// 12 个目标的文本参数叫 <c>content</c>，共用 <see cref="ContentPrefix"/>；只有 <c>GUI.DoWindow</c> 的文本参数
    /// 叫 <c>title</c>，单独用 <see cref="TitlePrefix"/>（Harmony 按参数名绑定 ref 参数；名字错不抛异常、只静默失效，
    /// 所以 PatchRegistry 会在装之前用反射核对参数名）。
    /// <para>
    /// 目标清单（文本参数位）：
    /// <list type="bullet">
    /// <item>GUILayout.DoLabel(GUIContent content, GUIStyle, GUILayoutOption[]) —— #0</item>
    /// <item>GUILayout.DoButton(GUIContent content, GUIStyle, GUILayoutOption[]) —— #0</item>
    /// <item>GUILayout.DoRepeatButton(GUIContent content, GUIStyle, GUILayoutOption[]) —— #0</item>
    /// <item>GUILayout.DoToggle(bool, GUIContent content, GUIStyle, GUILayoutOption[]) —— #1</item>
    /// <item>GUILayout.DoWindow(int, Rect, GUI.WindowFunction, GUIContent content, GUIStyle, GUILayoutOption[]) —— #3</item>
    /// <item>GUI.Label(Rect, GUIContent content, GUIStyle) —— #1</item>
    /// <item>GUI.Box(Rect, GUIContent content, GUIStyle) —— #1</item>
    /// <item>GUI.Button(Rect, GUIContent content, GUIStyle) —— #1</item>
    /// <item>GUI.Toggle(Rect, bool, GUIContent content, GUIStyle) —— #2</item>
    /// <item>GUI.Toggle(Rect, int, bool, GUIContent content, GUIStyle) —— #3</item>
    /// <item>GUI.RepeatButton(Rect, GUIContent content, GUIStyle) —— #1（T5b2 补）</item>
    /// <item>GUI.DoWindow(int, Rect, GUI.WindowFunction, GUIContent title, GUIStyle, GUISkin, bool) —— #3（T5b2 补；私有漏斗，参数名 title。GUI.Window 的 string 重载绕过公开 GUIContent 重载直达它，所以打它才覆盖 string 标题）</item>
    /// <item>GUI.DoModalWindow(int, Rect, GUI.WindowFunction, GUIContent content, GUIStyle, GUISkin) —— #3（T5b2 补；与 DoWindow 并列的独立漏斗，参数名 content 而非 title）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 红线 4：<b>绝不</b>写 <c>content.text</c> / <c>title.text</c>。GUIContent.Temp 返回的是共享实例，就地改写会污染
    /// 工坊/编辑器里所有同文本控件；这里只 <c>new GUIContent(...)</c> 再把新对象写回 <c>ref</c> 参数。
    /// </para>
    /// </summary>
    internal static class ImGuiPatches
    {
        /// <summary>12 个 IMGUI 目标（文本参数名 content）共用的前缀：命中则换成一个全新的 GUIContent，否则原样放行。</summary>
        private static void ContentPrefix(ref GUIContent content)
        {
            if (!Translator.HasAnyTranslation)
            {
                return;
            }

            try
            {
                GUIContent translated = Translator.TranslateContent(content);
                if (translated != null)
                {
                    content = translated;
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure("IMGUI.GUIContent", ex);
            }
        }

        /// <summary>
        /// <c>GUI.Window(int, Rect, GUI.WindowFunction, GUIContent title, GUIStyle)</c> 专用前缀：
        /// 该目标的文本参数名是 <c>title</c> 而不是 <c>content</c>，参数名对不上 Harmony 就不会注入，只能单独一个方法体。
        /// </summary>
        private static void TitlePrefix(ref GUIContent title)
        {
            if (!Translator.HasAnyTranslation)
            {
                return;
            }

            try
            {
                GUIContent translated = Translator.TranslateContent(title);
                if (translated != null)
                {
                    title = translated;
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure("IMGUI.Title", ex);
            }
        }
    }
}
