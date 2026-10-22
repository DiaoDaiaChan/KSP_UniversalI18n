using System;
using System.Collections.Generic;
using System.Reflection;

namespace UI18n.Core.Patching
{
    /// <summary>
    /// Harmony 前缀参数名核对（纯逻辑，无 Unity 依赖，可单测）。
    /// <para>
    /// 为什么需要它：Harmony 2.x <b>按参数名</b>把 <c>ref</c> 参数绑到目标方法的参数上。名字对不上<b>不会抛异常</b>，
    /// 而是这次注入静默失效 —— 日志里只有一行「patch ok」，而线上永远不生效。所以装 patch 之前先自己核对一遍，
    /// 对不上直接算安装失败并给出明确原因（红线 6：失败要计数 + 说清为什么）。
    /// </para>
    /// <para>
    /// 但前缀方法里允许出现 Harmony 的<b>魔法参数名</b>（<c>__instance</c> / <c>__result</c> / <c>___someField</c> …），
    /// 它们不是目标方法的参数，必须放行，否则守卫会把正确的 patch 判成失败。
    /// </para>
    /// </summary>
    public static class PrefixParameterMatcher
    {
        /// <summary>
        /// Harmony 2.x 文档里的魔法参数名（<b>精确匹配，Ordinal</b>）。故意只收已知名字：
        /// 这样 <c>__instnace</c> 这种拼写错误仍会被判成 mismatch，而不是悄悄漏过去。
        /// </summary>
        public static readonly string[] KnownMagicParameterNames =
        {
            "__instance",
            "__result",
            "__resultRef",
            "__state",
            "__args",
            "__originalMethod",
            "__runOriginal",
        };

        /// <summary>字段注入前缀：<c>___field</c>（三个下划线）可以取到目标的字段/属性。</summary>
        public const string FieldInjectionPrefix = "___";

        /// <summary>参数注入前缀：<c>__0</c> / <c>__1</c> …（两个下划线 + 纯数字）可以按序号取目标参数。</summary>
        public const string IndexInjectionPrefix = "__";

        /// <summary>
        /// 是否是 Harmony 注入的魔法参数名（<see cref="KnownMagicParameterNames"/>、<c>___field</c>、<c>__0</c> 这类）。
        /// <c>null</c> / 空串返回 <c>false</c>。
        /// </summary>
        public static bool IsMagicParameterName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (name.StartsWith(FieldInjectionPrefix, StringComparison.Ordinal))
            {
                return true;
            }

            for (int i = 0; i < KnownMagicParameterNames.Length; i++)
            {
                if (string.Equals(KnownMagicParameterNames[i], name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return IsIndexParameterName(name);
        }

        /// <summary>
        /// 核对前缀方法的每个参数名是否都能在目标方法的参数名里找到（魔法参数名放行）。
        /// 全部找到返回 <c>null</c>；否则返回第一条失败说明，格式固定为
        /// <c>'name' not in target parameters (a, b, c)</c>（该文本是日志契约的一部分，测试按它断言）。
        /// </summary>
        public static string FindMismatch(MethodInfo prefix, MethodBase target)
        {
            if (prefix == null)
            {
                return "prefix method is null";
            }

            if (target == null)
            {
                return "target method is null";
            }

            return FindMismatch(Names(prefix.GetParameters()), Names(target.GetParameters()));
        }

        /// <summary>
        /// 同上，只吃名字（便于单测：不需要真的反射出一个目标方法）。
        /// <paramref name="prefixParameterNames"/> 里为 <c>null</c> 的项视为「无名参数」，直接判失败。
        /// </summary>
        public static string FindMismatch(IEnumerable<string> prefixParameterNames, IEnumerable<string> targetParameterNames)
        {
            if (prefixParameterNames == null)
            {
                return "prefix has no parameters";
            }

            if (targetParameterNames == null)
            {
                return "target has no parameters";
            }

            List<string> targets = new List<string>(targetParameterNames);
            string available = Join(targets);

            foreach (string name in prefixParameterNames)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return "(unnamed prefix parameter) not in target parameters (" + available + ")";
                }

                if (IsMagicParameterName(name))
                {
                    continue;
                }

                bool found = false;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (string.Equals(targets[i], name, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return "'" + name + "' not in target parameters (" + available + ")";
                }
            }

            return null;
        }

        private static string[] Names(ParameterInfo[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
            {
                return new string[0];
            }

            string[] names = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                names[i] = parameters[i] == null ? null : parameters[i].Name;
            }

            return names;
        }

        private static string Join(List<string> values)
        {
            string s = string.Empty;
            for (int i = 0; i < values.Count; i++)
            {
                s += (i == 0 ? string.Empty : ", ") + (values[i] ?? "<unnamed>");
            }

            return s;
        }

        /// <summary><c>__</c> + 纯数字（Harmony 的按序号参数注入）。</summary>
        private static bool IsIndexParameterName(string name)
        {
            if (!name.StartsWith(IndexInjectionPrefix, StringComparison.Ordinal) || name.Length <= IndexInjectionPrefix.Length)
            {
                return false;
            }

            for (int i = IndexInjectionPrefix.Length; i < name.Length; i++)
            {
                char c = name[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
