using System;
using UnityEngine;
using UI18n.Runtime.Diagnostics;

namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// task-31 的影子前缀（<b>默认不装</b>，只有 <c>ShadowStyleDraw=True</c> 时才由
    /// <see cref="PatchRegistry.InstallShadowPatches"/> 装上）。
    /// <para>
    /// 两个目标都是 IMGUI 绘制的统一出口：<c>GUIStyle.Internal_Draw</c> 与 <c>Internal_Draw2</c>。
    /// 所有 public <c>GUIStyle.Draw</c> 重载都汇到 private <c>Draw(...)</c>，而它体内只有
    /// <c>controlId == -1 ? Internal_Draw(...) : Internal_Draw2(...)</c>，所以打这两个点等于覆盖
    /// <c>GUI.Label / Box / Button / RepeatButton / Toggle / GUILayout.*</c> 的全部绘制路径
    /// （窗口标题例外：它走 native <c>GUI.Internal_DoWindow</c>，不经过 GUIStyle.Draw）。
    /// </para>
    /// <para>
    /// <b>结构性保证绝不修改 content</b>：两个前缀的 <c>GUIContent</c> 参数都是<b>按值</b>接收的
    /// （不是 <c>ref</c>），所以即使代码写错也改不到调用方手里的对象。这正是「影子」该有的形状。
    /// </para>
    /// <para>
    /// <b>实验结论（task-34 round 8 实测 → task-35 纠正）：结论曾写错，已归档。</b>
    /// 当轮统计：<c>shadow: styleDraw calls=1479912 distinct=67, legacy distinct=69,
    /// styleDraw-only distinct=0 (translatable=0)</c>，<c>shadow-20261023-060127.jsonl</c> 0 行。
    /// <b>但这个 0 不能推广</b>：它只说明「当轮屏幕上被绘制过的串」全被现有 hook 覆盖了，
    /// 而当轮屏幕上<b>没有任何 Toolbar / SelectionGrid</b>（Alt+F12 打不开、探针也没画）。
    /// 真正的缺口是 <c>GUI.DoButtonGrid</c>：它直接 <c>GUIStyle.Draw</c>，绕过全部 <c>ref GUIContent</c> 目标，
    /// 已作为第 17 个 hook 补上（见 <see cref="ButtonGridPatches"/>，<c>ExpectedPatchCount</c> 16 → 17）。
    /// 所以 <c>GUIStyle.Internal_Draw</c> <b>仍然只做影子观测</b>（默认 <c>ShadowStyleDraw=False</c>）：
    /// 它会看到 <c>TextField/TextArea</c> 里玩家输入的文本，不适合做翻译点。
    /// 代码保留，作为将来排查「还有没有别的绘制路径绕过我们」的取证工具。
    /// </para>
    /// </summary>
    internal static class ShadowPatches
    {
        /// <summary><c>GUIStyle.Internal_Draw(Rect, GUIContent content, bool isHover, bool isActive, bool on, bool hasKeyboardFocus)</c>。</summary>
        private static void StyleDrawPrefix(GUIContent content)
        {
            Apply(content, "GUIStyle.Internal_Draw");
        }

        /// <summary><c>GUIStyle.Internal_Draw2(Rect position, GUIContent content, int controlID, bool on)</c>。</summary>
        private static void StyleDraw2Prefix(GUIContent content)
        {
            Apply(content, "GUIStyle.Internal_Draw2");
        }

        private static void Apply(GUIContent content, string sink)
        {
            // 整个方法体就是一个 try/catch：影子路径的任何异常都不许冒泡进游戏的绘制循环（红线 6）
            try
            {
                if (content == null)
                {
                    return;
                }

                ShadowTracker.NoteStyleDraw(content.text, sink);
            }
            catch (Exception ex)
            {
                ShadowTracker.ReportFailure(sink, ex);
            }
        }
    }
}
