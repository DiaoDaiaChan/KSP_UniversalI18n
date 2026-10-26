using System;
using System.Collections.Generic;

namespace UI18n.Core.Submission
{
    /// <summary>
    /// caller → modId 的归属判定（§6.1 的 <c>entries[].mod</c>）。
    /// <para>
    /// 口径必须与 <c>tools/i18n_tools/capture_ingest.py</c> 的 <c>infer_mods()</c> 一致：
    /// **拿 caller 的第一段当 mod 名**。这里多一步「双向最长前缀」是为了救
    /// <c>AtmosphereAutopilot.UI</c> 这类带点的 modId —— 它的 caller 形如
    /// <c>AtmosphereAutopilot.UI.MainWindow.OnGUI</c>，第一段只有 <c>AtmosphereAutopilot</c>，
    /// 精确匹配配不上，但「该段是 modId 的前缀」能配上。
    /// </para>
    /// <para>
    /// 不用 <c>packs/mods.json</c> 的 <c>assemblies</c>/<c>folders</c>/<c>aliases</c>：
    /// 那三张表现在还是 bootstrap 占位（全空），依赖它就是把归属全打到 Unattributed 上。
    /// </para>
    /// <para>本类纯函数、不抛异常、不依赖 UnityEngine（Core 契约）。</para>
    /// </summary>
    public sealed class ModResolver
    {
        /// <summary>归属失败时的伪 modId（与运行时 pack 同名，玩家可以在面板里单独关掉它）。</summary>
        public const string Unattributed = "UI18n-Unattributed";

        private readonly Dictionary<string, string> _exact =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _byLength = new List<string>();

        /// <summary>构造：吃一批 modId（已装语言包文件名派生出来的那些）。</summary>
        public ModResolver(IEnumerable<string> modIds)
        {
            if (modIds == null)
            {
                return;
            }

            foreach (string modId in modIds)
            {
                if (string.IsNullOrEmpty(modId) || _exact.ContainsKey(modId))
                {
                    continue;
                }

                _exact[modId] = modId;
                _byLength.Add(modId);
            }

            _byLength.Sort(delegate(string left, string right)
            {
                int byLength = right.Length.CompareTo(left.Length);
                return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
            });
        }

        /// <summary>登记的 modId 个数。</summary>
        public int Count
        {
            get { return _byLength.Count; }
        }

        /// <summary>把 caller 归到某个 modId；归不上返回 <see cref="Unattributed"/>。</summary>
        public string Resolve(string caller)
        {
            if (string.IsNullOrEmpty(caller))
            {
                return Unattributed;
            }

            string segment = FirstSegment(caller);
            if (string.IsNullOrEmpty(segment))
            {
                return Unattributed;
            }

            string exact;
            if (_exact.TryGetValue(segment, out exact))
            {
                return exact;
            }

            for (int i = 0; i < _byLength.Count; i++)
            {
                string modId = _byLength[i];
                if (modId.Length > segment.Length)
                {
                    if (modId.StartsWith(segment, StringComparison.OrdinalIgnoreCase))
                    {
                        return modId;
                    }
                }
                else if (modId.Length < segment.Length)
                {
                    if (segment.StartsWith(modId, StringComparison.OrdinalIgnoreCase))
                    {
                        return modId;
                    }
                }
            }

            return Unattributed;
        }

        /// <summary>便捷：一次性解析（会给每个 caller 重建索引，只适合低频路径）。</summary>
        public static string Resolve(string caller, IList<string> modIds)
        {
            return new ModResolver(modIds).Resolve(caller);
        }

        /// <summary>caller 的第一段（第一个 <c>.</c> 之前；没有点就是整串）。</summary>
        public static string FirstSegment(string caller)
        {
            if (string.IsNullOrEmpty(caller))
            {
                return string.Empty;
            }

            int dot = caller.IndexOf('.');
            return dot < 0 ? caller : caller.Substring(0, dot);
        }
    }
}
