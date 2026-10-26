using System;

namespace UI18n.Core.Config
{
    /// <summary>
    /// 配置里的枚举值解析（纯函数，无 IO；Runtime 的 ConfigNode 读取与单测都走这里，
    /// 保证「字符串 → 枚举」只有一处实现）。全部大小写不敏感，容忍首尾空白。
    /// <para>
    /// 契约：<b>无法识别时返回 <c>false</c>，<paramref name="mode"/> 回落到该枚举的默认值</b>，
    /// 调用方负责记一条带原始值的日志后继续（红线 6：只 log 不抛）。
    /// </para>
    /// </summary>
    public static class ConfigEnums
    {
        /// <summary>
        /// 解析 <c>FontMode</c>：<c>Off</c> / <c>OsFont</c> / <c>Bundled</c>，另接受
        /// <c>none|disabled</c>（=Off）、<c>os|system</c>（=OsFont）、<c>bundle|assetbundle</c>（=Bundled）与 <c>0|1|2</c>。
        /// </summary>
        public static bool TryParseFontMode(string raw, out FontMode mode)
        {
            mode = FontMode.OsFont;

            string v = Canonical(raw);
            if (v == null)
            {
                return false;
            }

            if (v == "off" || v == "none" || v == "disabled" || v == "0")
            {
                mode = FontMode.Off;
                return true;
            }

            if (v == "osfont" || v == "os" || v == "system" || v == "1")
            {
                mode = FontMode.OsFont;
                return true;
            }

            if (v == "bundled" || v == "bundle" || v == "assetbundle" || v == "2")
            {
                mode = FontMode.Bundled;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 解析 <c>TmpMode</c>：<c>Auto</c> / <c>KeepSource</c> / <c>Force</c>，另接受
        /// <c>keep|source|passthrough</c>（=KeepSource）与 <c>0|1|2</c>。
        /// </summary>
        public static bool TryParseTmpMode(string raw, out TmpMode mode)
        {
            mode = TmpMode.Auto;

            string v = Canonical(raw);
            if (v == null)
            {
                return false;
            }

            if (v == "auto" || v == "0")
            {
                mode = TmpMode.Auto;
                return true;
            }

            if (v == "keepsource" || v == "keep" || v == "source" || v == "passthrough" || v == "1")
            {
                mode = TmpMode.KeepSource;
                return true;
            }

            if (v == "force" || v == "2")
            {
                mode = TmpMode.Force;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 解析 <c>LogLevel</c>：<c>Error</c> / <c>Warn</c> / <c>Info</c> / <c>Verbose</c>，另接受
        /// <c>0|1|2|3</c> 与 <c>warning|warnings</c>（=Warn）、<c>debug|trace|all</c>（=Verbose）、<c>quiet|silent|off</c>（=Error）。
        /// 无法识别时返回 <c>false</c>，<paramref name="level"/> 回落 <see cref="LogLevel.Info"/>。
        /// </summary>
        public static bool TryParseLogLevel(string raw, out LogLevel level)
        {
            level = LogLevel.Info;

            string v = Canonical(raw);
            if (v == null)
            {
                return false;
            }

            if (v == "error" || v == "errors" || v == "quiet" || v == "silent" || v == "off" || v == "0")
            {
                level = LogLevel.Error;
                return true;
            }

            if (v == "warn" || v == "warning" || v == "warnings" || v == "1")
            {
                level = LogLevel.Warn;
                return true;
            }

            if (v == "info" || v == "2")
            {
                level = LogLevel.Info;
                return true;
            }

            if (v == "verbose" || v == "debug" || v == "trace" || v == "all" || v == "3")
            {
                level = LogLevel.Verbose;
                return true;
            }

            return false;
        }

        /// <summary>枚举 → 写回配置文件 / 打印进 diag 行时用的规范名（<c>Error</c> / <c>Warn</c> / <c>Info</c> / <c>Verbose</c>）。</summary>
        public static string ToName(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: return "Error";
                case LogLevel.Warn: return "Warn";
                case LogLevel.Verbose: return "Verbose";
                default: return "Info";
            }
        }

        /// <summary>枚举 → 写回配置文件时用的规范名（<c>Off</c> / <c>OsFont</c> / <c>Bundled</c>）。</summary>
        public static string ToName(FontMode mode)
        {
            switch (mode)
            {
                case FontMode.Off: return "Off";
                case FontMode.Bundled: return "Bundled";
                default: return "OsFont";
            }
        }

        /// <summary>枚举 → 写回配置文件时用的规范名（<c>Auto</c> / <c>KeepSource</c> / <c>Force</c>）。</summary>
        public static string ToName(TmpMode mode)
        {
            switch (mode)
            {
                case TmpMode.KeepSource: return "KeepSource";
                case TmpMode.Force: return "Force";
                default: return "Auto";
            }
        }

        /// <summary>去掉首尾空白并转小写；空 / 纯空白返回 <c>null</c>。</summary>
        private static string Canonical(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            string v = raw.Trim();
            return v.Length == 0 ? null : v.ToLowerInvariant();
        }
    }
}
