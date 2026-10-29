using System;
using System.Collections.Generic;
using System.IO;
using UI18n.Core.Config;
using UI18n.Core.Settings;

namespace UI18n.Core.Dict
{
    /// <summary>
    /// 包文件名 ⇄ modId 的<b>唯一</b>换算规则，以及「按 prefs 过滤包列表」。
    /// <para>
    /// 派生规则（Lead 用 97 个真实包实测拍板，<b>逐字实现，别再改</b>）：
    /// <list type="number">
    /// <item>去掉末尾 <c>.jsonl</c>（不区分大小写）。</item>
    /// <item>若剩下部分以 <c>.il</c> 或 <c>.cfg</c> 结尾，<b>只去掉这个后缀</b>
    /// （这两个 channel 是已知全集，没有别的；且只剥一次）。</item>
    /// <item>其余原样就是 modId。</item>
    /// </list>
    /// 实测：97 个文件 → 82 个唯一 modId，其中 15 个 modId 有两个 channel 文件
    /// （<c>BurstPQS.il.jsonl</c> + <c>BurstPQS.jsonl</c>）⇒ <b>同一 modId 的两个 channel 共享一个开关</b>。
    /// </para>
    /// <para>
    /// <b>陷阱</b>：<c>AtmosphereAutopilot.UI.il.jsonl</c> 的 modId 是 <c>AtmosphereAutopilot.UI</c>
    /// —— modId <b>可以含点号</b>，所以「剥掉最后一个点号段」是错的，只能剥精确后缀。
    /// </para>
    /// <para>
    /// 为什么放在 Core 而不是 Runtime：这段是纯字符串运算，放进 Runtime 就没法单测
    /// （Runtime 依赖 UnityEngine / KSP 程序集，本机跑不起来），而它一旦算错，
    /// 表现是「玩家关掉一个 mod，另一个 mod 的包也跟着不加载」——只能靠单测穷举。
    /// </para>
    /// </summary>
    public static class PackFilter
    {
        /// <summary>包文件扩展名（与 Update/ZipSafety 里的同名常量同值，这里不引用它以免 Core 内部耦合）。</summary>
        public const string PackExtension = ".jsonl";

        /// <summary>已知 channel 后缀全集的替身（<c>x.il.jsonl</c> / <c>x.cfg.jsonl</c>）。</summary>
        private static readonly string[] ChannelSuffixes = new string[] { ".il", ".cfg" };

        /// <summary>
        /// 由包文件路径（或纯文件名）推 modId。任何输入都不抛：null / 空 → 空串；
        /// 不是 <c>.jsonl</c> 的文件也照样返回主干名（调用方自己决定要不要用）。
        /// </summary>
        public static string ModIdFromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            int cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            string name = cut >= 0 ? path.Substring(cut + 1) : path;
            return ModIdFromFileName(name);
        }

