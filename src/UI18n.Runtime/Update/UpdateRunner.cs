using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UI18n.Core.Config;

namespace UI18n.Runtime.Update
{
    /// <summary>
    /// 更新子系统的线程与日志泵（**Unity-free**：不引用任何 UnityEngine 类型，方便离线验收直接编译）。
    /// <para>
    /// 分工：<see cref="ConfigureAndStart"/> 起一个后台线程跑 <see cref="UpdateWorkflow.Run"/>（网络 + 解压 +
    /// 写盘全在里面），主线程每帧只调 <see cref="Tick"/> 把排队的日志行吐出去。
    /// <b>启动期不等待它完成</b>，所以加载时间与主菜单到达时间完全不受更新影响。
    /// </para>
    /// <para>
    /// 线程是 <see cref="Thread.IsBackground"/> = true：游戏退出时不需要 join 也不会挂住进程。
    /// </para>
    /// </summary>
    internal static class UpdateRunner
    {
        private static readonly object Gate = new object();
        private static Thread _thread;
        private static UpdateResult _result;
        private static volatile bool _finished;
        private static bool _shutdown;

        private static Action<string, bool> _sink;

        /// <summary>日志输出端（主线程设置，一般是 Log.Info / Log.Warn）。</summary>
        internal static Action<string, bool> Sink
        {
            get { return _sink; }
            set { _sink = value; }
        }

        /// <summary>后台线程是否还在跑。</summary>
        internal static bool IsRunning
        {
            get
            {
                lock (Gate)
                {
                    return _thread != null && !_finished;
                }
            }
        }

        /// <summary>最近一次结果（还没跑完时为 null）。</summary>
        internal static UpdateResult LastResult
        {
            get
            {
                lock (Gate)
                {
                    return _result;
                }
            }
        }

        /// <summary>
        /// 主线程调用：清状态、按配置起后台线程。重复调用是安全的（第二次什么也不做）。
        /// </summary>
        internal static void ConfigureAndStart(UI18nConfig config, string modRoot, string pluginDataRoot, string localVersion, string language)
        {
            ConfigureAndStart(config, modRoot, pluginDataRoot, localVersion, language, null);
        }

        /// <summary>
        /// 同上一个重载，额外带上「玩家在 prefs.json 里关掉的 modId」：
        /// 更新只安装启用的包（<see cref="UpdateWorkflow"/> 里按名单过滤）。
        /// </summary>
        internal static void ConfigureAndStart(
            UI18nConfig config,
            string modRoot,
            string pluginDataRoot,
            string localVersion,
            string language,
            IList<string> disabledModIds)
        {
            if (config == null)
            {
                return;
            }

            lock (Gate)
            {
                if (_thread != null)
                {
                    return;
                }
            }

            UpdateLog.Reset();
            UpdateLog.Sink = Sink;

            UpdateEnvironment environment = new UpdateEnvironment(
                config.UpdateBaseUrl,
                config.UpdateChannel,
                modRoot,
                pluginDataRoot,
                localVersion,
                language,
                config.UpdateTimeoutSeconds,
                config.UpdateCheckDays,
                config.CheckForUpdates,
                config.AutoDownload,
                disabledModIds);

            Thread thread = new Thread(delegate()
            {
                UpdateResult result = null;
                try
                {
                    result = UpdateWorkflow.Run(environment);
                }
                catch (Exception ex)
                {
                    // Run 自己已经兜了异常；这一层是「兜底的兜底」，保证线程一定正常收尾
                    UpdateLog.Warn("update: background thread crashed (" + ex.GetType().Name + ": " + ex.Message + ")");
                    result = null;
                }
                finally
                {
                    lock (Gate)
                    {
                        _result = result;
                        _finished = true;
                    }
                }
            });

            thread.IsBackground = true;
            thread.Name = "UI18n.Update";

            lock (Gate)
            {
                _thread = thread;
                _finished = false;
                _result = null;
            }

            try
            {
                thread.Start();
            }
            catch (Exception ex)
            {
                lock (Gate)
                {
                    _finished = true;
                }

                UpdateLog.Warn("update: could not start the background thread (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        /// <summary>主线程每帧调用：把后台攒的日志行吐出去（没有新行时是空操作）。</summary>
        internal static void Tick()
        {
            if (UpdateLog.PendingCount == 0)
            {
                return;
            }

            UpdateLog.Sink = Sink;
            UpdateLog.Drain();
        }

        /// <summary>退出时调用（幂等）：把剩下的日志吐完，不等待线程结束。</summary>
        internal static void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;

            UpdateLog.Sink = Sink;
            UpdateLog.Drain();

            UpdateResult result;
            bool running;
            lock (Gate)
            {
                result = _result;
                running = _thread != null && !_finished;
            }

            UpdateResult final = result;
            if (final != null)
            {
                UpdateLog.Info("update: final result " + final.Describe());
                UpdateLog.Drain();
            }

            UpdateLog.Sink = Sink;
            UpdateLog.Info("update: shutdown (background thread " + (running ? "still running, left to die with the process" : "finished") + ")");
            UpdateLog.Drain();
        }
    }
}
