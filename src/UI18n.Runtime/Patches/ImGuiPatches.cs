using System;
using UnityEngine;

namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// IMGUI 层 13 个 patch（05 §2 的 10 个 + T5b2 补的 RepeatButton / DoWindow / DoModalWindow）
    /// + 1 个 uGUI 之外的屏幕消息目标见 <see cref="ScreenMessagesPatches"/>，全项目共 16 个 hook 目标。
    /// <para>
    /// <b>每个目标一个前缀方法</b>（P2 的 per-target 统计要求）：前缀里只做一次
    /// <c>Counters.X.Touch()</c> 和一次查表，公共逻辑收在 <see cref="Apply"/>。
    /// 因为 Harmony 是<b>按参数名</b>把 ref 参数绑到目标方法参数上的，而本层的文本参数名并不统一：
    /// 12 个目标叫 <c>content</c>，只有 <c>GUI.DoWindow</c> 叫 <c>title</c>；
    /// 名字写错不会抛异常、只会静默失效，所以 <see cref="PatchRegistry"/> 装之前会用
    /// <c>PrefixParameterMatcher</c> 核对参数名。
    /// </para>
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
    /// <item>GUI.RepeatButton(Rect, GUIContent content, GUIStyle) —— #1</item>
    /// <item>GUI.Toggle(Rect, bool, GUIContent content, GUIStyle) —— #2</item>
    /// <item>GUI.Toggle(Rect, int, bool, GUIContent content, GUIStyle) —— #3</item>
    /// <item>GUI.DoWindow(int, Rect, GUI.WindowFunction, GUIContent <b>title</b>, GUIStyle, GUISkin, bool) —— #3（私有漏斗）</item>
    /// <item>GUI.DoModalWindow(int, Rect, GUI.WindowFunction, GUIContent content, GUIStyle, GUISkin) —— #3（私有漏斗）</item>
    /// </list>
    /// 参数名逐条来自 IL 实测（<c>_scratch/il/gui.cs</c>）：Label/Box/Button/RepeatButton/Toggle 的两个重载
    /// 都叫 <c>content</c>；两个窗口漏斗里 <c>DoWindow</c> 叫 <c>title</c>、<c>DoModalWindow</c> 叫 <c>content</c>。
    /// </para>
    /// <para>
    /// 红线 4：<b>绝不</b>写 <c>content.text</c> / <c>title.text</c>。GUIContent.Temp 返回的是共享实例，就地改写会污染
    /// 工坊/编辑器里所有同文本控件；这里只 <c>new GUIContent(...)</c> 再把新对象写回 <c>ref</c> 参数。
    /// </para>
    /// </summary>
    internal static class ImGuiPatches
    {
        // ------------------------------------------------------------------
        // GUILayout 的 5 个私有实现（公开的 GUILayout.Label/Button/... 都是转发层，见 README §7）
        // ------------------------------------------------------------------

        private static void LayoutLabelPrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiLayoutLabel, PatchNames.GuiLayoutLabel);
        }

        private static void LayoutButtonPrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiLayoutButton, PatchNames.GuiLayoutButton);
        }

        private static void LayoutRepeatButtonPrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiLayoutRepeatButton, PatchNames.GuiLayoutRepeatButton);
        }

        private static void LayoutTogglePrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiLayoutToggle, PatchNames.GuiLayoutToggle);
        }

        private static void LayoutWindowPrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiLayoutWindow, PatchNames.GuiLayoutWindow);
        }

        // ------------------------------------------------------------------
        // GUI 的公有入口
        // ------------------------------------------------------------------

        private static void LabelPrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiLabel, PatchNames.GuiLabel);
        }

        private static void BoxPrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiBox, PatchNames.GuiBox);
        }

        private static void ButtonPrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiButton, PatchNames.GuiButton);
        }

        private static void RepeatButtonPrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiRepeatButton, PatchNames.GuiRepeatButton);
        }

        private static void TogglePrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiToggle, PatchNames.GuiToggle);
        }

        private static void ToggleIntPrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiToggleInt, PatchNames.GuiToggleInt);
        }

        // ------------------------------------------------------------------
        // 窗口标题：两个私有漏斗（string 重载绕过公开 GUIContent 重载直达它们）
        // ------------------------------------------------------------------

        /// <summary><c>GUI.DoWindow</c> 专用：该目标的文本参数名是 <c>title</c> 而不是 <c>content</c>。</summary>
        private static void DoWindowTitlePrefix(ref GUIContent title)
        {
            Apply(ref title, Counters.GuiDoWindowTitle, PatchNames.GuiDoWindowTitle);
        }

        /// <summary><c>GUI.DoModalWindow</c> 专用：这条漏斗的参数名又是 <c>content</c>（IL 实测）。</summary>
        private static void DoModalWindowPrefix(ref GUIContent content)
        {
            Apply(ref content, Counters.GuiDoModalWindow, PatchNames.GuiDoModalWindow);
        }

        // ------------------------------------------------------------------
        // 公共实现
        // ------------------------------------------------------------------

        /// <summary>
        /// 13 个 IMGUI 目标共用的动作：计数 → 查表 → 命中则换成<b>新对象</b>写回 ref 参数。
        /// 未命中/keep/空文本一律原样放行（零分配，05 §4）。
        /// </summary>
        private static void Apply(ref GUIContent content, PatchCounter counter, string name)
        {
            counter.Touch();

            if (!Translator.HasAnyTranslation)
            {
                return;
            }

            try
            {
                GUIContent translated = Translator.TranslateContent(content, name);
                if (translated != null)
                {
                    content = translated;
                    counter.Hits++;
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure(name, ex);
            }
        }
    }
}
