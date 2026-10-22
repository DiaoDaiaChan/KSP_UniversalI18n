using System;
using TMPro;
using UnityEngine;

namespace UI18n.Runtime.Fonts
{
    /// <summary>
    /// 字体探测（只探测、不注入、不改任何字体设置）。用途：进游戏后确认 UI 字体到底能不能显示中文，
    /// 若不能则说明需要玩家自己换字库（本 Mod 不替玩家改字体，见 05 §6 原生性要求）。
    /// </summary>
    internal static class FontProbe
    {
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

        /// <summary>探测用的样本字符：中 / 推 / 全角句号，覆盖汉字与全角标点。</summary>
        private static readonly char[] SampleChars = { '\u4E2D', '\u63A8', '\u3002' };

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

        /// <summary>探测并打印结论：有 CJK 字面 → 正常；没有 → 提示需要玩家换字库（不自动注入）。</summary>
        internal static Result ProbeAndLog()
        {
            Result result = Probe();
            Log.Info("font probe: " + result);

            if (result.HasCjk)
            {
                Log.Info("font probe: CJK glyphs available, Chinese text can render");
            }
            else
            {
                Log.Warn("font probe: no CJK glyphs found in loaded faces; Chinese text may show as □□□ "
                         + "(UI18n does not inject fonts — install a CJK font pack yourself)");
            }

            return result;
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
