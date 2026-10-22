using System;
using System.Text;
using TMPro;
using UnityEngine;

namespace UI18n.Runtime.Fonts
{
    /// <summary>
    /// 字体探测（只探测、不注入、不改任何字体设置）。
    /// <para>
    /// <b>只在主菜单之后调用</b>（<see cref="UI18nAddon.Update"/> 惰性触发）：<c>Resources.FindObjectsOfTypeAll</c>
    /// 会遍历所有已加载对象，在 loading 场景里既慢又可能撞上引擎内部正在进行的资产加载。
    /// </para>
    /// <para>
    /// 输出两件事：TMP 资产清单（每个资产一行，含 <c>HasCharacter('中')</c> / <c>HasCharacter('测')</c>）——
    /// 用于确认「KSP 库存里到底有没有 CJK TMP 资产」（决定 TMP 路径是「追加 fallback」还是「保留原文」）；
    /// 以及 uGUI <c>Font</c> 侧有没有现成的 CJK 字面。
    /// </para>
    /// </summary>
    internal static class FontProbe
    {
        /// <summary>清单最多打印多少个 TMP 资产（KSP 主菜单实测 100+ 个，全打会淹日志）。</summary>
        private const int MaxAssetLines = 40;

        /// <summary>探测结果快照。</summary>
        internal sealed class Result
        {
            internal int TmpFaces;
            internal int TmpCjkFaces;
            internal int LegacyFaces;
            internal int LegacyCjkFaces;
            internal string Sample;

            internal bool HasCjk
            {
                get { return TmpCjkFaces > 0 || LegacyCjkFaces > 0; }
            }

            public override string ToString()
            {
                return "TMP faces=" + TmpFaces + " (with CJK " + TmpCjkFaces + "), legacy Font faces=" + LegacyFaces
                       + " (with CJK " + LegacyCjkFaces + "), sample=\"" + Sample + "\"";
            }
        }

        /// <summary>探测用的样本字符：中 / 测 / 推 —— 汉字与常用动词，覆盖 CJK 基本区。</summary>
        private static readonly char[] SampleChars = { '\u4E2D', '\u6D4B', '\u63A8' };

        /// <summary>
        /// 扫描所有已加载的 TMP 字体资产与旧版 Font，统计含 CJK 的字面数量。任何异常只记日志，返回空结果。
        /// </summary>
        internal static Result Probe()
        {
            var result = new Result();
            result.Sample = new string(SampleChars);

            try
            {
                TMP_FontAsset[] tmpFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                result.TmpFaces = tmpFonts == null ? 0 : tmpFonts.Length;
                if (tmpFonts != null)
                {
                    for (int i = 0; i < tmpFonts.Length; i++)
                    {
                        TMP_FontAsset font = tmpFonts[i];
                        if (font != null && HasAnySample(font))
                        {
                            result.TmpCjkFaces++;
                        }
                    }
                }

                Font[] fonts = Resources.FindObjectsOfTypeAll<Font>();
                result.LegacyFaces = fonts == null ? 0 : fonts.Length;
                if (fonts != null)
                {
                    for (int i = 0; i < fonts.Length; i++)
                    {
                        Font font = fonts[i];
                        if (font != null && HasAnySample(font))
                        {
                            result.LegacyCjkFaces++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("font probe failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return result;
        }

        /// <summary>探测并打印结论（含 TMP 资产清单）。</summary>
        internal static Result ProbeAndLog()
        {
            Result result = Probe();
            Log.Info("font probe: " + result);

            LogTmpAssetInventory();

            if (result.HasCjk)
            {
                Log.Info("font probe: CJK glyphs available (TMP assets with CJK=" + result.TmpCjkFaces
                         + ", legacy Font with CJK=" + result.LegacyCjkFaces + ")");
            }
            else
            {
                Log.Warn("font probe: no CJK glyphs in any loaded face ⇒ uGUI 走系统字体注入（FontMode=OsFont/Bundled），"
                         + "TMP 1.2 位图图集无运行时造字入口 ⇒ TMP 文本按 TmpMode 保留原文");
            }

            return result;
        }

        /// <summary>
        /// 逐个打印已加载的 TMP 资产（最多 <see cref="MaxAssetLines"/> 行）：名字 + 是否含「中」/「测」。
        /// 这一行清单就是「KSP 库存有没有 CJK TMP 资产」的判定证据。
        /// </summary>
        private static void LogTmpAssetInventory()
        {
            try
            {
                TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (fonts == null || fonts.Length == 0)
                {
                    Log.Info("font probe: no TMP_FontAsset loaded yet");
                    return;
                }

                int cjk = 0;
                int shown = Math.Min(fonts.Length, MaxAssetLines);
                for (int i = 0; i < fonts.Length; i++)
                {
                    TMP_FontAsset font = fonts[i];
                    bool hasZhong = font != null && font.HasCharacter('\u4E2D');
                    bool hasCe = font != null && font.HasCharacter('\u6D4B');
                    if (hasZhong && hasCe)
                    {
                        cjk++;
                    }

                    if (i < shown)
                    {
                        Log.Info("  tmp asset[" + i + "] \"" + (font == null ? "<null>" : font.name)
                                 + "\" cjk(中)=" + hasZhong + " cjk(测)=" + hasCe);
                    }
                }

                if (fonts.Length > shown)
                {
                    Log.Info("  ...(" + (fonts.Length - shown) + " more TMP assets not listed)");
                }

                Log.Info("font probe: TMP assets with both 中 and 测 = " + cjk + "/" + fonts.Length);
            }
            catch (Exception ex)
            {
                Log.Warn("font probe: TMP inventory failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool HasAnySample(TMP_FontAsset font)
        {
            for (int i = 0; i < SampleChars.Length; i++)
            {
                if (font.HasCharacter(SampleChars[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasAnySample(Font font)
        {
            for (int i = 0; i < SampleChars.Length; i++)
            {
                if (font.HasCharacter(SampleChars[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
