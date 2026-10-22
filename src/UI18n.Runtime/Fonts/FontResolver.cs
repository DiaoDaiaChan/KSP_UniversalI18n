using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UI18n.Core.Config;
using UnityEngine;

namespace UI18n.Runtime.Fonts
{
    /// <summary>
    /// 字体兜底（P2）：让中文译文真的显示出来，或者在不该赌的时候<b>保留原文</b>。
    /// <para>
    /// <b>两条完全不同的路径</b>：
    /// <list type="bullet">
    /// <item><b>uGUI</b>（<c>UnityEngine.UI.Text</c>）：<c>Font</c> 支持运行时从系统字体建动态字面
    /// （<c>Font.CreateDynamicFontFromOSFont</c>）⇒ 中文可以真修：换掉组件上的 <c>font</c> 引用即可。</item>
    /// <item><b>TMP</b>（<c>TMPro.TMP_Text</c>）：KSP 1.12 自带的是 TMP 1.2 <b>位图图集</b>版，没有
    /// <c>atlasPopulationMode</c> / <c>TryAddCharacters</c> / 任何 <c>CreateFontAsset</c> 入口 ⇒
    /// <b>运行时有不了新字面</b>。唯一可行的是把「已加载且实测有汉字」的 TMP 资产追加进目标资产的
    /// <c>fallbackFontAssets</c>；库存里没有这种资产时，译文就是方框 ⇒ 于是保留原文。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>红线</b>：本类只改「这个文本组件自己的 font 引用」（<c>text.font = …</c>）与「这个 TMP 资产的
    /// fallback 列表」，<b>不碰</b>任何游戏静态状态（不做全局默认字体替换）、不调玩法 API、不 new 纹理、
    /// 不做离线烘字表。任何异常都只记一次日志并降级（红线 6）。
    /// </para>
    /// <para>线程模型：只在主线程调用（前缀方法内），无锁。</para>
    /// </summary>
    internal static class FontResolver
    {
        /// <summary>TMP/uGUI 字体没给字号时的兜底字号（KSP 的 uGUI 字号普遍 12~16）。</summary>
        private const int DefaultFontSize = 16;

        /// <summary>「中」：判断字面是否覆盖 CJK 的主判据。</summary>
        private static readonly char ProbeChar = '\u4E2D';

        /// <summary>「推」：第二个样本，避免某些字面只带少量汉字却恰好含「中」。</summary>
        private static readonly char ProbeChar2 = '\u63A8';

        /// <summary>内置候选顺序（Lead 指定；<c>OsFontName</c> 配的值排在它们前面）。</summary>
        private static readonly string[] DefaultOsFontCandidates =
        {
            "Microsoft YaHei",
            "Microsoft YaHei UI",
            "SimHei",
            "Noto Sans SC",
            "DengXian",
            "SimSun",
            "Microsoft JhengHei",
        };

        private static FontMode _mode = FontMode.OsFont;
        private static TmpMode _tmpMode = TmpMode.Auto;
        private static string _preferredOsFont = "Microsoft YaHei";
        private static string _bundledPath = string.Empty;

        private static bool _configured;
        private static string[] _installedOsFonts;
        private static readonly Dictionary<int, Font> OsFontBySize = new Dictionary<int, Font>(4);
        private static bool _osFontGaveUp;
        private static string _osFontChosen;

        private static bool _bundledTried;
        private static Font _bundledFont;
        private static AssetBundle _bundledBundle;

        private static TMP_FontAsset _cjkTmpAsset;
        private static bool _tmpAssetSearched;
        private static readonly HashSet<int> TmpFallbackAppended = new HashSet<int>();

        private static readonly HashSet<string> ReportedOnce = new HashSet<string>(StringComparer.Ordinal);

        // ---- 计数（P2 要求「三档都要有日志与计数」）----
        internal static long LegacyInjected;
        internal static long LegacyAlreadyOk;
        internal static long LegacyUnavailable;
        internal static long LegacyDisabled;
        internal static long TmpInjected;
        internal static long TmpAlreadyOk;
        internal static long TmpUnavailable;
        internal static long TmpKeptSource;
        internal static long BundledLoaded;
        internal static long BundledFailed;

