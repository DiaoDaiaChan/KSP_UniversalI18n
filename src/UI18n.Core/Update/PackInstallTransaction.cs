using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace UI18n.Core.Update
{
    /// <summary>一条待装文件（文件名只允许是包目录里的裸文件名）。</summary>
    public sealed class InstallPlanFile
    {
        private readonly string _fileName;
        private readonly byte[] _content;

        /// <summary>构造一条待装文件。</summary>
        public InstallPlanFile(string fileName, byte[] content)
        {
            _fileName = fileName;
            _content = content;
        }

        /// <summary>包目录里的裸文件名（不允许带目录分隔符）。</summary>
        public string FileName { get { return _fileName; } }

        /// <summary>文件内容（已通过 manifest 的 bytes + sha256 校验）。</summary>
        public byte[] Content { get { return _content; } }
    }

    /// <summary>一次安装事务的结果。</summary>
    public sealed class InstallTransactionResult
    {
        /// <summary>是否成功（失败时若 <see cref="RolledBack"/> 为 true，磁盘已恢复到安装前）。</summary>
        public bool Ok { get; internal set; }

        /// <summary>失败原因码（成功时为空串）。</summary>
        public string Reason { get; internal set; }

        /// <summary>真正写进磁盘的文件数。</summary>
        public int Installed { get; internal set; }

        /// <summary>内容与磁盘上现有文件一模一样、被跳过的文件数。</summary>
        public int Unchanged { get; internal set; }

        /// <summary>回滚时成功还原的文件数。</summary>
        public int Restored { get; internal set; }

        /// <summary>是否发生了回滚（失败才会是 true）。</summary>
        public bool RolledBack { get; internal set; }

        /// <summary>回滚后仍有文件没能还原的名字（正常情况下为空）。</summary>
        public IList<string> Unrestored { get; internal set; }

        /// <summary>写完之后逐文件回读比长度是否全对（只在没有回滚时才有意义）。</summary>
        public bool Verified { get; internal set; }

        /// <summary>本次涉及的文件名（按计划顺序）。</summary>
        public IList<string> Files { get; internal set; }

        /// <summary>一行日志用的摘要。</summary>
        public string Describe()
        {
            string text = "install: " + Installed.ToString(CultureInfo.InvariantCulture) + " written, "
                + Unchanged.ToString(CultureInfo.InvariantCulture) + " unchanged, verified="
                + (Verified ? "true" : "false") + ", rolledBack=" + (RolledBack ? "true" : "false");

            if (!string.IsNullOrEmpty(Reason))
            {
                text += ", reason=" + Reason;
            }

            if (RolledBack)
            {
                text += ", restored=" + Restored.ToString(CultureInfo.InvariantCulture)
                    + (Unrestored != null && Unrestored.Count > 0 ? ", unrestored=" + Unrestored.Count.ToString(CultureInfo.InvariantCulture) : string.Empty);
            }

            return text;
        }
    }

    /// <summary>
    /// 「要么全装、要么一个字节都不动」的包目录写入事务（**纯 IO，不碰 UnityEngine，可离线单测**）。
    /// <para>
    /// 为什么要有它：下载与校验再严，也拦不住写盘阶段出问题（磁盘满、杀软锁文件、玩家中途关机）。
    /// 老实现是「逐个文件原子替换」，单个文件确实不会出现半个，但**整批**可能停在半路 ——
    /// 目录里一半是新版包、一半是旧版包，字典版本错位比「全旧」更难查。这里改成三段式：
    /// </para>
    /// <list type="number">
    /// <item>**预备**（staging）：所有内容先写成 <c>&lt;name&gt;.ui18n-new</c>，任何一条写失败就只删临时文件，旧包一个都不碰；</item>
    /// <item>**提交**（commit）：逐条用 <see cref="File.Replace(string,string,string)"/>（不支持时退化成两次 Move）把临时文件换上去，旧文件落到 <c>&lt;name&gt;.ui18n-bak</c>；</item>
    /// <item>**校验 + 清理**：回读每条长度确认写全，再删备份。任何一步失败就按逆序把备份换回去。</item>
    /// </list>
    /// <para>
    /// 进程在提交中途被杀时，目录里会留下 <c>.ui18n-new</c> / <c>.ui18n-bak</c>：
    /// 这两个后缀**不以 <c>.jsonl</c> 结尾**，所以 <see cref="PackLocator"/> 的 <c>*.jsonl</c> 扫描不会把半个文件当包读；
    /// 下次启动调 <see cref="CleanStale"/> 收尾（有备份且目标缺失就还原，否则删掉临时物）。
    /// </para>
    /// </summary>
    public static class PackInstallTransaction
    {
        /// <summary>临时文件后缀。</summary>
        public const string TempSuffix = ".ui18n-new";

        /// <summary>备份文件后缀。</summary>
        public const string BackupSuffix = ".ui18n-bak";

        /// <summary>通过。</summary>
        public const string ReasonNone = "";

        /// <summary>文件名不合法（带目录、含 .. 、为空）。</summary>
        public const string ReasonBadName = "bad-name";

        /// <summary>目录建不出来。</summary>
        public const string ReasonNoDirectory = "no-directory";

        /// <summary>预备阶段写临时文件失败（旧包未受影响）。</summary>
        public const string ReasonStageFailed = "stage-failed";

        /// <summary>提交阶段失败（已回滚）。</summary>
        public const string ReasonCommitFailed = "commit-failed";

        /// <summary>提交后发现内容长度不对（已回滚）。</summary>
        public const string ReasonVerifyFailed = "verify-failed";

        /// <summary>回滚本身没做干净（正常情况下不该出现）。</summary>
        public const string ReasonRollbackIncomplete = "rollback-incomplete";

        /// <summary>文件名是否安全（裸文件名、不含分隔符、不是 . 或 ..）。</summary>
        public static bool IsSafeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName.Length > 200)
            {
                return false;
            }

            if (fileName == "." || fileName == "..")
            {
                return false;
            }

            if (fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
            {
                return false;
            }

            if (fileName.IndexOf(':') >= 0)
            {
                return false;
            }

            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            // 后备保险：拼接后必须还在同一个目录里
            return true;
        }

        /// <summary>
        /// 把 <paramref name="files"/> 全量装进 <paramref name="directory"/>：要么全部成功，要么磁盘回到调用前的样子。
        /// <b>永不抛</b>，失败给原因码。
        /// </summary>
        public static InstallTransactionResult Commit(string directory, IList<InstallPlanFile> files)
        {
            InstallTransactionResult result = new InstallTransactionResult
            {
                Ok = false,
                Reason = ReasonNone,
                Unrestored = new List<string>(),
                Files = new List<string>()
            };

            if (string.IsNullOrEmpty(directory))
            {
                result.Reason = ReasonNoDirectory + " (empty)";
                return result;
            }

            if (files == null || files.Count == 0)
            {
                result.Ok = true;
                result.Verified = true;
                return result;
            }

            for (int i = 0; i < files.Count; i++)
            {
                InstallPlanFile file = files[i];
                if (file == null || !IsSafeFileName(file.FileName) || file.Content == null)
                {
                    result.Reason = ReasonBadName + " (" + (file == null || file.FileName == null ? "(null)" : file.FileName) + ")";
                    return result;
                }

                result.Files.Add(file.FileName);
            }

            string root;
            try
            {
                Directory.CreateDirectory(directory);
                root = Path.GetFullPath(directory);
            }
            catch (Exception ex)
            {
                result.Reason = ReasonNoDirectory + " (" + ex.GetType().Name + ": " + ex.Message + ")";
                return result;
            }

            List<Staged> staged = new List<Staged>(files.Count);

            // ---- 预备：先全部写成临时文件，任何一条失败就只清临时文件 ----
            for (int i = 0; i < files.Count; i++)
            {
                InstallPlanFile file = files[i];
                string target = Path.Combine(root, file.FileName);
                string targetFull = Path.GetFullPath(target);
                if (!targetFull.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetFull, root, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteTemps(staged);
                    result.Reason = ReasonBadName + " (" + file.FileName + " escapes the directory)";
                    return result;
                }

                Staged item = new Staged();
                item.Target = targetFull;
                item.Temp = targetFull + TempSuffix;
                item.Backup = targetFull + BackupSuffix;
                item.ExpectedBytes = file.Content.LongLength;

                if (File.Exists(targetFull) && SameBytes(targetFull, file.Content))
                {
                    item.Unchanged = true;
                    result.Unchanged++;
                    staged.Add(item);
                    continue;
                }

                try
                {
                    File.WriteAllBytes(item.Temp, file.Content);
                }
                catch (Exception ex)
                {
                    TryDelete(item.Temp);
                    DeleteTemps(staged);
                    result.Reason = ReasonStageFailed + " (" + file.FileName + ": " + ex.GetType().Name + ": " + ex.Message + ")";
                    return result;
                }

                staged.Add(item);
            }

            // ---- 提交：逐条换上去，旧文件留备份 ----
            for (int i = 0; i < staged.Count; i++)
            {
                Staged item = staged[i];
                if (item.Unchanged)
                {
                    continue;
                }

                string error;
                if (!Swap(item, out error))
                {
                    TryDelete(item.Temp);
                    result.Reason = ReasonCommitFailed + " (" + item.Target + ": " + error + ")";
                    Rollback(staged, i, result);
                    return result;
                }

                item.Committed = true;
                result.Installed++;
            }

            // ---- 校验：回读每条长度 ----
            bool verified = true;
            for (int i = 0; i < staged.Count; i++)
            {
                Staged item = staged[i];
                if (item.Unchanged)
                {
                    continue;
                }

                try
                {
                    FileInfo info = new FileInfo(item.Target);
                    if (!info.Exists || info.Length != item.ExpectedBytes)
                    {
                        verified = false;
                        result.Reason = ReasonVerifyFailed + " (" + item.Target + ": "
                            + (info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) : "missing") + " B, expected "
                            + item.ExpectedBytes.ToString(CultureInfo.InvariantCulture) + " B)";
                        break;
                    }
                }
                catch (Exception ex)
                {
                    verified = false;
                    result.Reason = ReasonVerifyFailed + " (" + item.Target + ": " + ex.GetType().Name + ")";
                    break;
                }
            }

            if (!verified)
            {
                Rollback(staged, staged.Count, result);
                return result;
            }

            // ---- 清理备份 ----
            for (int i = 0; i < staged.Count; i++)
            {
                if (staged[i].Committed || File.Exists(staged[i].Backup))
                {
                    TryDelete(staged[i].Backup);
                }
            }

            result.Ok = true;
            result.Verified = true;
            result.Reason = ReasonNone;
            return result;
        }

        /// <summary>
        /// 收拾上一次被打断留下的临时物：有备份且目标缺失就还原，其余临时文件删掉。
        /// 返回删掉的文件数，<paramref name="restored"/> 是还原的个数。<b>永不抛</b>。
        /// </summary>
        public static int CleanStale(string directory, out int restored)
        {
            restored = 0;

            if (string.IsNullOrEmpty(directory))
            {
                return 0;
            }

            string root;
            try
            {
                if (!Directory.Exists(directory))
                {
                    return 0;
                }

                root = Path.GetFullPath(directory);
            }
            catch
            {
                return 0;
            }

            int removed = 0;
            string[] names;
            try
            {
                names = Directory.GetFiles(root);
            }
            catch
            {
                return 0;
            }

            for (int i = 0; i < names.Length; i++)
            {
                string path = names[i];
                string fileName = Path.GetFileName(path);

                if (fileName.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    string target = path.Substring(0, path.Length - BackupSuffix.Length);
                    try
                    {
                        if (!File.Exists(target))
                        {
                            File.Move(path, target);
                            restored++;
                            continue;
                        }

                        File.Delete(path);
                        removed++;
                    }
                    catch
                    {
                    }

                    continue;
                }

                if (fileName.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryDelete(path))
                    {
                        removed++;
                    }
                }
            }

            return removed;
        }

        private static void Rollback(List<Staged> staged, int upTo, InstallTransactionResult result)
        {
            result.RolledBack = true;

            for (int i = upTo - 1; i >= 0; i--)
            {
                Staged item = staged[i];
                if (item.Unchanged || (!item.Committed && !File.Exists(item.Backup) && !File.Exists(item.Temp)))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(item.Backup))
                    {
                        if (File.Exists(item.Target))
                        {
                            File.Delete(item.Target);
                        }

                        File.Move(item.Backup, item.Target);
                        result.Restored++;
                    }
                    else
                    {
                        // 新装的文件（原来没有），回滚就是删掉
                        if (File.Exists(item.Target))
                        {
                            File.Delete(item.Target);
                            result.Restored++;
                        }
                    }
                }
                catch
                {
                    result.Unrestored.Add(item.Target);
                }

                TryDelete(item.Temp);
            }

            if (result.Unrestored.Count > 0)
            {
                result.Reason = ReasonRollbackIncomplete + " (" + result.Unrestored.Count.ToString(CultureInfo.InvariantCulture)
                    + " file(s) could not be restored; reason before rollback: " + result.Reason + ")";
            }
        }

        private static void DeleteTemps(List<Staged> staged)
        {
            for (int i = 0; i < staged.Count; i++)
            {
                TryDelete(staged[i].Temp);
            }
        }

        private static bool Swap(Staged item, out string error)
        {
            error = null;

            try
            {
                if (File.Exists(item.Target))
                {
                    try
                    {
                        // 原子替换 + 顺手把旧文件留成备份
                        File.Replace(item.Temp, item.Target, item.Backup);
                        return true;
                    }
                    catch (Exception replaceEx)
                    {
                        // 某些文件系统不支持 Replace：退化到「旧文件挪成备份、新文件挪上来」
                        File.Move(item.Target, item.Backup);
                        try
                        {
                            File.Move(item.Temp, item.Target);
                            return true;
                        }
                        catch (Exception moveEx)
                        {
                            error = replaceEx.GetType().Name + " then " + moveEx.GetType().Name + ": " + moveEx.Message;
                            return false;
                        }
                    }
                }

                File.Move(item.Temp, item.Target);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static bool SameBytes(string path, byte[] expected)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length != expected.LongLength)
                {
                    return false;
                }

                byte[] current = File.ReadAllBytes(path);
                if (current.LongLength != expected.LongLength)
                {
                    return false;
                }

                for (int i = 0; i < current.Length; i++)
                {
                    if (current[i] != expected[i])
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private sealed class Staged
        {
            internal string Target;
            internal string Temp;
            internal string Backup;
            internal long ExpectedBytes;
            internal bool Unchanged;
            internal bool Committed;
        }
    }
}
