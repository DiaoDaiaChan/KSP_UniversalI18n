namespace UI18n.Core.Config
{
    /// <summary>
    /// 旧版 uGUI（<c>UnityEngine.UI.Text</c>）的字体兜底模式。uGUI 的 <c>Font</c> 支持运行时从系统字体动态建字面
    /// （<c>Font.CreateDynamicFontFromOSFont</c>），所以中文可以真修；TMP 不行，见 <see cref="TmpMode"/>。
    /// <para>配置键：<c>FontMode</c>，取值 <c>Off</c> / <c>OsFont</c> / <c>Bundled</c>（大小写不敏感，见 <see cref="ConfigEnums"/>）。</para>
    /// </summary>
    public enum FontMode
    {
        /// <summary>完全不碰字体（翻译照做，缺字时玩家自己看到方框 —— 给知道自己在干什么的人留的出口）。</summary>
        Off = 0,

        /// <summary>默认。从系统已安装字体里按候选名建动态字面，建出来必须 <c>HasCharacter('中')</c> 实测通过才用。</summary>
        OsFont = 1,

        /// <summary>从 <c>BundledFontPath</c> 指向的 AssetBundle 里取 <c>Font</c>；失败只记原因并回落 <see cref="OsFont"/>。</summary>
        Bundled = 2,
    }
}
