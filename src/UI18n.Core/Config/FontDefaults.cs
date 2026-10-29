using System;
using System.Collections.Generic;

namespace UI18n.Core.Config
{
    /// <summary>
    /// 中文字体候选的出厂默认值：<b>全仓唯一一份</b>。
    /// <para>
    /// 这些名字以前有两份副本（Runtime 的 <c>FontResolver</c> 常量表 + <c>Config.cfg</c> 注释里的散文列表），
    /// 改一处忘另一处就会漂移：文档说会试 SimHei，代码其实根本没试。现在只有这里定义，Runtime 在
    /// <c>FontResolver.Configure</c> 里读 <see cref="UI18nConfig.OsFonts"/>（默认值即本表），
    /// 玩家可以在 Config.cfg 用 <c>OsFonts = 字体A, 字体B</c> 整表覆盖 —— 换 Linux / macOS 不必改代码。
    /// </para>
    /// <para>
    /// 放在 Core 而不是 Runtime 的原因：Core 是双目标纯 BCL 库，单测能直接断言这张表的行为；
    /// Runtime 依赖 KSP 程序集，在没有游戏的机器上根本编译不出来（本机就是这种情况）。
    /// </para>
    /// </summary>
    public static class FontDefaults
    {
        /// <summary>
        /// 首选系统字体名的出厂默认值（配置键 <c>OsFontName</c>）。
        /// 必须与 <see cref="OsFontCandidates"/> 首项一致：不一致会让「首选」和「回退顺序第一名」打架，
        /// 日志里就会出现「asked X, tried Y first」这种自相矛盾的记录。
        /// </summary>
        public const string OsFontName = "Microsoft YaHei";

        /// <summary>
        /// 首选字体不在本机时的回退顺序（按精确名匹配 <c>Font.GetOSInstalledFontNames()</c>，
        /// 建出来还要 <c>HasCharacter('中')</c> 实测通过才会用）。
        /// 覆盖 Windows（雅黑 / 雅黑 UI / 黑体 / 等线 / 宋体 / 微软正黑）与跨平台常见的 Noto Sans SC。
        /// <para>
        /// <b>不要就地改这个数组</b>：它是静态共享的，任何就地修改都会污染所有实例；
        /// 需要持有副本请用 <see cref="NewOsFontCandidates"/>。
        /// </para>
        /// </summary>
        public static readonly string[] OsFontCandidates =
        {
            "Microsoft YaHei",
            "Microsoft YaHei UI",
            "SimHei",
            "Noto Sans SC",
            "DengXian",
            "SimSun",
            "Microsoft JhengHei",
        };

        /// <summary>候选表的<b>副本</b>：配置对象持有副本，改它不会污染出厂默认值。</summary>
        public static string[] NewOsFontCandidates()
        {
            return (string[])OsFontCandidates.Clone();
        }

        /// <summary>
        /// 解析配置里的 <c>OsFonts</c>（逗号或分号分隔）：去首尾空白、丢空项、按序去重（忽略大小写）。
        /// <para>
        /// 空值 / 全是分隔符 / 全是空白 → 返回空数组，调用方据此<b>保留内置顺序</b>：
        /// 一个写错的配置键不该把「找中文字体」这件事直接关掉（红线 6：配置非法只降级不阻断）。
        /// </para>
        /// </summary>
        public static string[] ParseFontList(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return new string[0];
            }

            string[] parts = raw.Split(new char[] { ',', ';' }, StringSplitOptions.None);
            var list = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string name = parts[i] == null ? string.Empty : parts[i].Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                bool duplicate = false;
                for (int j = 0; j < list.Count; j++)
                {
                    if (string.Equals(list[j], name, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    list.Add(name);
                }
            }

            return list.ToArray();
        }
    }
}
