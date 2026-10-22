using System;
using System.Globalization;
using System.IO;
using UI18n.Core.Config;
using UI18n.Core.Dict;

namespace UI18n.Runtime
{
    /// <summary>
    /// 读 <c>GameData/UI18n/Config.cfg</c> 与 <c>GameData/UI18n/Packs/&lt;lang&gt;/*.jsonl</c>。
    /// 文件缺失 / 字段缺失 / 值非法一律回落到 <see cref="UI18nConfig.Default"/>，不抛异常、不阻断启动。
    /// <para>
    /// 字典目录布局按 D35 / 06 §6.4：<c>Packs/&lt;lang&gt;/&lt;modId&gt;.jsonl</c>（每语言一个子目录）。
    /// <c>Packs/</c> 根下的平铺文件**不读**——那里放的是源产物（pool.jsonl / glossary.jsonl 之类），
    /// 当成运行时字典装进来只会让每一行都 ParseError。
    /// </para>
    /// </summary>
    internal static class ConfigLoader
    {
        internal const string ModFolder = "GameData/UI18n";
        internal const string ConfigFileName = "Config.cfg";

        internal static string ModPath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, ModFolder.Replace('/', Path.DirectorySeparatorChar)); }
        }

        /// <summary>读配置。任何异常都吞掉并返回默认值（启动期不允许因为配置文件炸掉）。</summary>
        internal static UI18nConfig Load()
        {
            UI18nConfig config = UI18nConfig.Default;
            string path = Path.Combine(ModPath, ConfigFileName);

            try
            {
                if (!File.Exists(path))
                {
                    Log.Warn("config not found: " + path + ", using defaults");
                    return config;
                }

                ConfigNode root = ConfigNode.Load(path);
                ConfigNode node = FindUi18nNode(root);
                if (node == null)
                {
                    Log.Warn("config has no UI18N node: " + path + ", using defaults");
                    return config;
                }

                config.Enabled = ReadBool(node, "Enabled", config.Enabled);
                config.ForceOverride = ReadBool(node, "ForceOverride", config.ForceOverride);
                config.CheckForUpdates = ReadBool(node, "CheckForUpdates", config.CheckForUpdates);
                config.AutoDownload = ReadBool(node, "AutoDownload", config.AutoDownload);
                config.ReportOptIn = ReadBool(node, "ReportOptIn", config.ReportOptIn);
                config.MaxEntries = ReadInt(node, "MaxEntries", config.MaxEntries);
                if (config.MaxEntries <= 0)
                {
                    config.MaxEntries = UI18nConfig.Default.MaxEntries;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("config load failed (" + ex.GetType().Name + ": " + ex.Message + "), using defaults");
            }

            return config;
        }

        /// <summary>字典根目录（<c>GameData/UI18n/Packs</c>）。</summary>
        internal static string PacksPath
        {
            get { return Path.Combine(ModPath, PacksFolderName); }
        }

        internal const string PacksFolderName = "Packs";

        /// <summary>
        /// 把游戏语言（<c>zh-cn</c> / <c>zh_CN</c> / <c>zh-Hans</c> …）规整成目录名：小写、下划线换连字符。
        /// 空 / 纯空白返回 null。
        /// </summary>
        internal static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                return null;
            }

            string v = language.Trim().Replace('_', '-').ToLowerInvariant();
            return v.Length == 0 ? null : v;
        }

        /// <summary>
        /// 语言目录候选，按优先级：完整语言（<c>zh-cn</c>）→ 主语言（<c>zh</c>）。
        /// 这样玩家把 <c>zh-CN</c> 的包放在 <c>Packs/zh/</c> 也能读到，反过来不行（精确目录优先）。
        /// </summary>
        internal static string[] LanguageCandidates(string language)
        {
            string normalized = NormalizeLanguage(language);
            if (normalized == null)
            {
                return new string[0];
            }

            int dash = normalized.IndexOf('-');
            if (dash <= 0)
            {
                return new[] { normalized };
            }

            string primary = normalized.Substring(0, dash);
            return string.Equals(primary, normalized, StringComparison.Ordinal) ? new[] { normalized } : new[] { normalized, primary };
        }

        /// <summary>按语言解析出实际存在的字典目录；找不到返回 null（不算错误）。</summary>
        internal static string ResolvePackDir(string language)
        {
            string root = PacksPath;
            foreach (string candidate in LanguageCandidates(language))
            {
                string dir = Path.Combine(root, candidate);
                if (Directory.Exists(dir))
                {
                    return dir;
                }
            }

            return null;
        }

        /// <summary>
        /// 读 <c>GameData/UI18n/Packs/&lt;lang&gt;/*.jsonl</c>（D35 布局）。
        /// 目录不存在 / 该语言没有包都只记日志、返回空字典，不抛异常。
        /// </summary>
        internal static DictPack LoadPacks(UI18nConfig config, string language)
        {
            var pack = new DictPack();
            string root = PacksPath;

            try
            {
                if (!Directory.Exists(root))
                {
                    Log.Info("packs dir not found: " + root + ", dictionary is empty");
                    return pack;
                }

                string dir = ResolvePackDir(language);
                if (dir == null)
                {
                    Log.Warn("no pack dir for language=" + PrintableLanguage(language) + " (looked for "
                             + DescribeCandidates(language) + " under " + root + "), dictionary is empty"
                             + DescribeAvailablePackDirs(root));
                    return pack;
                }

                string[] files = Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                if (files.Length == 0)
                {
                    Log.Warn("no *.jsonl in " + dir + ", dictionary is empty");
                    return pack;
                }

                int limit = config != null && config.MaxEntries > 0 ? config.MaxEntries : UI18nConfig.Default.MaxEntries;
                string langFolder = Path.GetFileName(dir);

                for (int i = 0; i < files.Length; i++)
                {
                    if (pack.Entries >= limit)
                    {
                        Log.Warn("MaxEntries=" + limit + " reached, remaining pack files ignored ("
                                 + (files.Length - i) + " file(s) skipped, e.g. " + langFolder + "/" + Path.GetFileName(files[i]) + ")");
                        break;
                    }

                    int before = pack.Entries;
                    foreach (string line in File.ReadLines(files[i]))
                    {
                        if (pack.Entries >= limit)
                        {
                            Log.Warn("MaxEntries=" + limit + " reached, rest of " + langFolder + "/" + Path.GetFileName(files[i]) + " ignored");
                            break;
                        }

                        pack.AddJsonLine(line);
                    }

                    Log.Info("pack loaded: " + langFolder + "/" + Path.GetFileName(files[i]) + ", entries=+" + (pack.Entries - before));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("packs load failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            return pack;
        }

        private static string PrintableLanguage(string language)
        {
            return string.IsNullOrEmpty(language) ? "<null>" : language;
        }

        private static string DescribeCandidates(string language)
        {
            string[] candidates = LanguageCandidates(language);
            if (candidates.Length == 0)
            {
                return "Packs/<any>";
            }

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < candidates.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(" or ");
                }

                sb.Append(PacksFolderName).Append('/').Append(candidates[i]);
            }

            return sb.ToString();
        }

        /// <summary>诊断用：列出 Packs 下实际存在哪些语言目录（最多 8 个），帮玩家发现目录名拼错。</summary>
        private static string DescribeAvailablePackDirs(string root)
        {
            try
            {
                string[] dirs = Directory.GetDirectories(root);
                if (dirs.Length == 0)
                {
                    return "; " + root + " has no language subdirectory";
                }

                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                var sb = new System.Text.StringBuilder("; available: ");
                int shown = Math.Min(dirs.Length, 8);
                for (int i = 0; i < shown; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(Path.GetFileName(dirs[i]));
                }

                if (dirs.Length > shown)
                {
                    sb.Append(", …(+").Append(dirs.Length - shown).Append(')');
                }

                return sb.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        internal static ConfigNode FindUi18nNode(ConfigNode root)
        {
            if (root == null)
            {
                return null;
            }

            if (string.Equals(root.name, "UI18N", StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return root.HasNode("UI18N") ? root.GetNode("UI18N") : null;
        }

        internal static bool ReadBool(ConfigNode node, string key, bool fallback)
        {
            if (node == null || !node.HasValue(key))
            {
                return fallback;
            }

            string raw = node.GetValue(key);
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            string v = raw.Trim().ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes" || v == "on")
            {
                return true;
            }

            if (v == "false" || v == "0" || v == "no" || v == "off")
            {
                return false;
            }

            Log.Warn("config key " + key + "=\"" + raw + "\" is not a bool, using " + fallback);
            return fallback;
        }

        internal static int ReadInt(ConfigNode node, string key, int fallback)
        {
            if (node == null || !node.HasValue(key))
            {
                return fallback;
            }

            string raw = node.GetValue(key);
            int value;
            if (int.TryParse(raw == null ? string.Empty : raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            Log.Warn("config key " + key + "=\"" + raw + "\" is not an int, using " + fallback);
            return fallback;
        }
    }
}
