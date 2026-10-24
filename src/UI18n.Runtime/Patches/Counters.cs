namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// 17 个 hook 目标的计数器句柄（P2 新增；第 17 个 <c>GUI.DoButtonGrid</c> 见 task-35）。由 <see cref="Counters.Initialize"/> 在装 patch 前统一创建，
    /// 之后前缀方法每次调用只做 <c>Counters.X.Touch()</c>（静态字段读 + 字段自增，无字典查找）。
    /// <para>
    /// 为什么是 17 个字段而不是一个字典：<b>每个目标一个前缀方法</b>（见 ImGuiPatches / TextPatches / ButtonGridPatches），
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

        /// <summary>Toolbar / SelectionGrid 的私有漏斗（task-35，第 17 个目标）。</summary>
        internal static PatchCounter GuiButtonGrid;

        /// <summary>
        /// 按 <see cref="PatchRegistry"/> 的表序创建 17 个计数器。
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
            GuiButtonGrid = PatchStats.Create(PatchNames.GuiButtonGrid);

            // 表自检（task-35）：Runtime 没有测试工程（tests/ 只引用 net10.0 的 Core，而 Runtime 是 net472 +
            // Unity 引用，测试工程引用不了），所以把「短名唯一」做成运行时自检 —— 有人加目标时复制粘贴出重复短名，
            // 日志里立刻能看见，而不是等 round 里数统计行才发现两个目标被合并成一行。
            string duplicate = FindDuplicateName();
            if (duplicate != null)
            {
                Log.Error("patch stats: duplicate short name \"" + duplicate + "\" in PatchNames (per-target stats would merge; see Counters.Initialize)");
            }
        }

        /// <summary>返回第一个重复的短名（没有重复返回 <c>null</c>）。</summary>
        private static string FindDuplicateName()
        {
            string[] names =
            {
                PatchNames.UnityText, PatchNames.TmpText,
                PatchNames.GuiLabel, PatchNames.GuiBox, PatchNames.GuiButton, PatchNames.GuiRepeatButton,
                PatchNames.GuiToggle, PatchNames.GuiToggleInt, PatchNames.GuiDoWindowTitle, PatchNames.GuiDoModalWindow,
                PatchNames.GuiLayoutLabel, PatchNames.GuiLayoutButton, PatchNames.GuiLayoutRepeatButton,
                PatchNames.GuiLayoutToggle, PatchNames.GuiLayoutWindow,
                PatchNames.ScreenMessage, PatchNames.GuiButtonGrid,
            };

            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++)
            {
                if (!seen.Add(names[i]))
                {
                    return names[i];
                }
            }

            return null;
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

        /// <summary>第 17 个目标（task-35）：Toolbar / SelectionGrid 的按钮文字。按元素计 hit（见 README §9.4）。</summary>
        internal const string GuiButtonGrid = "GUI.DoButtonGrid(GUIContent[])";
    }
}
