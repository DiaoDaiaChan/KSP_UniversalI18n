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

        /// <summary><c>Type.Method</c>（类型用完整名 <see cref="TypeFullName"/>，task-42 起不再截成简单名）。</summary>
        public override string ToString()
        {
            return TypeFullName + "." + MethodName;
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
                    frames.Add(new CaptureFrame(declaringType == null ? null : CleanTypeName(declaringType), method.Name));
                }

                return PickCaller(frames);
            }
            catch (Exception)
            {
                return Unknown;
            }
        }

        /// <summary>
        /// 按 <see cref="ShouldSkipFrame"/> 过滤后取第一帧的 <c>Namespace.Type.Method</c>；没有则 <see cref="Unknown"/>。
        /// <para>
        /// <b>task-42</b>：导出用<b>完整</b>类型名而不是简单名 —— 下游 <c>tools/i18n_tools/capture_ingest.py</c>
        /// 的 <c>infer_mods()</c> 拿 caller 的第一段当 mod 名、<c>attribute_callers()</c> 用 <c>rpartition(".")</c>
        /// 把 base 与 method 拆开，两边都按「Namespace.Type.Method」设计；只留简单名会让 mod 名退化成类型名。
        /// </para>
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

                return frame.TypeFullName + "." + frame.MethodName;
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

        /// <summary>
        /// 从反射拿到的类型拼出<b>干净</b>的完整名：<c>命名空间.外层类型.自身</c>，泛型去掉 arity（<c>`1</c>）。
        /// <para>
        /// <b>为什么不用 <see cref="Type.FullName"/></b>：对已构造的泛型类型它返回程序集限定名，
        /// 例如 <c>Dictionary`2[[System.String, mscorlib, Version=…, Culture=neutral, PublicKeyToken=…]]</c>，
        /// 再被截断就留下 <c>…PublicKeyToken=null]].X</c> 这种残片（v1.1 实测 5 条）。
        /// 这里只用 <see cref="Type.Name"/> 与 <see cref="Type.DeclaringType"/> 逐段拼，
        /// 结构上不可能带出程序集信息、逗号、方括号或反引号。
        /// </para>
        /// <para>嵌套类型用 <c>.</c> 连接（不是反射的 <c>+</c>），与下游 python 的 <c>rpartition(".")</c> 口径一致。</para>
        /// </summary>
        public static string CleanTypeName(Type type)
        {
            if (type == null)
            {
                return string.Empty;
            }

            string simple = StripArity(type.Name);
            Type declaring = type.DeclaringType;
            if (declaring != null)
            {
                string outer = CleanTypeName(declaring);
                return outer.Length == 0 ? simple : outer + "." + simple;
            }

            string ns = type.Namespace;
            return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
        }

        /// <summary>去掉泛型 arity 后缀（<c>List`1</c> → <c>List</c>、<c>Outer`2</c> → <c>Outer</c>）。</summary>
        public static string StripArity(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            int tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        /// <summary>
        /// 一个方法的「类型.方法」描述（<see cref="CleanTypeName"/> + 方法名）；取不到时返回空串。
        /// <para>这是 <see cref="FromStack"/> 内部投影栈帧用的同一套命名，单测可直接断言它不含程序集信息。</para>
        /// </summary>
        public static string DescribeMethod(MethodBase method)
        {
            if (method == null)
            {
                return string.Empty;
            }

            string type = CleanTypeName(method.DeclaringType);
            return type.Length == 0 ? method.Name : type + "." + method.Name;
        }

        /// <summary>
        /// 类型的简单名（只取最后一个 <c>.</c> 之后那一段）。
        /// <para>
        /// <b>task-42 起导出 caller 不再用它</b>（见 <see cref="PickCaller"/> 的说明）：保留是因为它仍是
        /// 「这段全名里哪部分是类型」的常用小工具，删掉会让外部调用点编译不过。
        /// </para>
        /// </summary>
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
