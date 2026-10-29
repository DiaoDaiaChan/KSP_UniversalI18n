namespace UI18n.Core
{
    /// <summary>
    /// 本产品自己在 <c>GameData</c> 下的安装身份（**唯一真源**）。
    /// <para>
    /// 为什么单独一个类：安装目录名至少在三处被用到 —— 更新包 zip 条目校验
    /// （<see cref="UI18n.Core.Update.ZipSafety"/>）、运行时配置与字典根目录
    /// （<c>UI18n.Runtime.ConfigLoader</c>）、发布脚本的暂存前缀。2026-10 的硬编码审计里，
    /// 它是 <c>ZipSafety</c> 与 <c>ConfigLoader</c> 各写一遍的字面量，被记为存疑项；
    /// 这本身不是「写死某个 mod」（指向的是**我们自己的**目录，不是任何第三方 mod），
    /// 但两处一旦漂移，就会出现「更新包装到了我们不读的目录」这种没人会想到的故障。
    /// </para>
    /// <para>
    /// 纪律：这里放的是**本产品**的身份，不是对别的 mod 的判断。任何第三方 mod 的名字、
    /// 文件夹名、程序集名都不许出现在本文件（门禁：<c>tests/hardcode/test-no-mod-hardcode.py</c>）。
    /// </para>
    /// </summary>
    public static class ProductInfo
    {
        /// <summary>插件目录名（<c>GameData</c> 下的一级目录）。</summary>
        public const string FolderName = "UI18n";

        /// <summary>相对 <c>&lt;KSP&gt;</c> 的安装目录，正斜杠、**不带**结尾斜杠。</summary>
        public const string FolderPath = "GameData/" + FolderName;

        /// <summary>发布包 / zip 条目里的 mod 根前缀（正斜杠、带结尾斜杠）。</summary>
        public const string RootPrefix = FolderPath + "/";
    }
}
