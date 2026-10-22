using System;
using HarmonyLib;
using UI18n.Core.Config;
using UI18n.Core.Dict;
using UI18n.Core.Policy;
using UI18n.Runtime.Fonts;
using UI18n.Runtime.Patches;
using UnityEngine;

namespace UI18n.Runtime
{
    /// <summary>
    /// 启动入口。<c>Startup.Instantly</c>：在主菜单之前就跑，保证第一帧 UI 已被接管。
    /// <para>
    /// 启动顺序（严格按此顺序，任何一步失败都只记日志、绝不抛给 KSP）：
    /// 读配置 → 读语言 → <b>语言门禁</b>（非中文直接 return，装 0 个 patch）→ 读字典 → 字体探测 → 装 patch → 一行汇总。
    /// </para>
    /// <para>红线 5：语言不是中文时一个 patch 都不装。红线 6：失败只 log 不抛。</para>
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class UI18nAddon : MonoBehaviour
    {
        internal const string Version = "0.1.0";
        internal const string HarmonyId = "com.universali18n.runtime";

        private static Harmony _harmony;
        private static bool _started;

        /// <summary>已装 patch 数（供后续联调/状态面板读取）。</summary>
        internal static int InstalledPatches { get; private set; }

        /// <summary>安装失败的 patch 数。</summary>
        internal static int FailedPatches { get; private set; }

        /// <summary>启动时看到的游戏语言。</summary>
        internal static string Language { get; private set; }

        private void Start()
        {
            if (_started)
            {
                // 场景切换后 addon 可能再被 Start 一次；重复装 patch 会让前缀跑两遍，直接跳过
                Log.Info("already initialized, skip (patches=" + InstalledPatches + ")");
                return;
            }

            _started = true;

            try
            {
                Run();
            }
            catch (Exception ex)
            {
                Log.Error("startup failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Run()
        {
            Log.Info("v" + Version + " starting (harmony id " + HarmonyId + ")");

            // 1) 配置
            UI18nConfig config = ConfigLoader.Load();

            // 2) 语言
            string language = ReadLanguage();
            Language = language;
            Log.Info("language=" + Printable(language) + ", enabled=" + config.Enabled + ", forceOverride=" + config.ForceOverride);

            if (!config.Enabled)
            {
                Log.Info("disabled in Config.cfg (Enabled=False), patches skipped");
                return;
            }

            // 3) 语言门禁：非中文 → 装 0 个 patch，一行日志说明原因（红线 5）
            if (!LanguageGate.ShouldTranslate(language, config.ForceOverride))
            {
                Log.Info("language=" + Printable(language) + ", patches skipped"
                         + (config.ForceOverride ? string.Empty : " (only zh* is translated; set ForceOverride=True to override)"));
                return;
            }

            // 4) 字典
            DictPack pack = ConfigLoader.LoadPacks(config, language);
            Translator.Initialize(config, pack);
            Log.Info("dictionary: entries=" + pack.Entries + ", parseErrors=" + pack.ParseErrors);

            if (pack.Entries == 0)
            {
                Log.Warn("dictionary is empty, patches skipped (install a pack under "
                         + ConfigLoader.ModFolder + "/" + ConfigLoader.PacksFolderName + "/<lang>/)");
                return;
            }

            // 5) 字体探测（只探测不注入）
            FontProbe.ProbeAndLog();

            // 6) 装 patch
            Harmony harmony = GetHarmony();
            if (harmony == null)
            {
                Log.Error("Harmony instance unavailable, patches skipped (is 0Harmony.dll loaded?)");
                return;
            }

            int ok;
            int failed;
            PatchRegistry.InstallAll(harmony, pack, out ok, out failed);
            InstalledPatches = ok;
            FailedPatches = failed;

            // 7) 一行汇总
            Log.Info("ready: patches=" + ok + "/" + (ok + failed) + ", dict entries=" + pack.Entries
                     + ", language=" + Printable(language) + ", version=" + Version);
        }

        private static string ReadLanguage()
        {
            try
            {
                // 必须写全名：本命名空间里有个自己的 Translator，裸写容易误读成 KSP 的 Localizer
                return KSP.Localization.Localizer.CurrentLanguage;
            }
            catch (Exception ex)
            {
                Log.Warn("read language failed: " + ex.GetType().Name + ": " + ex.Message + ", treating as non-Chinese");
                return null;
            }
        }

        private static string Printable(string language)
        {
            return string.IsNullOrEmpty(language) ? "<null>" : language;
        }

        private static Harmony GetHarmony()
        {
            if (_harmony != null)
            {
                return _harmony;
            }

            try
            {
                _harmony = new Harmony(HarmonyId);
                return _harmony;
            }
            catch (Exception ex)
            {
                Log.Error("Harmony init failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }
    }
}
