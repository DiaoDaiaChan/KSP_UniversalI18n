using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using UI18n.Core.Update;
using Xunit;
using SysZip = System.IO.Compression.ZipArchive;

namespace UI18n.Core.Tests
{
    /// <summary>
    /// src/UI18n.Runtime/build-update-package.ps1 的契约测试（task-42c）。
    /// <para>
    /// 测的不是「脚本跑完了」，而是「脚本产出的东西能被加载器原样吃下」：
    /// 清单交给 <see cref="UpdateManifest.TryParse"/>（运行时用的同一个入口，parseErrors 必须 0），
    /// 字节交给 <see cref="UpdateVerify.CheckZip"/> / <see cref="UpdateVerify.CheckEntry"/>（**不另写一套校验**），
    /// 路径交给 <see cref="ZipSafety.TryMapToModRelative"/> + <see cref="ZipSafety.TryGetPackTarget"/>
    /// （证明每条都能被 TryGetPackTarget 认成 Packs/&lt;lang&gt;/&lt;file&gt;.jsonl）。
    /// </para>
    /// <para>
    /// 安全边界：更新包只准有 packs。Plugins/*.dll、UI18n.cfg、.version、PluginData/** 一旦出现，
    /// 脚本必须硬失败 —— 那是能改客户端代码的文件类型，不该走「下载覆盖」这条链路。
    /// </para>
    /// <para>
    /// 用例都用临时目录里的假 packs（两个小包），只有 <see cref="RealPacksArtifactPassesLoaderChecks"/>
    /// 跑仓库里真实的 packs/zh-cn：前者保证快、可复现，后者保证真实产物也过闸。
    /// </para>
    /// </summary>
    public class UpdatePackageScriptTests
    {
        private const string Channel = "stable";
        private const string Lang = "zh-cn";

        // ---------------------------------------------------------------- 环境定位

        private static readonly string RepoRoot = FindRepoRoot();
        private static readonly string ScriptPath = RepoRoot == null
            ? null
            : Path.Combine(RepoRoot, "src", "UI18n.Runtime", "build-update-package.ps1");
        private static readonly string PwshExe = FindPwsh();

