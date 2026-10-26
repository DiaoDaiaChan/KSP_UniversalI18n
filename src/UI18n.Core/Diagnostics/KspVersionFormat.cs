using System.Globalization;

namespace UI18n.Core.Diagnostics
{
    /// <summary>
    /// KSP 版本串的格式化（纯函数，零 UnityEngine 依赖，所以能在 net10.0 测试工程里直接跑）。
    /// <para>
    /// <b>权威源是 Assembly-CSharp 的 <c>Versioning.version_major</c> / <c>.version_minor</c> /
    /// <c>.Revision</c>（第三段叫 <c>Revision</c>，没有 <c>version_revision</c> 这个成员），
    /// 不是 <c>UnityEngine.Application.version</c>。</b>
    /// 后者是 Unity 的 <c>PlayerSettings.bundleVersion</c>：实测本机装的 KSP 1.12.5
    /// （<c>buildID64.txt</c> build id 03190 / <c>readme.txt</c> "Version 1.12.5"）里
    /// <c>KSP_x64_Data/globalgamemanagers</c> 内嵌的 bundleVersion 仍写着 <c>1.12.4</c>
    /// —— Squad 出 1.12.5 时没 bump 这个字段。拿 <c>Application.version</c> 当 KSP 版本，
    /// 会把 1.12.5 的记录成 1.12.4（diag 行与投稿 bundle 的 <c>actor.kspVersion</c> 都会错）。
    /// </para>
    /// <para>
    /// 拆分出来单独成类的理由：Runtime 侧读 <c>Versioning</c> 必须有 Assembly-CSharp 才能编译，
    /// 而版本串的拼法（分隔符、未知值回退）是纯逻辑，应当可以在测试工程里钉住。
    /// </para>
    /// </summary>
    public static class KspVersionFormat
    {
        /// <summary>版本读不出来时的占位串（与 diag 行其余 Safe* 取值口径一致）。</summary>
        public const string Unknown = "?";

        private const char Separator = '.';

        /// <summary>
        /// 拼 <c>major.minor.revision</c>。<b>绝不抛</b>。
        /// <para>
        /// 上界不设（KSP 2 之类写 2.0.0 也照样输出），只判「明显不是版本号」：
        /// <c>major &lt;= 0</c>（含全 0）或任何一段为负 → 返回 <see cref="Unknown"/>，
        /// 因为 Unity 未初始化时 <c>Versioning</c> 的三段全是 0，输出 "0.0.0" 只会误导读者。
        /// </para>
        /// </summary>
        public static string FromParts(int major, int minor, int revision)
        {
            if (major <= 0 || minor < 0 || revision < 0)
            {
                return Unknown;
            }

            return major.ToString(CultureInfo.InvariantCulture)
                + Separator.ToString()
                + minor.ToString(CultureInfo.InvariantCulture)
                + Separator.ToString()
                + revision.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>是不是「读不出来」的占位串（<see cref="Unknown"/> 或空）。</summary>
        public static bool IsUnknown(string version)
        {
            return string.IsNullOrEmpty(version) || string.Equals(version, Unknown, System.StringComparison.Ordinal);
        }
    }
}
