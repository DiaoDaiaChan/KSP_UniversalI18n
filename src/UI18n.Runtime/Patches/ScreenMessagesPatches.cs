using System;

namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// KSP 屏幕消息（右下角/中下方的提示条）的 patch：只有 <b>1 个</b>目标。
    /// <para>
    /// 漏斗结构（反编译 <c>Assembly-CSharp.dll</c> 的 <c>ScreenMessages</c> 实测）：
    /// 10 个公开 <c>PostScreenMessage(...)</c> 重载（9 个收 <c>string message</c>、1 个收 <c>ScreenMessage</c>）全部转发到
    /// 两个私有重载：5 参 <c>PostMessage(ScreenMessage, string msg, ScreenMessagesText, float, ScreenMessageStyle)</c>
    /// 与 6 参同名方法（多一个 <c>Color</c>）；而 5 参版的函数体就是 6 参版（补 <c>defaultColor</c>）。
    /// ⇒ 只打 6 参重载即覆盖 100% 屏幕消息，5 参重载再打一遍是纯冗余（多一次必然未命中的查表）。
    /// </para>
    /// <para>
    /// 红线 3：这里改的只是<b>传进来的值</b> <c>msg</c>（ref 参数的重写），没有碰 <c>ScreenMessage</c> 实例、
    /// 没有改 <c>ScreenMessages</c> 的静态状态、没有调任何玩法 API。也正因为是 string 而不是 GUIContent，
    /// 它是本项目<b>唯一</b>允许改 <c>string</c> 参数的游戏侧目标；其余目标一律只换 GUIContent 对象（红线 4）。
    /// </para>
    /// <para>
    /// 屏幕消息的字体不走 <see cref="Runtime.Fonts.FontResolver"/>：它由 KSP 自己的 <c>ScreenMessagesText</c>
    /// 预制体提供，字体问题归 KSP 与语言包，不由本模块改；这里只统计调用/命中。
    /// </para>
    /// </summary>
    internal static class ScreenMessagesPatches
    {
        /// <summary>
        /// 把屏幕消息文本换成译文；未命中/keep 时返回 null，原样放行。
        /// 保存原实例中的英文 message 到 __state，防止 KSP 内部将静态/复用 ScreenMessage 的 message 字段永久污染为中文。
        /// 失败只记一次（<see cref="Translator.ReportPrefixFailure"/> 内部去重），绝不抛（红线 6）。
        /// </summary>
        private static void MessagePrefix(ScreenMessage current, ref string msg, out string __state)
        {
            __state = current != null ? current.message : null;

            PatchCounter counter = Counters.ScreenMessage;
            counter.Touch();

            if (!Translator.HasAnyTranslation)
            {
                return;
            }

            try
            {
                string translated = Translator.Translate(msg, PatchNames.ScreenMessage);
                if (translated != null)
                {
                    msg = translated;
                    counter.Hits++;
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure(PatchNames.ScreenMessage, ex);
            }
        }

        /// <summary>
        /// 后缀恢复：若调用方传入了现成 ScreenMessage 实例，在显示后将其 message 字段还原为原英文，
        /// 避免常驻或静态 ScreenMessage 对象被覆写导致后续显示无法再次命中翻译或无法切回英文。
        /// </summary>
        private static void MessagePostfix(ScreenMessage current, string __state)
        {
            if (current != null && __state != null)
            {
                current.message = __state;
            }
        }
    }
}
