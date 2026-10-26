using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UI18n.Core.Capture;
using UI18n.Core.Dict;
using UI18n.Core.Settings;
using UI18n.Core.Submission;
using UI18n.Runtime.Settings;
using UnityEngine;

namespace UI18n.Runtime
{
    /// <summary>
    /// 一次「预览 / 导出」的结果。面板只读这些字段，不猜状态。
    /// </summary>
    internal sealed class SubmissionExportResult
    {
        /// <summary><c>ok</c>：拿到了 bundle（导出时还意味着文件已落盘）。</summary>
        internal const string OutcomeOk = "ok";

        /// <summary>玩家没同意（§6.2 默认不投）。</summary>
        internal const string OutcomeNotConsented = "not-consented";

        /// <summary>本会话没有捕获文件（<c>CaptureMisses=False</c> 或还没 flush 过）。</summary>
        internal const string OutcomeNoSource = "no-source";

        /// <summary>读到了行，但三步脱敏之后一条都不剩。</summary>
        internal const string OutcomeNoEntries = "no-entries";

        /// <summary>写盘失败（目录不可写 / 文件被占用）。</summary>
        internal const string OutcomeWriteFailed = "write-failed";

        /// <summary>bundle 没通过契约校验（正常情况下不会发生，发生了就是 bug）。</summary>
        internal const string OutcomeInvalid = "invalid";

        internal string Outcome = OutcomeNoSource;
        internal string Error = string.Empty;

        internal string Id = string.Empty;
        internal string FilePath = string.Empty;

        internal int Input;
        internal int Mods;
        internal int Entries;
        internal int Dropped;
        internal int DroppedPath;
        internal int DroppedPrivate;
        internal int DroppedSingleton;
        internal int DroppedNonProse;
        internal int DroppedAlreadyCjk;
        internal int RedactedPrivate;
        internal int SkippedInvalid;
        internal int SkippedTruncated;

        internal bool Ok
        {
            get { return string.Equals(Outcome, OutcomeOk, StringComparison.Ordinal); }
        }

        /// <summary>日志用的一行摘要（不含路径，面板另有文案）。</summary>
        internal string Describe()
        {
            return "submission: " + Outcome + ", mods=" + Mods + ", entries=" + Entries
                   + ", input=" + Input + ", dropped=" + Dropped
                   + " (path=" + DroppedPath + " private=" + DroppedPrivate + " singleton=" + DroppedSingleton
                   + " nonProse=" + DroppedNonProse + " cjk=" + DroppedAlreadyCjk + ")"
                   + ", truncated=" + SkippedTruncated + ", invalid=" + SkippedInvalid
                   + (Error.Length > 0 ? ", error=" + Error : string.Empty);
        }
    }

