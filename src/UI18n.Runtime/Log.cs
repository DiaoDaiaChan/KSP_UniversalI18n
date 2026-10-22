using UnityEngine;

namespace UI18n.Runtime
{
    /// <summary>
    /// 日志统一出口：所有行都带 <c>[UI18n]</c> 前缀，方便玩家在 KSP.log 里一眼 grep。
    /// 红线 6：失败只记日志、不抛异常；调用方负责计数并给出明确原因。
    /// </summary>
    internal static class Log
    {
        internal const string Prefix = "[UI18n] ";

        internal static void Info(string message)
        {
            Debug.Log(Prefix + message);
        }

        internal static void Warn(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        internal static void Error(string message)
        {
            Debug.LogError(Prefix + message);
        }
    }
}
