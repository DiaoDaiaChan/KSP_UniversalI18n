using System;
using System.IO;

namespace UI18n.Runtime.Behavior
{
    /// <summary>
    /// Runtime 行为门禁的入口（tests/runtime-behavior，见同目录 README.md）。
    /// <para>
    /// 这个工程把 <c>src/UI18n.Runtime/**/*.cs</c> 的真实源码 + tests/runtime-compile/stubs 的桩
    /// **编进同一个可执行程序集**，于是 internal 成员（<c>Translator</c> / <c>ConfigLoader</c> /
    /// <c>MissCapture</c>）可以直接调用，不需要任何 InternalsVisibleTo 或产品侧改动。
    /// </para>
    /// <para>
    /// 它证明什么：翻译主链在**真数据**上按设计工作 —— 包加载 → 三层优先级 → 骨架填回 →
    /// keep → per-mod 策略 → 缺命中捕获 → 开关 → 条目上限。
    /// 它不证明什么：没有任何 Unity / KSP 真实对象参与（桩只顶签名），真机行为仍以游戏内为准；
    /// Config.cfg 的解析也不在范围内（桩的 <c>ConfigNode.Load</c> 恒返回 null）。
    /// </para>
    /// 退出码：0 = 全部场景通过；1 = 有失败。
    /// 参数：--keep 保留临时 KSP 根；--quiet 只打失败与汇总。
    /// </summary>
    internal static class Program
    {
        private const string TempPrefix = "ui18n-behavior-";

        private static int Main(string[] args)
        {
            bool keep = Has(args, "--keep");
            bool quiet = Has(args, "--quiet");
            string root = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var runner = new Runner(quiet);
            try
            {
                runner.Run("S1 包加载 + 命中 + 缓存", delegate { Scenarios.PacksLoadAndHit(Path.Combine(root, "s1")); });
                runner.Run("S2 三层优先级（ctx > site > global）", delegate { Scenarios.KeyLevels(Path.Combine(root, "s2")); });
                runner.Run("S3 骨架填回（pattern 通道）", delegate { Scenarios.SkeletonFillBack(Path.Combine(root, "s3")); });
                runner.Run("S4 keep 行 = 不翻译、不捕获", delegate { Scenarios.KeepRows(Path.Combine(root, "s4")); });
                runner.Run("S5 per-mod 策略（NATIVE 包加载期跳过）", delegate { Scenarios.NativePolicy(Path.Combine(root, "s5")); });
                runner.Run("S6 缺命中捕获（落盘 + 重复只计数）", delegate { Scenarios.CaptureMisses(Path.Combine(root, "s6")); });
                runner.Run("S7 Enabled=false 全短路", delegate { Scenarios.Disabled(Path.Combine(root, "s7")); });
                runner.Run("S8 MaxEntries 截断", delegate { Scenarios.MaxEntries(Path.Combine(root, "s8")); });
                runner.Run("S9 没有包目录 → 空包但不抛", delegate { Scenarios.NoPacks(Path.Combine(root, "s9")); });
            }
            finally
            {
                if (!keep)
                {
                    TryDelete(root);
                }
            }

            Console.WriteLine(runner.Summary());
            return runner.Failures == 0 ? 0 : 1;
        }

        private static bool Has(string[] args, string flag)
        {
            if (args == null)
            {
                return false;
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void TryDelete(string root)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>场景跑手：一个场景一个异常边界，失败继续跑下一个（一次跑完能看全）。</summary>
    internal sealed class Runner
    {
        private readonly bool _quiet;

        internal Runner(bool quiet)
        {
            _quiet = quiet;
        }

        internal int Scenarios;
        internal int Failures;

        internal void Run(string name, Action body)
        {
            Scenarios++;
            try
            {
                body();
                if (!_quiet)
                {
                    Console.WriteLine("scenario: " + name);
                    Console.WriteLine("  ok  : " + name);
                }
            }
            catch (CheckFailed failure)
            {
                Failures++;
                Console.WriteLine("  FAIL: " + name + " - " + failure.Message);
            }
            catch (Exception error)
            {
                Failures++;
                Console.WriteLine("  FAIL: " + name + " - " + error.GetType().Name + ": " + error.Message);
            }
        }

        internal string Summary()
        {
            return "behavior: " + Scenarios + " scenarios, " + Check.Count + " checks, " + Failures + " failures";
        }
    }

    /// <summary>断言失败（不是 bug 崩溃，失败信息要能直接定位到场景里的哪一句）。</summary>
    internal sealed class CheckFailed : Exception
    {
        internal CheckFailed(string message) : base(message)
        {
        }
    }

    /// <summary>极简断言集：只有「期望 vs 实际」，失败的原文写进异常消息。</summary>
    internal static class Check
    {
        internal static int Count;

        internal static void True(string what, bool ok, string detail)
        {
            Count++;
            if (!ok)
            {
                throw new CheckFailed(what + (string.IsNullOrEmpty(detail) ? string.Empty : " (" + detail + ")"));
            }
        }

        internal static void True(string what, bool ok)
        {
            True(what, ok, null);
        }

        internal static void False(string what, bool value)
        {
            True(what, !value, "expected false");
        }

        internal static void Eq(string what, string expected, string actual)
        {
            Count++;
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new CheckFailed(what + ": expected [" + Show(expected) + "], got [" + Show(actual) + "]");
            }
        }

        internal static void Eq(string what, long expected, long actual)
        {
            Count++;
            if (expected != actual)
            {
                throw new CheckFailed(what + ": expected " + expected + ", got " + actual);
            }
        }

        internal static void Eq(string what, bool expected, bool actual)
        {
            Count++;
            if (expected != actual)
            {
                throw new CheckFailed(what + ": expected " + expected + ", got " + actual);
            }
        }

        internal static void Null(string what, string actual)
        {
            Count++;
            if (actual != null)
            {
                throw new CheckFailed(what + ": expected null, got [" + Show(actual) + "]");
            }
        }

        internal static void NotNull(string what, object value)
        {
            Count++;
            if (value == null)
            {
                throw new CheckFailed(what + ": expected not null");
            }
        }

        internal static void Contains(string what, string haystack, string needle)
        {
            Count++;
            string text = haystack ?? string.Empty;
            if (text.IndexOf(needle, StringComparison.Ordinal) < 0)
            {
                throw new CheckFailed(what + ": expected to contain [" + needle + "], got [" + Show(text) + "]");
            }
        }

        private static string Show(string text)
        {
            if (text == null)
            {
                return "null";
            }

            string one = text.Replace("\r", "\\r").Replace("\n", "\\n");
            return one.Length <= 160 ? one : one.Substring(0, 160) + "...";
        }
    }
}
