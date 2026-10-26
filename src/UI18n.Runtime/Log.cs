using UI18n.Core.Config;
using UnityEngine;

namespace UI18n.Runtime
{
    /// <summary>
    /// 日志统一出口：所有行都带 <c>[UI18n]</c> 前缀，方便玩家在 KSP.log 里一眼 grep。
    /// 红线 6：失败只记日志、不抛异常；调用方负责计数并给出明确原因。
    /// <para>
    /// 级别门控（v1.3）：<see cref="Level"/> 由 <c>Config.cfg</c> 的 <c>LogLevel</c> 决定，
    /// 判据是 <c>级别 &lt;= Level</c>。默认 <c>Info</c>，即行为与 v1.2 逐行一致；
    /// 设成 <c>Warn</c>/<c>Error</c> 后 Info 行（含每 20 s 的 patch stats 与每条 <c>capture:</c>）全部静音。
    /// </para>
    /// <para>
    /// <b>门控必须在拼字符串之前</b>：<c>Log.Info("x=" + expensive())</c> 的参数在进入方法前就已求值，
    /// 所以热路径要写成 <c>if (Log.IsEnabled(LogLevel.Verbose)) { Log.Verbose(...); }</c> 或直接用
    /// <see cref="Verbose(string)"/>（它自己先判再拼，代价是一次 int 比较）。
    /// </para>
    /// </summary>
    internal static class Log
    {
        internal const string Prefix = "[UI18n] ";

        private static LogLevel _level = LogLevel.Info;

        /// <summary>当前级别。ConfigLoader 在读完 <c>Config.cfg</c> 后第一时间赋值。</summary>
        internal static LogLevel Level
        {
            get { return _level; }
            set { _level = value; }
        }

        /// <summary>本条日志在当前级别下是否出得来（热路径用它做前置判断，避免无谓的字符串拼接）。</summary>
        internal static bool IsEnabled(LogLevel level)
        {
            return level <= _level;
        }

        internal static void Verbose(string message)
        {
            if (IsEnabled(LogLevel.Verbose))
            {
                Debug.Log(Prefix + message);
            }
        }

        internal static void Info(string message)
        {
            if (IsEnabled(LogLevel.Info))
            {
                Debug.Log(Prefix + message);
            }
        }

        /// <summary>告警：<c>LogLevel=Error</c> 时静音。</summary>
        internal static void Warn(string message)
        {
            if (IsEnabled(LogLevel.Warn))
            {
                Debug.LogWarning(Prefix + message);
            }
        }

        /// <summary>错误：任何级别都出（<c>LogLevel</c> 的最低档就是它，不存在「连错误都静音」的配置）。</summary>
        internal static void Error(string message)
        {
            Debug.LogError(Prefix + message);
        }

        /// <summary>
        /// 不受级别影响的普通行：**只给启动自检的两行 <c>diag:</c> 用**（每局两行，代价可忽略）。
        /// <para>
        /// 理由：那两行是玩家报障时唯一可机读的证据，玩家把 <c>LogLevel</c> 设成 <c>Error</c> 想让日志安静，
        /// 恰恰意味着他遇到了问题 —— 此时若连自检行都被自己静音，收上来的日志就只剩错误，
        /// 连「装的是哪个版本、字典多少条、17 个 patch 装上几个」都无从判断。
        /// </para>
        /// 除 diag 外的任何行都别用这个方法，否则 <c>LogLevel</c> 形同虚设。
        /// </summary>
        internal static void Always(string message)
        {
            Debug.Log(Prefix + message);
        }
    }
}
