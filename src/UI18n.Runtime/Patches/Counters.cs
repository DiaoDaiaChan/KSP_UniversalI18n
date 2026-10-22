namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// 16 个 hook 目标的计数器句柄（P2 新增）。由 <see cref="PatchRegistry.BuildSpecs"/> 在装 patch 时赋值，
    /// 之后前缀方法每次调用只做 <c>Counters.X.Calls++</c>（静态字段读 + 字段自增，无字典查找）。
    /// <para>
    /// 为什么是 16 个字段而不是一个字典：<b>每个目标一个前缀方法</b>（见 ImGuiPatches / TextPatches），
    /// 这样 Harmony 的参数名绑定与 per-target 计数都是逐目标的、互不干扰；共享一个字典会让热路径多一次哈希查询。
    /// </para>
    /// </summary>
    internal static class Counters
    {
        internal static PatchCounter UnityText;
        internal static PatchCounter TmpText;

        internal static PatchCounter GuiLabel;
        internal static PatchCounter GuiBox;
        internal static PatchCounter GuiButton;
        internal static PatchCounter GuiRepeatButton;
        internal static PatchCounter GuiToggle;
        internal static PatchCounter GuiToggleInt;
        internal static PatchCounter GuiDoWindowTitle;
        internal static PatchCounter GuiDoModalWindow;

        internal static PatchCounter GuiLayoutLabel;
        internal static PatchCounter GuiLayoutButton;
        internal static PatchCounter GuiLayoutRepeatButton;
        internal static PatchCounter GuiLayoutToggle;
        internal static PatchCounter GuiLayoutWindow;

        internal static PatchCounter ScreenMessage;
    }

    /// <summary>
    /// 统计/日志里用的短目标名（registry 里的 <c>spec.Target</c> 带参数名注记，太长，不适合每 20 秒一行）。
    /// </summary>
    internal static class PatchNames
    {
        internal const string UnityText = "UnityEngine.UI.Text.set_text";
        internal const string TmpText = "TMPro.TMP_Text.set_text";

        internal const string GuiLabel = "GUI.Label";
        internal const string GuiBox = "GUI.Box";
        internal const string GuiButton = "GUI.Button";
        internal const string GuiRepeatButton = "GUI.RepeatButton";
        internal const string GuiToggle = "GUI.Toggle";
        internal const string GuiToggleInt = "GUI.Toggle(int)";
        internal const string GuiDoWindowTitle = "GUI.DoWindow(title)";
        internal const string GuiDoModalWindow = "GUI.DoModalWindow";

        internal const string GuiLayoutLabel = "GUILayout.DoLabel";
        internal const string GuiLayoutButton = "GUILayout.DoButton";
        internal const string GuiLayoutRepeatButton = "GUILayout.DoRepeatButton";
        internal const string GuiLayoutToggle = "GUILayout.DoToggle";
        internal const string GuiLayoutWindow = "GUILayout.DoWindow";

        internal const string ScreenMessage = "ScreenMessages.PostMessage";
    }
}
