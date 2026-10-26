using System;
using System.Collections.Generic;

namespace UI18n.Runtime
{
    /// <summary>
    /// 本局「每个 modId 装进字典多少条」的表（task-71 的开关面板要显示）。
    /// <para>
    /// 生命周期：<c>ConfigLoader.LoadPacks</c> 开头 <see cref="Reset"/>，每个包文件加载完 <see cref="Add"/>，
    /// 之后全程只读。开关面板只读它，**不重算、不重读磁盘**：包文件在启动期就已决定读不读，运行期没有再统计一次的余地。
    /// </para>
    /// <para>
    /// 线程：启动期主线程写，面板在主线程读 —— 因此刻意不加锁（加了也只是给启动路径白付一次 Monitor 开销）。
    /// </para>
    /// </summary>
    internal static class PackStats
    {
        private static readonly Dictionary<string, int> Entries =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static int _totalEntries;
        private static int _fileCount;

        /// <summary>本局装进字典的条目总数（= ready 行里的 dict entries）。</summary>
        internal static int TotalEntries
        {
            get { return _totalEntries; }
        }

        /// <summary>实际读了几个包文件（被 prefs 关掉的与 MaxEntries 截断后剩余的不计）。</summary>
        internal static int FileCount
        {
            get { return _fileCount; }
        }

        internal static void Reset()
        {
            Entries.Clear();
            _totalEntries = 0;
            _fileCount = 0;
        }

        /// <summary>记一个包文件的贡献。<paramref name="entries"/> 为负（理论上不会）按 0 处理，绝不从总数里减。</summary>
        internal static void Add(string modId, int entries)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return;
            }

            int positive = entries > 0 ? entries : 0;
            int previous;
            Entries.TryGetValue(modId, out previous);
            Entries[modId] = previous + positive;
            _totalEntries += positive;
            _fileCount++;
        }

        /// <summary>该 modId 本局装了多少条；0 = 没装（被关掉 / 没有包 / 被 MaxEntries 截断）。</summary>
        internal static int Get(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return 0;
            }

            int entries;
            return Entries.TryGetValue(modId, out entries) ? entries : 0;
        }

        /// <summary>本局装进字典的 modId（ordinal 排序，面板默认列序）。</summary>
        internal static string[] ModIds()
        {
            string[] ids = new string[Entries.Count];
            Entries.Keys.CopyTo(ids, 0);
            Array.Sort(ids, StringComparer.Ordinal);
            return ids;
        }

        internal static string Describe()
        {
            return "pack stats: mods=" + Entries.Count + ", files=" + _fileCount + ", entries=" + _totalEntries;
        }
    }
}
