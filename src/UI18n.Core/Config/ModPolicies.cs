using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UI18n.Core.Update;

namespace UI18n.Core.Config
{
    /// <summary>
    /// 一个 mod 的本地化档位（<c>packs/&lt;lang&gt;/policies.json</c> 的一行）。
    /// <para>
    /// 档位口径与 <c>tools/i18n_tools/policy.py</c> 一一对应（docs/00 §8.3）：
    /// <list type="bullet">
    /// <item><c>NATIVE</c>：mod 自带 <c>Localization/zh-cn/</c>，游戏自己能显示中文 → 运行时的 Harmony 通道不该再管它。</item>
    /// <item><c>NATIVE_PARTIAL</c>：自带 Localization 但没有 zh-cn 目录 → 由我们生成的原生 cfg 补。</item>
    /// <item><c>PARTIAL</c>：有 <c>#LOC_</c> 键但没语言目录。</item>
    /// <item><c>LEGACY</c>：纯硬编码字符串，只能靠 Harmony 拦文本出口。</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class ModPolicyEntry
    {
        /// <summary>modId（与包文件名同源，见 <see cref="UI18n.Core.Dict.PackFilter.ModIdFromPath"/>）。</summary>
        public string ModId { get; internal set; }

        /// <summary>档位字符串（原样保留数据里的写法；比较一律不区分大小写）。</summary>
        public string Policy { get; internal set; }

        /// <summary>该 mod 的原生 <c>#LOC_</c> 键数（<c>--gamedata</c> 扫描时为实测值，池推断时由池行数给出）。</summary>
        public long LocKeys { get; internal set; }

        /// <summary>证据来源：<c>gamedata</c> / <c>pool</c> / <c>none</c>。</summary>
        public string Source { get; internal set; }

        /// <summary>该 mod 的语言目录名（可能为空数组）。</summary>
        public string[] Languages { get; internal set; }

        /// <summary>该 mod 的文件夹名（<c>--gamedata</c> 扫描时才有；池推断时为空）。</summary>
        public string[] Folders { get; internal set; }

        /// <summary>档位是否为 <c>NATIVE</c>（不区分大小写）。</summary>
        public bool IsNative
        {
            get { return string.Equals(Policy, ModPolicies.PolicyNative, StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// <summary>
    /// <c>Packs/&lt;lang&gt;/policies.json</c> 的只读视图（schema <c>ui18n-policies/1</c>），
    /// 由 <c>tools/i18n_tools/policy.py</c> 生成。
    /// <para>
    /// 为什么要读它：<c>NATIVE</c> 档的 mod 游戏自己就能显示中文，我们再拦一遍文本出口纯属白烧 CPU；
    /// 更难受的是「mod 自己的中文文本恰好命中我们的英文哈希表」时会被改回别的中文（玩家眼里像随机变字）。
    /// 加载期按档案位过滤一次包文件，运行期热路径零成本（与 prefs 过滤同一套思路，见 <c>ConfigLoader.LoadPacks</c>）。
    /// </para>
    /// <para>
    /// 红线：数据缺失 / 字段非法 / 文件损坏一律降级为空表（= 老行为，所有包照旧加载），
    /// 永不抛异常、永不阻断启动。代码里不点名任何 mod —— 档位全部来自数据文件。
    /// </para>
    /// </summary>
    public sealed class ModPolicies
    {
        /// <summary>数据文件的 schema 标识（与 policy.py 的 <c>SCHEMA</c> 同值）。</summary>
        public const string SchemaId = "ui18n-policies/1";

        /// <summary>文件名（与 <c>packs/&lt;lang&gt;/display-names.json</c> 同目录）。</summary>
        public const string FileName = "policies.json";

        /// <summary>档位：自带 <c>Localization/zh-cn/</c>，游戏自己能显示中文。</summary>
        public const string PolicyNative = "NATIVE";

        /// <summary>档位：自带 Localization 但没有 zh-cn。</summary>
        public const string PolicyNativePartial = "NATIVE_PARTIAL";

        /// <summary>档位：有 <c>#LOC_</c> 键但没有语言目录。</summary>
        public const string PolicyPartial = "PARTIAL";

        /// <summary>档位：纯硬编码字符串。</summary>
        public const string PolicyLegacy = "LEGACY";

        /// <summary>无错误。</summary>
        public const string ErrorNone = "";

        /// <summary>文件不存在（正常情况：还没生成过策略表）。</summary>
        public const string ErrorNoFile = "no-file";

        /// <summary>不是合法 JSON。</summary>
        public const string ErrorBadJson = "bad-json";

        /// <summary>schema 不是 <see cref="SchemaId"/>。</summary>
        public const string ErrorBadSchema = "bad-schema";

        /// <summary>没有 <c>mods</c> 对象或它是空的。</summary>
        public const string ErrorNoMods = "no-mods";

        private static readonly ModPolicies EmptyInstance = new ModPolicies(
            new Dictionary<string, ModPolicyEntry>(StringComparer.OrdinalIgnoreCase), string.Empty);

        private readonly Dictionary<string, ModPolicyEntry> _mods;
        private readonly string _language;

        private ModPolicies(Dictionary<string, ModPolicyEntry> mods, string language)
        {
            _mods = mods ?? new Dictionary<string, ModPolicyEntry>(StringComparer.OrdinalIgnoreCase);
            _language = language ?? string.Empty;
        }

        /// <summary>空表（<c>NATIVE</c> 判定永远 false ⇒ 行为与没有策略表时完全一致）。</summary>
        public static ModPolicies Empty { get { return EmptyInstance; } }

        /// <summary>表里的 mod 数。</summary>
        public int Count { get { return _mods.Count; } }

        /// <summary>数据里的语言（<c>zh-cn</c>），缺失时为空串。</summary>
        public string Language { get { return _language; } }

        /// <summary>全部 modId（ordinal 升序，日志与面板对账用）。</summary>
        public string[] ModIds()
        {
            string[] ids = new string[_mods.Count];
            _mods.Keys.CopyTo(ids, 0);
            Array.Sort(ids, StringComparer.Ordinal);
            return ids;
        }

        /// <summary>取某个 mod 的档位；表里没有返回 false（<b>没有记录不等于 NATIVE</b>）。</summary>
        public bool TryGet(string modId, out ModPolicyEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(modId))
            {
                return false;
            }

            return _mods.TryGetValue(modId, out entry);
        }

        /// <summary>该 mod 是否 <c>NATIVE</c> 档（表里没有 / 档位是别的值都返回 false）。</summary>
        public bool IsNative(string modId)
        {
            ModPolicyEntry entry;
            return TryGet(modId, out entry) && entry.IsNative;
        }

        /// <summary>按档位计数（不区分大小写）。</summary>
        public int CountByPolicy(string policy)
        {
            if (string.IsNullOrEmpty(policy))
            {
                return 0;
            }

            int n = 0;
            foreach (KeyValuePair<string, ModPolicyEntry> pair in _mods)
            {
                if (string.Equals(pair.Value.Policy, policy, StringComparison.OrdinalIgnoreCase))
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>一行摘要，进日志用（<c>policies: mods=N (native=.., native-partial=.., partial=.., legacy=..)</c>）。</summary>
        public string Describe()
        {
            return "policies: mods=" + _mods.Count
                   + " (native=" + CountByPolicy(PolicyNative)
                   + ", native-partial=" + CountByPolicy(PolicyNativePartial)
                   + ", partial=" + CountByPolicy(PolicyPartial)
                   + ", legacy=" + CountByPolicy(PolicyLegacy) + ")";
        }

        /// <summary>
        /// 解析策略表 JSON。<b>永不抛</b>：失败时返回 <see cref="Empty"/> 并给出原因码，
        /// 调用方只记一条日志就该继续跑（策略表是优化项，不是启动前置条件）。
        /// </summary>
        public static ModPolicies Parse(string json, out string error)
        {
            error = ErrorNone;
            if (string.IsNullOrEmpty(json))
            {
                error = ErrorBadJson;
                return Empty;
            }

            JsonValue root;
            string jsonError;
            if (!Json.TryParse(json, out root, out jsonError))
            {
                error = ErrorBadJson + ": " + jsonError;
                return Empty;
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                error = ErrorBadJson + ": root-is-not-object";
                return Empty;
            }

            string schema = root.GetString("schema", null);
            if (!string.Equals(schema, SchemaId, StringComparison.Ordinal))
            {
                error = ErrorBadSchema + ": " + (schema ?? "missing");
                return Empty;
            }

            JsonValue mods = root.Get("mods");
            if (mods == null || mods.Kind == JsonKind.Null || mods.Count == 0
                || (mods.Kind != JsonKind.Object && mods.Kind != JsonKind.Array))
            {
                error = ErrorNoMods;
                return Empty;
            }

            var table = new Dictionary<string, ModPolicyEntry>(StringComparer.OrdinalIgnoreCase);
            string language = root.GetString("lang", string.Empty);

            // 主形态（policy.py 写的）："mods": { "<modId>": { policy, locKeys, source, ... } }。
            // 兼容形态：手写 / 裁剪过的小文件可能写成 "mods": [ { "modId": ..., "policy": ... } ]。
            if (mods.Kind == JsonKind.Object)
            {
                foreach (KeyValuePair<string, JsonValue> pair in mods.Members)
                {
                    AddEntry(table, pair.Value, pair.Key);
                }
            }
            else
            {
                IList<JsonValue> items = mods.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    AddEntry(table, items[i], null);
                }
            }

            if (table.Count == 0)
            {
                error = ErrorNoMods;
                return Empty;
            }

            return new ModPolicies(table, language);
        }

        private static void AddEntry(Dictionary<string, ModPolicyEntry> table, JsonValue item, string fallbackId)
        {
            if (item == null || item.Kind != JsonKind.Object)
            {
                return;
            }

            string modId = item.GetString("modId", null) ?? fallbackId;
            if (string.IsNullOrEmpty(modId))
            {
                return;
            }

            var entry = new ModPolicyEntry();
            entry.ModId = modId;
            entry.Policy = item.GetString("policy", null) ?? PolicyLegacy;
            entry.Source = item.GetString("source", null) ?? "none";
            long locKeys;
            entry.LocKeys = item.TryGetLong("locKeys", out locKeys) ? locKeys : 0;
            entry.Languages = ReadStringArray(item.Get("languages"));
            entry.Folders = ReadStringArray(item.Get("folders"));
            table[modId] = entry;
        }

        private static string[] ReadStringArray(JsonValue value)
        {
            if (value == null || value.Kind != JsonKind.Array || value.Count == 0)
            {
                return new string[0];
            }

            IList<JsonValue> items = value.Items;
            var list = new List<string>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                string s = items[i] == null ? null : items[i].AsString();
                if (!string.IsNullOrEmpty(s))
                {
                    list.Add(s);
                }
            }

            return list.ToArray();
        }

        /// <summary>读 <c>&lt;directory&gt;/policies.json</c>；目录或文件不存在返回 <see cref="ErrorNoFile"/>。</summary>
        public static ModPolicies LoadFromDirectory(string directory, out string error)
        {
            error = ErrorNone;
            if (string.IsNullOrEmpty(directory))
            {
                error = ErrorNoFile + ": empty-directory";
                return Empty;
            }

            return LoadFromFile(Path.Combine(directory, FileName), out error);
        }

        /// <summary>读指定文件；任何 IO / 编码异常都归到 <see cref="ErrorNoFile"/>，永不抛。</summary>
        public static ModPolicies LoadFromFile(string path, out string error)
        {
            error = ErrorNone;
            if (string.IsNullOrEmpty(path))
            {
                error = ErrorNoFile + ": empty-path";
                return Empty;
            }

            string text;
            try
            {
                if (!File.Exists(path))
                {
                    error = ErrorNoFile + ": " + path;
                    return Empty;
                }

                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                error = ErrorNoFile + ": " + ex.GetType().Name + ": " + ex.Message;
                return Empty;
            }

            return Parse(text, out error);
        }
    }
}
