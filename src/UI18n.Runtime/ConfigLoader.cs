using System;
using System.Diagnostics;
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

                // 日志级别第一件事就读（task-44）：它决定本方法后面每条日志是否出得来。
                // 无法识别的值只 Warn 并保持 Info —— 绝不因为写错一个词就把日志全关掉。
                string rawLogLevel = ReadString(node, "LogLevel", null);
                if (!string.IsNullOrEmpty(rawLogLevel))
                {
                    LogLevel logLevel;
                    if (ConfigEnums.TryParseLogLevel(rawLogLevel, out logLevel))
                    {
                        config.LogLevel = logLevel;
                    }
                    else
                    {
                        Log.Warn("config key LogLevel=\"" + rawLogLevel + "\" is not recognised (Error | Warn | Info | Verbose), using "
                                 + ConfigEnums.ToName(config.LogLevel));
                    }
                }

                Log.Level = config.LogLevel;

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

                // P2 的字体四项。枚举值非法时不阻断启动：记一条 Warn 后保持默认值（红线 6）。
                string rawFontMode = ReadString(node, "FontMode", null);
                if (!string.IsNullOrEmpty(rawFontMode))
                {
                    FontMode fontMode;
                    if (ConfigEnums.TryParseFontMode(rawFontMode, out fontMode))
                    {
                        config.FontMode = fontMode;
                    }
                    else
                    {
                        Log.Warn("config key FontMode=\"" + rawFontMode + "\" is not recognised (Off | OsFont | Bundled), using "
                                 + ConfigEnums.ToName(config.FontMode));
                    }
                }

                string rawTmpMode = ReadString(node, "TmpMode", null);
                if (!string.IsNullOrEmpty(rawTmpMode))
                {
                    TmpMode tmpMode;
                    if (ConfigEnums.TryParseTmpMode(rawTmpMode, out tmpMode))
                    {
                        config.TmpMode = tmpMode;
                    }
                    else
                    {
                        Log.Warn("config key TmpMode=\"" + rawTmpMode + "\" is not recognised (Auto | KeepSource | Force), using "
                                 + ConfigEnums.ToName(config.TmpMode));
                    }
                }

                string osFontName = ReadString(node, "OsFontName", null);
                if (!string.IsNullOrEmpty(osFontName))
                {
                    config.OsFontName = osFontName;
                }

                string bundledPath = ReadString(node, "BundledFontPath", null);
                if (!string.IsNullOrEmpty(bundledPath))
                {
                    config.BundledFontPath = bundledPath;
                }

                // 诊断项（round 2）：首次命中样本上限，0 = 关闭采样；负数按 0 处理（不阻断启动）。
                int hitSampleLimit = ReadInt(node, "HitSampleLimit", config.HitSampleLimit);
                config.HitSampleLimit = hitSampleLimit < 0 ? 0 : hitSampleLimit;

                // 缺命中捕获（task-30）：只写本地文件、不上传；上限负数按 0 处理（0 = 不限制）。
                config.CaptureMisses = ReadBool(node, "CaptureMisses", config.CaptureMisses);
                int captureMax = ReadInt(node, "CaptureMaxEntries", config.CaptureMaxEntries);
                config.CaptureMaxEntries = captureMax < 0 ? 0 : captureMax;

                // 实验开关（task-31）：默认 False。关闭时零 patch、零日志、零开销。
                config.ShadowStyleDraw = ReadBool(node, "ShadowStyleDraw", config.ShadowStyleDraw);

                // 联网分发（task-40）：UpdateBaseUrl 默认空 = 完全不联网（连 socket 都不开）。
                config.UpdateBaseUrl = ReadString(node, "UpdateBaseUrl", config.UpdateBaseUrl);
                if (config.UpdateBaseUrl == null)
                {
                    config.UpdateBaseUrl = string.Empty;
                }

                config.UpdateBaseUrl = config.UpdateBaseUrl.Trim();

                string channel = ReadString(node, "UpdateChannel", null);
                if (!string.IsNullOrEmpty(channel))
                {
                    config.UpdateChannel = channel.Trim();
                }

                // 超时：负数按默认 10 秒处理（0 或负值都不该让一次检查永远挂着）
                int timeoutSeconds = ReadInt(node, "UpdateTimeoutSeconds", config.UpdateTimeoutSeconds);
                config.UpdateTimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 10;

                // 间隔：负数按 0 处理（0 = 每次启动都查，见 UpdateState.IsCheckDue）
                int checkDays = ReadInt(node, "UpdateCheckDays", config.UpdateCheckDays);
                config.UpdateCheckDays = checkDays < 0 ? 0 : checkDays;
            }
            catch (Exception ex)
            {
                Log.Warn("config load failed (" + ex.GetType().Name + ": " + ex.Message + "), using defaults");
            }

            // 文件缺失 / 节点缺失 / 读到一半炸掉，三条路径都必须让默认级别生效（否则 Log.Level 会留上一次的值）。
            Log.Level = config.LogLevel;
            return config;
        }

        /// <summary>字典根目录（<c>GameData/UI18n/Packs</c>）。</summary>
        internal static string PacksPath
        {
            get { return Path.Combine(ModPath, PacksFolderName); }
        }

        internal const string PacksFolderName = "Packs";

        /// <summary>
        /// 读 <c>GameData/UI18n/Packs/&lt;lang&gt;/*.jsonl</c>（D35 布局）。
        /// 目录发现全部交给 <see cref="PackLocator"/>（Core 里单测覆盖）：这里<b>不允许</b>再自己拼 Packs 路径
        /// 或判断 <c>Directory.Exists</c> —— 那正是 P1 那次「按契约放的包一条都读不到」事故的复发路径。
        /// 目录不存在 / 该语言没有包都只记日志、返回空字典，不抛异常。
        /// </summary>
        internal static DictPack LoadPacks(UI18nConfig config, string language)
        {
            var pack = new DictPack();

            try
            {
                string root = PacksPath;
                PackResolution packs = PackLocator.Resolve(root, language);

                // 事故现场必须显式说出来：Packs 根下的平铺 jsonl 是生产端产物（pool/glossary），运行时永不读。
                if (packs.FlatFileCount > 0)
                {
                    Log.Warn("packs: " + packs.FlatFileCount + " flat *.jsonl file(s) directly under " + root
                             + " are ignored (expected " + PacksFolderName + "/<lang>/<modId>.jsonl); " + packs.Describe());
                }

                if (packs.Status != PackDirStatus.Found)
                {
                    Log.Warn(packs.Describe() + ", dictionary is empty");
                    return pack;
                }

                if (packs.Files.Length == 0)
                {
                    Log.Warn(packs.Describe() + ", no *.jsonl in the pack dir, dictionary is empty");
                    return pack;
                }

                Log.Info("packs: lang=" + PrintableLanguage(language) + ", dir=" + packs.SelectedLanguage
                         + ", files=" + packs.Files.Length);

                int limit = config != null && config.MaxEntries > 0 ? config.MaxEntries : UI18nConfig.Default.MaxEntries;
                string langFolder = packs.SelectedLanguage;

                // 加载耗时（毫秒）必须进日志：玩家报「启动变慢」时这是唯一的客观依据。
                var totalWatch = Stopwatch.StartNew();
                int loadedFiles = 0;

                for (int i = 0; i < packs.Files.Length; i++)
                {
                    string file = packs.Files[i];
                    if (pack.Entries >= limit)
                    {
                        Log.Warn("MaxEntries=" + limit + " reached, remaining pack files ignored ("
                                 + (packs.Files.Length - i) + " file(s) skipped, e.g. " + langFolder + "/" + Path.GetFileName(file) + ")");
                        break;
                    }

                    int before = pack.Entries;
                    var fileWatch = Stopwatch.StartNew();
                    foreach (string line in File.ReadLines(file))
                    {
                        if (pack.Entries >= limit)
                        {
                            Log.Warn("MaxEntries=" + limit + " reached, rest of " + langFolder + "/" + Path.GetFileName(file) + " ignored");
                            break;
                        }

                        pack.AddJsonLine(line);
                    }

                    fileWatch.Stop();
                    loadedFiles++;
                    Log.Info("pack loaded: " + langFolder + "/" + Path.GetFileName(file) + ", entries=+" + (pack.Entries - before)
                             + ", " + fileWatch.ElapsedMilliseconds + " ms");
                }

                totalWatch.Stop();
                Log.Info("dictionary load: files=" + loadedFiles + ", entries=" + pack.Entries + ", parseErrors=" + pack.ParseErrors
                         + ", " + totalWatch.ElapsedMilliseconds + " ms");
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

        /// <summary>读字符串；键缺失或值为空白时返回 <paramref name="fallback"/>（不 Trim 掉有意义的内嵌空格，只 Trim 首尾）。</summary>
        internal static string ReadString(ConfigNode node, string key, string fallback)
        {
            if (node == null || !node.HasValue(key))
            {
                return fallback;
            }

            string raw = node.GetValue(key);
            if (raw == null)
            {
                return fallback;
            }

            string value = raw.Trim();
            return value.Length == 0 ? fallback : value;
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