        /// <summary>把配置灌进来并清空所有缓存/计数（只在启动时调用一次）。</summary>
        internal static void Configure(UI18nConfig config)
        {
            UI18nConfig c = config ?? UI18nConfig.Default;

            _mode = c.FontMode;
            _tmpMode = c.TmpMode;
            _preferredOsFont = string.IsNullOrEmpty(c.OsFontName) ? "Microsoft YaHei" : c.OsFontName.Trim();
            _bundledPath = c.BundledFontPath == null ? string.Empty : c.BundledFontPath.Trim();
            _configured = true;

            _installedOsFonts = null;
            OsFontBySize.Clear();
            _osFontGaveUp = false;
            _osFontChosen = null;

            _bundledTried = false;
            _bundledFont = null;

            _cjkTmpAsset = null;
            _tmpAssetSearched = false;
            TmpFallbackAppended.Clear();
            ReportedOnce.Clear();

            LegacyInjected = 0;
            LegacyAlreadyOk = 0;
            LegacyUnavailable = 0;
            LegacyDisabled = 0;
            TmpInjected = 0;
            TmpAlreadyOk = 0;
            TmpUnavailable = 0;
            TmpKeptSource = 0;
            BundledLoaded = 0;
            BundledFailed = 0;

            Log.Info("font mode=" + ConfigEnums.ToName(_mode) + ", tmp mode=" + ConfigEnums.ToName(_tmpMode)
                     + ", os font=\"" + _preferredOsFont + "\""
                     + (_mode == FontMode.Bundled ? ", bundled path=\"" + _bundledPath + "\"" : string.Empty));
        }

        // ------------------------------------------------------------------
        // uGUI
        // ------------------------------------------------------------------

        /// <summary>
        /// 保证这个 uGUI 文本组件能显示中文：已经是 CJK 字体就什么都不做；否则换成本模块缓存的动态 CJK 字面。
        /// </summary>
        internal static FontCheck EnsureLegacyFont(UnityEngine.UI.Text text)
        {
            if (_mode == FontMode.Off)
            {
                LegacyDisabled++;
                return FontCheck.Disabled;
            }

            if (text == null)
            {
                return FontCheck.Unavailable;
            }

            try
            {
                Font current = text.font;
                if (current != null && current.HasCharacter(ProbeChar))
                {
                    LegacyAlreadyOk++;
                    return FontCheck.Available;
                }

                Font cjk = GetCjkFont(text.fontSize);
                if (cjk == null)
                {
                    LegacyUnavailable++;
                    return FontCheck.Unavailable;
                }

                if (!ReferenceEquals(current, cjk))
                {
                    // 只改这一个组件的引用（红线 3：不改游戏静态状态、不调玩法 API）
                    text.font = cjk;
                    LegacyInjected++;
                }
                else
                {
                    LegacyAlreadyOk++;
                }

                return FontCheck.Available;
            }
            catch (Exception ex)
            {
                LegacyUnavailable++;
                ReportOnce("legacy-font", ex);
                return FontCheck.Unavailable;
            }
        }

        /// <summary>取（或建）指定字号的 CJK 字面；失败返回 <c>null</c> 且只解释一次原因。</summary>
        private static Font GetCjkFont(int size)
        {
            int px = size <= 0 ? DefaultFontSize : size;

            Font cached;
            if (OsFontBySize.TryGetValue(px, out cached))
            {
                return cached;
            }

            if (_osFontGaveUp)
            {
                return null;
            }

            Font font = null;

            if (_mode == FontMode.Bundled)
            {
                Font bundled = GetBundledFont();
                if (bundled != null && HasCjkGlyphs(bundled))
                {
                    font = bundled;
                }
                else if (bundled != null)
                {
                    Log.Warn("font: bundled font \"" + bundled.name + "\" has no CJK glyphs (HasCharacter('中')=false), falling back to OsFont");
                }
            }

            if (font == null)
            {
                font = CreateOsFont(px);
            }

            if (font == null)
            {
                _osFontGaveUp = true;
                return null;
            }

            OsFontBySize[px] = font;
            return font;
        }