    /// <summary>
    /// 投稿 bundle 的客户端导出（docs/10 §6，契约冻结）。
    /// <para>
    /// 只做四件事：①门禁（同意 / 有没有捕获源）②把捕获行喂给 Core 的
    /// <see cref="SubmissionBuilder"/> ③落盘到 <c>PluginData/UI18n/submissions/</c> ④记一行日志。
    /// 排序、脱敏、canonical 序列化、id 全在 Core，所以能被单元测试逐字节钉住。
    /// </para>
    /// <para>
    /// <b>绝不联网</b>：bundle 永远先落盘，之后由玩家自己把文件拖进 GitHub Issue（§6.9）。
    /// </para>
    /// </summary>
    internal static class SubmissionExporter
    {
        /// <summary>落盘目录（<c>PluginData/UI18n/submissions</c>）。</summary>
        internal static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "PluginData"), "UI18n"),
                    "submissions");
            }
        }

        /// <summary>文件名：<c>ui18n-submission-&lt;id&gt;.json</c>（id 由内容决定，同名即同内容）。</summary>
        internal static string FileNameFor(string id)
        {
            return "ui18n-submission-" + (id ?? string.Empty) + ".json";
        }

        /// <summary>
        /// 只算不写：给面板的「确认」界面用（显示 N 个 mod / M 条原文）。
        /// </summary>
        /// <param name="configConsent">安装级默认（Config.cfg 的 <c>SubmissionConsent</c>，默认 None）。</param>
        /// <param name="language">当前游戏语言。</param>
        /// <param name="allowLongSingletons">§6.3 第三步：是否放开只出现一次的长文本。</param>
        internal static SubmissionExportResult Preview(string configConsent, string language, bool allowLongSingletons)
        {
            return Run(configConsent, language, allowLongSingletons, false);
        }

        /// <summary>算 + 落盘（<c>.tmp</c> 写了再改名，中途崩掉不会留半个 JSON 骗服务端）。</summary>
        internal static SubmissionExportResult Export(string configConsent, string language, bool allowLongSingletons)
        {
            return Run(configConsent, language, allowLongSingletons, true);
        }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static SubmissionExportResult Run(string configConsent, string language, bool allowLongSingletons, bool write)
        {
            SubmissionExportResult result = new SubmissionExportResult();

            try
            {
                Prefs prefs = PrefsRuntime.Current;
                SubmissionPrefs submission = prefs != null ? prefs.Submission : null;

                // §6.2：默认不投。prefs 里有明确决定就用它，否则看 Config.cfg 的安装级默认。
                if (!SubmissionPolicy.IsGranted(submission, configConsent))
                {
                    result.Outcome = SubmissionExportResult.OutcomeNotConsented;
                    return result;
                }

                string root;
                try
                {
                    root = KSPUtil.ApplicationRootPath;
                }
                catch (Exception)
                {
                    root = string.Empty;
                }

                if (root == null)
                {
                    root = string.Empty;
                }

                SubmissionBuildOptions options = BuildOptions(
                    submission, language, allowLongSingletons, SafeKspVersion(), SafeUi18nVersion());

                SubmissionBuildResult built = SubmissionBuilder.Build(CaptureLines(), options);
                CopyCounts(built, result);

                if (!built.Ok)
                {
                    result.Outcome = MapOutcome(built.Outcome);
                    result.Error = built.Error ?? string.Empty;
                    Log.Warn("submission: " + built.Outcome + (built.Error != null && built.Error.Length > 0 ? " (" + built.Error + ")" : string.Empty));
                    return result;
                }

                SubmissionBundle bundle = built.Bundle;
                result.Id = bundle.Id;

                if (!write)
                {
                    result.Outcome = SubmissionExportResult.OutcomeOk;
                    return result;
                }

                string directory = DirectoryPath;
                Directory.CreateDirectory(directory);
                string finalPath = Path.Combine(directory, FileNameFor(bundle.Id));
                string tmpPath = finalPath + ".tmp";

                File.WriteAllText(tmpPath, bundle.ToFileJson(), Utf8NoBom);
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(tmpPath, finalPath);

                result.FilePath = finalPath;
                result.Outcome = SubmissionExportResult.OutcomeOk;

                Log.Info("submission: exported " + bundle.Id
                         + ", mods=" + result.Mods + ", entries=" + result.Entries
                         + ", dropped=" + result.Dropped
                         + " (path=" + result.DroppedPath + " private=" + result.DroppedPrivate
                         + " singleton=" + result.DroppedSingleton + ")"
                         + ", file=" + finalPath);
                return result;
            }
            catch (Exception ex)
            {
                // 红线 6：投稿是可选功能，任何异常只记原因，绝不影响游戏主循环。
                result.Outcome = SubmissionExportResult.OutcomeWriteFailed;
                result.Error = ex.GetType().Name + ": " + ex.Message;
                Log.Warn("submission: export failed (" + result.Error + ")");
                return result;
            }
        }

        private static SubmissionBuildOptions BuildOptions(
            SubmissionPrefs submission, string language, bool allowLongSingletons, string kspVersion, string ui18nVersion)
        {
            SubmissionBuildOptions options = SubmissionBuildOptions.Default;
            options.InstallId = InstallId.Ensure(submission != null ? submission.InstallId : string.Empty);
            options.Ui18nVersion = ui18nVersion;
            options.KspVersion = kspVersion;
            options.Language = string.IsNullOrEmpty(language) ? "zh-cn" : language;
            options.CreatedAt = SubmissionBuilder.FormatNow();
            options.ConsentGrantedAt = submission != null ? submission.GrantedAt : string.Empty;
            options.PolicyVersion = SubmissionPolicy.PolicyVersion;
            options.AllowLongSingletons = allowLongSingletons;
            options.PrivateValues = CollectPrivateValues();

            Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> modIds = new List<string>(PackStats.ModIds());
            modIds.Add(ModResolver.Unattributed);

            try
            {
                PackResolution resolution = PackLocator.Resolve(ConfigLoader.PacksPath, options.Language);
                if (resolution != null && resolution.Files != null)
                {
                    for (int i = 0; i < resolution.Files.Length; i++)
                    {
                        string file = resolution.Files[i];
                        if (string.IsNullOrEmpty(file))
                        {
                            continue;
                        }

                        string modId = PackFilter.ModIdFromFileName(file);
                        if (modId.Length == 0)
                        {
                            continue;
                        }

                        if (!modIds.Contains(modId))
                        {
                            modIds.Add(modId);
                        }

                        if (!files.ContainsKey(modId))
                        {
                            files[modId] = Path.GetFileName(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("submission: pack list unavailable (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            options.ModIds = modIds;
            options.ModFileNames = files;
            return options;
        }

        private static string MapOutcome(string coreOutcome)
        {
            if (string.Equals(coreOutcome, SubmissionBuilder.OutcomeValidateFailed, StringComparison.Ordinal))
            {
                return SubmissionExportResult.OutcomeInvalid;
            }

            if (string.Equals(coreOutcome, SubmissionBuilder.OutcomeInvalid, StringComparison.Ordinal))
            {
                return SubmissionExportResult.OutcomeInvalid;
            }

            if (string.Equals(coreOutcome, SubmissionBuilder.OutcomeNoEntries, StringComparison.Ordinal))
            {
                return SubmissionExportResult.OutcomeNoEntries;
            }

            return SubmissionExportResult.OutcomeNoSource;
        }

        private static void CopyCounts(SubmissionBuildResult built, SubmissionExportResult result)
        {
            result.Input = built.Input;
            result.Mods = built.Mods;
            result.Entries = built.Entries;
            result.SkippedInvalid = built.SkippedInvalid;
            result.SkippedTruncated = built.SkippedTruncated;

            SubmissionFilterResult filter = built.Filter;
            if (filter != null)
            {
                result.Dropped = filter.DroppedTotal;
                result.DroppedPath = filter.DroppedPath;
                result.DroppedPrivate = filter.DroppedPrivate;
                result.DroppedSingleton = filter.DroppedSingleton;
                result.DroppedNonProse = filter.DroppedNonProse;
                result.DroppedAlreadyCjk = filter.DroppedAlreadyCjk;
                result.RedactedPrivate = filter.RedactedPrivate;
            }
        }

        /// <summary>
        /// 本会话的捕获行：已落盘的部分（<c>MissCapture.FilePath</c>）+ 还没 flush 的缓冲。
        /// 逐行流式读，不整文件进内存（单次会话预算 64 MiB）。
        /// </summary>
        private static IEnumerable<string> CaptureLines()
        {
            string path = MissCapture.FilePath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        yield return line;
                    }
                }
            }

            CaptureStore store = MissCapture.Store;
            if (store != null)
            {
                string[] pending = store.PendingJsonLines();
                for (int i = 0; i < pending.Length; i++)
                {
                    yield return pending[i];
                }
            }
        }

        /// <summary>
        /// 玩家私有标识（§6.3 第二步）。全部读取都是只读游戏状态 + try/catch：
        /// 拿不到就少一条替换规则，绝不因此中断投稿。
        /// </summary>
        private static IList<SubmissionPrivateValue> CollectPrivateValues()
        {
            List<SubmissionPrivateValue> values = new List<SubmissionPrivateValue>(16);

            try
            {
                AddPrivate(values, SubmissionPrivateKind.Save, HighLogic.SaveFolder);
            }
            catch (Exception)
            {
            }

            try
            {
                if (HighLogic.CurrentGame != null)
                {
                    AddPrivate(values, SubmissionPrivateKind.Save, HighLogic.CurrentGame.Title);
                }
            }
            catch (Exception)
            {
            }

            try
            {
                Vessel active = FlightGlobals.ActiveVessel;
                if (active != null)
                {
                    AddPrivate(values, SubmissionPrivateKind.Vessel, active.vesselName);

                    List<ProtoCrewMember> crew = active.GetVesselCrew();
                    if (crew != null)
                    {
                        for (int i = 0; i < crew.Count; i++)
                        {
                            if (crew[i] != null)
                            {
                                AddPrivate(values, SubmissionPrivateKind.Kerbal, crew[i].name);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            try
            {
                List<Vessel> loaded = FlightGlobals.VesselsLoaded;
                if (loaded != null)
                {
                    for (int i = 0; i < loaded.Count; i++)
                    {
                        if (loaded[i] != null)
                        {
                            AddPrivate(values, SubmissionPrivateKind.Vessel, loaded[i].vesselName);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            try
            {
                AddPrivate(values, SubmissionPrivateKind.UserName, Environment.UserName);
            }
            catch (Exception)
            {
            }

            return values;
        }

        private static void AddPrivate(List<SubmissionPrivateValue> values, SubmissionPrivateKind kind, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            value = value.Trim();
            if (value.Length < SubmissionFilter.MinPrivateValueLength)
            {
                return;
            }

            for (int i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i].Value, value, StringComparison.OrdinalIgnoreCase)
                    && values[i].Kind == kind)
                {
                    return;
                }
            }

            values.Add(SubmissionPrivateValue.Of(kind, value));
        }

        private static string SafeKspVersion()
        {
            try
            {
                return Application.version ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeUi18nVersion()
        {
            try
            {
                string version = UI18nAddon.Version;
                if (string.IsNullOrEmpty(version))
                {
                    return string.Empty;
                }

                int plus = version.IndexOf('+');
                return plus > 0 ? version.Substring(0, plus) : version;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
