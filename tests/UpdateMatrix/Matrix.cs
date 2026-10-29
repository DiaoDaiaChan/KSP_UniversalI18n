using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UI18n.Core.Config;
using UI18n.Core.Update;
using UI18n.Runtime.Update;

namespace UpdCheck
{
    /// <summary>
    /// 离线端到端验收矩阵：起一个本地 HTTP 服务喂手工构造的 manifest + zip，
    /// 直接驱动 <see cref="UpdateWorkflow.Run"/>（真实生产代码，不是复制品），
    /// 每条用例都打印日志原文与「安装前后 Packs 目录逐文件 sha256」对比。
    /// </summary>
    internal static class Matrix
    {
        private static int _checks;
        private static int _failures;
        private static readonly List<string> Log = new List<string>();

        private sealed class CaseRun
        {
            internal string Name;
            internal UpdateResult Result;
            internal Dictionary<string, string> Before = new Dictionary<string, string>(StringComparer.Ordinal);
            internal Dictionary<string, string> After = new Dictionary<string, string>(StringComparer.Ordinal);
            internal string Sandbox;
            internal string PacksDirectory;
            internal string ModRoot;
            internal string PluginDataRoot;

            internal bool TreeUnchanged
            {
                get { return DiffLines().Count == 0; }
            }

            internal List<string> DiffLines()
            {
                List<string> diff = new List<string>();
                List<string> names = new List<string>();
                foreach (string n in Before.Keys) { names.Add(n); }
                foreach (string n in After.Keys) { if (!Before.ContainsKey(n)) { names.Add(n); } }
                names.Sort(StringComparer.Ordinal);

                for (int i = 0; i < names.Count; i++)
                {
                    string n = names[i];
                    bool had = Before.ContainsKey(n);
                    bool has = After.ContainsKey(n);
                    if (had && has)
                    {
                        if (!string.Equals(Before[n], After[n], StringComparison.Ordinal))
                        {
                            diff.Add("changed  " + n + "  " + Prefix(Before[n]) + " -> " + Prefix(After[n]));
                        }
                    }
                    else if (!had)
                    {
                        diff.Add("added    " + n + "  " + Prefix(After[n]));
                    }
                    else
                    {
                        diff.Add("removed  " + n + "  " + Prefix(Before[n]));
                    }
                }

                return diff;
            }
        }

