using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UI18n.Core.Dict;
using UI18n.Core.Update;

namespace UI18n.Runtime.Update
{
    /// <summary>一次更新尝试的结果（只用于日志与离线验收，不参与任何游戏逻辑）。</summary>
    internal sealed class UpdateResult
    {
        internal bool Ok;
        internal string Outcome;
        internal string Version;
        internal int InstalledFiles;
        internal string FailureReason;

        internal string Describe()
        {
            return "outcome=" + (Outcome ?? "(unknown)")
                + ", version=" + (string.IsNullOrEmpty(Version) ? "(none)" : Version)
                + ", files=" + InstalledFiles.ToString(CultureInfo.InvariantCulture)
                + (string.IsNullOrEmpty(FailureReason) ? string.Empty : ", reason=" + FailureReason);
        }
    }

    /// <summary>
    /// 更新流程编排（**纯逻辑 + IO，不碰任何 UnityEngine 类型**，所以能被离线验收工具直接驱动）。
    /// <para>
    /// 硬约束：
    /// 1) 任何失败只 Warn + 保留旧包，<b>绝不抛异常</b>（异常在 <see cref="Run"/> 全部兜住）；
    /// 2) <b>先全量校验再写盘</b>：zip 里所有要装的包先在内存里按 manifest 的 bytes + sha256 验完，
    ///    任何一条不过就一个字节都不写；
    /// 3) 单个文件写入是原子替换（先写 <c>.tmp</c> 再 rename），不会出现半个文件；
    /// 4) 只装 <c>Packs/&lt;lang&gt;/&lt;modId&gt;.jsonl</c>，绝不碰 Plugins / dll / 配置。
    /// </para>
    /// </summary>
    internal static class UpdateWorkflow
    {
        /// <summary>执行的入口：永不抛。</summary>
        internal static UpdateResult Run(UpdateEnvironment environment)
        {
            try
            {
                return RunCore(environment);
            }
            catch (Exception ex)
            {
                UpdateLog.Warn("update: failed (" + ex.GetType().Name + ": " + ex.Message + "), existing packs were left untouched");
                return Failure("exception: " + ex.GetType().Name);
            }
        }

