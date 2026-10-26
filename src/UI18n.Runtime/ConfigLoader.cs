using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UI18n.Core.Config;
using UI18n.Core.Dict;
using UI18n.Core.Settings;

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

                // 模板档单独限额（v1.3）：口径与上面不同，0 = 一条都不录、负数 = 不限制，所以这里不夹负数。
                // v1.5（docs/08 D-T2）起捕获端不再因为条数丢正文，这两个键只解析、不生效（老配置照读）。
                config.CaptureMaxTemplateEntries =
                    ReadInt(node, "CaptureMaxTemplateEntries", config.CaptureMaxTemplateEntries);

                // v1.5：单条文本上限（超长只截断不丢；0 = 不限制）+ 会话字节预算（0 = 不限制）。
                int maxTextLength = ReadInt(node, "CaptureMaxTextLength", config.CaptureMaxTextLength);
                config.CaptureMaxTextLength = maxTextLength < 0 ? 0 : maxTextLength;

                long maxSessionBytes = ReadLong(node, "CaptureMaxSessionBytes", config.CaptureMaxSessionBytes);
                config.CaptureMaxSessionBytes = maxSessionBytes < 0 ? 0 : maxSessionBytes;

                // 数字骨架通道（v1.3）：默认 True。关掉 = 完全回到 v1.2 的精确匹配行为。
                config.PatternMatching = ReadBool(node, "PatternMatching", config.PatternMatching);

                // 实验开关（task-31）：默认 False。关闭时零 patch、零日志、零开销。
                config.ShadowStyleDraw = ReadBool(node, "ShadowStyleDraw", config.ShadowStyleDraw);

                // 联网分发（task-40）：UpdateBaseUrl 默认空 = 完全不联网（连 socket 都不开）。
                config.UpdateBaseUrl = ReadString(node, "UpdateBaseUrl", config.UpdateBaseUrl);
                if (config.UpdateBaseUrl == null)
                {
                    config.UpdateBaseUrl = string.Empty;
                }

                config.UpdateBaseUrl = config.UpdateBaseUrl.Trim();

                // KSP 的 ConfigNode 在预处理阶段把 "//" 之后的内容整段砍掉（PreFormatConfig，反编译证据见 docs/09 §6.5.1），
                // 所以任何带 :// 的 URL 到这里的值都是残的（https: / http:）。加引号也救不了：截断发生在引号处理之前。
                // 发现残值就从文件原文补读一遍 —— 一条被吃掉两个斜杠的配置不该把整条联网链路静默关掉（v1.5 现场事故）。
                if (IsTruncatedUrl(config.UpdateBaseUrl))
                {
                    string rawUrl = ConfigText.ReadValue(path, "UpdateBaseUrl");
                    if (!string.IsNullOrEmpty(rawUrl))
                    {
                        Log.Info("config: UpdateBaseUrl \"" + config.UpdateBaseUrl
                                 + "\" was truncated by KSP's // comment rule, re-read \"" + rawUrl + "\" from the raw text");
                        config.UpdateBaseUrl = rawUrl.Trim();
                    }
                    else
                    {
                        Log.Warn("config: UpdateBaseUrl \"" + config.UpdateBaseUrl
                                 + "\" was truncated by KSP's // comment rule and no intact value was found in the raw text; put the URL on its own line");
                    }
                }

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
            return LoadPacks(config, language, null);
        }

        /// <summary>
        /// 同 <see cref="LoadPacks(UI18nConfig, string)"/>，但按玩家状态 <paramref name="prefs"/> 过滤包文件：
        /// <b>被关掉的 mod 的包连解析都不做</b>（不读文件、不进内存字典）。
        /// <para>
        /// 为什么过滤放在加载期而不是查表期（docs/10 §10）：运行期查表每秒几十万次，
        /// 加一次 prefs 判定就是给热路径加分支；而包数量在启动期就固定了，这里过滤一次，运行期零成本。
        /// </para>
        /// <para><paramref name="prefs"/> 为 <c>null</c> = 全开（老调用方与自检路径）。</para>
        /// </summary>
        internal static DictPack LoadPacks(UI18nConfig config, string language, Prefs prefs)
        {
            var pack = new DictPack();
            PackStats.Reset();

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

                int disabledCount;
                string[] files = PackFilter.SelectEnabled(packs.Files, prefs, out disabledCount);

                // 一行里同时给出总数 / 启用 / 关闭：装错 prefs 时一眼能对账（task-71 验收要求）。
                Log.Info("packs: lang=" + PrintableLanguage(language) + ", dir=" + packs.SelectedLanguage
                         + ", files=" + packs.Files.Length + ", enabled=" + files.Length + ", disabled=" + disabledCount);

                if (disabledCount > 0)
                {
                    Log.Info("packs: disabled by prefs: [" + JoinDisabledModIds(packs.Files, prefs) + "]");
                }

                if (files.Length == 0)
                {
                    Log.Warn("all packs disabled by prefs.json (" + disabledCount
                             + " file(s)), dictionary is empty; edit " + PrefsFile.FileName + " to re-enable");
                    return pack;
                }

                int limit = config != null && config.MaxEntries > 0 ? config.MaxEntries : UI18nConfig.Default.MaxEntries;
                string langFolder = packs.SelectedLanguage;

                // 加载耗时（毫秒）必须进日志：玩家报「启动变慢」时这是唯一的客观依据。
                var totalWatch = Stopwatch.StartNew();
                int loadedFiles = 0;

                for (int i = 0; i < files.Length; i++)
                {
                    string file = files[i];
                    if (pack.Entries >= limit)
                    {
                        Log.Warn("MaxEntries=" + limit + " reached, remaining pack files ignored ("
                                 + (files.Length - i) + " file(s) skipped, e.g. " + langFolder + "/" + Path.GetFileName(file) + ")");
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
                    PackStats.Add(PackFilter.ModIdFromPath(file), pack.Entries - before);
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

        /// <summary>
        /// 被关掉的 modId 列表（ordinal 去重排序，最多 8 个 + <c>…</c>）。
        /// 玩家报「我关的那个 mod 没生效」时，日志里必须能直接看到运行时认为关掉了谁。
        /// </summary>
        private static string JoinDisabledModIds(string[] files, Prefs prefs)
        {
            if (files == null || files.Length == 0 || prefs == null)
            {
                return string.Empty;
            }

            var seen = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var ids = new System.Collections.Generic.List<string>();
            for (int i = 0; i < files.Length; i++)
            {
                string modId = PackFilter.ModIdFromPath(files[i]);
                if (modId.Length == 0 || prefs.IsEnabled(modId) || seen.ContainsKey(modId))
                {
                    continue;
                }

                seen[modId] = true;
                ids.Add(modId);
            }

            ids.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder(96);
            int shown = ids.Count > 8 ? 8 : ids.Count;
            for (int i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(ids[i]);
            }

            if (ids.Count > shown)
            {
                sb.Append(", …(+").Append(ids.Count - shown).Append(')');
            }

            return sb.ToString();
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

        /// <summary>
        /// 值看起来被 ConfigNode 的 <c>//</c> 注释规则砍过：以 ':' 结尾且不含 '/'（<c>https:</c> / <c>http:</c>）。
        /// </summary>
        private static bool IsTruncatedUrl(string value)
        {
            return !string.IsNullOrEmpty(value) && value[value.Length - 1] == ':' && value.IndexOf('/') < 0;
        }

        /// <summary>读 long（字节预算这类可能上万的值用；负数照原样返回，由调用方决定夹不夹）。</summary>
        internal static long ReadLong(ConfigNode node, string key, long fallback)
        {
            if (node == null || !node.HasValue(key))
            {
                return fallback;
            }

            string raw = node.GetValue(key);
            long value;
            if (long.TryParse(raw == null ? string.Empty : raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            Log.Warn("config key " + key + "=\"" + raw + "\" is not a long, using " + fallback);
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
