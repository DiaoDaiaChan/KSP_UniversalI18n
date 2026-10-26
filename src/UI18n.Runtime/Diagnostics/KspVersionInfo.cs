using System;
using UI18n.Core.Diagnostics;
using UnityEngine;

namespace UI18n.Runtime.Diagnostics
{
    /// <summary>
    /// KSP 版本的两个口径（1.7.0，lead 派单）。
    /// <para>
    /// <see cref="Authoritative"/> 读 Assembly-CSharp 的 <c>Versioning.version_major</c> /
    /// <c>.version_minor</c> / <c>.Revision</c>（三个都是 <c>int</c> 静态属性；注意第三段的名字是
    /// <c>Revision</c>，<b>不是</b> <c>version_revision</c> —— 后者在本程序集里根本不存在，
    /// 元数据里只有 <c>get_Revision</c>）—— 这是 KSP 自己的版本号，
    /// 主菜单显示的那个口径，也是 diag 行与投稿 bundle 里 <c>actor.kspVersion</c> 该用的值。
    /// </para>
    /// <para>
    /// <see cref="BundleVersion"/> 读 <c>UnityEngine.Application.version</c> —— 那是 Unity 侧的
    /// <c>PlayerSettings.bundleVersion</c>。<b>它不可靠</b>：本机 1.12.5 的安装里这个字段仍是
    /// "1.12.4"（Squad 没 bump）。保留它只为「玩家报版本对不上时能立刻区分是装错了版本，
    /// 还是 Squad 没 bump bundleVersion」，不作为任何记录的主口径。
    /// </para>
    /// <para>
    /// 两个方法都<b>绝不抛</b>：diag 行存在的意义就是「环境可疑时也能打出来」，
    /// 而 <c>Versioning</c> 的静态字段在极早期（Unity 尚未初始化）取值可能是 0 或抛类型初始化异常。
    /// </para>
    /// </summary>
    internal static class KspVersionInfo
    {
        /// <summary>权威版本（<c>major.minor.revision</c>）；读不出来返回 <c>"?"</c>。</summary>
        internal static string Authoritative()
        {
            try
            {
                return KspVersionFormat.FromParts(
                    Versioning.version_major,
                    Versioning.version_minor,
                    Versioning.Revision);
            }
            catch (Exception)
            {
                return KspVersionFormat.Unknown;
            }
        }

        /// <summary>Unity bundleVersion（仅供对照）；读不出来返回空串。</summary>
        internal static string BundleVersion()
        {
            try
            {
                return Application.version ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