        /// <summary>按候选顺序从系统字体建动态字面；建出来必须 <c>HasCharacter('中')</c> 才算成功。</summary>
        private static Font CreateOsFont(int size)
        {
            string[] installed = GetInstalledOsFonts();
            string[] candidates = BuildCandidates();

            var matched = new StringBuilder();
            for (int i = 0; i < candidates.Length; i++)
            {
                string name = candidates[i];
                if (!ContainsOrdinal(installed, name))
                {
                    continue;
                }

                if (matched.Length > 0)
                {
                    matched.Append(", ");
                }

                matched.Append(name);

                Font font = null;
                try
                {
                    font = Font.CreateDynamicFontFromOSFont(name, size);
                }
                catch (Exception ex)
                {
                    ReportOnce("os-font-create:" + name, ex);
                }

                if (font == null)
                {
                    Log.Warn("font: Font.CreateDynamicFontFromOSFont(\"" + name + "\", " + size + ") returned null, trying next");
                    continue;
                }

                if (!HasCjkGlyphs(font))
                {
                    Log.Warn("font: \"" + name + "\" built at size " + size + " but HasCharacter('中')=false, trying next");
                    continue;
                }

                _osFontChosen = name;
                Log.Info("font: using OS font \"" + name + "\" (dynamic, size " + size + "), CJK glyph check passed");
                return font;
            }

            Log.Warn("font: no usable CJK OS font at size " + size + " (asked \"" + _preferredOsFont + "\"; "
                     + (matched.Length == 0
                         ? "none of the candidate names is installed (" + installed.Length + " OS fonts visible)"
                         : "candidates present but unusable: " + matched)
                     + ") → uGUI translations will be kept in English (set FontMode=Off to translate anyway)");
            return null;
        }

        private static string[] BuildCandidates()
        {
            var list = new List<string>(DefaultOsFontCandidates.Length + 1);
            if (!string.IsNullOrEmpty(_preferredOsFont))
            {
                list.Add(_preferredOsFont);
            }

            for (int i = 0; i < DefaultOsFontCandidates.Length; i++)
            {
                if (!list.Contains(DefaultOsFontCandidates[i]))
                {
                    list.Add(DefaultOsFontCandidates[i]);
                }
            }

            return list.ToArray();
        }

        private static string[] GetInstalledOsFonts()
        {
            if (_installedOsFonts != null)
            {
                return _installedOsFonts;
            }

            try
            {
                string[] names = Font.GetOSInstalledFontNames();
                _installedOsFonts = names ?? new string[0];
            }
            catch (Exception ex)
            {
                ReportOnce("os-font-list", ex);
                _installedOsFonts = new string[0];
            }

            return _installedOsFonts;
        }

        /// <summary>Bundled 模式：载入 AssetBundle 里的 <c>Font</c>；路径/载入失败只记原因并回落 OsFont。</summary>
        private static Font GetBundledFont()
        {
            if (_bundledTried)
            {
                return _bundledFont;
            }

            _bundledTried = true;

            try
            {
                if (string.IsNullOrEmpty(_bundledPath))
                {
                    BundledFailed++;
                    Log.Warn("font: FontMode=Bundled but BundledFontPath is empty → falling back to OsFont");
                    return null;
                }

                string path = _bundledPath;
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(ConfigLoader.ModPath, path.Replace('/', Path.DirectorySeparatorChar));
                }

                if (!File.Exists(path))
                {
                    BundledFailed++;
                    Log.Warn("font: bundled font not found: " + path + " → falling back to OsFont");
                    return null;
                }

                AssetBundle bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    BundledFailed++;
                    Log.Warn("font: AssetBundle.LoadFromFile returned null: " + path + " → falling back to OsFont");
                    return null;
                }

