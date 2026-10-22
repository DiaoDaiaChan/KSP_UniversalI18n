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
    /// 它是本项目<b>唯一</b>允许改 <c>string</c> 参数的目标；其余目标一律只换 GUIContent 对象（红线 4）。
    /// </para>
    /// </summary>
    internal static class ScreenMessagesPatches
    {
        /// <summary>
        /// 把屏幕消息文本换成译文；未命中/keep 时返回 null，原样放行。
        /// 失败只记一次（<see cref="Translator.ReportPrefixFailure"/> 内部去重），绝不抛（红线 6）。
        /// </summary>
        private static void MessagePrefix(ref string msg)
        {
            if (!Translator.HasAnyTranslation)
            {
                return;
            }

            try
            {
                string translated = Translator.Translate(msg);
                if (translated != null)
                {
                    msg = translated;
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure("KSP.ScreenMessages", ex);
            }
        }
    }
}
