namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// 16 个 hook 目标的计数器句柄（P2 新增）。由 <see cref="Counters.Initialize"/> 在装 patch 前统一创建，
    /// 之后前缀方法每次调用只做 <c>Counters.X.Touch()</c>（静态字段读 + 字段自增，无字典查找）。
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

        /// <summary>
        /// 按 <see cref="PatchRegistry"/> 的表序创建 16 个计数器。
        /// 幂等：重复调用会先清空 <see cref="PatchStats"/> 的登记表（统计随之归零）。
        /// </summary>
        internal static void Initialize()
        {
            PatchStats.Reset();

            UnityText = PatchStats.Create(PatchNames.UnityText);
            TmpText = PatchStats.Create(PatchNames.TmpText);

            GuiLabel = PatchStats.Create(PatchNames.GuiLabel);
            GuiBox = PatchStats.Create(PatchNames.GuiBox);
            GuiButton = PatchStats.Create(PatchNames.GuiButton);
            GuiRepeatButton = PatchStats.Create(PatchNames.GuiRepeatButton);
            GuiToggle = PatchStats.Create(PatchNames.GuiToggle);
            GuiToggleInt = PatchStats.Create(PatchNames.GuiToggleInt);
            GuiDoWindowTitle = PatchStats.Create(PatchNames.GuiDoWindowTitle);
            GuiDoModalWindow = PatchStats.Create(PatchNames.GuiDoModalWindow);

            GuiLayoutLabel = PatchStats.Create(PatchNames.GuiLayoutLabel);
            GuiLayoutButton = PatchStats.Create(PatchNames.GuiLayoutButton);
            GuiLayoutRepeatButton = PatchStats.Create(PatchNames.GuiLayoutRepeatButton);
            GuiLayoutToggle = PatchStats.Create(PatchNames.GuiLayoutToggle);
            GuiLayoutWindow = PatchStats.Create(PatchNames.GuiLayoutWindow);

            ScreenMessage = PatchStats.Create(PatchNames.ScreenMessage);
        }
    }

    /// <summary>
    /// 统计/日志里用的短目标名（registry 里的 <c>spec.Target</c> 带参数名注记，太长，不适合每 20 秒一行）。
    /// <para>名字基于<b>实际被 patch 的方法</b>：所以列表里是 <c>GUILayout.DoLabel</c> 而不是公开便捷方法
    /// <c>GUILayout.Label</c>（后者只是转发层）。</para>
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
