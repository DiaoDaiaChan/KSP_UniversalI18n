// 出包脚本的 reader round-trip 门禁：用**运行时同一个 C# 读取器**解刚产出的 manifest。
//
// 为什么需要它（2026-10-26 的 1.6.0 事故）：build-update-package.ps1 自 v1.2 起就按 files[] 聚合形态写
// mods[]，而当时的 UI18n.Core 只认单文件 file 形态，于是游戏里每份清单都被
// "bad-mod-entry:0: missing-file" 整份拒掉 —— 出包脚本自己的断言是拿 PowerShell 的 ConvertFrom-Json
// 读回自己写的 JSON，读取器的断言是拿手写 fixture 喂自己，两边都不校验对方，所以整整五个版本没人发现。
//
// 这个程序只做一件事：把清单交给 UI18n.Core.Update.UpdateManifest 真解一遍，并核对 mods[]/files[]
// 的对应关系。退出码 0 = 通过，1 = 不通过（原因打到 stdout），2 = 用法错误。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UI18n.Core.Update;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: ManifestRoundtrip <manifest.json>");
            return 2;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("FATAL 找不到清单: " + path);
            return 2;
        }

        StringBuilder problems = new StringBuilder();
        string text = File.ReadAllText(path, Encoding.UTF8);

        UpdateManifest manifest;
        string error;
        if (!UpdateManifest.TryParse(text, out manifest, out error))
        {
            problems.AppendLine("reader rejected: " + error);
            Console.WriteLine("FAIL roundtrip: " + problems.ToString().TrimEnd());
            return 1;
        }

        // 清单自带的契约标记必须就是本读取器的（TryParse 已校验，这里再报一次，方便人看日志）
        Console.WriteLine("ok readerContract=" + UpdateManifest.ReaderContract
                          + " version=" + manifest.Version
                          + " mods=" + manifest.ModCount
                          + " files=" + manifest.FileCount);

        if (manifest.FileCount == 0)
        {
            problems.AppendLine("files[] 为空：更新包没有任何包文件");
        }

        if (manifest.ModCount == 0)
        {
            problems.AppendLine("mods[] 为空：运行时 PackInstallFilter 拿不到权威表，玩家开关会失效");
        }

        HashSet<string> declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile file in manifest.Files)
        {
            declared.Add(file.Path);
        }

        HashSet<string> claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifest.Mods.Count; i++)
        {
            ManifestMod mod = manifest.Mods[i];
            if (mod.FileCount < 1)
            {
                problems.AppendLine("mods[" + i + "] " + mod.ModId + " 名下 0 个文件");
            }

            foreach (string raw in mod.Files)
            {
                if (!declared.Contains(raw))
                {
                    problems.AppendLine("mods[" + i + "] " + mod.ModId + " -> " + raw + " 不在 files[] 里");
                }

                if (!claimed.Add(raw))
                {
                    problems.AppendLine(raw + " 同时被多个 modId 认领");
                }
            }
        }

        foreach (ManifestFile file in manifest.Files)
        {
            if (!claimed.Contains(file.Path))
            {
                problems.AppendLine(file.Path + " 不属于任何 modId（mods[] 漏了它）");
            }
        }

        if (problems.Length > 0)
        {
            Console.WriteLine("FAIL roundtrip:");
            Console.Write(problems.ToString());
            return 1;
        }

        Console.WriteLine("PASS roundtrip: reader 接受这份清单，mods[] 与 files[] 一一对应");
        return 0;
    }
}
