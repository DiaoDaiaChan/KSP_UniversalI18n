namespace UI18n.Core.Config
{
    /// <summary>
    /// TMP（<c>TMPro.TMP_Text</c>）的处理档位。KSP 1.12 自带的是 TMP 1.2 位图图集版：没有
    /// <c>TMP_FontAssetUtilities</c> / <c>atlasPopulationMode</c> / <c>TryAddCharacters</c>，也没有任何
    /// <c>CreateFontAsset</c> 入口 ⇒ <b>无法在运行时给 TMP 造中文字面</b>。唯一可行路径是把「已加载且实测有汉字」的
    /// TMP 资产追加进目标资产的 <c>fallbackFontAssets</c>；库存里没有这种资产时，翻译出来就是方框。
    /// <para>因此默认 <see cref="Auto"/> 采取「可判定降级」：先用 <c>HasCharacter</c> 判断能不能显示，
    /// 不能就保留原文（确定性，不赌）。</para>
    /// <para>配置键：<c>TmpMode</c>，取值 <c>Auto</c> / <c>KeepSource</c> / <c>Force</c>（见 <see cref="ConfigEnums"/>）。</para>
    /// </summary>
    public enum TmpMode
    {
        /// <summary>默认。能修则修（追加 fallback），修不了保留原文。</summary>
        Auto = 0,

        /// <summary>一律不碰 TMP：只保留原文，不追加 fallback、不改任何 TMP 状态。</summary>
        KeepSource = 1,

        /// <summary>照常翻译并接受方框（给已自行装好 CJK TMP 资产的玩家用）。</summary>
        Force = 2,
    }
}
