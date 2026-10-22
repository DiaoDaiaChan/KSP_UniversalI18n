using System;
using System.Reflection;
using HarmonyLib;
using UI18n.Core.Dict;
using UI18n.Core.Patching;
using UnityEngine;

namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// patch 注册表：16 个 hook 目标逐个安装，每个单独 try/catch。
    /// 任何一个失败都只记一条 <c>[UI18n] patch failed: {目标} → {原因}</c> 并继续装下一个（红线 6：
    /// 失败只 log 不抛，但必须计数并给出明确原因）。
    /// <para>
    /// 每个目标有自己的前缀方法（见 <see cref="TextPatches"/> / <see cref="ImGuiPatches"/> /
    /// <see cref="ScreenMessagesPatches"/>）与自己的计数器（<see cref="Counters"/>），装之前用
    /// <see cref="PrefixParameterMatcher"/> 核对参数名 —— Harmony 按参数名绑定 ref 参数，名字错不抛异常只静默失效。
    /// </para>
    /// </summary>
    internal static class PatchRegistry
    {
        /// <summary>
        /// 整个 hook 面 = 16 个方法：05 §2 的 12 个（2 个 set_text + 10 个 IMGUI）
        /// + T5b2 补的 4 个（<c>GUI.RepeatButton</c> / <c>GUI.DoWindow</c> / <c>GUI.DoModalWindow</c> / <c>ScreenMessages.PostMessage</c>）。
        /// 两个窗口目标用<b>私有漏斗</b>而不是公开重载：<c>GUI.Window(…, string)</c> / <c>ModalWindow(…, string)</c> 会绕过公开的
        /// GUIContent 重载直连漏斗，打漏斗才覆盖 string 标题（IL 实测，见 README §7）。
        /// T5b 曾补过 <c>GUILayout.DoBox</c>，T5b2 已撤：它的方法体只有一句 <c>GUI.Box(...)</c>，而 GUI.Box 本来就已打，
        /// 多这一层只多一次必然未命中的查表（05 §2 的 12 个里也不含它）。
        /// 「哪些同样带 GUIContent 但有意不打」见 README 第 7 节「覆盖矩阵与有意放弃的目标」。
        /// </summary>
        internal const int ExpectedPatchCount = 16;

        private sealed class PatchSpec
        {
            internal string Target;        // 日志里的目标签名
            internal string TypeName;      // 目标类型全名（走 AccessTools.TypeByName，类型缺失可优雅降级）
            internal string MethodName;    // 目标方法名
            internal Type[] Signature;     // 精确参数类型，避免重载歧义
            internal Type PrefixHolder;    // 前缀方法所在类型
            internal string PrefixName;    // 前缀方法名（private static）
        }

        /// <summary>
        /// 装全部 patch。<paramref name="pack"/> 只用于日志（条目数）；语言门禁已在上游判过，
        /// 走到这里就说明「中文 + 字典非空」，一个 patch 都不会漏装。
        /// </summary>
        internal static void InstallAll(Harmony harmony, DictPack pack, out int ok, out int failed)
        {
            ok = 0;
            failed = 0;

            // 计数器按表序创建（dump 顺序 = 表序，0 命中的目标也会出现在日志里）
            Counters.Initialize();

            PatchSpec[] specs = BuildSpecs();

            if (harmony == null)
            {
                failed = specs.Length;
                Log.Error("installed 0/" + specs.Length + " patches → Harmony instance is null");
                return;
            }

            int installed = 0;
            int errors = 0;
            for (int i = 0; i < specs.Length; i++)
            {
                Install(harmony, specs[i], ref installed, ref errors);
            }

            ok = installed;
            failed = errors;

            Log.Info("installed " + installed + "/" + specs.Length + " patches"
                     + (errors > 0 ? " (" + errors + " failed, see [UI18n] patch failed lines above)" : string.Empty));

            if (specs.Length != ExpectedPatchCount)
            {
                Log.Warn("patch table has " + specs.Length + " entries, expected " + ExpectedPatchCount
                         + " (05 §2 的 12 个 + T5b2 的 RepeatButton / DoWindow / DoModalWindow / ScreenMessages.PostMessage)");
            }
        }

        private static PatchSpec[] BuildSpecs()
        {
            var contentSignature = new[] { typeof(GUIContent), typeof(GUIStyle), typeof(GUILayoutOption[]) };

            return new[]
            {
                // ---- 文本层（uGUI 与 TMP）：前缀带 __instance 以便做字体判定 ----
                Spec("UnityEngine.UI.Text.set_text(string value)",
                    "UnityEngine.UI.Text", "set_text", new[] { typeof(string) },
                    typeof(TextPatches), "UnityTextPrefix"),
                Spec("TMPro.TMP_Text.set_text(string value)",
                    "TMPro.TMP_Text", "set_text", new[] { typeof(string) },
                    typeof(TextPatches), "TmpTextPrefix"),

                // ---- IMGUI：GUILayout 的 5 个私有实现（文本位 #0 / #0 / #0 / #1 / #3）----
                Spec("GUILayout.DoLabel(GUIContent #0, GUIStyle, GUILayoutOption[])",
                    "UnityEngine.GUILayout", "DoLabel", contentSignature,
                    typeof(ImGuiPatches), "LayoutLabelPrefix"),
                Spec("GUILayout.DoButton(GUIContent #0, GUIStyle, GUILayoutOption[])",
                    "UnityEngine.GUILayout", "DoButton", contentSignature,
                    typeof(ImGuiPatches), "LayoutButtonPrefix"),
                Spec("GUILayout.DoRepeatButton(GUIContent #0, GUIStyle, GUILayoutOption[])",
                    "UnityEngine.GUILayout", "DoRepeatButton", contentSignature,
                    typeof(ImGuiPatches), "LayoutRepeatButtonPrefix"),
                Spec("GUILayout.DoToggle(bool, GUIContent #1, GUIStyle, GUILayoutOption[])",
                    "UnityEngine.GUILayout", "DoToggle",
                    new[] { typeof(bool), typeof(GUIContent), typeof(GUIStyle), typeof(GUILayoutOption[]) },
                    typeof(ImGuiPatches), "LayoutTogglePrefix"),
                Spec("GUILayout.DoWindow(int, Rect, GUI.WindowFunction, GUIContent #3, GUIStyle, GUILayoutOption[])",
                    "UnityEngine.GUILayout", "DoWindow",
                    new[] { typeof(int), typeof(Rect), typeof(GUI.WindowFunction), typeof(GUIContent), typeof(GUIStyle), typeof(GUILayoutOption[]) },
                    typeof(ImGuiPatches), "LayoutWindowPrefix"),

                // ---- IMGUI：GUI 的公有入口（文本位 #1 / #1 / #1 / #1 / #2 / #3）----
                Spec("GUI.Label(Rect, GUIContent #1, GUIStyle)",
                    "UnityEngine.GUI", "Label", new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "LabelPrefix"),
                Spec("GUI.Box(Rect, GUIContent #1, GUIStyle)",
                    "UnityEngine.GUI", "Box", new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "BoxPrefix"),
                Spec("GUI.Button(Rect, GUIContent #1, GUIStyle)",
                    "UnityEngine.GUI", "Button", new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "ButtonPrefix"),
                // GUI.RepeatButton 公开方法（GUILayout.DoRepeatButton 里走的是它，但 mod 也可能直接调 GUI.RepeatButton）。
                Spec("GUI.RepeatButton(Rect, GUIContent #1, GUIStyle)",
                    "UnityEngine.GUI", "RepeatButton", new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "RepeatButtonPrefix"),
                Spec("GUI.Toggle(Rect, bool, GUIContent #2, GUIStyle)",
                    "UnityEngine.GUI", "Toggle", new[] { typeof(Rect), typeof(bool), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "TogglePrefix"),
                Spec("GUI.Toggle(Rect, int, bool, GUIContent #3, GUIStyle)",
                    "UnityEngine.GUI", "Toggle", new[] { typeof(Rect), typeof(int), typeof(bool), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "ToggleIntPrefix"),

                // ---- 窗口标题：两个私有漏斗（参数名 title / content，IL 实测）----
                Spec("GUI.DoWindow(int, Rect, GUI.WindowFunction, GUIContent #3 title, GUIStyle, GUISkin, bool)",
                    "UnityEngine.GUI", "DoWindow",
                    new[] { typeof(int), typeof(Rect), typeof(GUI.WindowFunction), typeof(GUIContent), typeof(GUIStyle), typeof(GUISkin), typeof(bool) },
                    typeof(ImGuiPatches), "DoWindowTitlePrefix"),
                Spec("GUI.DoModalWindow(int, Rect, GUI.WindowFunction, GUIContent #3 content, GUIStyle, GUISkin)",
                    "UnityEngine.GUI", "DoModalWindow",
                    new[] { typeof(int), typeof(Rect), typeof(GUI.WindowFunction), typeof(GUIContent), typeof(GUIStyle), typeof(GUISkin) },
                    typeof(ImGuiPatches), "DoModalWindowPrefix"),

                // ---- KSP 屏幕消息：只打 6 参私有重载（5 参版把 defaultColor 转给它，10 个公开重载全部汇到这两个）----
                Spec("ScreenMessages.PostMessage(ScreenMessage, string #1 msg, ScreenMessagesText, float, ScreenMessageStyle, Color)",
                    "ScreenMessages", "PostMessage",
                    new[] { typeof(ScreenMessage), typeof(string), typeof(ScreenMessagesText), typeof(float), typeof(ScreenMessageStyle), typeof(Color) },
                    typeof(ScreenMessagesPatches), "MessagePrefix"),
            };
        }

        private static PatchSpec Spec(string target, string typeName, string methodName, Type[] signature, Type prefixHolder, string prefixName)
        {
            var spec = new PatchSpec();
            spec.Target = target;
            spec.TypeName = typeName;
            spec.MethodName = methodName;
            spec.Signature = signature;
            spec.PrefixHolder = prefixHolder;
            spec.PrefixName = prefixName;
            return spec;
        }

        private static void Install(Harmony harmony, PatchSpec spec, ref int ok, ref int failed)
        {
            MethodInfo prefix = null;
            try
            {
                prefix = spec.PrefixHolder.GetMethod(spec.PrefixName, BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error("patch failed: " + spec.Target + " → prefix lookup threw " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (prefix == null)
            {
                failed++;
                Log.Error("patch failed: " + spec.Target + " → prefix method not found: "
                          + spec.PrefixHolder.Name + "." + spec.PrefixName);
                return;
            }

            MethodBase target = null;
            try
            {
                Type type = AccessTools.TypeByName(spec.TypeName);
                if (type == null)
                {
                    failed++;
                    Log.Error("patch failed: " + spec.Target + " → type not found: " + spec.TypeName
                              + " (game assembly changed? check KSP version)");
                    return;
                }

                target = AccessTools.Method(type, spec.MethodName, spec.Signature);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error("patch failed: " + spec.Target + " → target lookup threw " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (target == null)
            {
                failed++;
                Log.Error("patch failed: " + spec.Target + " → method not found on " + spec.TypeName
                          + " with the expected signature (KSP/Unity version mismatch?)");
                return;
            }

            // Harmony 按「参数名」把 ref 参数绑到目标方法的参数上：名字对不上不会抛，而是这次注入静默失效。
            // 这是本项目历史上最容易踩的坑（title / content / msg 三个名字各不同），所以装之前自己核对一遍
            // （__instance / __result 等魔法参数名由 PrefixParameterMatcher 白名单放行）。
            string mismatch = PrefixParameterMatcher.FindMismatch(prefix, target);
            if (mismatch != null)
            {
                failed++;
                Log.Error("patch failed: " + spec.Target + " → prefix parameter name mismatch: " + mismatch);
                return;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                ok++;
                Log.Info("patch ok: " + spec.Target);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error("patch failed: " + spec.Target + " → " + ex.Message);
            }
        }
    }
}
