using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UI18n.Core.Submission
{
    /// <summary>
    /// 客户端安装标识：<c>Guid.NewGuid().ToString("N")</c>（32 位小写 hex）。
    /// <para>
    /// 用途只有一个：服务端做本地去重（同一个安装重复投同一份 bundle ⇒ 幂等 already）。
    /// **不许**换成任何机器指纹：MAC、CPU id、磁盘序列号、用户名、机器名、安装路径一律禁止（§6.2/§6.4）。
    /// 值只存在 <c>PluginData/UI18n/prefs.json</c>，不进仓库、不进 bundle 之外的任何地方。
    /// </para>
    /// </summary>
    public static class InstallId
    {
        /// <summary>合法长度（Guid "N" 格式）。</summary>
        public const int Length = 32;

        private static readonly Regex ShapeRegex =
            new Regex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

        /// <summary>生成一个新的安装标识。</summary>
        public static string Create()
        {
            return Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        }

        /// <summary>形状校验（32 位小写 hex）。</summary>
        public static bool IsValid(string value)
        {
            return !string.IsNullOrEmpty(value) && ShapeRegex.IsMatch(value);
        }

        /// <summary>形状合法就归一成小写复用，否则生成一个新的（读旧档用）。</summary>
        public static string Ensure(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Create();
            }

            string trimmed = value.Trim();
            if (IsValid(trimmed))
            {
                return trimmed;
            }

            if (IsValid(trimmed.ToLowerInvariant()))
            {
                return trimmed.ToLowerInvariant();
            }

            return Create();
        }
    }
}