        /// <summary>
        /// 由纯文件名推 modId（<c>Foo.il.jsonl</c> → <c>Foo</c>，<c>Bar.cfg.jsonl</c> → <c>Bar</c>，
        /// <c>Baz.jsonl</c> → <c>Baz</c>，<c>AtmosphereAutopilot.UI.il.jsonl</c> → <c>AtmosphereAutopilot.UI</c>）。
        /// </summary>
        public static string ModIdFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }

            string name = fileName;
            if (name.EndsWith(PackExtension, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - PackExtension.Length);
            }

            for (int i = 0; i < ChannelSuffixes.Length; i++)
            {
                string suffix = ChannelSuffixes[i];
                if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                    break;
                }
            }

            return name;
        }

        /// <summary>
        /// modId 合法性：允许字母数字与 <c>_ - .</c>（真实例子里有 <c>000_TexturesUnlimited</c>、
        /// <c>B9-PWings-Fork</c>、<c>AtmosphereAutopilot.UI</c>）；不许空、不许首尾点、不许出现 <c>..</c>。
        /// </summary>
        public static bool IsValidModId(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return false;
            }

            if (modId[0] == '.' || modId[modId.Length - 1] == '.')
            {
                return false;
            }

            if (modId.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            for (int i = 0; i < modId.Length; i++)
            {
                char c = modId[i];
                bool ok = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || c == '_' || c == '-' || c == '.';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        private static readonly Dictionary<string, string> _customDisplayNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 注册自定义 mod 显示名称（如从 mods.json 加载的 displayName，或运行时插件注册）。
        /// </summary>
        public static void RegisterDisplayName(string modId, string displayName)
        {
            if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(displayName))
            {
                return;
            }

            lock (_customDisplayNames)
            {
                _customDisplayNames[modId] = displayName;
            }
        }

        /// <summary>
        /// 批量注册 mod 显示名称。
        /// </summary>
        public static void RegisterDisplayNames(IEnumerable<KeyValuePair<string, string>> mappings)
        {
            if (mappings == null)
            {
                return;
            }

            lock (_customDisplayNames)
            {
                foreach (KeyValuePair<string, string> kvp in mappings)
                {
                    if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                    {
                        _customDisplayNames[kvp.Key] = kvp.Value;
                    }
                }
            }
        }

        /// <summary>
        /// 清除已注册的自定义显示名称（主要用于单测隔离）。
        /// </summary>
        public static void ClearCustomDisplayNames()
        {
            lock (_customDisplayNames)
            {
                _customDisplayNames.Clear();
            }
        }

        /// <summary>本产品自造的伪 mod 前缀（<c>UI18n-Unattributed</c> / <c>UI18n-ConfigCache</c>）。</summary>
        public const string InternalPackPrefix = "UI18n-";

        /// <summary>
        /// 是否本产品自造的伪 mod（不是任何第三方 mod）。**只按前缀判断，不列举具体名字**：
        /// 新增一个 <c>UI18n-Foo</c> 通道不用改这里。界面文案由 UI 层（PrefsPanel 的 <c>T()</c>）负责。
        /// </summary>
        public static bool IsInternalPack(string modId)
        {
            if (string.IsNullOrEmpty(modId) || modId.Length <= InternalPackPrefix.Length)
            {
                return false;
            }

            return modId.StartsWith(InternalPackPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 面板与日志里给玩家看的名字。**只认数据**：<see cref="PackDisplayNames"/> 读进来的
        /// <c>Packs/&lt;lang&gt;/display-names.json</c>，或运行时 <see cref="RegisterDisplayName"/> 注册的映射。
        /// <para>
        /// 这里<b>不内置任何 mod 名</b>、<b>也不内置任何界面文案</b>（v1.7 去硬编码：原来写死
        /// 「<c>UI18n-Unattributed</c> → 未归属文本」「<c>Squad</c> → 本体」三条 if）。没有登记就原样回 modId，
        /// 伪 mod 的人话名字由 UI 层配合 <see cref="IsInternalPack"/> 补，别在这里分叉。
        /// </para>
        /// </summary>
        public static string DisplayName(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return string.Empty;
            }

            lock (_customDisplayNames)
            {
                string custom;
                if (_customDisplayNames.TryGetValue(modId, out custom))
                {
                    return custom;
                }
            }

            return modId;
        }

        /// <summary>
        /// 按 prefs 过滤包文件列表，<b>保持输入顺序</b>（PackLocator 已按文件名 ordinal 升序，
        /// 顺序变了会让「同一 h 出现在多个包里」的取舍结果漂移，见 docs/10 §10）。
        /// <paramref name="prefs"/> 为 null 视为全开（老调用方与单测走这条路）。
        /// </summary>
        public static string[] SelectEnabled(string[] files, Prefs prefs, out int disabledCount)
        {
            disabledCount = 0;
            if (files == null || files.Length == 0)
            {
                return new string[0];
            }

            if (prefs == null)
            {
                string[] all = new string[files.Length];
                Array.Copy(files, all, files.Length);
                return all;
            }

            List<string> enabled = new List<string>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                if (file == null)
                {
                    continue;
                }

                string modId = ModIdFromPath(file);
                if (prefs.IsEnabled(modId))
                {
                    enabled.Add(file);
                }
                else
                {
                    disabledCount++;
                }
            }

            return enabled.ToArray();
        }

        /// <summary>
        /// 按策略表（<see cref="ModPolicies"/>）过滤包文件列表，<b>保持输入顺序</b>。
        /// <para>
        /// 目前只滤掉 <c>NATIVE</c> 档：那种 mod 自带 <c>Localization/zh-cn/</c>，游戏自己能显示中文，
        /// 我们再拿字典拦一遍文本出口纯属白烧 CPU（还可能把它自己的中文文本按哈希改错）。
        /// 其余三档（<c>NATIVE_PARTIAL</c> / <c>PARTIAL</c> / <c>LEGACY</c>）都还要靠 Harmony 通道，一律保留。
        /// </para>
        /// <para>
        /// <paramref name="policies"/> 为 <c>null</c> 或空表 ⇒ 原样返回（老行为）。
        /// 表里查不到的 mod 视为非 NATIVE —— <b>没有记录不等于原生</b>，缺数据时绝不静默少加载。
        /// </para>
        /// </summary>
        public static string[] SelectByPolicy(string[] files, ModPolicies policies, out int nativeCount)
        {
            nativeCount = 0;
            if (files == null || files.Length == 0)
            {
                return new string[0];
            }

            if (policies == null || policies.Count == 0)
            {
                string[] all = new string[files.Length];
                Array.Copy(files, all, files.Length);
                return all;
            }

            List<string> kept = new List<string>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                if (file == null)
                {
                    continue;
                }

                if (policies.IsNative(ModIdFromPath(file)))
                {
                    nativeCount++;
                }
                else
                {
                    kept.Add(file);
                }
            }

            return kept.ToArray();
        }

        /// <summary>
        /// 从包文件列表里取出全部 modId（ordinal 去重排序），面板列清单用。
        /// </summary>
        public static string[] DistinctModIds(string[] files)
        {
            if (files == null || files.Length == 0)
            {
                return new string[0];
            }

            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            List<string> ids = new List<string>();
            for (int i = 0; i < files.Length; i++)
            {
                string modId = ModIdFromPath(files[i]);
                if (modId.Length == 0 || seen.ContainsKey(modId))
                {
                    continue;
                }

                seen[modId] = true;
                ids.Add(modId);
            }

            ids.Sort(StringComparer.Ordinal);
            return ids.ToArray();
        }

        /// <summary>
        /// 由 modId 反推该 mod 的包文件名主干前缀（面板显示 <c>条目=N</c>、日志对账用）。
        /// 只用于展示，不参与加载决策。
        /// </summary>
        public static string FileStemOf(string modId)
        {
            return string.IsNullOrEmpty(modId) ? string.Empty : modId;
        }
    }
}
