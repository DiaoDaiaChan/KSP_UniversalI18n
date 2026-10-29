using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UI18n.Core.Update;
using Xunit;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// 包目录写入事务（M4-d 的 Core 半边，docs/11 §6「失败必须回滚」）。
    /// <para>
    /// 这里验的是「要么全装、要么一个字节都不动」，不是单个文件的原子性（那个 <c>File.Replace</c> 本来就保证）：
    /// 真正会坏事的场景是<b>整批停在半路</b>——目录里一半新版一半旧版，字典版本错位比全旧更难查。
    /// 所以每个失败用例都要断言「旧内容还在 / 新装的文件被删掉 / 没留下 .ui18n-new、.ui18n-bak」。
    /// </para>
    /// </summary>
    public class PackInstallTransactionTests
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private sealed class TempDirectory : IDisposable
        {
            internal readonly string Path;

            internal TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ui18n-install-" + Guid.NewGuid().ToString("n"));
                Directory.CreateDirectory(Path);
            }

            internal string File(string name)
            {
                return System.IO.Path.Combine(Path, name);
            }

            internal string[] Entries()
            {
                string[] all = Directory.GetFileSystemEntries(Path);
                Array.Sort(all, StringComparer.Ordinal);
                return all;
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, true);
                }
                catch
                {
                }
            }
        }

        private static InstallPlanFile Plan(string name, string text)
        {
            return new InstallPlanFile(name, Utf8.GetBytes(text));
        }

        private static string Read(string path)
        {
            return Utf8.GetString(File.ReadAllBytes(path));
        }

        // ---------- 正常路径 ----------

        [Fact]
        public void Commit_WritesEveryFile()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                InstallTransactionResult result = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>
                {
                    Plan("Squad.jsonl", "{\"h\":\"a\"}\n"),
                    Plan("TUFX.jsonl", "{\"h\":\"b\"}\n")
                });

                Assert.True(result.Ok);
                Assert.Equal(2, result.Installed);
                Assert.Equal(0, result.Unchanged);
                Assert.False(result.RolledBack);
                Assert.True(result.Verified);
                Assert.Equal("{\"h\":\"a\"}\n", Read(dir.File("Squad.jsonl")));
                Assert.Equal("{\"h\":\"b\"}\n", Read(dir.File("TUFX.jsonl")));
            }
        }

        [Fact]
        public void Commit_WritesExactBytes()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                byte[] payload = new byte[] { 0x00, 0x01, 0x0d, 0x0a, 0xff, 0x20 };
                InstallTransactionResult result = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>
                {
                    new InstallPlanFile("Bin.jsonl", payload)
                });

                Assert.True(result.Ok);
                Assert.Equal(payload, File.ReadAllBytes(dir.File("Bin.jsonl")));
            }
        }

        [Fact]
        public void Commit_CreatesMissingDirectory()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                string nested = System.IO.Path.Combine(dir.Path, "Packs", "zh-cn");
                InstallTransactionResult result = PackInstallTransaction.Commit(nested, new List<InstallPlanFile> { Plan("A.jsonl", "a") });

                Assert.True(result.Ok);
                Assert.True(File.Exists(System.IO.Path.Combine(nested, "A.jsonl")));
            }
        }

        [Fact]
        public void Commit_SkipsUnchangedFiles()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                List<InstallPlanFile> plan = new List<InstallPlanFile> { Plan("A.jsonl", "same"), Plan("B.jsonl", "old") };

                Assert.True(PackInstallTransaction.Commit(dir.Path, plan).Ok);

                InstallTransactionResult second = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>
                {
                    Plan("A.jsonl", "same"),
                    Plan("B.jsonl", "new")
                });

                Assert.True(second.Ok);
                Assert.Equal(1, second.Installed);
                Assert.Equal(1, second.Unchanged);
                Assert.Equal("new", Read(dir.File("B.jsonl")));
            }
        }

        [Fact]
        public void Commit_LeavesNoTempOrBackupBehind()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("A.jsonl"), "old", Utf8);

                Assert.True(PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile> { Plan("A.jsonl", "new") }).Ok);

                string[] entries = dir.Entries();
                Assert.Single(entries);
                Assert.EndsWith("A.jsonl", entries[0], StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Commit_DoesNotTouchUnrelatedFiles()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("keep.txt"), "keep", Utf8);

                Assert.True(PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile> { Plan("A.jsonl", "a") }).Ok);
                Assert.Equal("keep", Read(dir.File("keep.txt")));
            }
        }

        // ---------- 参数与边界 ----------

        [Fact]
        public void Commit_EmptyOrNullPlan_IsOk()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                InstallTransactionResult empty = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>());
                Assert.True(empty.Ok);
                Assert.Equal(0, empty.Installed);

                InstallTransactionResult nil = PackInstallTransaction.Commit(dir.Path, null);
                Assert.True(nil.Ok);
            }
        }

        [Fact]
        public void Commit_EmptyDirectory_Fails()
        {
            InstallTransactionResult result = PackInstallTransaction.Commit("", new List<InstallPlanFile> { Plan("A.jsonl", "a") });
            Assert.False(result.Ok);
            Assert.StartsWith(PackInstallTransaction.ReasonNoDirectory, result.Reason, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("../evil.jsonl")]
        [InlineData("sub/evil.jsonl")]
        [InlineData("sub\\evil.jsonl")]
        [InlineData("C:evil.jsonl")]
        [InlineData("")]
        [InlineData(".")]
        [InlineData("..")]
        public void Commit_RejectsUnsafeName(string name)
        {
            using (TempDirectory dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("A.jsonl"), "keep", Utf8);

                InstallTransactionResult result = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>
                {
                    Plan("A.jsonl", "new"),
                    Plan(name, "evil")
                });

                Assert.False(result.Ok);
                Assert.StartsWith(PackInstallTransaction.ReasonBadName, result.Reason, StringComparison.Ordinal);
                Assert.Equal(0, result.Installed);
                Assert.Equal("keep", Read(dir.File("A.jsonl"))); // 名字检查在写盘之前：一个字节都没动
            }
        }

        [Theory]
        [InlineData("A.jsonl", true)]
        [InlineData("Mod.v1.jsonl", true)]
        [InlineData("../A.jsonl", false)]
        [InlineData("", false)]
        [InlineData("A/B.jsonl", false)]
        public void IsSafeFileName_Matrix(string name, bool expected)
        {
            Assert.Equal(expected, PackInstallTransaction.IsSafeFileName(name));
        }

        [Fact]
        public void Commit_NullContent_Fails()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                InstallTransactionResult result = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>
                {
                    new InstallPlanFile("A.jsonl", null)
                });

                Assert.False(result.Ok);
                Assert.StartsWith(PackInstallTransaction.ReasonBadName, result.Reason, StringComparison.Ordinal);
            }
        }

        // ---------- 失败与回滚 ----------

        [Fact]
        public void StageFailure_LeavesOldFilesIntact()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("A.jsonl"), "old-a", Utf8);
                // 第二条的临时文件位置上先放一个同名目录：预备阶段写不进去
                Directory.CreateDirectory(dir.File("B.jsonl" + PackInstallTransaction.TempSuffix));

                InstallTransactionResult result = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>
                {
                    Plan("A.jsonl", "new-a"),
                    Plan("B.jsonl", "new-b")
                });

                Assert.False(result.Ok);
                Assert.StartsWith(PackInstallTransaction.ReasonStageFailed, result.Reason, StringComparison.Ordinal);
                Assert.Equal(0, result.Installed);
                Assert.False(result.RolledBack);           // 还没提交过，不需要回滚
                Assert.Equal("old-a", Read(dir.File("A.jsonl")));
                Assert.False(File.Exists(dir.File("A.jsonl" + PackInstallTransaction.TempSuffix)));
            }
        }

        [Fact]
        public void CommitFailure_RestoresPreviousContent()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("A.jsonl"), "old-a", Utf8);
                // 第二条的目标路径是个目录：File.Exists 为 false，走 Move 必然失败
                Directory.CreateDirectory(dir.File("B.jsonl"));

                InstallTransactionResult result = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>
                {
                    Plan("A.jsonl", "new-a"),
                    Plan("B.jsonl", "new-b")
                });

                Assert.False(result.Ok);
                Assert.StartsWith(PackInstallTransaction.ReasonCommitFailed, result.Reason, StringComparison.Ordinal);
                Assert.True(result.RolledBack);
                Assert.Equal(1, result.Restored);
                Assert.Equal("old-a", Read(dir.File("A.jsonl")));
                Assert.False(File.Exists(dir.File("A.jsonl" + PackInstallTransaction.BackupSuffix)));
            }
        }

        [Fact]
        public void CommitFailure_RemovesNewlyCreatedFiles()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                Directory.CreateDirectory(dir.File("B.jsonl"));

                InstallTransactionResult result = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>
                {
                    Plan("A.jsonl", "new-a"),
                    Plan("B.jsonl", "new-b")
                });

                Assert.False(result.Ok);
                Assert.True(result.RolledBack);
                Assert.False(File.Exists(dir.File("A.jsonl"))); // 新装的文件回滚 = 删掉
            }
        }

        [Fact]
        public void CommitFailure_KeepsUnchangedFilesUntouched()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("A.jsonl"), "same", Utf8);
                Directory.CreateDirectory(dir.File("B.jsonl"));

                InstallTransactionResult result = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>
                {
                    Plan("A.jsonl", "same"),
                    Plan("B.jsonl", "new-b")
                });

                Assert.False(result.Ok);
                Assert.Equal(0, result.Installed);
                Assert.Equal(1, result.Unchanged);
                Assert.Equal("same", Read(dir.File("A.jsonl")));
            }
        }

        [Fact]
        public void Describe_MentionsCountsAndRollback()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                InstallTransactionResult ok = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile> { Plan("A.jsonl", "a") });
                Assert.Contains("1 written", ok.Describe(), StringComparison.Ordinal);
                Assert.Contains("rolledBack=false", ok.Describe(), StringComparison.Ordinal);

                Directory.CreateDirectory(dir.File("B.jsonl"));
                InstallTransactionResult bad = PackInstallTransaction.Commit(dir.Path, new List<InstallPlanFile>
                {
                    Plan("C.jsonl", "c"),
                    Plan("B.jsonl", "b")
                });
                Assert.Contains("rolledBack=true", bad.Describe(), StringComparison.Ordinal);
                Assert.Contains("reason=", bad.Describe(), StringComparison.Ordinal);
            }
        }

        // ---------- 崩溃后的收尾 ----------

        [Fact]
        public void CleanStale_RemovesTemps()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("A.jsonl" + PackInstallTransaction.TempSuffix), "partial", Utf8);

                int restored;
                int removed = PackInstallTransaction.CleanStale(dir.Path, out restored);

                Assert.Equal(1, removed);
                Assert.Equal(0, restored);
                Assert.False(File.Exists(dir.File("A.jsonl" + PackInstallTransaction.TempSuffix)));
            }
        }

        [Fact]
        public void CleanStale_RestoresBackupWhenTargetMissing()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("A.jsonl" + PackInstallTransaction.BackupSuffix), "old-a", Utf8);

                int restored;
                PackInstallTransaction.CleanStale(dir.Path, out restored);

                Assert.Equal(1, restored);
                Assert.Equal("old-a", Read(dir.File("A.jsonl")));
            }
        }

        [Fact]
        public void CleanStale_DropsBackupWhenTargetPresent()
        {
            using (TempDirectory dir = new TempDirectory())
            {
                File.WriteAllText(dir.File("A.jsonl"), "new-a", Utf8);
                File.WriteAllText(dir.File("A.jsonl" + PackInstallTransaction.BackupSuffix), "old-a", Utf8);

                int restored;
                int removed = PackInstallTransaction.CleanStale(dir.Path, out restored);

                Assert.Equal(0, restored);
                Assert.Equal(1, removed);
                Assert.Equal("new-a", Read(dir.File("A.jsonl")));
            }
        }

        [Fact]
        public void CleanStale_MissingDirectory_IsNoOp()
        {
            int restored;
            Assert.Equal(0, PackInstallTransaction.CleanStale(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ui18n-missing-" + Guid.NewGuid().ToString("n")), out restored));
            Assert.Equal(0, restored);
            Assert.Equal(0, PackInstallTransaction.CleanStale(null, out restored));
        }
    }
}
