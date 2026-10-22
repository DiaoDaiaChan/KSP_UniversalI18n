using System;
using System.Collections.Generic;
using UI18n.Core.Config;
using UI18n.Core.Dict;
using UI18n.Core.Hashing;
using UI18n.Core.Text;
using UnityEngine;

namespace UI18n.Runtime
{
    /// <summary>
    /// 运行时翻译门面：原文 → 归一化 → FNV-1a 64 → 字典查表，带两级缓存。
    /// <para>
    /// 名字刻意不叫 Localizer，避免与本工程大量使用的 <c>KSP.Localization.Localizer</c> 撞名
    /// （在 UI18n.Runtime 命名空间里，裸写 Localizer 会先解析到本程序集）。
    /// </para>
    /// <para>
    /// <b>为什么缓存 GUIContent 实例而不是字符串</b>（决策 D25）：命中路径每帧都在跑，KSP 用 Boehm GC，
    /// 每帧新建字符串/对象约 2.9 MB/s 分配；这里把「原文 → 译文」和「原文 → 新 GUIContent」都缓存下来，
    /// 命中后只剩两次字典查找。
    /// </para>
    /// <para>
    /// <b>绝不能做的事</b>：不写 <c>content.text</c>（<c>GUIContent.Temp</c> 返回共享实例，写它会污染所有同文本控件），
    /// 只 <c>new GUIContent(...)</c> 并写回 <c>ref</c> 参数；本类只读游戏状态，不碰玩法 API。
    /// </para>
    /// <para>线程模型：只允许主线程调用（IMGUI / uGUI 都在主线程）。</para>
    /// </summary>
    internal static class Translator
    {
        /// <summary>缓存上限，超过就整体清空（UI 文本种类远小于此，正常游戏不会触发）。</summary>
        private const int CacheLimit = 20000;

        private static readonly Dictionary<string, string> TextCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GUIContent> ContentCache = new Dictionary<string, GUIContent>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReportedFailures = new HashSet<string>(StringComparer.Ordinal);

        private static UI18nConfig _config;
        private static DictPack _pack;

        /// <summary>语言 / 开关已过门禁且字典非空 —— patch 前缀的第一道短路（05 §6.2）。</summary>
        internal static bool HasAnyTranslation
        {
            get { return _pack != null && _pack.Entries > 0; }
        }

        internal static UI18nConfig Config
        {
            get { return _config; }
        }

        internal static DictPack Pack
        {
            get { return _pack; }
        }

        internal static int CachedTexts
        {
            get { return TextCache.Count; }
        }

        internal static void Initialize(UI18nConfig config, DictPack pack)
        {
            _config = config ?? UI18nConfig.Default;
            _pack = pack;
            TextCache.Clear();
            ContentCache.Clear();
            ReportedFailures.Clear();
        }

        /// <summary>
        /// 原文 → 译文。命中返回译文；未命中 / keep / 空输入返回 <c>null</c>（调用方据此零分配跳过）。
        /// 未命中也会缓存（存空串），避免同一串被反复哈希。
        /// </summary>
        internal static string Translate(string source)
        {
            if (string.IsNullOrEmpty(source) || _pack == null)
            {
                return null;
            }

            UI18nConfig config = _config;
            if (config != null && !config.Enabled)
            {
                return null;
            }

            string cached;
            if (TextCache.TryGetValue(source, out cached))
            {
                return cached.Length == 0 ? null : cached;
            }

            // 归一化（04 §4.1 五步）+ FNV-1a 64（D11）；已归一时 Normalize 返回原引用，零分配
            string normalized = TextNormalizer.Normalize(source);
            string translation = null;

            if (normalized.Length > 0)
            {
                ulong hash = Fnv1a.Hash(normalized);
                string hit;
                if (_pack.TryGet(hash, normalized, out hit))
                {
                    translation = hit;
                }
            }

            if (TextCache.Count >= CacheLimit)
            {
                TextCache.Clear();
                ContentCache.Clear();
            }

            TextCache[source] = translation ?? string.Empty;
            return translation;
        }

        /// <summary>
        /// GUIContent 版本：命中返回<b>新建并缓存</b>的 GUIContent（保留 image / tooltip），
        /// 未命中返回 <c>null</c>（调用方保留原对象，不产生分配）。
        /// </summary>
        internal static GUIContent TranslateContent(GUIContent content)
        {
            if (content == null)
            {
                return null;
            }

            string text = content.text;
            string translation = Translate(text);
            if (translation == null)
            {
                return null;
            }

            // 带图标 / tooltip 的控件很少，这类不进缓存：缓存按文本做 key，混用会串图标
            if (content.image != null || !string.IsNullOrEmpty(content.tooltip))
            {
                return new GUIContent(translation, content.image, content.tooltip);
            }

            GUIContent cached;
            if (ContentCache.TryGetValue(text, out cached))
            {
                return cached;
            }

            // 只能是新对象：GUIContent.Temp(...) 返回共享实例，就地改写会污染工坊里所有同文本控件
            cached = new GUIContent(translation);
            ContentCache[text] = cached;
            return cached;
        }

        /// <summary>patch 前缀里的异常只记一次（红线 6），不打断游戏、不刷屏。</summary>
        internal static void ReportPrefixFailure(string patch, Exception exception)
        {
            if (!ReportedFailures.Add(patch))
            {
                return;
            }

            Log.Error("prefix failed: " + patch + " → " + exception.GetType().Name + ": " + exception.Message
                      + "（同类错误每局只记一次）");
        }
    }
}
