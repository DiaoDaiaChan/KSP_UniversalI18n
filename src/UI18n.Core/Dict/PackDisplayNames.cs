using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UI18n.Core.Update;

namespace UI18n.Core.Dict
{
    /// <summary>
    /// 面板显示名的<b>数据表</b>：<c>&lt;Packs&gt;/&lt;lang&gt;/display-names.json</c>。
    /// <para>
    /// 为什么要这张表：显示名是**内容**，不是逻辑。过去 <see cref="PackFilter.DisplayName"/> 里写死了
    /// 「<c>UI18n-Unattributed</c> → 未归属文本」「<c>Squad</c> → 本体」三条 if —— 那是把一个具体 mod 的
    /// 名字和一段中文文案钉进 Core（Core 是数据无关的核），换个 mod、换种语言都得改代码 + 重新编译。
    /// 现在这些名字住在语言包里的 JSON，运行期读进来注册到 <see cref="PackFilter.RegisterDisplayNames"/>，
    /// Core 里只剩「查表 → 回退 modId」两条规则。
    /// </para>
    /// <para>
    /// 文件形态（单个 JSON 对象，<b>不是 JSONL</b>；所以它永远不会被 <see cref="PackLocator"/> 当成语言包）：
    /// <code>
    /// {"schema":"ui18n-display-names/1",
    ///  "names":{"Squad":"本体","UI18n-Unattributed":"未归属文本"}}
    /// </code>
    /// 键是 modId（<b>稳定键</b>，见 docs/10 §8），值是给玩家看的名字；值是空串 / 非字符串的条目跳过。
    /// 顶层没有 <c>names</c> 这层时，整个对象直接当映射用（两种写法都认，<c>names</c> 那层留给
    /// 将来的 <c>schema</c> / 注释键）。
    /// </para>
    /// <para>
    /// 本类纯 BCL、<b>绝不抛</b>（红线 6）：文件不存在 / 权限不足 / JSON 坏都只是「少注册几条」，
    /// 返回值是实际注册成功的条数，调用方据此决定要不要打日志。
    /// </para>
    /// </summary>
    public static class PackDisplayNames
    {
        /// <summary>文件名（放在 <c>Packs/&lt;lang&gt;/</c> 下；不是 <c>.jsonl</c>，因此不会被当成语言包）。</summary>
        public const string FileName = "display-names.json";

        /// <summary>可选的外层键：<c>{"names":{...}}</c>；没有这层时顶层对象直接当映射用。</summary>
        private const string NamesKey = "names";

        /// <summary><c>&lt;packDirectory&gt;/display-names.json</c>；目录为空返回空串。</summary>
        public static string PathFor(string packDirectory)
        {
            if (string.IsNullOrEmpty(packDirectory))
            {
                return string.Empty;
            }

            return Path.Combine(packDirectory, FileName);
        }

        /// <summary>读 <c>&lt;packDirectory&gt;/display-names.json</c>；目录为空 / 文件不存在都返回 0。</summary>
        public static int LoadFromDirectory(string packDirectory)
        {
            string path = PathFor(packDirectory);
            if (path.Length == 0)
            {
                return 0;
            }

            return LoadFromFile(path);
        }

        /// <summary>读一个显示名文件并注册；返回注册成功条数（永不抛）。</summary>
        public static int LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return 0;
            }

            string text;
            try
            {
                if (!File.Exists(path))
                {
                    return 0;
                }

                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (ArgumentException)
            {
                return 0;
            }
            catch (NotSupportedException)
            {
                return 0;
            }
            catch (System.Security.SecurityException)
            {
                return 0;
            }

            return LoadFromJson(text);
        }

        /// <summary>解析一段 JSON 并注册；返回注册成功条数（永不抛）。</summary>
        public static int LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return 0;
            }

            JsonValue root;
            string error;
            if (!Json.TryParse(json, out root, out error) || root == null)
            {
                return 0;
            }

            JsonValue table = root.Get(NamesKey);
            if (table == null || table.Kind != JsonKind.Object)
            {
                table = root;
            }

            if (table.Kind != JsonKind.Object)
            {
                return 0;
            }

            Dictionary<string, string> pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] names = table.MemberNames();
            for (int i = 0; i < names.Length; i++)
            {
                string modId = names[i];
                if (string.IsNullOrEmpty(modId) || !PackFilter.IsValidModId(modId))
                {
                    continue;
                }

                JsonValue value = table.Get(modId);
                string display = value == null ? null : value.AsString();
                if (string.IsNullOrEmpty(display))
                {
                    continue;
                }

                pairs[modId] = display;
            }

            if (pairs.Count == 0)
            {
                return 0;
            }

            PackFilter.RegisterDisplayNames(pairs);
            return pairs.Count;
        }
    }
}