                // 故意不 Unload：Font / TMP_FontAsset 的生命周期必须活过整个会话，
                // Unload(false) 虽然保留对象，但包体本身会被 Unity 回收数据，实测容易留下坏引用。
                _bundledBundle = bundle;

                UnityEngine.Object[] assets = bundle.LoadAllAssets();
                for (int i = 0; i < assets.Length; i++)
                {
                    Font font = assets[i] as Font;
                    if (font != null && _bundledFont == null)
                    {
                        _bundledFont = font;
                    }

                    TMP_FontAsset tmp = assets[i] as TMP_FontAsset;
                    if (tmp != null && _cjkTmpAsset == null && HasCjkGlyphs(tmp))
                    {
                        _cjkTmpAsset = tmp;
                    }
                }

                if (_bundledFont == null)
                {
                    BundledFailed++;
                    Log.Warn("font: AssetBundle has no Font asset (" + assets.Length + " assets): " + path + " → falling back to OsFont");
                    return null;
                }

                BundledLoaded++;
                Log.Info("font: bundled font loaded: \"" + _bundledFont.name + "\" from " + path);
                return _bundledFont;
            }
            catch (Exception ex)
            {
                BundledFailed++;
                ReportOnce("bundled-font", ex);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // TMP
        // ------------------------------------------------------------------

        /// <summary>
        /// TMP 三档判定（Auto 默认 / KeepSource 一律不碰 / Force 照常翻译接受方框）。
        /// 返回 <see cref="FontCheck.Available"/> 才允许把文本换成译文。
        /// </summary>
        internal static FontCheck EnsureTmpFallback(TMP_Text text)
        {
            if (_tmpMode == TmpMode.KeepSource)
            {
                TmpKeptSource++;
                return FontCheck.Unavailable;
            }

            if (text == null)
            {
                return _tmpMode == TmpMode.Force ? FontCheck.Available : FontCheck.Unavailable;
            }

            try
            {
                TMP_FontAsset current = text.font;
                if (current == null)
                {
                    TmpUnavailable++;
                    return _tmpMode == TmpMode.Force ? FontCheck.Available : FontCheck.Unavailable;
                }

                if (current.HasCharacter(ProbeChar) || current.HasCharacter(ProbeChar, true))
                {
                    TmpAlreadyOk++;
                    return FontCheck.Available;
                }

                TMP_FontAsset cjk = GetCjkTmpAsset();
                if (cjk == null)
                {
                    TmpUnavailable++;
                    return _tmpMode == TmpMode.Force ? FontCheck.Available : FontCheck.Unavailable;
                }

                int id = current.GetInstanceID();
                if (TmpFallbackAppended.Add(id))
                {
                    if (current.fallbackFontAssets == null)
                    {
                        current.fallbackFontAssets = new List<TMP_FontAsset>(1);
                    }

                    current.fallbackFontAssets.Add(cjk);
                    TmpInjected++;
                    Log.Info("font: TMP fallback appended \"" + cjk.name + "\" → \"" + current.name + "\""
                             + " (HasCharacter('中', searchFallbacks)=" + current.HasCharacter(ProbeChar, true) + ")");
                }

                if (current.HasCharacter(ProbeChar, true))
                {
                    return FontCheck.Available;
                }

                TmpUnavailable++;
                return _tmpMode == TmpMode.Force ? FontCheck.Available : FontCheck.Unavailable;
            }
            catch (Exception ex)
            {
                TmpUnavailable++;
                ReportOnce("tmp-font", ex);
                return _tmpMode == TmpMode.Force ? FontCheck.Available : FontCheck.Unavailable;
            }
        }

        /// <summary>在已加载的 TMP 资产里找第一个实测含「中」「推」的资产（只搜一次，结果缓存）。</summary>
        private static TMP_FontAsset GetCjkTmpAsset()
        {
            if (_cjkTmpAsset != null)
            {
                return _cjkTmpAsset;
            }

            if (_tmpAssetSearched)
            {
                return null;
            }

            _tmpAssetSearched = true;

            try
            {
                if (_mode == FontMode.Bundled)
                {
                    GetBundledFont();
                    if (_cjkTmpAsset != null)
                    {
                        return _cjkTmpAsset;
                    }
                }

                TMP_FontAsset[] assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (assets == null || assets.Length == 0)
                {
                    Log.Info("font: no TMP_FontAsset loaded ⇒ TMP translations will be kept in English (TmpMode="
                             + ConfigEnums.ToName(_tmpMode) + ")");
                    return null;
                }

                int cjkCount = 0;
                for (int i = 0; i < assets.Length; i++)
                {
                    TMP_FontAsset asset = assets[i];
                    if (asset == null || !HasCjkGlyphs(asset))
                    {
                        continue;
                    }

                    cjkCount++;
                    if (_cjkTmpAsset == null)
                    {
                        _cjkTmpAsset = asset;
                    }
                }

                if (_cjkTmpAsset == null)
                {
                    Log.Warn("font: none of the " + assets.Length + " loaded TMP_FontAsset carries CJK glyphs"
                             + " (KSP 1.12 的 TMP 1.2 位图图集无运行时造字入口，且库存无 CJK TMP 资产)"
                             + " ⇒ TMP translations will be kept in English (TmpMode=" + ConfigEnums.ToName(_tmpMode) + ")");
                }
                else
                {
                    Log.Info("font: CJK TMP asset found: \"" + _cjkTmpAsset.name + "\" (" + cjkCount
                             + "/" + assets.Length + " loaded TMP assets carry CJK) ⇒ TMP path is usable");
                }
            }
            catch (Exception ex)
            {
                ReportOnce("tmp-asset-scan", ex);
            }

            return _cjkTmpAsset;
        }

        private static bool HasCjkGlyphs(TMP_FontAsset asset)
        {
            return asset.HasCharacter(ProbeChar) && asset.HasCharacter(ProbeChar2);
        }

        private static bool HasCjkGlyphs(Font font)
        {
            return font.HasCharacter(ProbeChar) && font.HasCharacter(ProbeChar2);
        }

        // ------------------------------------------------------------------
        // 诊断
        // ------------------------------------------------------------------

        /// <summary>一行计数摘要（周期日志用）：每个档位各自命中了多少次。</summary>
        internal static string Describe()
        {
            var sb = new StringBuilder("font stats: ");
            sb.Append("uGUI injected=").Append(LegacyInjected)
              .Append(" already-ok=").Append(LegacyAlreadyOk)
              .Append(" unavailable=").Append(LegacyUnavailable)
              .Append(" off=").Append(LegacyDisabled);
            sb.Append(" | TMP injected=").Append(TmpInjected)
              .Append(" already-ok=").Append(TmpAlreadyOk)
              .Append(" kept-src=").Append(TmpKeptSource)
              .Append(" unavailable=").Append(TmpUnavailable);
            sb.Append(" | mode=").Append(ConfigEnums.ToName(_mode))
              .Append('/').Append(ConfigEnums.ToName(_tmpMode));

            if (_osFontChosen != null)
            {
                sb.Append(" os-font=\"").Append(_osFontChosen).Append('"');
            }

            if (_cjkTmpAsset != null)
            {
                sb.Append(" tmp-asset=\"").Append(_cjkTmpAsset.name).Append('"');
            }

            sb.Append(BundledLoaded + BundledFailed > 0
                ? " bundled=" + BundledLoaded + "/" + (BundledLoaded + BundledFailed)
                : string.Empty);

            return sb.ToString();
        }

        /// <summary>失败只记一次（同类错误每局一条），避免每帧刷屏（红线 6）。</summary>
        private static void ReportOnce(string key, Exception exception)
        {
            if (!ReportedOnce.Add(key))
            {
                return;
            }

            Log.Error("font: " + key + " failed → " + exception.GetType().Name + ": " + exception.Message);
        }
    }
}
