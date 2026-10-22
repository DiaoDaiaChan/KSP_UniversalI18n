namespace UI18n.Runtime.Fonts
{
    /// <summary>
    /// 「这个词条翻译成中文后，能不能真的显示出来」的三态判定（P2 的「可判定降级」核心）。
    /// <para>uGUI 与 TMP 共用同一套三态，前缀据此决定「换文本」还是「保留原文」。</para>
    /// </summary>
    internal enum FontCheck
    {
        /// <summary><c>FontMode=Off</c>：玩家明确要求不碰字体 —— 照常翻译，缺字自己承担。</summary>
        Disabled = 0,

        /// <summary>已确认（或已修好）能显示中文 ⇒ 允许把文本换成译文。</summary>
        Available = 1,

        /// <summary>无法保证能显示中文，且玩家没有强制要求 ⇒ 保留原文（确定性，不赌）。</summary>
        Unavailable = 2,
    }
}