        internal static int Run(string workRoot, string baseUrl, string fixturesRoot)
        {
            Console.WriteLine("== update matrix ==");
            Console.WriteLine("     work root  : " + Path.GetFullPath(workRoot));
            Console.WriteLine("     base url   : " + baseUrl);
            Console.WriteLine("     fixtures   : " + Path.GetFullPath(fixturesRoot));

            if (Directory.Exists(workRoot))
            {
                try { Directory.Delete(workRoot, true); } catch { }
            }

            string local = "1.1.0";

            // ---- 1) 不配地址：完全不联网 ----
            CaseRun c1 = Execute(workRoot, "01-disabled-empty-url", string.Empty, "stable", "zh-cn", 10, 7, true, true, local);
            Expect(c1, "UpdateBaseUrl empty -> outcome=disabled", c1.Result.Outcome == "disabled");
            Expect(c1, "UpdateBaseUrl empty -> 日志明确说 disabled", Has("update: disabled (UpdateBaseUrl empty)"));
            Expect(c1, "UpdateBaseUrl empty -> 一个请求都没发", !HasPrefix("update: GET "));
            Expect(c1, "UpdateBaseUrl empty -> Packs 一字未动", c1.TreeUnchanged);

            // ---- 2) CheckForUpdates=False ----
            CaseRun c2 = Execute(workRoot, "02-disabled-checkforupdates", baseUrl, "stable", "zh-cn", 10, 7, false, true, local);
            Expect(c2, "CheckForUpdates=False -> outcome=disabled", c2.Result.Outcome == "disabled");
            Expect(c2, "CheckForUpdates=False -> 日志明确说原因", Has("update: disabled (CheckForUpdates=False)"));
            Expect(c2, "CheckForUpdates=False -> 一个请求都没发", !HasPrefix("update: GET "));
            Expect(c2, "CheckForUpdates=False -> Packs 一字未动", c2.TreeUnchanged);

            // ---- 3) 正常更新 ----
            string okUrl = baseUrl + "/ok";
            CaseRun c3 = Execute(workRoot, "03-ok-install", okUrl, "stable", "zh-cn", 10, 7, true, true, local);
            Expect(c3, "ok -> outcome=installed", c3.Result.Outcome == "installed");
            Expect(c3, "ok -> 装了 3 个语言匹配的包文件（en-us 与 dll/cfg 都被跳过）", c3.Result.InstalledFiles == 3);
            Expect(c3, "ok -> 装了 1.2.0", c3.Result.Version == "1.2.0");
            Expect(c3, "ok -> 日志有 installed 行", Has("update: installed 3 file(s), version 1.2.0"));
            Expect(c3, "ok -> Packs 目录确实变了", !c3.TreeUnchanged);
            Expect(c3, "ok -> 别的语言目录没被创建", !Directory.Exists(Path.Combine(c3.ModRoot, "Packs", "en-us")));
            Expect(c3, "ok -> Plugins 目录没被创建", !Directory.Exists(Path.Combine(c3.ModRoot, "Plugins")));
            Expect(c3, "ok -> Config.cfg 没被创建/覆盖", !File.Exists(Path.Combine(c3.ModRoot, "Config.cfg")));
            Expect(c3, "ok -> 未在清单里的旧包 Keep.jsonl 原样保留", KeptUnchanged(c3, "Keep.jsonl"));

            // 装完之后，逐文件与清单里的 sha256 对齐（这就是「装出来的 == zip 里的」的硬证据）
            UpdateManifest okManifest;
            string manifestError;
            string fixtureManifestJson = File.ReadAllText(Path.Combine(fixturesRoot, "ok", "stable", "manifest.json"), Encoding.UTF8);
            bool fixtureManifestOk = UpdateManifest.TryParse(fixtureManifestJson, out okManifest, out manifestError);
            ExpectGlobal("ok -> fixture manifest 可解析 (" + manifestError + ")", fixtureManifestOk);
            if (okManifest != null)
            {
                int matched = 0;
                int expected = 0;
                for (int i = 0; i < okManifest.Files.Count; i++)
                {
                    ManifestFile file = okManifest.Files[i];
                    string language;
                    string fileName;
                    string reason;
                    if (!ZipSafety.TryGetPackTarget(file.ModRelativePath, out language, out fileName, out reason))
                    {
                        continue;
                    }

                    if (!string.Equals(language, "zh-cn", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    expected++;
                    string installed = Path.Combine(c3.PacksDirectory, fileName);
                    if (!File.Exists(installed))
                    {
                        continue;
                    }

                    string actual = Sha256Hex.OfBytes(File.ReadAllBytes(installed));
                    if (string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        matched++;
                    }
                }

                Expect(c3, "ok -> 安装后逐文件 sha256 与清单一致 (" + matched + "/" + expected + ")", expected == 3 && matched == expected);
            }

            UpdateState state;
            string stateError;
            bool stateOk = UpdateState.TryParse(File.ReadAllText(Path.Combine(c3.PluginDataRoot, "update-state.json"), Encoding.UTF8), out state, out stateError);
            Expect(c3, "ok -> update-state.json 可读回 (" + (stateError ?? "ok") + ")", stateOk);
            Expect(c3, "ok -> state 记下版本 1.2.0", stateOk && state != null && state.LastVersion == "1.2.0");

            // ---- 4) 版本相同：不下载、不动盘 ----
            CaseRun c4 = Execute(workRoot, "04-same-version", baseUrl + "/samever", "stable", "zh-cn", 10, 7, true, true, local);
            Expect(c4, "samever -> outcome=up-to-date", c4.Result.Outcome == "up-to-date");
            Expect(c4, "samever -> 日志说 up to date", Has("update: up to date (local 1.1.0, remote 1.1.0)"));
            Expect(c4, "samever -> 没有下载", !HasPrefix("update: downloading "));
            Expect(c4, "samever -> Packs 一字未动", c4.TreeUnchanged);

            // ---- 5) sha256 不符 ----
            CaseRun c5 = Execute(workRoot, "05-bad-sha256", baseUrl + "/badsha", "stable", "zh-cn", 10, 7, true, true, local);
            Expect(c5, "badsha -> 失败", !c5.Result.Ok);
            Expect(c5, "badsha -> 原因 sha256-mismatch", Contains("zip verification failed (sha256-mismatch"));
            Expect(c5, "badsha -> Packs 一字未动（绝不半写）", c5.TreeUnchanged);

            // ---- 6) bytes 不符 ----
            CaseRun c6 = Execute(workRoot, "06-bad-bytes", baseUrl + "/badbytes", "stable", "zh-cn", 10, 7, true, true, local);
            Expect(c6, "badbytes -> 失败", !c6.Result.Ok);
            Expect(c6, "badbytes -> 原因 size-mismatch", Contains("zip verification failed (size-mismatch"));
            Expect(c6, "badbytes -> Packs 一字未动", c6.TreeUnchanged);

            // ---- 7) 404 ----
            CaseRun c7 = Execute(workRoot, "07-not-found", baseUrl + "/missing", "stable", "zh-cn", 10, 7, true, true, local);
            Expect(c7, "404 -> 失败", !c7.Result.Ok);
            Expect(c7, "404 -> 日志说 HTTP 404", Contains("manifest request failed (HTTP 404"));
            Expect(c7, "404 -> Packs 一字未动", c7.TreeUnchanged);

            // ---- 8) 超时（服务端 sleep 6 s，客户端超时 3 s） ----
            DateTime started = DateTime.UtcNow;
            CaseRun c8 = Execute(workRoot, "08-timeout", baseUrl + "/slow", "stable", "zh-cn", 3, 7, true, true, local);
            TimeSpan elapsed = DateTime.UtcNow - started;
            Expect(c8, "slow -> 失败", !c8.Result.Ok);
            Expect(c8, "slow -> 原因 timeout after 3s", Contains("manifest request failed (timeout after 3s"));
            Expect(c8, "slow -> 真的没傻等 6 秒（实测 " + elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s）", elapsed.TotalSeconds < 5.5);
            Expect(c8, "slow -> Packs 一字未动", c8.TreeUnchanged);

            // ---- 9) 加测：清单里的 zip-slip 路径整份拒绝 ----
            CaseRun c9 = Execute(workRoot, "09-zip-slip-manifest", baseUrl + "/zipslip", "stable", "zh-cn", 10, 7, true, true, local);
            Expect(c9, "zipslip -> 失败", !c9.Result.Ok);
            Expect(c9, "zipslip -> 原因 unsafe-path", Contains("manifest rejected (unsafe-path"));
            Expect(c9, "zipslip -> Packs 一字未动", c9.TreeUnchanged);
            Expect(c9, "zipslip -> 没有逃逸文件被创建", !File.Exists(Path.Combine(c9.ModRoot, "evil.jsonl")));

            // ---- 10) 加测：AutoDownload=False 只提示不下载 ----
            CaseRun c10 = Execute(workRoot, "10-no-autodownload", baseUrl + "/ok", "stable", "zh-cn", 10, 7, true, false, local);
            Expect(c10, "no-autodownload -> outcome=update-available", c10.Result.Outcome == "update-available");
            Expect(c10, "no-autodownload -> 日志说 download skipped", Contains("update: download skipped (AutoDownload=False)"));
            Expect(c10, "no-autodownload -> Packs 一字未动", c10.TreeUnchanged);

            // ---- 11) 加测：间隔未到（UpdateCheckDays） ----
            string sandbox11 = PrepareSandbox(workRoot, "11-not-due");
            WriteStateFile(Path.Combine(sandbox11, "PluginData", "UI18n"), DateTime.UtcNow.AddHours(-6), "1.1.0", UpdateState.ResultUpToDate);
            CaseRun c11 = ExecuteIn(sandbox11, "11-not-due", baseUrl + "/ok", "stable", "zh-cn", 10, 7, true, true, local);
            Expect(c11, "not-due -> outcome=not-due", c11.Result.Outcome == "not-due");
            Expect(c11, "not-due -> 一个请求都没发", !HasPrefix("update: GET "));
            Expect(c11, "not-due -> Packs 一字未动", c11.TreeUnchanged);

            // ---- 12) 加测：UpdateRunner 起线程不阻塞调用方，日志靠 Tick 在主线程吐出 ----
            RunThreadCase(workRoot, baseUrl);

            // ---- 13) 加测：装到一半失败必须整批回滚（PackInstallTransaction） ----
            RunRollbackCase(workRoot, baseUrl);

            // ---- 14) 加测：远端版本比本地旧（发布倒车）绝不能降级 ----
            CaseRun c14 = Execute(workRoot, "14-remote-older", baseUrl + "/samever", "stable", "zh-cn", 10, 7, true, true, "1.3.0");
            Expect(c14, "remote-older -> outcome=up-to-date", c14.Result.Outcome == UpdateState.ResultUpToDate);
            Expect(c14, "remote-older -> 日志说 up to date (local 1.3.0, remote 1.1.0)", Has("update: up to date (local 1.3.0, remote 1.1.0)"));
            Expect(c14, "remote-older -> 没有下载", !HasPrefix("update: downloading "));
            Expect(c14, "remote-older -> Packs 一字未动", c14.TreeUnchanged);

            Console.WriteLine();
            Console.WriteLine("checks=" + _checks + " failures=" + _failures);
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// 线程语义验收：<see cref="UpdateRunner.ConfigureAndStart"/> 必须立刻返回（网络在后台），
        /// 日志只有在主线程调 <see cref="UpdateRunner.Tick"/> 时才会被吐出去。
        /// 这是「主菜单到达时间不被拖慢」这条硬约束的可执行证据。
        /// </summary>
        private static void RunThreadCase(string workRoot, string baseUrl)
        {
            string sandbox = PrepareSandbox(workRoot, "12-thread-nonblocking");
            string modRoot = Path.Combine(sandbox, "GameData", "UI18n");
            string pluginDataRoot = Path.Combine(sandbox, "PluginData", "UI18n");
            string packs = Path.Combine(Path.Combine(modRoot, "Packs"), "zh-cn");
            Dictionary<string, string> before = Snapshot(packs);

            List<string> seen = new List<string>();
            UpdateLog.Reset();
            UpdateRunner.Sink = delegate(string message, bool warning)
            {
                lock (seen)
                {
                    seen.Add((warning ? "[WARN] " : "[INFO] ") + message);
                }
            };

            UI18nConfig config = UI18nConfig.Default;
            config.UpdateBaseUrl = baseUrl + "/ok";
            config.UpdateChannel = "stable";
            config.UpdateTimeoutSeconds = 10;
            config.UpdateCheckDays = 7;
            config.CheckForUpdates = true;
            config.AutoDownload = true;

            DateTime started = DateTime.UtcNow;
            UpdateRunner.ConfigureAndStart(config, modRoot, pluginDataRoot, "1.1.0", "zh-cn");
            TimeSpan startup = DateTime.UtcNow - started;

            Console.WriteLine();
            Console.WriteLine("=== case 12-thread-nonblocking ===");
            Console.WriteLine("    ConfigureAndStart returned in "
                + (startup.TotalMilliseconds).ToString("0.0", CultureInfo.InvariantCulture) + " ms");

            ExpectGlobal("thread -> ConfigureAndStart 立刻返回，不等待网络（" + startup.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms < 500 ms）",
                startup.TotalMilliseconds < 500);

            // 主线程「每帧」泵一下，最多等 15 秒
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (UpdateRunner.IsRunning && DateTime.UtcNow < deadline)
            {
                UpdateRunner.Tick();
                System.Threading.Thread.Sleep(50);
            }

            UpdateRunner.Tick();

            string[] lines;
            lock (seen)
            {
                lines = seen.ToArray();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                Console.WriteLine("    " + lines[i]);
            }

            bool installed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("update: installed 3 file(s), version 1.2.0", StringComparison.Ordinal) >= 0)
                {
                    installed = true;
                }
            }

            UpdateResult result = UpdateRunner.LastResult;
            ExpectGlobal("thread -> 后台线程跑完并通过 Tick 吐出 installed 行", installed);
            ExpectGlobal("thread -> LastResult 记下 outcome=installed",
                result != null && result.Outcome == "installed" && result.InstalledFiles == 3);
            ExpectGlobal("thread -> 线程已结束", !UpdateRunner.IsRunning);

            Dictionary<string, string> after = Snapshot(packs);
            bool same = before.Count == after.Count;
            if (same)
            {
                foreach (KeyValuePair<string, string> pair in before)
                {
                    string other;
                    if (!after.TryGetValue(pair.Key, out other) || !string.Equals(other, pair.Value, StringComparison.Ordinal))
                    {
                        same = false;
                        break;
                    }
                }
            }

            Console.WriteLine("    packs  : " + before.Count + " -> " + after.Count + " file(s) (expect changed)");
            ExpectGlobal("thread -> Packs 被更新（" + before.Count + " -> " + after.Count + "）", !same);
        }

        /// <summary>
        /// 回滚验收（M4-d）：把第 3 个包文件的落点预先占成同名目录，制造「Squad 已替换、NewMod 提交失败」。
        /// 期望整批回滚：Squad.jsonl 回到种子内容、RealismOverhaul.jsonl（新装）被删掉、无 .ui18n-new/.ui18n-bak 残留。
        /// </summary>
        private static void RunRollbackCase(string workRoot, string baseUrl)
        {
            const string name = "13-install-rollback";
            string sandbox = PrepareSandbox(workRoot, name);
            string packs = Path.Combine(Path.Combine(Path.Combine(sandbox, "GameData", "UI18n"), "Packs"), "zh-cn");

            // NewMod.jsonl 是清单里的第 3 个包文件：同名目录挡路 -> File.Exists=false 且 File.Move 必失败
            Directory.CreateDirectory(Path.Combine(packs, "NewMod.jsonl"));

            CaseRun run = ExecuteIn(sandbox, name, baseUrl + "/ok", "stable", "zh-cn", 10, 7, true, true, "1.1.0");
            Expect(run, "rollback -> 整体失败", !run.Result.Ok);
            Expect(run, "rollback -> outcome=failed / reason=install",
                run.Result.Outcome == UpdateState.ResultFailed && string.Equals(run.Result.FailureReason, "install", StringComparison.Ordinal));
            Expect(run, "rollback -> 日志说 install failed (commit-failed …)", Contains("update: install failed (commit-failed"));
            Expect(run, "rollback -> 日志说 file(s) were restored", Contains("file(s) were restored, packs are exactly as before"));
            Expect(run, "rollback -> Packs 逐文件回到装前（Squad.jsonl 仍是种子内容）", run.TreeUnchanged);
            Expect(run, "rollback -> 没有残留 .ui18n-new / .ui18n-bak", CountTransactionLeftovers(packs) == 0);
            Expect(run, "rollback -> 也没有装进去的半成品 NewMod.jsonl", !File.Exists(Path.Combine(packs, "NewMod.jsonl")));
        }

        private static int CountTransactionLeftovers(string packsDirectory)
        {
            if (!Directory.Exists(packsDirectory))
            {
                return 0;
            }

            return Directory.GetFiles(packsDirectory, "*.ui18n-*", SearchOption.AllDirectories).Length;
        }

        private static CaseRun Execute(string workRoot, string name, string baseUrl, string channel, string language,
            int timeoutSeconds, int checkDays, bool checkForUpdates, bool autoDownload, string localVersion)
        {
            string sandbox = PrepareSandbox(workRoot, name);
            return ExecuteIn(sandbox, name, baseUrl, channel, language, timeoutSeconds, checkDays, checkForUpdates, autoDownload, localVersion);
        }

        private static CaseRun ExecuteIn(string sandbox, string name, string baseUrl, string channel, string language,
            int timeoutSeconds, int checkDays, bool checkForUpdates, bool autoDownload, string localVersion)
        {
            string modRoot = Path.Combine(sandbox, "GameData", "UI18n");
            string pluginDataRoot = Path.Combine(sandbox, "PluginData", "UI18n");
            string packs = Path.Combine(Path.Combine(modRoot, "Packs"), language);

            CaseRun run = new CaseRun
            {
                Name = name,
                Sandbox = sandbox,
                ModRoot = modRoot,
                PluginDataRoot = pluginDataRoot,
                PacksDirectory = packs,
                Before = Snapshot(packs)
            };

            Log.Clear();
            UpdateLog.Reset();
            UpdateLog.Sink = delegate(string message, bool warning)
            {
                string line = (warning ? "[WARN] " : "[INFO] ") + message;
                Log.Add(line);
            };

            // disabledModIds=null：本矩阵关心的是「装/不装/装坏怎么办」，玩家关包过滤由 prefs.json 侧另测
            UpdateEnvironment environment = new UpdateEnvironment(baseUrl, channel, modRoot, pluginDataRoot, localVersion,
                language, timeoutSeconds, checkDays, checkForUpdates, autoDownload, null);

            DateTime started = DateTime.UtcNow;
            run.Result = UpdateWorkflow.Run(environment);
            TimeSpan elapsed = DateTime.UtcNow - started;
            UpdateLog.Drain();

            run.After = Snapshot(packs);

            Console.WriteLine();
            Console.WriteLine("=== case " + name + " ===");
            Console.WriteLine("    env    : " + environment.Describe());
            Console.WriteLine("    elapsed: " + elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s");
            for (int i = 0; i < Log.Count; i++)
            {
                Console.WriteLine("    " + Log[i]);
            }

            Console.WriteLine("    packs  : " + packs);
            Console.WriteLine("    before : " + run.Before.Count + " file(s)");
            foreach (KeyValuePair<string, string> pair in Sorted(run.Before))
            {
                Console.WriteLine("             " + pair.Key + "  " + pair.Value);
            }

            Console.WriteLine("    after  : " + run.After.Count + " file(s)");
            foreach (KeyValuePair<string, string> pair in Sorted(run.After))
            {
                Console.WriteLine("             " + pair.Key + "  " + pair.Value);
            }

            List<string> diff = run.DiffLines();
            Console.WriteLine("    diff   : " + (diff.Count == 0 ? "(no change)" : diff.Count + " entry(ies)"));
            for (int i = 0; i < diff.Count; i++)
            {
                Console.WriteLine("             " + diff[i]);
            }

            Console.WriteLine("    result : " + (run.Result == null ? "(null)" : run.Result.Describe()));
            return run;
        }

        private static string PrepareSandbox(string workRoot, string name)
        {
            string sandbox = Path.Combine(workRoot, name);
            string packs = Path.Combine(Path.Combine(Path.Combine(sandbox, "GameData", "UI18n"), "Packs"), "zh-cn");
            Directory.CreateDirectory(packs);

            // 两个"玩家已装好的旧包"：Squad 会被更新覆盖，Keep 必须原样保留
            File.WriteAllText(Path.Combine(packs, "Squad.jsonl"), "{\"h\":\"0000000000000001\",\"n\":\"seed squad\",\"s\":\"seed squad\",\"t\":\"旧\",\"mod\":\"Squad\"}\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(packs, "Keep.jsonl"), "{\"h\":\"0000000000000002\",\"n\":\"keep me\",\"s\":\"keep me\",\"t\":\"保留\",\"mod\":\"Keep\"}\n", new UTF8Encoding(false));

            Directory.CreateDirectory(Path.Combine(sandbox, "PluginData", "UI18n"));
            return sandbox;
        }

        private static void WriteStateFile(string pluginDataRoot, DateTime lastCheckUtc, string version, string result)
        {
            Directory.CreateDirectory(pluginDataRoot);
            UpdateState state = UpdateState.Empty.WithCheck(lastCheckUtc, version, result);
            File.WriteAllText(Path.Combine(pluginDataRoot, "update-state.json"), state.ToJson(), new UTF8Encoding(false));
        }

        private static Dictionary<string, string> Snapshot(string packsDirectory)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(packsDirectory))
            {
                return map;
            }

            string[] files = Directory.GetFiles(packsDirectory, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length; i++)
            {
                string relative = files[i].Substring(packsDirectory.Length).TrimStart('\\', '/').Replace('\\', '/');
                map[relative] = Sha256Hex.OfBytes(File.ReadAllBytes(files[i]));
            }

            return map;
        }

        private static List<KeyValuePair<string, string>> Sorted(Dictionary<string, string> map)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>(map);
            list.Sort(delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b)
            {
                return string.CompareOrdinal(a.Key, b.Key);
            });
            return list;
        }

