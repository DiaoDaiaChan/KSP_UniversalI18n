using System;
using System.IO;
using UI18n.Core.Capture;
using UI18n.Core.Config;
using UnityEngine;

namespace UI18n.Runtime
{
    /// <summary>
    /// 缺命中捕获的运行时外壳（task-30）：建 <see cref="CaptureStore"/>、定落盘路径、每 60 s 与退出时追加写。
    /// <para>
    /// 过滤 / 去重 / 上限 / 序列化全在 <c>UI18n.Core.Capture</c>（纯 BCL，可单测）；这里只做「Unity 侧的事」：
    /// 时钟、路径、日志、异常吞掉。翻译主路径永远不因捕获失败而受影响（红线 6：只 log 不抛）。
    /// </para>
    /// <para>
    /// 落盘位置：<c>&lt;KSP 根&gt;/PluginData/UI18n/capture-&lt;yyyyMMdd-HHmmss&gt;.jsonl</c>（UTC 时间戳）。
    /// PluginData 是 KSP 约定的「mod 私有数据」目录，不进 GameData、不会被 CKAN / ModuleManager 扫到。
    /// </para>
    /// <para>线程模型：只在主线程调用（补丁前缀 / Update / 退出钩子）。</para>
    /// </summary>
    internal static class MissCapture
    {
        /// <summary>自动落盘间隔（秒）。退出时还会强制写一次。</summary>
        internal const float FlushIntervalSeconds = 60f;

        private static CaptureStore _store;
        private static string _path;
        private static float _nextFlushAt;
        private static bool _configured;
        private static bool _writeFailureLogged;
        private static bool _shutdownDone;
        private static long _flushes;

        /// <summary>当前捕获器（未启用时为 <c>null</c>）。</summary>
        internal static CaptureStore Store
        {
            get { return _store; }
        }

        /// <summary>落盘文件全路径（未启用时为 <c>null</c>）。</summary>
        internal static string FilePath
        {
            get { return _path; }
        }

        /// <summary>已成功落盘的轮数（诊断用）。</summary>
        internal static long Flushes
        {
            get { return _flushes; }
        }

        /// <summary>
        /// 按配置建捕获器。必须在 <c>Translator.Initialize</c> 之后调用（Initialize 会清掉挂载），
        /// 并在挂载之后调 <c>Translator.AttachCapture(MissCapture.Store)</c>。
        /// </summary>
        internal static void Configure(UI18nConfig config)
        {
            _store = null;
            _path = null;
            _configured = false;
            _writeFailureLogged = false;
            _flushes = 0;

            if (config == null || !config.CaptureMisses)
            {
                Log.Info("capture: disabled (CaptureMisses=False)");
                return;
            }

            try
            {
                CaptureOptions options = CaptureOptions.Default;
                options.Enabled = true;
                options.MaxEntries = config.CaptureMaxEntries < 0 ? 0 : config.CaptureMaxEntries;
                options.MaxTemplateEntries = config.CaptureMaxTemplateEntries;

                string dir = CaptureDirectory();
                _path = Path.Combine(dir, CaptureStore.BuildFileName(DateTime.UtcNow));
                _store = new CaptureStore(options);
                _nextFlushAt = Time.realtimeSinceStartup + FlushIntervalSeconds;
                _configured = true;

                Log.Info("capture: enabled, max=" + options.MaxEntries
                         + ", maxTemplate=" + (options.MaxTemplateEntries < 0 ? "off" : options.MaxTemplateEntries.ToString())
                         + ", flush every " + (int)FlushIntervalSeconds + " s, file=" + _path);
            }
            catch (Exception ex)
            {
                _store = null;
                _path = null;
                _configured = false;
                Log.Warn("capture: setup failed → " + ex.GetType().Name + ": " + ex.Message + "（捕获关闭，翻译不受影响）");
            }
        }

        /// <summary>每帧调一次：到点就落盘（没到点只做一次浮点比较，零分配）。</summary>
        internal static void Tick()
        {
            if (!_configured)
            {
                return;
            }

            if (Time.realtimeSinceStartup < _nextFlushAt)
            {
                return;
            }

            _nextFlushAt = Time.realtimeSinceStartup + FlushIntervalSeconds;
            Flush("interval");
        }

        /// <summary>
        /// 把还没写过的记录追加到文件。<b>绝不抛</b>：失败只 Warn 一次（红线 6），下次重试同一批。
        /// </summary>
        internal static void Flush(string reason)
        {
            CaptureStore store = _store;
            if (!_configured || store == null || string.IsNullOrEmpty(_path))
            {
                return;
            }

            try
            {
                if (store.ConsumeCapNotice())
                {
                    Log.Warn("capture: cap reached (CaptureMaxEntries=" + store.Options.MaxEntries
                             + "), further distinct misses are counted only (overflow=" + store.Overflow + ")");
                }

                int written = store.WritePending(_path);
                if (written > 0)
                {
                    _flushes++;
                    Log.Info("capture: wrote " + written + " line(s) (" + reason + "), " + store.Describe());
                    _writeFailureLogged = false;
                }
                else if (!string.IsNullOrEmpty(store.LastWriteError) && !_writeFailureLogged)
                {
                    _writeFailureLogged = true;
                    Log.Warn("capture: write failed (" + reason + ") → " + store.LastWriteError
                             + "（只记一次；翻译不受影响）");
                }
            }
            catch (Exception ex)
            {
                if (!_writeFailureLogged)
                {
                    _writeFailureLogged = true;
                    Log.Warn("capture: flush failed (" + reason + ") → " + ex.GetType().Name + ": " + ex.Message
                             + "（只记一次；翻译不受影响）");
                }
            }
        }

        /// <summary>退出前收尾：强制落盘一次并把统计打全（幂等，可重复调）。</summary>
        internal static void Shutdown()
        {
            CaptureStore store = _store;
            if (!_configured || store == null || _shutdownDone)
            {
                return;
            }

            _shutdownDone = true;
            Flush("shutdown");
            Log.Info(Describe());

            // task-35：caller 聚合行。栈是在捕获那一刻取的、落盘后无法回溯，所以「caller 修好没有」
            // 只能靠运行时留证据 —— 这一行在退出 dump 里，读 KSP.log 就能判断，不必解 JSONL。
            Log.Info(store.DescribeCallers());
        }

        /// <summary>统计行：<c>capture: distinct=N, written=M</c>（未启用时 <c>capture: disabled</c>）。</summary>
        internal static string Describe()
        {
            CaptureStore store = _store;
            if (store == null)
            {
                return "capture: disabled";
            }

            return store.Describe();
        }

        private static string CaptureDirectory()
        {
            string root = KSPUtil.ApplicationRootPath;
            return Path.Combine(Path.Combine(root, "PluginData"), "UI18n");
        }
    }
}
