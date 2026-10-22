using System;
using System.Reflection;
using HarmonyLib;
using UI18n.Core.Dict;
using UnityEngine;

namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// patch 注册表：16 个 hook 目标逐个安装，每个单独 try/catch。
    /// 任何一个失败都只记一条 <c>[UI18n] patch failed: {目标} → {原因}</c> 并继续装下一个（红线 6：
    /// 失败只 log 不抛，但必须计数并给出明确原因）。
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
                // ---- 文本层（uGUI 与 TMP）----
                Spec("UnityEngine.UI.Text.set_text(string value)",
                    "UnityEngine.UI.Text", "set_text", new[] { typeof(string) },
                    typeof(TextPatches), "UnityTextPrefix"),
                Spec("TMPro.TMP_Text.set_text(string value)",
                    "TMPro.TMP_Text", "set_text", new[] { typeof(string) },
                    typeof(TextPatches), "TmpTextPrefix"),

                // ---- IMGUI：GUILayout 的 5 个私有实现（文本位 #0 / #0 / #0 / #1 / #3）----
                Spec("GUILayout.DoLabel(GUIContent #0, GUIStyle, GUILayoutOption[])",
                    "UnityEngine.GUILayout", "DoLabel", contentSignature,
                    typeof(ImGuiPatches), "ContentPrefix"),
                Spec("GUILayout.DoButton(GUIContent #0, GUIStyle, GUILayoutOption[])",
                    "UnityEngine.GUILayout", "DoButton", contentSignature,
                    typeof(ImGuiPatches), "ContentPrefix"),
                Spec("GUILayout.DoRepeatButton(GUIContent #0, GUIStyle, GUILayoutOption[])",
                    "UnityEngine.GUILayout", "DoRepeatButton", contentSignature,
                    typeof(ImGuiPatches), "ContentPrefix"),
                Spec("GUILayout.DoToggle(bool, GUIContent #1, GUIStyle, GUILayoutOption[])",
                    "UnityEngine.GUILayout", "DoToggle",
                    new[] { typeof(bool), typeof(GUIContent), typeof(GUIStyle), typeof(GUILayoutOption[]) },
                    typeof(ImGuiPatches), "ContentPrefix"),
                Spec("GUILayout.DoWindow(int, Rect, GUI.WindowFunction, GUIContent #3, GUIStyle, GUILayoutOption[])",
                    "UnityEngine.GUILayout", "DoWindow",
                    new[] { typeof(int), typeof(Rect), typeof(GUI.WindowFunction), typeof(GUIContent), typeof(GUIStyle), typeof(GUILayoutOption[]) },
                    typeof(ImGuiPatches), "ContentPrefix"),

                // ---- IMGUI：GUI 的公有入口（文本位 #1 / #1 / #1 / #2 / #3）----
                Spec("GUI.Label(Rect, GUIContent #1, GUIStyle)",
                    "UnityEngine.GUI", "Label", new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "ContentPrefix"),
                Spec("GUI.Box(Rect, GUIContent #1, GUIStyle)",
                    "UnityEngine.GUI", "Box", new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "ContentPrefix"),
                Spec("GUI.Button(Rect, GUIContent #1, GUIStyle)",
                    "UnityEngine.GUI", "Button", new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "ContentPrefix"),
                Spec("GUI.Toggle(Rect, bool, GUIContent #2, GUIStyle)",
                    "UnityEngine.GUI", "Toggle", new[] { typeof(Rect), typeof(bool), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "ContentPrefix"),
                Spec("GUI.Toggle(Rect, int, bool, GUIContent #3, GUIStyle)",
                    "UnityEngine.GUI", "Toggle", new[] { typeof(Rect), typeof(int), typeof(bool), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "ContentPrefix"),

                // ---- IMGUI：T5b2 补的 3 个公有入口（文本位 #1 / #3 / #3）----
                // GUI.RepeatButton 公开方法（GUILayout.DoRepeatButton 里走的是它，但 mod 也可能直接调 GUI.RepeatButton）。
                Spec("GUI.RepeatButton(Rect, GUIContent #1, GUIStyle)",
                    "UnityEngine.GUI", "RepeatButton", new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) },
                    typeof(ImGuiPatches), "ContentPrefix"),
                // 窗口标题走**私有漏斗** GUI.DoWindow：它同时是 GUI.Window(GUIContent) 与 GUI.Window(string) 的去处
                // （string 重载直接 DoWindow(…, GUIContent.Temp(text), …)，不经公开 GUIContent 重载），
                // 所以打它一个就覆盖全部 Window 重载。参数名 title → TitlePrefix。
                Spec("GUI.DoWindow(int, Rect, GUI.WindowFunction, GUIContent #3 title, GUIStyle, GUISkin, bool)",
                    "UnityEngine.GUI", "DoWindow",
                    new[] { typeof(int), typeof(Rect), typeof(GUI.WindowFunction), typeof(GUIContent), typeof(GUIStyle), typeof(GUISkin), typeof(bool) },
                    typeof(ImGuiPatches), "TitlePrefix"),
                // ModalWindow 走的是**另一条**漏斗 GUI.DoModalWindow（经 Internal_DoModalWindow，不经 DoWindow）；
                // 它的文本参数名是 content（System.Reflection.Metadata 实测，不是 title）→ 复用 ContentPrefix。
                Spec("GUI.DoModalWindow(int, Rect, GUI.WindowFunction, GUIContent #3 content, GUIStyle, GUISkin)",
                    "UnityEngine.GUI", "DoModalWindow",
                    new[] { typeof(int), typeof(Rect), typeof(GUI.WindowFunction), typeof(GUIContent), typeof(GUIStyle), typeof(GUISkin) },
                    typeof(ImGuiPatches), "ContentPrefix"),

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

        /// <summary>
        /// 核对前缀方法的每个参数名是否都能在目标方法的参数名里找到；全部找到返回 null，否则返回第一个对不上的名字。
        /// （前缀只允许出现目标方法自己的参数名；本项目的每个前缀都恰好只有一个 <c>ref</c> 参数。）
        /// </summary>
        private static string FindParamNameMismatch(MethodInfo prefix, MethodBase target)
        {
            ParameterInfo[] prefixParams = prefix.GetParameters();
            ParameterInfo[] targetParams = target.GetParameters();

            for (int i = 0; i < prefixParams.Length; i++)
            {
                string name = prefixParams[i].Name;
                bool found = false;
                for (int j = 0; j < targetParams.Length; j++)
                {
                    if (string.Equals(targetParams[j].Name, name, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    string available = string.Empty;
                    for (int j = 0; j < targetParams.Length; j++)
                    {
                        available += (j == 0 ? string.Empty : ", ") + targetParams[j].Name;
                    }

                    return "'" + name + "' not in target parameters (" + available + ")";
                }
            }

            return null;
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
            // 这是本类历史上最容易踩的坑（title / content / msg 三个名字各不同），所以装之前自己核对一遍，
            // 对不上就直接算安装失败并给出明确原因（红线 6：失败要计数 + 说清为什么）。
            string mismatch = FindParamNameMismatch(prefix, target);
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