        private static string FindRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "UI18n.sln")))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        // PowerShell 才是这个脚本的运行环境：优先 pwsh（7+），退回 Windows PowerShell 5.1。
        private static string FindPwsh()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] names = new string[] { "pwsh.exe", "pwsh", "powershell.exe", "powershell" };
            foreach (string name in names)
            {
                foreach (string rawDir in path.Split(Path.PathSeparator))
                {
                    string dir = rawDir == null ? string.Empty : rawDir.Trim().Trim('"');
                    if (dir.Length == 0)
                    {
                        continue;
                    }

                    string candidate;
                    try
                    {
                        candidate = Path.Combine(dir, name);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            string windowsPowerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(windowsPowerShell) ? windowsPowerShell : null;
        }

        private static void RequireScript()
        {
            if (RepoRoot == null)
            {
                Assert.Skip("找不到仓库根：从测试输出目录向上 12 层都没有 UI18n.sln。");
            }

            if (ScriptPath == null || !File.Exists(ScriptPath))
            {
                Assert.Skip("找不到打包脚本：" + ScriptPath);
            }

            if (PwshExe == null)
            {
                Assert.Skip("本机既没有 pwsh 也没有 powershell，跳过打包脚本契约测试。");
            }
        }

        // ---------------------------------------------------------------- 执行与临时目录

        private sealed class ScriptRun
        {
            public int ExitCode { get; set; }
            public string StdOut { get; set; }
            public string StdErr { get; set; }

            public string All
            {
                get { return (StdOut ?? string.Empty) + "\n--- stderr ---\n" + (StdErr ?? string.Empty); }
            }
        }

        private static ScriptRun RunScript(params string[] extraArgs)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = PwshExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = RepoRoot,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(ScriptPath);
            foreach (string arg in extraArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            using (Process process = Process.Start(psi))
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(180000))
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch (InvalidOperationException)
                    {
                        // 进程已经自己退了：忽略
                    }

                    Assert.Fail("build-update-package.ps1 180 s 内没退出；挂住的用例会拖死整个门禁。");
                }

                return new ScriptRun
                {
                    ExitCode = process.ExitCode,
                    StdOut = stdout.GetAwaiter().GetResult(),
                    StdErr = stderr.GetAwaiter().GetResult(),
                };
            }
        }

        private static string CreateTempRoot(string tag)
        {
            string root = Path.Combine(
                AppContext.BaseDirectory, "update-package-tests", tag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (IOException)
            {
                // 临时目录清不掉不影响结论
            }
            catch (UnauthorizedAccessException)
            {
                // 同上
            }
        }

        private static string WritePacks(string root, string lang, IDictionary<string, string> files)
        {
            string dir = Path.Combine(root, "packs", lang);
            Directory.CreateDirectory(dir);
            foreach (KeyValuePair<string, string> pair in files)
            {
                File.WriteAllText(Path.Combine(dir, pair.Key), pair.Value, new UTF8Encoding(false));
            }

            return Path.Combine(root, "packs");
        }

        private static Dictionary<string, string> TwoSmallPacks()
        {
            Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal);
            files["Alpha.jsonl"] = string.Join("\n", new string[]
            {
                "{\"h\":\"cbf29ce484222325\",\"n\":\"Squad\",\"s\":\"Squad\",\"t\":\"Squad\",\"mod\":\"Squad\"}",
                "{\"h\":\"af63dc4c8601ec8c\",\"n\":\"推力\",\"s\":\"Thrust\",\"t\":\"推力\",\"mod\":\"Squad\"}",
                string.Empty,
            });
            // 故意带 CRLF、制表符、裸引号与 emoji：zip 里存的必须是源字节的逐字节拷贝，不能被「规范化」掉
            files["Beta.jsonl"] = "{\"h\":1,\"s\":\"Ünïcødé 中文 😀 \\\"quoted\\\"\",\"t\":\"中文\"}\r\n"
                + "{\"h\":2,\"s\":\"tab\there\"}\n"
                + "{\"h\":3}";
            return files;
        }

        private static Dictionary<string, byte[]> ReadZip(string zipPath)
        {
            Dictionary<string, byte[]> map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (FileStream fs = File.OpenRead(zipPath))
            using (SysZip zip = new SysZip(fs, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    using (Stream stream = entry.Open())
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        Assert.False(map.ContainsKey(entry.FullName), "zip 里出现重复条目：" + entry.FullName);
                        map[entry.FullName] = buffer.ToArray();
                    }
                }
            }

            return map;
        }

        // ---------------------------------------------------------------- 断言：清单与 zip 必须过加载器的闸

        private static void AssertLoaderAccepts(string channelDir, string version, string zipFileName, int expectedEntries)
        {
            string manifestPath = Path.Combine(channelDir, "manifest.json");
            string zipPath = Path.Combine(channelDir, zipFileName);
            Assert.True(File.Exists(manifestPath), "缺少清单：" + manifestPath);
            Assert.True(File.Exists(zipPath), "缺少 zip：" + zipPath);

            UpdateManifest manifest;
            string error;
            bool parsed = UpdateManifest.TryParse(File.ReadAllText(manifestPath, new UTF8Encoding(false)), out manifest, out error);
            Assert.True(parsed, "运行时加载器必须能原样吃下这份 manifest，实际 error=" + error);
            Assert.True(string.IsNullOrEmpty(error), "解析成功时 error 必须为空，实际：" + error);
            Assert.NotNull(manifest);
            Assert.Equal(version, manifest.Version);
            Assert.Equal(zipFileName, manifest.ZipName);
            Assert.Equal(expectedEntries, manifest.FileCount);

            byte[] zipBytes = File.ReadAllBytes(zipPath);
            Assert.Equal(manifest.ZipBytes, (long)zipBytes.Length);

            string reason;
            Assert.True(
                UpdateVerify.CheckZip(zipBytes, manifest, out reason),
                "UpdateVerify.CheckZip 必须通过（zip 的 bytes/sha256 与清单不一致就是发布事故），实际 reason=" + reason);

            Dictionary<string, byte[]> entries = ReadZip(zipPath);
            Assert.Equal(expectedEntries, entries.Count);

            foreach (KeyValuePair<string, byte[]> pair in entries)
            {
                string entryName = pair.Key;

                // 安全边界 1：只准有 packs，别的文件类型一律不许出现
                Assert.StartsWith("GameData/UI18n/Packs/" + Lang + "/", entryName);
                Assert.DoesNotContain("Plugins", entryName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("PluginData", entryName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(".dll", entryName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(".pdb", entryName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(".cfg", entryName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(".version", entryName, StringComparison.OrdinalIgnoreCase);

                // 安全边界 2：路径必须能走完运行时那套 zip-slip + 三层结构校验
                string modRelative;
                Assert.True(
                    ZipSafety.TryMapToModRelative(entryName, out modRelative, out reason),
                    "条目路径没通过 zip-slip 归一化：" + entryName + " / " + reason);
                string language;
                string fileName;
                Assert.True(
                    ZipSafety.TryGetPackTarget(modRelative, out language, out fileName, out reason),
                    "条目装不进 Packs/<lang>/，运行时永远用不上它：" + entryName + " / " + reason);
                Assert.Equal(Lang, language);

                // 安全边界 3：字节必须与清单登记的一致（用 UpdateVerify，不另写一套）
                ManifestFile file = manifest.Find(modRelative);
                Assert.NotNull(file);
                Assert.True(
                    UpdateVerify.CheckEntry(pair.Value, file, out reason),
                    "条目内容与清单登记不一致：" + entryName + " / " + reason);
                Assert.Equal(fileName, Path.GetFileName(entryName));
            }
        }

        // ---------------------------------------------------------------- 正向：产物 + 加载器

        [Fact]
        public void ProducesPacksOnlyZipWithLoaderReadableManifest()
        {
            RequireScript();
            string root = CreateTempRoot("ok");
            try
            {
                string packsRoot = WritePacks(root, Lang, TwoSmallPacks());
                ScriptRun run = RunScript(
                    "-Version", "1.2.3",
                    "-PacksDir", packsRoot,
                    "-OutDir", Path.Combine(root, "out"),
                    "-WorkDir", Path.Combine(root, "work"));

                Assert.True(run.ExitCode == 0, "脚本应成功，实际退出码 " + run.ExitCode + "：" + run.All);
                Assert.Contains("[update] OK", run.StdOut);

                string channelDir = Path.Combine(root, "out", Channel);
                AssertLoaderAccepts(channelDir, "1.2.3", "UI18n-1.2.3-zh-cn-packs.zip", 2);

                // 产物目录里只有这两件：多一个文件就说明有东西混进更新包了
                string[] produced = Directory.GetFileSystemEntries(channelDir);
                Assert.Equal(2, produced.Length);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void ZipEntriesAreByteIdenticalToSourcePacks()
        {
            RequireScript();
            string root = CreateTempRoot("bytes");
            try
            {
                Dictionary<string, string> packs = TwoSmallPacks();
                string packsRoot = WritePacks(root, Lang, packs);
                ScriptRun run = RunScript(
                    "-Version", "1.2.3",
                    "-PacksDir", packsRoot,
                    "-OutDir", Path.Combine(root, "out"),
                    "-WorkDir", Path.Combine(root, "work"));
                Assert.True(run.ExitCode == 0, "脚本应成功：" + run.All);

                Dictionary<string, byte[]> entries = ReadZip(
                    Path.Combine(root, "out", Channel, "UI18n-1.2.3-zh-cn-packs.zip"));
                foreach (KeyValuePair<string, string> pair in packs)
                {
                    string entryName = "GameData/UI18n/Packs/" + Lang + "/" + pair.Key;
                    Assert.True(entries.ContainsKey(entryName), "zip 里缺少条目：" + entryName);

                    byte[] source = File.ReadAllBytes(Path.Combine(packsRoot, Lang, pair.Key));
                    Assert.Equal(source, entries[entryName]);
                }
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ---------------------------------------------------------------- 可复现

        [Fact]
        public void SameInputTwiceProducesByteIdenticalArtifacts()
        {
            RequireScript();
            string root = CreateTempRoot("repro");
            try
            {
                string packsRoot = WritePacks(root, Lang, TwoSmallPacks());
                string outA = Path.Combine(root, "out-a");
                string outB = Path.Combine(root, "out-b");
                ScriptRun runA = RunScript("-Version", "2.0.0", "-PacksDir", packsRoot, "-OutDir", outA, "-WorkDir", Path.Combine(root, "work-a"));
                ScriptRun runB = RunScript("-Version", "2.0.0", "-PacksDir", packsRoot, "-OutDir", outB, "-WorkDir", Path.Combine(root, "work-b"));
                Assert.True(runA.ExitCode == 0, "第一次打包应成功：" + runA.All);
                Assert.True(runB.ExitCode == 0, "第二次打包应成功：" + runB.All);

                string zipName = "UI18n-2.0.0-zh-cn-packs.zip";
                byte[] zipA = File.ReadAllBytes(Path.Combine(outA, Channel, zipName));
                byte[] zipB = File.ReadAllBytes(Path.Combine(outB, Channel, zipName));
                Assert.Equal(Sha256Hex.OfBytes(zipA), Sha256Hex.OfBytes(zipB));
                Assert.Equal(zipA, zipB);

                string textA = File.ReadAllText(Path.Combine(outA, Channel, "manifest.json"), new UTF8Encoding(false));
                string textB = File.ReadAllText(Path.Combine(outB, Channel, "manifest.json"), new UTF8Encoding(false));
                Assert.Equal(textA, textB);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void BetaChannelWritesIntoBetaFolderOnly()
        {
            RequireScript();
            string root = CreateTempRoot("beta");
            try
            {
                string packsRoot = WritePacks(root, Lang, TwoSmallPacks());
                string outRoot = Path.Combine(root, "out");
                ScriptRun run = RunScript(
                    "-Version", "3.1.0",
                    "-Channel", "beta",
                    "-PacksDir", packsRoot,
                    "-OutDir", outRoot,
                    "-WorkDir", Path.Combine(root, "work"));
                Assert.True(run.ExitCode == 0, "beta 通道应成功：" + run.All);

                Assert.True(File.Exists(Path.Combine(outRoot, "beta", "manifest.json")), "beta 通道的清单没落在 <OutDir>\\beta\\");
                Assert.False(Directory.Exists(Path.Combine(outRoot, "stable")), "beta 通道不该碰 stable 目录");
                AssertLoaderAccepts(Path.Combine(outRoot, "beta"), "3.1.0", "UI18n-3.1.0-zh-cn-packs.zip", 2);
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ---------------------------------------------------------------- 负向：安全边界命中即硬失败

        [Fact]
        public void RejectsDevOnlyUnderscorePack()
        {
            RequireScript();
            string root = CreateTempRoot("reserved");
            try
            {
                Dictionary<string, string> packs = TwoSmallPacks();
                packs["_smoke.jsonl"] = "{\"h\":1}\n";
                string packsRoot = WritePacks(root, Lang, packs);
                string outRoot = Path.Combine(root, "out");
                ScriptRun run = RunScript(
                    "-Version", "1.0.0",
                    "-PacksDir", packsRoot,
                    "-OutDir", outRoot,
                    "-WorkDir", Path.Combine(root, "work"));

                Assert.True(run.ExitCode != 0, "下划线开头的开发期包必须让打包失败，实际退出码 " + run.ExitCode + "：" + run.All);
                Assert.Contains("reserved-name", run.All);
                Assert.False(File.Exists(Path.Combine(outRoot, Channel, "manifest.json")), "断言失败时绝不能落地清单");
                Assert.False(File.Exists(Path.Combine(outRoot, Channel, "UI18n-1.0.0-zh-cn-packs.zip")), "断言失败时绝不能落地 zip");
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void RejectsPackFileNameTheLoaderCannotInstall()
        {
            RequireScript();
            string root = CreateTempRoot("badname");
            try
            {
                Dictionary<string, string> packs = TwoSmallPacks();
                packs["bad name.jsonl"] = "{\"h\":1}\n";
                string packsRoot = WritePacks(root, Lang, packs);
                ScriptRun run = RunScript(
                    "-Version", "1.0.0",
                    "-PacksDir", packsRoot,
                    "-OutDir", Path.Combine(root, "out"),
                    "-WorkDir", Path.Combine(root, "work"));

                Assert.True(run.ExitCode != 0, "文件名不合法必须让打包失败，实际退出码 " + run.ExitCode + "：" + run.All);
                Assert.Contains("bad-file-name", run.All);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void RejectsEmptyPacksDirectory()
        {
            RequireScript();
            string root = CreateTempRoot("empty");
            try
            {
                string packsRoot = Path.Combine(root, "packs");
                Directory.CreateDirectory(Path.Combine(packsRoot, Lang));
                ScriptRun run = RunScript(
                    "-Version", "1.0.0",
                    "-PacksDir", packsRoot,
                    "-OutDir", Path.Combine(root, "out"),
                    "-WorkDir", Path.Combine(root, "work"));

                Assert.True(run.ExitCode != 0, "一个包都没有时必须失败，实际退出码 " + run.ExitCode + "：" + run.All);
                Assert.Contains("empty-packs", run.All);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void RejectsMalformedVersionAndLanguage()
        {
            RequireScript();
            string root = CreateTempRoot("args");
            try
            {
                string packsRoot = WritePacks(root, Lang, TwoSmallPacks());

                ScriptRun twoPartVersion = RunScript("-Version", "1.2", "-PacksDir", packsRoot, "-OutDir", Path.Combine(root, "out"), "-WorkDir", Path.Combine(root, "work"));
                Assert.True(twoPartVersion.ExitCode != 0, "-Version 1.2 必须被拒，实际退出码 " + twoPartVersion.ExitCode + "：" + twoPartVersion.All);
                Assert.Contains("-Version", twoPartVersion.All);

                ScriptRun missingVersion = RunScript("-PacksDir", packsRoot, "-OutDir", Path.Combine(root, "out"), "-WorkDir", Path.Combine(root, "work"));
                Assert.True(missingVersion.ExitCode != 0, "缺 -Version 必须被拒，实际退出码 " + missingVersion.ExitCode + "：" + missingVersion.All);

                ScriptRun badLanguage = RunScript("-Version", "1.2.3", "-Lang", "zh cn", "-PacksDir", packsRoot, "-OutDir", Path.Combine(root, "out"), "-WorkDir", Path.Combine(root, "work"));
                Assert.True(badLanguage.ExitCode != 0, "-Lang 里带空格必须被拒，实际退出码 " + badLanguage.ExitCode + "：" + badLanguage.All);
                Assert.Contains("-Lang", badLanguage.All);
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ---------------------------------------------------------------- 真实语言包

        [Fact]
        public void RealPacksArtifactPassesLoaderChecks()
        {
            RequireScript();
            string packsRoot = Path.Combine(RepoRoot, "packs", Lang);
            if (!Directory.Exists(packsRoot))
            {
                Assert.Skip("仓库里没有 packs/" + Lang + "（packs 是生成物，可能还没跑流水线）。");
            }

            int expected = Directory.GetFiles(packsRoot, "*.jsonl").Length;
            if (expected == 0)
            {
                Assert.Skip("packs/" + Lang + " 里没有 *.jsonl。");
            }

            string root = CreateTempRoot("real");
            try
            {
                ScriptRun run = RunScript(
                    "-Version", "9.9.9",
                    "-OutDir", Path.Combine(root, "out"),
                    "-WorkDir", Path.Combine(root, "work"));
                Assert.True(run.ExitCode == 0, "真实语言包打包应成功：" + run.All);
                Assert.Contains("[update] OK", run.StdOut);

                AssertLoaderAccepts(Path.Combine(root, "out", Channel), "9.9.9", "UI18n-9.9.9-zh-cn-packs.zip", expected);
            }
            finally
            {
                TryDelete(root);
            }
        }
    }
}
