using System;
using System.Collections.Generic;
using UnityEngine;

namespace UI18n.Runtime.Patches
{
    /// <summary>
    /// 第 17 个 hook（task-35）：<c>GUI.DoButtonGrid</c> —— Toolbar / SelectionGrid 的按钮文字。
    /// <para>
    /// 为什么要单独打这一层：<c>GUILayout.Toolbar</c> / <c>GUI.Toolbar</c> / <c>GUI.SelectionGrid</c> 都汇到 private
    /// <c>GUI.DoButtonGrid(Rect position, int selected, GUIContent[] contents, string[] controlNames, int xCount,
    /// GUIStyle style, GUIStyle firstStyle, GUIStyle midStyle, GUIStyle lastStyle, GUI.ToolbarButtonSize buttonSize,
    /// bool[] contentsEnabled)</c>，而它<b>直接</b> <c>GUIStyle.Draw(rect, content, …)</c> —— 不经过
    /// <c>GUI.Label/Box/Button/RepeatButton/Toggle</c>，也不经过 <c>GUILayout.Do*</c>，所以其余 16 个目标
    /// <b>一个都看不到</b>它（T31 的「影子收敛」结论之所以说零收益，是因为当轮屏幕上没有 Toolbar/SelectionGrid，
    /// 见 README §9.9）。Toolbar 文本还常常是 <c>Enum.GetNames(...)</c> 的运行时产物（KSP 的 DebugToolbar 就是这样），
    /// 静态扫描也永远看不见。
    /// </para>
    /// <para>
    /// 与其余目标的差别：它们的文本参数是 <c>ref GUIContent</c>，可以直接换成新对象；这里是
    /// <c>GUIContent[] contents</c>，<b>元素</b>才是文本载体。做法是<b>换数组槽位</b>：
    /// <c>contents[i] = Translator.TranslateContent(contents[i], …)</c>（该方法返回<b>新建并缓存</b>的 GUIContent，
    /// 见 D25），并记下 (下标, 原对象)，Postfix 里逐个放回原对象。
    /// </para>
    /// <para>
    /// <b>为什么不写 <c>contents[i].text</c></b>（红线 4）：数组里的 GUIContent 往往来自
    /// <c>GUIContent.Temp(texts)</c>，那是<b>静态池里的共享实例</b>；一旦就地写 <c>.text</c>，只要原方法在
    /// prefix 与 postfix 之间抛异常（Harmony 此时<b>不执行</b> postfix），这个实例就永久带着译文，之后所有复用
    /// 到它的控件都会显示错误文本。换槽位只改数组本身，共享实例一个字节都没动 —— 比「就地写 .text + 还原」更安全，
    /// 代价只是第一次命中时 new 一个 GUIContent（之后走 ContentCache）。
    /// </para>
    /// <para>
    /// 还原记录用静态栈（<see cref="ChangedIndices"/> / <see cref="Originals"/>）+ <c>int __state</c>
    /// （本次调用改了几个）。理论上 <c>DoButtonGrid</c> 不会重入（体内只调 <c>GUIStyle.Draw</c> 与 native），
    /// 栈按 LIFO 语义也能容忍重入；真正要防的是「原方法抛异常 → postfix 没跑 → 记录泄漏」，
    /// 所以 prefix 开头发现栈非空就丢弃过期记录并记一次日志。
    /// </para>
    /// <para>线程模型：只允许主线程（IMGUI）。</para>
    /// </summary>
    internal static class ButtonGridPatches
    {
        private const int InitialCapacity = 32;

        /// <summary>被换过的数组下标（与 <see cref="Originals"/> 一一对应，LIFO）。</summary>
        private static readonly List<int> ChangedIndices = new List<int>(InitialCapacity);

        /// <summary>被换掉的原对象（postfix 里原样放回槽位，因此连原文都不用记）。</summary>
        private static readonly List<GUIContent> Originals = new List<GUIContent>(InitialCapacity);

        private static bool _staleRecordsLogged;

        /// <summary>重新装 patch 前清空还原记录（<see cref="PatchRegistry.InstallAll"/> 调用）。</summary>
        internal static void Reset()
        {
            ChangedIndices.Clear();
            Originals.Clear();
            _staleRecordsLogged = false;
        }

        /// <summary>
        /// 逐元素换成译文 GUIContent（命中才换），并记录 (下标, 原对象) 供 postfix 还原。
        /// <paramref name="__state"/> = 本次换了几个；异常只记一次日志、绝不 rethrow（红线 6），
        /// 已经换过的元素仍然由 postfix 还原（异常在我们自己的 try 里被吞掉，原方法照常执行到返回）。
        /// </summary>
        private static void ButtonGridPrefix(GUIContent[] contents, ref int __state)
        {
            __state = 0;

            PatchCounter counter = Counters.GuiButtonGrid;
            counter.Touch();

            if (!Translator.HasAnyTranslation)
            {
                return;
            }

            if (contents == null || contents.Length == 0)
            {
                return;
            }

            if (ChangedIndices.Count != 0)
            {
                // 上一次的 postfix 没跑（原方法抛异常时 Harmony 不执行 postfix）→ 记录已过期，丢弃
                if (!_staleRecordsLogged)
                {
                    _staleRecordsLogged = true;
                    Log.Warn("button grid: previous postfix did not run (original threw?), stale restore records dropped");
                }

                ChangedIndices.Clear();
                Originals.Clear();
            }

            try
            {
                for (int i = 0; i < contents.Length; i++)
                {
                    GUIContent original = contents[i];
                    if (original == null || string.IsNullOrEmpty(original.text))
                    {
                        continue;
                    }

                    GUIContent translated = Translator.TranslateContent(original, PatchNames.GuiButtonGrid);
                    if (translated == null || ReferenceEquals(translated, original))
                    {
                        continue;
                    }

                    contents[i] = translated;
                    ChangedIndices.Add(i);
                    Originals.Add(original);
                    counter.Hits++;
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure(PatchNames.GuiButtonGrid, ex);
            }

            __state = ChangedIndices.Count;
        }

        /// <summary>把 <paramref name="__state"/> 个槽位逐个放回原对象（LIFO），并清掉这些记录。</summary>
        private static void ButtonGridPostfix(GUIContent[] contents, int __state)
        {
            if (__state <= 0)
            {
                return;
            }

            try
            {
                int count = __state < ChangedIndices.Count ? __state : ChangedIndices.Count;
                for (int k = count - 1; k >= 0; k--)
                {
                    int index = ChangedIndices[k];
                    if (contents != null && index >= 0 && index < contents.Length)
                    {
                        contents[index] = Originals[k];
                    }
                }

                if (count > 0)
                {
                    ChangedIndices.RemoveRange(ChangedIndices.Count - count, count);
                    Originals.RemoveRange(Originals.Count - count, count);
                }
            }
            catch (Exception ex)
            {
                Translator.ReportPrefixFailure(PatchNames.GuiButtonGrid, ex);
            }
        }
    }
}
