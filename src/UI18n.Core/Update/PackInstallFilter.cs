using System;
using System.Collections.Generic;
using UI18n.Core.Dict;

namespace UI18n.Core.Update
{
    /// <summary>
    /// 「只装玩家启用的 mod 包」这一判断的<b>纯函数</b>实现（v1.6 M2 第 3 件事）。
    /// <para>
    /// 放在 Core 的理由：真实的安装动作在 Runtime（要写盘、要 <c>File.Replace</c>），
    /// 但「哪个文件属于哪个 mod、要不要装」这层判断一旦写错，表现是<b>玩家的 DLL 没被更新</b>
    /// 或<b>被关掉的包又被装回来</b>，本机没法靠进游戏验证；抽出来就能被 xunit 穷举。
    /// </para>
    /// <para>
    /// 三条硬语义：
    /// <list type="number">
    /// <item><b>非包文件永远不筛</b>：<c>Plugins/*.dll</c>、<c>UI18n.cfg</c>、<c>.version</c>
    /// 照旧全装（筛掉它们等于按 mod 开关去控制 mod 本体更新，那是另一个产品决策，v1.6 不做）。</item>
    /// <item><b>mod 归属优先看清单</b>：manifest v2 的 <c>mods[]</c> 是权威表；
    /// 只有清单没写 <c>mods[]</c>（v1 旧清单）时才退回文件名派生（<see cref="PackFilter.ModIdFromPath"/>）。</item>
    /// <item><b>大小写不敏感</b>：modId 来自文件名，Windows 上不可靠。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class PackInstallFilter
    {
        /// <summary>是否是合法的语言包路径（<c>Packs/&lt;lang&gt;/&lt;file&gt;.jsonl</c>）。</summary>
        public static bool IsPackPath(string modRelativePath)
        {
            string language;
            string fileName;
            string reason;
            return ZipSafety.TryGetPackTarget(modRelativePath, out language, out fileName, out reason);
        }

        /// <summary>只按文件名派生 modId（清单没有 <c>mods[]</c> 时的兜底）。</summary>
        public static string ModIdOfPackPath(string modRelativePath)
        {
            if (string.IsNullOrEmpty(modRelativePath))
            {
                return string.Empty;
            }

            return PackFilter.ModIdFromPath(modRelativePath);
        }

        /// <summary>
        /// 解析某条文件记录的 modId：先查 <see cref="UpdateManifest.Mods"/> 的 <c>file</c> → <c>modId</c> 映射，
        /// 再退回文件名派生。不是包文件 / 推不出 modId 时返回空串。
        /// </summary>
        public static string ResolveModId(UpdateManifest manifest, ManifestFile file)
        {
            if (file == null)
            {
                return string.Empty;
            }

            if (manifest != null)
            {
                string mapped = manifest.ModIdOfFile(file.ModRelativePath);
                if (!string.IsNullOrEmpty(mapped))
                {
                    return mapped;
                }
            }

            if (!IsPackPath(file.ModRelativePath))
            {
                return string.Empty;
            }

            return ModIdOfPackPath(file.ModRelativePath);
        }

        /// <summary>
        /// 拆成「要装」与「被 prefs 关掉」两组，<b>保持输入顺序</b>（安装顺序影响日志与覆盖行为，
        /// 顺序一漂日志就没法对账）。非包文件一律进 <c>enabled</c>。
        /// </summary>
        public static void Partition(
            UpdateManifest manifest,
            IList<ManifestFile> files,
            IList<string> disabledModIds,
            out List<ManifestFile> enabled,
            out List<ManifestFile> disabled)
        {
            enabled = new List<ManifestFile>();
            disabled = new List<ManifestFile>();

            if (files == null || files.Count == 0)
            {
                return;
            }

            if (disabledModIds == null || disabledModIds.Count == 0)
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (files[i] != null)
                    {
                        enabled.Add(files[i]);
                    }
                }

                return;
            }

            HashSet<string> off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < disabledModIds.Count; i++)
            {
                string id = disabledModIds[i];
                if (!string.IsNullOrEmpty(id))
                {
                    off.Add(id);
                }
            }

            for (int i = 0; i < files.Count; i++)
            {
                ManifestFile file = files[i];
                if (file == null)
                {
                    continue;
                }

                if (!IsPackPath(file.ModRelativePath))
                {
                    // 非包文件（DLL / cfg / version）不参与按 mod 筛选。
                    enabled.Add(file);
                    continue;
                }

                string modId = ResolveModId(manifest, file);
                if (modId.Length > 0 && off.Contains(modId))
                {
                    disabled.Add(file);
                    continue;
                }

                enabled.Add(file);
            }
        }

        /// <summary>该文件是否被 prefs 关掉（非包文件恒 false）。</summary>
        public static bool IsDisabledByPrefs(UpdateManifest manifest, ManifestFile file, IList<string> disabledModIds)
        {
            if (file == null || disabledModIds == null || disabledModIds.Count == 0)
            {
                return false;
            }

            if (!IsPackPath(file.ModRelativePath))
            {
                return false;
            }

            string modId = ResolveModId(manifest, file);
            if (modId.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < disabledModIds.Count; i++)
            {
                if (string.Equals(disabledModIds[i], modId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 一行日志用的摘要：<c>update: installing 93 pack(s), 4 disabled by prefs (UI18n packs skipped)</c>。
        /// 由调用方决定打不打（条目为 0 时调用方通常会跳过）。
        /// </summary>
        public static string DescribeInstall(int enabledPacks, int disabledPacks)
        {
            return "update: installing " + enabledPacks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " pack(s), " + disabledPacks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " disabled by prefs (UI18n packs skipped)";
        }
    }
}
