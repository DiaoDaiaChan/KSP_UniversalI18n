using System;
using System.Globalization;
using System.IO;

namespace UI18n.Runtime.Update
{
    /// <summary>
    /// 更新一次运行所需的全部输入（路径 + 策略），由 <see cref="UpdateRunner"/> 从
    /// KSP 侧填好，然后整个对象交给后台线程只读使用。
    /// <para>
    /// 刻意做成「构造后不可变」：后台线程读它时不需要任何锁，也不会读到半个配置。
    /// </para>
    /// </summary>
    internal sealed class UpdateEnvironment
    {
        internal UpdateEnvironment(
            string baseUrl,
            string channel,
            string modRoot,
            string pluginDataRoot,
            string localVersion,
            string language,
            int timeoutSeconds,
            int checkDays,
            bool checkForUpdates,
            bool autoDownload)
        {
            BaseUrl = baseUrl ?? string.Empty;
            Channel = channel ?? string.Empty;
            ModRoot = modRoot ?? string.Empty;
            PluginDataRoot = pluginDataRoot ?? string.Empty;
            LocalVersion = localVersion ?? string.Empty;
            Language = language ?? string.Empty;
            TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 10;
            CheckDays = checkDays;
            CheckForUpdates = checkForUpdates;
            AutoDownload = autoDownload;
        }

        /// <summary>更新源根地址（空 = 不联网）。</summary>
        internal string BaseUrl { get; private set; }

        /// <summary>通道（stable / testing ...）。</summary>
        internal string Channel { get; private set; }

        /// <summary>mod 根目录（游戏里是 <c>&lt;KSP&gt;/GameData/UI18n</c>）。</summary>
        internal string ModRoot { get; private set; }

        /// <summary>插件数据根目录（游戏里是 <c>&lt;KSP&gt;/PluginData/UI18n</c>）。</summary>
        internal string PluginDataRoot { get; private set; }

        /// <summary>本地版本（形如 <c>1.1.0+29ffa5c...</c>，比较时由 ReleaseVersion 截掉后缀）。</summary>
        internal string LocalVersion { get; private set; }

        /// <summary>当前语言（只安装该语言的包）。</summary>
        internal string Language { get; private set; }

        /// <summary>单次请求超时秒数。</summary>
        internal int TimeoutSeconds { get; private set; }

        /// <summary>两次检查的最小间隔天数（0 = 每次都查）。</summary>
        internal int CheckDays { get; private set; }

        /// <summary>是否允许检查更新。</summary>
        internal bool CheckForUpdates { get; private set; }

        /// <summary>是否允许自动下载安装。</summary>
        internal bool AutoDownload { get; private set; }

        /// <summary>下载缓存目录（zip 落这里，只作证据，安装从内存装）。</summary>
        internal string DownloadsDirectory
        {
            get { return Path.Combine(PluginDataRoot, "downloads"); }
        }

        /// <summary>状态文件路径。</summary>
        internal string StateFilePath
        {
            get { return Path.Combine(PluginDataRoot, "update-state.json"); }
        }

        /// <summary>本地包目录（<c>&lt;mod&gt;/Packs/&lt;lang&gt;</c>）。</summary>
        internal string PacksDirectory
        {
            get { return Path.Combine(Path.Combine(ModRoot, "Packs"), Language); }
        }

        /// <summary>一行摘要（启动日志用，不含任何凭据）。</summary>
        internal string Describe()
        {
            return "base=" + (BaseUrl.Length == 0 ? "(empty)" : BaseUrl)
                + ", channel=" + (Channel.Length == 0 ? "(empty)" : Channel)
                + ", local=" + (LocalVersion.Length == 0 ? "(unknown)" : LocalVersion)
                + ", lang=" + (Language.Length == 0 ? "(unknown)" : Language)
                + ", timeout=" + TimeoutSeconds.ToString(CultureInfo.InvariantCulture) + "s"
                + ", everyDays=" + CheckDays.ToString(CultureInfo.InvariantCulture)
                + ", autoDownload=" + (AutoDownload ? "True" : "False");
        }
    }
}