        private static UpdateResult RunCore(UpdateEnvironment env)
        {
            if (env == null)
            {
                return Failure("no-environment");
            }

            // ---- 门禁一：不配地址就完全不联网 ----
            if (string.IsNullOrEmpty(env.BaseUrl) || env.BaseUrl.Trim().Length == 0)
            {
                UpdateLog.Info("update: disabled (UpdateBaseUrl empty)");
                return new UpdateResult { Ok = true, Outcome = "disabled", FailureReason = "UpdateBaseUrl empty" };
            }

            // ---- 门禁二：玩家关了检查 ----
            if (!env.CheckForUpdates)
            {
                UpdateLog.Info("update: disabled (CheckForUpdates=False)");
                return new UpdateResult { Ok = true, Outcome = "disabled", FailureReason = "CheckForUpdates=False" };
            }

            // ---- 门禁三：只允许 https，或 http 回环 ----
            string urlReason;
            if (!UpdateUrlGate.IsAllowedBaseUrl(env.BaseUrl, out urlReason))
            {
                // 把地址原样打出来：v1.5 之前只打原因码，遇到「地址明明合法却 invalid-url」时无从下手
                UpdateLog.Warn("update: disabled (" + urlReason + ", UpdateBaseUrl=\"" + (env.BaseUrl ?? "(null)")
                               + "\"), only https:// or http://127.0.0.1/localhost are accepted; nothing was requested");
                return new UpdateResult { Ok = false, Outcome = "disabled", FailureReason = urlReason };
            }

            if (!UpdateUrlGate.IsValidChannel(env.Channel))
            {
                UpdateLog.Warn("update: disabled (invalid-channel: " + (env.Channel ?? "(null)") + ")");
                return new UpdateResult { Ok = false, Outcome = "disabled", FailureReason = UpdateUrlGate.ReasonBadChannel };
            }

            string targetLanguage = PackLocator.NormalizeLanguage(env.Language);
            if (targetLanguage.Length == 0 || !ZipSafety.IsValidLanguageTag(targetLanguage))
            {
                UpdateLog.Warn("update: skipped (language '" + (env.Language ?? "(null)") + "' is not a usable language tag)");
                return new UpdateResult { Ok = false, Outcome = "skipped", FailureReason = "bad-language" };
            }

            string baseUrl = UpdateUrlGate.NormalizeBaseUrl(env.BaseUrl);
            UpdateLog.Info("update: enabled, " + env.Describe());

            // ---- 到期判断（update-state.json） ----
            UpdateState previous = LoadState(env);
            DateTime nowUtc = DateTime.UtcNow;
            if (!previous.IsCheckDue(nowUtc, env.CheckDays))
            {
                TimeSpan since = nowUtc - previous.LastCheckUtc;
                UpdateLog.Info("update: skipped (checked " + since.TotalDays.ToString("0.0", CultureInfo.InvariantCulture)
                    + " day(s) ago, UpdateCheckDays=" + env.CheckDays.ToString(CultureInfo.InvariantCulture) + ")");
                return new UpdateResult { Ok = true, Outcome = "not-due", Version = previous.LastVersion };
            }

            // ---- 取 manifest ----
            string manifestUrl = UpdateUrlGate.BuildManifestUrl(baseUrl, env.Channel);
            UpdateLog.Info("update: GET " + manifestUrl + " (timeout " + env.TimeoutSeconds.ToString(CultureInfo.InvariantCulture) + "s)");

            UpdateHttp.Response response = UpdateHttp.Get(manifestUrl, env.TimeoutSeconds);
            if (!response.Ok)
            {
                UpdateLog.Warn("update: manifest request failed (" + DescribeHttpFailure(response) + ") -> keeping existing packs");
                return Failure(DescribeHttpFailure(response));
            }

            UpdateManifest manifest;
            string manifestError;
            if (!UpdateManifest.TryParse(response.BodyText, out manifest, out manifestError))
            {
                UpdateLog.Warn("update: manifest rejected (" + manifestError + ") -> keeping existing packs");
                return Failure("manifest: " + manifestError);
            }

            UpdateLog.Info(manifest.Describe());

            // ---- 版本比较 ----
            ReleaseVersion local = ReleaseVersion.ParseOrNone(env.LocalVersion);
            ReleaseVersion remote = manifest.ParsedVersion;
            if (!local.IsValid)
            {
                UpdateLog.Warn("update: local version '" + env.LocalVersion + "' is not a release version, comparing as 0.0.0");
            }

            if (!remote.IsNewerThan(local))
            {
                UpdateLog.Info("update: up to date (local " + Display(local) + ", remote " + manifest.Version + ")");
                SaveState(env, manifest.Version, UpdateState.ResultUpToDate, nowUtc);
                return new UpdateResult { Ok = true, Outcome = UpdateState.ResultUpToDate, Version = manifest.Version };
            }

            UpdateLog.Info("update: version " + manifest.Version + " available (local " + Display(local) + ")");

            if (!env.AutoDownload)
            {
                UpdateLog.Info("update: download skipped (AutoDownload=False); set AutoDownload=True to install automatically");
                SaveState(env, manifest.Version, UpdateState.ResultUpdateAvailable, nowUtc);
                return new UpdateResult { Ok = true, Outcome = UpdateState.ResultUpdateAvailable, Version = manifest.Version };
            }

            // ---- 下载 zip ----
            string assetUrl = UpdateUrlGate.BuildAssetUrl(baseUrl, env.Channel, manifest.ZipName);
            UpdateLog.Info("update: downloading " + manifest.ZipName + " (" + Sha256Hex.DescribeBytes(manifest.ZipBytes) + " expected)");

            UpdateHttp.Response download = UpdateHttp.Get(assetUrl, env.TimeoutSeconds);
            if (!download.Ok)
            {
                UpdateLog.Warn("update: download failed (" + DescribeHttpFailure(download) + ") -> keeping existing packs");
                return Failure(DescribeHttpFailure(download));
            }

            string verifyReason;
            if (!UpdateVerify.CheckZip(download.Body, manifest, out verifyReason))
            {
                UpdateLog.Warn("update: zip verification failed (" + verifyReason + ") -> keeping existing packs");
                return Failure("zip: " + verifyReason);
            }

            UpdateLog.Info("update: zip verified (" + Sha256Hex.DescribeBytes(download.Body.LongLength) + ", sha256 " + Prefix(manifest.ZipSha256) + ")");
            SaveDownload(env, manifest, download.Body);

            // ---- 解析 zip ----
            ZipArchive archive;
            string zipError;
            if (!ZipArchiveReader.TryRead(download.Body, out archive, out zipError))
            {
                UpdateLog.Warn("update: zip rejected (" + zipError + ") -> keeping existing packs");
                return Failure("zip-read: " + zipError);
            }

            // ---- 先全量校验，一个字节都不写 ----
            List<PlannedFile> planned = new List<PlannedFile>();
            for (int i = 0; i < manifest.Files.Count; i++)
            {
                ManifestFile file = manifest.Files[i];
                string language;
                string fileName;
                string packReason;
                if (!ZipSafety.TryGetPackTarget(file.ModRelativePath, out language, out fileName, out packReason))
                {
                    continue; // 不是包文件（config / dll / 文档）——本次更新一概不碰
                }

                if (!LanguageMatches(language, targetLanguage))
                {
                    continue; // 别的语言
                }

                byte[] content;
                string extractError;
                if (!TryExtractEntry(archive, file, out content, out extractError))
                {
                    UpdateLog.Warn("update: entry extraction failed (" + file.Path + ": " + extractError + ") -> nothing was written");
                    return Failure("extract: " + extractError);
                }

                string entryReason;
                if (!UpdateVerify.CheckEntry(content, file, out entryReason))
                {
                    UpdateLog.Warn("update: entry verification failed (" + file.Path + ": " + entryReason + ") -> nothing was written");
                    return Failure("entry: " + entryReason);
                }

                planned.Add(new PlannedFile { FileName = fileName, Language = language, Content = content, Bytes = file.Bytes });
            }

            if (planned.Count == 0)
            {
                UpdateLog.Warn("update: nothing to install (manifest has no Packs/<lang>/*.jsonl for lang=" + targetLanguage + ")");
                SaveState(env, manifest.Version, UpdateState.ResultFailed, nowUtc);
                return new UpdateResult { Ok = false, Outcome = "nothing-to-install", Version = manifest.Version, FailureReason = "no-pack-files" };
            }

            // ---- 写盘（每文件原子替换） ----
            string packsDirectory = Path.Combine(Path.Combine(env.ModRoot, "Packs"), targetLanguage);
            int installed = InstallFiles(env, packsDirectory, planned);
            if (installed < 0)
            {
                return Failure("install");
            }

            UpdateLog.Info("update: installed " + installed.ToString(CultureInfo.InvariantCulture) + " file(s), version " + manifest.Version);
            UpdateLog.Info("update: packs directory " + packsDirectory);
            SaveState(env, manifest.Version, UpdateState.ResultUpdateAvailable, nowUtc);

            UpdateResult result = new UpdateResult
            {
                Ok = true,
                Outcome = "installed",
                Version = manifest.Version,
                InstalledFiles = installed
            };

            UpdateLog.Info("update: done (" + result.Describe() + ")");
            return result;
        }

