using System;
using System.Collections.Generic;

namespace UI18n.Runtime.Update
{
    /// <summary>
    /// 更新子系统的日志门面。
    /// <para>
    /// 为什么需要它：更新在<b>后台线程</b>跑，而 KSP 的 <c>Debug.Log</c> 只能在主线程调
    /// （<see cref="UI18n.Runtime.Log"/> 是 Unity 依赖）。所以这里只做「线程安全入队」，
    /// 主线程的 <see cref="Drain"/> 再把攒下的行交给 <see cref="Sink"/> 真正打出去。
    /// </para>
    /// <para>
    /// 这个文件<b>刻意不引用任何 UnityEngine 类型</b>：它连同同目录其它文件会被
    /// <c>_scratch/p1/updcheck</c> 直接编译进一个 net10 控制台，用来离线跑验收矩阵。
    /// </para>
    /// </summary>
    internal static class UpdateLog
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Pending = new List<string>();
        private static int _dropped;

        /// <summary>真正的输出端（主线程设置）：参数是（行，是否告警）。为 null 时丢弃。</summary>
        internal static Action<string, bool> Sink;

        /// <summary>排队上限：后台线程要是疯了，不能把内存吃爆。</summary>
        internal const int MaxPending = 4000;

        internal static void Info(string message)
        {
            Enqueue(message, false);
        }

        internal static void Warn(string message)
        {
            Enqueue(message, true);
        }

        private static void Enqueue(string message, bool warning)
        {
            if (message == null)
            {
                return;
            }

            string line = warning ? "WARN: " + message : message;
            lock (Gate)
            {
                if (Pending.Count >= MaxPending)
                {
                    _dropped++;
                    return;
                }

                Pending.Add(line);
            }
        }

        /// <summary>主线程调用：把攒下的行全部吐出去，返回吐了多少行。</summary>
        internal static int Drain()
        {
            string[] lines;
            int dropped;
            lock (Gate)
            {
                if (Pending.Count == 0 && _dropped == 0)
                {
                    return 0;
                }

                lines = Pending.ToArray();
                Pending.Clear();
                dropped = _dropped;
                _dropped = 0;
            }

            Action<string, bool> sink = Sink;
            if (sink != null)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string raw = lines[i];
                    bool isWarning = raw.StartsWith("WARN: ", StringComparison.Ordinal);
                    string text = isWarning ? raw.Substring(6) : raw;
                    try
                    {
                        sink(text, isWarning);
                    }
                    catch
                    {
                        // 输出端自己抛异常时不再传染：日志烂掉也不能影响更新流程
                    }
                }

                if (dropped > 0)
                {
                    try
                    {
                        sink("update log dropped " + dropped + " line(s) (queue full)", true);
                    }
                    catch
                    {
                    }
                }
            }

            return lines.Length;
        }

        /// <summary>清空（配置重启时用；不影响 <see cref="Sink"/>）。</summary>
        internal static void Reset()
        {
            lock (Gate)
            {
                Pending.Clear();
                _dropped = 0;
            }
        }

        /// <summary>当前待输出的行数（诊断用）。</summary>
        internal static int PendingCount
        {
            get
            {
                lock (Gate)
                {
                    return Pending.Count;
                }
            }
        }
    }
}
