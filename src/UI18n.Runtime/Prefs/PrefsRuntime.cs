using System;
using System.IO;
using UI18n.Core.Settings;

namespace UI18n.Runtime.Settings
{
    /// <summary>
    /// 运行时侧只做三件事：算出 prefs.json 的路径、读一次（永不抛）、把结果缓存给 <see cref="Current"/>。
    /// <para>
    /// 为什么不在 <c>ConfigLoader</c> 里顺手读：配置是「安装级」（<c>GameData/UI18n/Config.cfg</c>），
    /// 状态是「玩家级」（<c>PluginData/UI18n/prefs.json</c>）。前者随版本发布覆盖，后者绝不能随更新被覆盖 ——
    /// 混在一起迟早出现「升级一次玩家关掉的 mod 全被打开」。
    /// </para>
    /// <para>
    /// 红线 6：读失败只 Warn + 默认值，绝不阻断启动；文件内容一个字节都不改（解析失败时也原样留着，便于报障）。
    /// </para>
    /// </summary>
    internal static class PrefsRuntime
    {
        /// <summary>相对 KSP 根目录的状态目录（与 <c>UpdateRunner</c> 用的是同一个）。</summary>
        internal const string Folder = "PluginData/UI18n";

        private static Prefs _current;
        private static string _path = string.Empty;
        private static string _lastError = string.Empty;
        private static bool _exists;

        /// <summary><c>&lt;KSP&gt;/PluginData/UI18n</c>。</summary>
        internal static string RootPath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, Path.Combine("PluginData", "UI18n")); }
        }

        /// <summary><c>&lt;KSP&gt;/PluginData/UI18n/prefs.json</c>。</summary>
        internal static string FilePath
        {
            get
            {
                if (_path.Length == 0)
                {
                    _path = PrefsFile.FilePathIn(RootPath);
                }

                return _path;
            }
        }

        /// <summary>当前状态（未读过则先读一次；永不返回 null）。</summary>
        internal static Prefs Current
        {
            get
            {
                if (_current == null)
                {
                    Load();
                }

                return _current;
            }
        }

        /// <summary>上次读取时文件是否存在。</summary>
        internal static bool Exists
        {
            get { return _exists; }
        }

        /// <summary>上次读取/保存的错误串（空 = 没问题）。</summary>
        internal static string LastError
        {
            get { return _lastError; }
        }

        /// <summary>被玩家关掉的 modId（ordinal 排序；没有记录 mod 时返回空数组）。</summary>
        internal static string[] DisabledModIds()
        {
            return Current.DisabledModIds();
        }

        /// <summary>
        /// 读 prefs.json。三条路径各打一行明确的日志（任务书要求一眼可验证）：
        /// <list type="bullet">
        /// <item>文件不存在 → <c>prefs: no file, defaults (all mods translated)</c></item>
        /// <item>文件坏 → <c>prefs: parse failed (…), using defaults</c> + Warn</item>
        /// <item>正常 → <c>prefs: loaded &lt;path&gt; (schema=…, language=…, mods=N, disabled=M)</c></item>
        /// </list>
        /// </summary>
        internal static void Load()
        {
            _path = FilePath;

            string error;
            bool exists;
            Prefs prefs = PrefsFile.Load(_path, out error, out exists);

            // Core 的 Load 已经承诺「永不抛」；这一层只兜「连路径都算不出来」的极端情况。
            if (prefs == null)
            {
                prefs = Prefs.CreateDefault();
                error = error ?? "load-returned-null";
            }

            _current = prefs;
            _exists = exists;
            _lastError = error ?? string.Empty;

            if (!exists)
            {
                Log.Info("prefs: no file, defaults (all mods translated): " + _path);
                return;
            }

            if (!string.IsNullOrEmpty(_lastError))
            {
                Log.Warn("prefs: parse failed (" + _lastError + "), using defaults (all mods translated); file left untouched: " + _path);
                return;
            }

            Log.Info("prefs: loaded " + _path + " (" + Detail(prefs) + ")");
        }

        /// <summary>把当前状态写回 prefs.json（面板「保存」按钮）。失败只返回 error，不抛。</summary>
        internal static bool Save(out string error)
        {
            if (_current == null)
            {
                _current = Prefs.CreateDefault();
            }

            bool ok = PrefsFile.TrySave(FilePath, _current, out error);
            _lastError = error ?? string.Empty;
            return ok;
        }

        private static string Detail(Prefs prefs)
        {
            string describe = prefs.Describe();
            const string prefix = "prefs: ";
            if (describe != null && describe.StartsWith(prefix, StringComparison.Ordinal))
            {
                return describe.Substring(prefix.Length);
            }

            return describe ?? string.Empty;
        }
    }
}