        /// <summary>zip 里取一条的字节：先按 manifest 里写的完整路径找，找不到退化到「相对 mod 根」的路径。</summary>
        private static bool TryExtractEntry(ZipArchive archive, ManifestFile file, out byte[] content, out string error)
        {
            if (archive.TryExtract(file.Path, out content, out error))
            {
                return true;
            }

            string fallbackError;
            byte[] fallbackContent;
            if (archive.TryExtract(file.ModRelativePath, out fallbackContent, out fallbackError))
            {
                content = fallbackContent;
                error = null;
                return true;
            }

            content = null;
            error = error ?? fallbackError;
            return false;
        }

        /// <summary>包的语言目录与目标语言是否匹配（允许 zh-cn 回落到 zh，与 PackLocator 同向）。</summary>
        private static bool LanguageMatches(string packLanguage, string targetLanguage)
        {
            if (string.Equals(packLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int dash = targetLanguage.IndexOf('-');
            if (dash > 0)
            {
                string primary = targetLanguage.Substring(0, dash);
                if (string.Equals(packLanguage, primary, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>逐个文件写盘；返回写入个数，失败返回 -1（已成功写入的保留，日志会说明）。</summary>
        private static int InstallFiles(UpdateEnvironment env, string packsDirectory, List<PlannedFile> planned)
        {
            string fullRoot;
            try
            {
                Directory.CreateDirectory(packsDirectory);
                fullRoot = Path.GetFullPath(packsDirectory);
            }
            catch (Exception ex)
            {
                UpdateLog.Warn("update: cannot prepare packs directory ("
                    + ex.GetType().Name + ": " + ex.Message + ") -> keeping existing packs");
                return -1;
            }

            int installed = 0;
            for (int i = 0; i < planned.Count; i++)
            {
                PlannedFile file = planned[i];
                string target = Path.Combine(fullRoot, file.FileName);
                string targetFull = Path.GetFullPath(target);

                // 双保险：文件名已过 ZipSafety 校验，这里再确认落点确实在包目录里
                if (!targetFull.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateLog.Warn("update: refusing to write outside the packs directory (" + file.FileName + ") -> keeping existing packs");
                    return -1;
                }

                string temp = targetFull + ".tmp";
                try
                {
                    File.WriteAllBytes(temp, file.Content);
                    if (File.Exists(targetFull))
                    {
                        try
                        {
                            File.Replace(temp, targetFull, null);
                        }
                        catch (Exception replaceEx)
                        {
                            // 某些文件系统不支持 Replace：退化到「先删再移」，仍然只暴露完整文件
                            UpdateLog.Info("update: File.Replace unsupported (" + replaceEx.GetType().Name + "), falling back to delete+move");
                            File.Delete(targetFull);
                            File.Move(temp, targetFull);
                        }
                    }
                    else
                    {
                        File.Move(temp, targetFull);
                    }

                    installed++;
                }
                catch (Exception ex)
                {
                    TryDelete(temp);
                    UpdateLog.Warn("update: write failed (" + file.FileName + ": " + ex.GetType().Name + ": " + ex.Message
                        + ") -> " + installed.ToString(CultureInfo.InvariantCulture) + " of " + planned.Count.ToString(CultureInfo.InvariantCulture) + " file(s) were installed");
                    return -1;
                }
            }

            return installed;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        /// <summary>把 zip 存到 downloads/ 留证据（失败只 Warn，不影响安装结果）。</summary>
        private static void SaveDownload(UpdateEnvironment env, UpdateManifest manifest, byte[] body)
        {
            try
            {
                Directory.CreateDirectory(env.DownloadsDirectory);
                string path = Path.Combine(env.DownloadsDirectory, manifest.ZipName);
                File.WriteAllBytes(path, body);
                UpdateLog.Info("update: saved " + Sha256Hex.DescribeBytes(body.LongLength) + " to " + path);
            }
            catch (Exception ex)
            {
                UpdateLog.Warn("update: could not cache the download (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        private static UpdateState LoadState(UpdateEnvironment env)
        {
            string path = env.StateFilePath;
            UpdateState state;
            string error;
            try
            {
                if (!File.Exists(path))
                {
                    return UpdateState.Empty;
                }

                if (UpdateState.TryParse(File.ReadAllText(path), out state, out error))
                {
                    return state;
                }

                UpdateLog.Warn("update: state file ignored (" + error + "), treating this run as the first check");
            }
            catch (Exception ex)
            {
                UpdateLog.Warn("update: state file unreadable (" + ex.GetType().Name + ": " + ex.Message + "), treating this run as the first check");
            }

            return UpdateState.Empty;
        }

        private static void SaveState(UpdateEnvironment env, string version, string result, DateTime whenUtc)
        {
            try
            {
                Directory.CreateDirectory(env.PluginDataRoot);
                UpdateState state = UpdateState.Empty.WithCheck(whenUtc, version, result);
                File.WriteAllText(env.StateFilePath, state.ToJson());
                UpdateLog.Info("update: state " + state.ToString());
            }
            catch (Exception ex)
            {
                UpdateLog.Warn("update: could not write state file (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        private static string DescribeHttpFailure(UpdateHttp.Response response)
        {
            if (response == null)
            {
                return "no-response";
            }

            if (!string.IsNullOrEmpty(response.Error))
            {
                return response.Error;
            }

            return "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture);
        }

        private static string Display(ReleaseVersion version)
        {
            return version.IsValid ? version.ToString() : "0.0.0";
        }

        private static string Prefix(string sha256)
        {
            if (string.IsNullOrEmpty(sha256))
            {
                return "(none)";
            }

            return sha256.Length <= 8 ? sha256 : sha256.Substring(0, 8) + "...";
        }

        private static UpdateResult Failure(string reason)
        {
            return new UpdateResult { Ok = false, Outcome = UpdateState.ResultFailed, FailureReason = reason };
        }

        private sealed class PlannedFile
        {
            internal string FileName;
            internal string Language;
            internal byte[] Content;
            internal long Bytes;
        }
    }
}