        private static bool KeptUnchanged(CaseRun run, string fileName)
        {
            string before;
            string after;
            if (!run.Before.TryGetValue(fileName, out before) || !run.After.TryGetValue(fileName, out after))
            {
                return false;
            }

            return string.Equals(before, after, StringComparison.Ordinal);
        }

        private static string Prefix(string sha256)
        {
            if (string.IsNullOrEmpty(sha256))
            {
                return "(none)";
            }

            return sha256.Substring(0, 16) + "...";
        }

        private static bool Has(string expected)
        {
            for (int i = 0; i < Log.Count; i++)
            {
                if (string.Equals(Log[i], "[INFO] " + expected, StringComparison.Ordinal)
                    || string.Equals(Log[i], "[WARN] " + expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasPrefix(string prefix)
        {
            for (int i = 0; i < Log.Count; i++)
            {
                if (Log[i].StartsWith("[INFO] " + prefix, StringComparison.Ordinal)
                    || Log[i].StartsWith("[WARN] " + prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Contains(string fragment)
        {
            for (int i = 0; i < Log.Count; i++)
            {
                if (Log[i].IndexOf(fragment, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ExpectGlobal(string what, bool ok)
        {
            _checks++;
            if (ok)
            {
                Console.WriteLine("    ok   : " + what);
            }
            else
            {
                _failures++;
                Console.WriteLine("    FAIL : " + what);
            }
        }

        private static void Expect(CaseRun run, string what, bool ok)
        {
            _checks++;
            if (ok)
            {
                Console.WriteLine("    ok   : " + what);
            }
            else
            {
                _failures++;
                Console.WriteLine("    FAIL : " + what);
            }
        }
    }
}
