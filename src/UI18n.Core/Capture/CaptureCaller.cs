using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace UI18n.Core.Capture
{
    /// <summary>
    /// 栈帧的极简投影（<see cref="CaptureCaller.PickCaller"/> 的输入，单测可直接构造）。
    /// </summary>
    public sealed class CaptureFrame
    {
        /// <summary>声明类型的完整名（<c>Namespace.Type</c>；全局命名空间的类型只有 <c>Type</c>）。</summary>
        public string TypeFullName { get; private set; }

        /// <summary>方法名（Harmony 生成的补丁方法形如 <c>set_text_Patch1</c>）。</summary>
        public string MethodName { get; private set; }

        /// <summary>建一帧。</summary>
        public CaptureFrame(string typeFullName, string methodName)
        {
            TypeFullName = typeFullName ?? string.Empty;
            MethodName = methodName ?? string.Empty;
        }

        /// <summary><c>Type.Method</c>（类型取简单名，与历史格式一致）。</summary>
        public override string ToString()
        {
            return CaptureCaller.SimpleTypeName(TypeFullName) + "." + MethodName;
        }
    }

    /// <summary>
    /// 取「谁第一个撞上这条未命中」—— 只在某条串<b>首次</b>未命中时调用一次。
    /// <para>
    /// <b>绝不在 miss 路径上每帧取栈</b>：<c>new StackTrace(...)</c> 在本机实测约 2 µs 量级
    /// （03 §5.1 D27 记的 2073 ns ≈ 100 次字典查表），放进每帧路径会直接毁掉 05 §4 的 17 ns 预算。
    /// </para>
    /// <para>
    /// <b>task-34 缺陷 1 的修法</b>：v1.0 只跳 <c>UI18n*</c> 命名空间，结果记下来的是我们自己的
    /// Harmony 补丁（round 8 实测 2,585 行 caller = <c>TMP_Text.TMPro.TMP_Text.set_text_Patch1</c>）。
    /// 现在沿栈逐帧判定 <see cref="ShouldSkipFrame"/>，跳过：我们自己的命名空间、<c>HarmonyLib</c> /
    /// <c>MonoMod</c>、Unity 侧（<c>UnityEngine</c> / <c>Unity</c> / <c>TMPro</c>）、BCL（<c>System</c>），
    /// 以及任何 <c>*_PatchN</c> 形态的 Harmony 生成方法；取第一个剩下的真实调用者。
    /// 全部跳完返回 <see cref="Unknown"/>（<c>unknown</c>），<b>永不抛</b>。
    /// </para>
    /// <para>
    /// 为什么连 Unity 侧一起跳：<c>GUILayout.DoLabel</c> → <c>GUI.Label</c> → … 这些是我们自己
    /// 也打了补丁的引擎转发层，对「这条文本来自哪个 mod」没有信息量；真正有用的是它后面那个
    /// mod 方法（例如 <c>RCSBuildAid.RCSBuildAidEditor.OnGUI</c>）。
    /// </para>
    /// </summary>
    public static class CaptureCaller
    {
        /// <summary>我们自己的命名空间前缀（历史常量，保留以兼容旧调用点）。</summary>
        public const string SkipNamespacePrefix = "UI18n";

        /// <summary>取不到真实调用者时的占位值。</summary>
        public const string Unknown = "unknown";

        /// <summary>
        /// 一律跳过的命名空间前缀（按 <c>前缀</c> 或 <c>前缀.</c> 匹配，大小写敏感）。
        /// <para>
        /// <c>System</c> 是为了跳过 BCL 转发（<c>System.Reflection</c> 等）；
        /// mod 与 KSP 自己的类型都不在这些命名空间下，所以不会误伤。
        /// </para>
        /// </summary>
        public static readonly string[] SkipNamespacePrefixes =
        {
            "UI18n",
            "HarmonyLib",
            "MonoMod",
            "UnityEngine",
            "Unity",
            "TMPro",
            "System"
        };

        /// <summary>Harmony 生成方法名的标记（<c>set_text_Patch1</c>、<c>OnGUI_Patch3</c>）。</summary>
        public const string HarmonyMethodMarker = "_Patch";

        /// <summary>从当前栈取调用者（<paramref name="skipFrames"/> 额外跳过的帧数）。</summary>
        public static string FromStack(int skipFrames)
        {
            try
            {
                StackTrace trace = new StackTrace(skipFrames, false);
                int count = trace.FrameCount;
                if (count == 0)
                {
                    return Unknown;
                }

                var frames = new List<CaptureFrame>(count);
                for (int i = 0; i < count; i++)
                {
                    MethodBase method = trace.GetFrame(i).GetMethod();
                    if (method == null)
                    {
                        continue;
                    }

                    Type declaringType = method.DeclaringType;
                    frames.Add(new CaptureFrame(declaringType == null ? null : declaringType.FullName, method.Name));
                }

                return PickCaller(frames);
            }
            catch (Exception)
            {
                return Unknown;
            }
        }

        /// <summary>
        /// 按 <see cref="ShouldSkipFrame"/> 过滤后取第一帧的 <c>Type.Method</c>；没有则 <see cref="Unknown"/>。
        /// </summary>
        public static string PickCaller(IList<CaptureFrame> frames)
        {
            if (frames == null)
            {
                return Unknown;
            }

            for (int i = 0; i < frames.Count; i++)
            {
                CaptureFrame frame = frames[i];
                if (frame == null)
                {
                    continue;
                }

                if (ShouldSkipFrame(frame.TypeFullName, frame.MethodName))
                {
                    continue;
                }

                return SimpleTypeName(frame.TypeFullName) + "." + frame.MethodName;
            }

            return Unknown;
        }

        /// <summary>
        /// 这一帧该跳吗：命名空间在 <see cref="SkipNamespacePrefixes"/> 里、方法名含
        /// <see cref="HarmonyMethodMarker"/>、类型名含 <c>Patch</c>（Harmony 的辅助类），
        /// 或者类型/方法名缺失（Harmony 动态方法的声明类型可能为 null）。
        /// </summary>
        public static bool ShouldSkipFrame(string typeFullName, string methodName)
        {
            if (string.IsNullOrEmpty(typeFullName) || string.IsNullOrEmpty(methodName))
            {
                return true;
            }

            if (methodName.IndexOf(HarmonyMethodMarker, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (typeFullName.IndexOf("Patch", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            for (int i = 0; i < SkipNamespacePrefixes.Length; i++)
            {
                string prefix = SkipNamespacePrefixes[i];
                if (typeFullName.Length < prefix.Length)
                {
                    continue;
                }

                if (string.CompareOrdinal(typeFullName, 0, prefix, 0, prefix.Length) != 0)
                {
                    continue;
                }

                if (typeFullName.Length == prefix.Length || typeFullName[prefix.Length] == '.')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>类型的简单名（去掉命名空间；嵌套类型保留 <c>+</c>）。</summary>
        public static string SimpleTypeName(string typeFullName)
        {
            if (string.IsNullOrEmpty(typeFullName))
            {
                return string.Empty;
            }

            int dot = typeFullName.LastIndexOf('.');
            return dot < 0 ? typeFullName : typeFullName.Substring(dot + 1);
        }
    }
}
