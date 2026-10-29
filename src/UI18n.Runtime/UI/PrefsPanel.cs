using System;
using System.Collections.Generic;
using System.IO;
using KSP.UI.Screens;
using UI18n.Core.Config;
using UI18n.Core.Dict;
using UI18n.Core.Settings;
using UI18n.Core.Submission;
using UI18n.Runtime.Settings;
using UnityEngine;

namespace UI18n.Runtime.UI
{
    /// <summary>
    /// 游戏内「按 mod 开关翻译」面板（v1.6 M2，docs/10 §9）。
    /// <para>
    /// 三条硬约束（违反即返工）：
    /// <list type="number">
    /// <item>**只改状态、不热重载字典**：点保存只是写 <c>PluginData/UI18n/prefs.json</c>，
    /// 然后提示「重启游戏生效」。理由：字典在启动期一次性装好、17 个 patch 已按条目数决定装不装，
    /// 运行期换字典就要拆 patch + 重建索引，那是另一套架构，不是本任务的范围。</item>
    /// <item>**只在主线程读写**：所有状态（<see cref="Prefs"/>、行列表、<see cref="PackStats"/>、
    /// <see cref="RenderCoverage"/>）都在 <c>OnGUI</c> / <c>Update</c> 里访问，绝不跨线程。
    /// 后台更新线程只读它自己那份不可变的 <c>UpdateEnvironment</c>。</item>
    /// <item>**绝不抛**（红线 6）：<c>OnGUI</c> 整体兜异常，出错只 Warn 一次并把面板自己关掉，
    /// 绝不因为一个 UI 异常影响游戏主循环。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 为什么不用 <c>MultiOptionDialog</c> 平铺：包有 80+ 个（实测 97 个文件 → 82 个 modId），
    /// 平铺对话框会撑爆屏幕且无法滚动；这里用 <c>GUI.BeginScrollView</c> 自己滚。
    /// </para>
    /// </summary>
    internal sealed class PrefsPanel : MonoBehaviour
    {
        // ---- 版式（px）----
        private const float WindowWidth = 680f;
        private const float WindowHeight = 648f;
        private const float RowHeight = 20f;
        private const float HeaderHeight = 96f;
        private const float FooterHeight = 44f;
        private const float Pad = 8f;

        /// <summary>窗口底部「玩家投稿」区块的高度（4 行：同意 / 长文本 / 按钮 / 明细）。</summary>
        private const float SubmissionBlockHeight = 92f;

        /// <summary>投稿页面（§6.9：零服务器，玩家把 json 贴进 GitHub Issue）。<b>只在玩家点按钮时才打开</b>。</summary>
        private const string SubmissionPageUrl =
            "https://github.com/DiaoDaiaChan/KSP_UniversalI18n/issues/new?template=submit-texts.yml";

        private const string OtherLabel = "其他（无法归属）";
        private const string OtherLabelEn = "(unattributed)";

        /// <summary>KSP 的窗口 id 只要全局唯一即可；这里用固定的 32 位常量，避免 GetHashCode 跨会话变化。</summary>
        private const int WindowId = 0x55493131;

        private static readonly string[] Empty = new string[0];

        private string _language = string.Empty;
        private Prefs _prefs;

        private readonly List<Row> _rows = new List<Row>(128);
        private readonly List<string> _prefixes = new List<string>(128);
        private readonly List<KeyValuePair<string, long>> _callerMiss = new List<KeyValuePair<string, long>>(224);

        private Vector2 _scroll;
        private Rect _windowRect = new Rect(240f, 120f, WindowWidth, WindowHeight);
        private bool _visible;
        private bool _onlyMiss;
        private bool _built;

        private string _status = string.Empty;
        private float _statusUntil;

        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private float _nextAddAttempt;
        private int _addAttempts;
        private bool _addGaveUp;
        private bool _reported;

        private bool _cjkChecked;
        private bool _cjkOk;

        private GUIStyle _right;

        // ---- 每帧字符串的缓存（IMGUI 一帧要跑 Layout / Repaint / 若干事件趟，见 FillRowText / Summary）----
        /// <summary>汇总行的缓存文本；<c>null</c> 表示下次调用要重算。</summary>
        private string _summary;
        private float _summaryAt;

        /// <summary>显示名表已经读过哪个目录（<c>null</c> = 一次都没读过）。</summary>
        private string _displayNamesLoadedFor;

        /// <summary>per-mod 策略表（M5-a）。同一个包目录只读一次；读不到就是 null（= 全是非 NATIVE）。</summary>
        private ModPolicies _policies;

        /// <summary>上面那份策略表是按哪个目录读的（换了语言目录就重读）。</summary>
        private string _policiesLoadedFor;

        /// <summary>「已关闭 N 个」那行的缓存（N 变才重拼）。</summary>
        private int _disabledCountCached = -1;
        private string _disabledLabel = string.Empty;

        // ---- 投稿区块状态（全部只在主线程读写）----
        private string _submissionConsent = SubmissionPolicy.ConsentNone;
        private bool _allowLongSingletons;
        private bool _submissionConfirm;
        private bool _submissionReady;
        private string _submissionPath = string.Empty;
        private string _submissionInfo = string.Empty;

        /// <summary>刚构建出的 bundle（预览或导出都会填）；「上报到 GitHub」用它拼 Issue 正文。</summary>
        private SubmissionBundle _submissionBundle;

        /// <summary>本会话是否已经点过「上报到 GitHub」（只影响状态行文案）。</summary>
        private bool _submissionReported;

        /// <summary>由 <c>UI18nAddon.Run</c> 调用；<b>不装任何 patch</b>，与语言门禁无关。</summary>
        /// <param name="config">
        /// 安装级配置。面板只读两项：<c>SubmissionConsent</c>（投稿默认状态，§6.2）与语言；
        /// 其余开关仍然只由 <c>Config.cfg</c> 决定（改它们要重启，面板不假装能热改）。
        /// </param>
        internal void Configure(UI18nConfig config, string language)
        {
            _language = language ?? string.Empty;
            _prefs = PrefsRuntime.Current;
            _submissionConsent = config != null && config.SubmissionConsent != null
                ? SubmissionPolicy.NormalizeConsent(config.SubmissionConsent)
                : SubmissionPolicy.ConsentNone;
        }

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        private void Update()
        {
            if (_addGaveUp)
            {
                return;
            }

            ApplicationLauncher launcher = ApplicationLauncher.Instance;
            if (launcher == null)
            {
                return;
            }

            // 按钮可能随 launcher / 场景一起被销毁：引用为 null 就重新挂一次（KSP 里这是标准做法）。
            if (_button != null)
            {
                return;
            }

            if (Time.realtimeSinceStartup < _nextAddAttempt)
            {
                return;
            }

            _nextAddAttempt = Time.realtimeSinceStartup + 2f;
            _addAttempts++;
            TryAddButton(launcher);
        }

        private void OnDestroy()
        {
            RemoveButton();

            if (_icon != null)
            {
                Destroy(_icon);
                _icon = null;
            }
        }

        private void TryAddButton(ApplicationLauncher launcher)
        {
            try
            {
                if (_icon == null)
                {
                    _icon = BuildIcon();
                }

                _button = launcher.AddModApplication(
                    OnButtonTrue,
                    OnButtonFalse,
                    OnButtonHover,
                    OnButtonHoverOut,
                    OnButtonEnable,
                    OnButtonDisable,
                    ApplicationLauncher.AppScenes.ALWAYS,
                    _icon);

                if (_button != null)
                {
                    Log.Info("panel: ApplicationLauncher button added (mods=" + _prefs.ModCount + ", disabled=" + _prefs.DisabledCount + ")");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("panel: could not add ApplicationLauncher button (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            // 60 秒还挂不上就放弃，并说清楚原因（自检行之外的唯一线索）。
            if (_addAttempts >= 30)
            {
                _addGaveUp = true;
                Log.Warn("panel: giving up adding the ApplicationLauncher button after " + _addAttempts
                         + " attempts; edit PluginData/UI18n/prefs.json by hand instead");
            }
        }

        private void RemoveButton()
        {
            if (_button == null)
            {
                return;
            }

            try
            {
                ApplicationLauncher launcher = ApplicationLauncher.Instance;
                if (launcher != null)
                {
                    launcher.RemoveModApplication(_button);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("panel: could not remove ApplicationLauncher button (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            _button = null;
        }

        private void OnButtonTrue()
        {
            _visible = true;
            BuildRows();
        }

        private void OnButtonFalse()
        {
            _visible = false;
        }

        // 悬停回调：KSP 不同版本给 ApplicationLauncherButton 设提示文本的 API 不一致
        // （没有稳定的 SetTrueTooltip 公共方法），这里刻意什么都不做 —— 绝不去猜 API 名，
        // 一个猜错就是运行期 MissingMethodException。按钮的可见名字由窗口标题承担。
        private void OnButtonHover()
        {
        }

        private void OnButtonHoverOut()
        {
        }

        private void OnButtonEnable()
        {
        }

        private void OnButtonDisable()
        {
            _visible = false;
        }

        // ------------------------------------------------------------------
        // 数据
        // ------------------------------------------------------------------

        private sealed class Row
        {
            internal string ModId;
            internal string Display;
            internal int Entries;
            internal long Miss;
            internal bool Enabled;

            /// <summary>策略表把它归到 NATIVE 档：游戏自己能显示中文，这一局没有加载它的包（M5-a）。</summary>
            internal bool Native;

            /// <summary>左侧那列已经拼好的文字（建表时算一次，见 <see cref="FillRowText"/>）。</summary>
            internal string Label;

            /// <summary>右侧那列已经拼好的文字（同上）。</summary>
            internal string Counts;
        }

        /// <summary>
        /// 重建行列表：modId 全集 = 磁盘上的包文件 ∪ 本局已装的 modId。
        /// <b>磁盘是权威</b>：被关掉的包这一局根本没读，不可能出现在 <see cref="PackStats"/> 里，
        /// 只按已装集合列行会把「刚关掉的 mod」从面板上弄丢，玩家就再也打不开了。
        /// </summary>
        private void BuildRows()
        {
            _rows.Clear();
            _prefixes.Clear();
            LoadDisplayNames();
            LoadPolicies();

            List<string> ids = new List<string>(128);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] loaded = PackStats.ModIds();
            for (int i = 0; i < loaded.Length; i++)
            {
                if (seen.Add(loaded[i]))
                {
                    ids.Add(loaded[i]);
                }
            }

            string[] onDisk = DiscoverPackModIds();
            for (int i = 0; i < onDisk.Length; i++)
            {
                if (seen.Add(onDisk[i]))
                {
                    ids.Add(onDisk[i]);
                }
            }

            ids.Sort(StringComparer.Ordinal);

            Dictionary<string, long> missByMod = BuildMissByMod(ids);
            long otherMiss;
            missByMod.TryGetValue(OtherLabel, out otherMiss);

            for (int i = 0; i < ids.Count; i++)
            {
                string modId = ids[i];
                Row row = new Row();
                row.ModId = modId;
                row.Display = DescribeMod(modId);
                row.Entries = PackStats.Get(modId);
                long miss;
                missByMod.TryGetValue(modId, out miss);
                row.Miss = miss;
                row.Enabled = _prefs == null || _prefs.IsEnabled(modId);
                row.Native = _policies != null && _policies.IsNative(modId);
                FillRowText(row);
                _rows.Add(row);
            }

            _prefixes.AddRange(ids);
            _prefixes.Sort(delegate(string a, string b)
            {
                int byLength = b.Length.CompareTo(a.Length);
                return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
            });

            if (otherMiss > 0)
            {
                Row other = new Row();
                other.ModId = string.Empty;
                other.Display = T(OtherLabel, OtherLabelEn);
                other.Entries = 0;
                other.Miss = otherMiss;
                other.Enabled = true;
                FillRowText(other);
                _rows.Add(other);
            }

            // 默认按 modId 排序（可查找）；「只看未命中多的」按未命中降序。
            if (_onlyMiss)
            {
                _rows.RemoveAll(delegate(Row r) { return r.Miss <= 0; });
                _rows.Sort(delegate(Row a, Row b)
                {
                    int byMiss = b.Miss.CompareTo(a.Miss);
                    return byMiss != 0 ? byMiss : string.CompareOrdinal(a.ModId, b.ModId);
                });
            }

            _summary = null;                                // 行集合变了，汇总行跟着重算
            _built = true;
        }

        /// <summary>
        /// 把一行里两段要显示的文字<b>在建表时</b>拼好。
        /// <para>
        /// IMGUI 一帧要跑多趟（Layout / Repaint / 每个事件各一趟），在 <see cref="DrawRow"/> 里拼
        /// 就等于每帧分配十几次；而这两段只依赖建表那一刻的快照（<c>row.Entries</c> / <c>row.Miss</c>
        /// 本来就是快照），所以搬到建表时算一次，界面完全等价。
        /// </para>
        /// </summary>
        private void FillRowText(Row row)
        {
            row.Label = row.ModId.Length == 0
                ? row.Display
                : row.Display + "  (" + row.ModId + ")";

            // NATIVE 档的包这一局根本没加载（配置全部为 0），不标一句玩家会以为是漏翻
            if (row.Native)
            {
                row.Label = row.Label + T("  · 原生（游戏自带中文）", "  · native (game localizes it)");
            }

            row.Counts = row.Entries <= 0 && row.Miss <= 0
                ? "—"
                : T("条目 " + row.Entries + " · 未命中 " + row.Miss, "entries " + row.Entries + " · miss " + row.Miss);
        }

        /// <summary>
        /// 把语言包里的显示名表读进 <see cref="PackFilter"/> 的注册表。
        /// <para>
        /// <b>同一个目录只读一次</b>：面板每次重建行列表（刷新统计 / 切「只看未命中」/ 重读 prefs）都会走到这里，
        /// 每趟都 <see cref="PackLocator.Resolve"/> + 读文件是白花的 I/O —— 表内容在进程内也不会变。
        /// 换了语言 → 解析出的目录不同 → 自然会再读一次。
        /// </para>
        /// <para>缺文件 / 坏 JSON 都只是不注册 —— 面板退化成显示 modId，绝不抛。</para>
        /// </summary>
        private void LoadDisplayNames()
        {
            try
            {
                PackResolution packs = PackLocator.Resolve(ConfigLoader.PacksPath, _language);
                if (packs == null || string.IsNullOrEmpty(packs.Directory))
                {
                    return;
                }

                if (string.Equals(_displayNamesLoadedFor, packs.Directory, StringComparison.Ordinal))
                {
                    return;
                }

                _displayNamesLoadedFor = packs.Directory;
                int loaded = PackDisplayNames.LoadFromDirectory(packs.Directory);
                if (loaded > 0)
                {
                    Log.Info("panel: display names loaded=" + loaded + " (" + PackDisplayNames.FileName + ")");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("panel: could not load display names (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        /// <summary>
        /// 读语言包目录里的 per-mod 策略表（<c>policies.json</c>，M5-a），用于在行上标注「原生」——
        /// 那一档的包<b>这一局根本没被加载</b>（<see cref="ConfigLoader"/> 在加载期就滤掉了），
        /// 面板不标一句，玩家会以为是我们漏翻。
        /// <para>同一个目录只读一次；缺文件 / 坏 JSON 都只是「全是非 NATIVE」，绝不抛。</para>
        /// </summary>
        private void LoadPolicies()
        {
            try
            {
                PackResolution packs = PackLocator.Resolve(ConfigLoader.PacksPath, _language);
                if (packs == null || string.IsNullOrEmpty(packs.Directory))
                {
                    return;
                }

                if (string.Equals(_policiesLoadedFor, packs.Directory, StringComparison.Ordinal))
                {
                    return;
                }

                _policiesLoadedFor = packs.Directory;
                string error;
                ModPolicies policies = ModPolicies.LoadFromDirectory(packs.Directory, out error);
                _policies = policies;
                if (policies.Count > 0)
                {
                    Log.Info("panel: " + policies.Describe() + " (" + ModPolicies.FileName + ")");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("panel: could not load policies (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        /// <summary>
        /// 一行 mod 的显示名：数据表（<see cref="PackFilter.DisplayName"/>）优先；数据里没登记时，
        /// 本产品自造的伪 mod 用界面文案（**文案属于 UI 层，Core 里不留界面文字**），其余原样回 modId。
        /// </summary>
        private string DescribeMod(string modId)
        {
            string name = PackFilter.DisplayName(modId);
            if (!string.Equals(name, modId, StringComparison.Ordinal))
            {
                return name;
            }

            if (!PackFilter.IsInternalPack(modId))
            {
                return name;
            }

            if (modId.EndsWith("-Unattributed", StringComparison.OrdinalIgnoreCase))
            {
                return T("未归属文本", "unattributed text");
            }

            if (modId.EndsWith("-ConfigCache", StringComparison.OrdinalIgnoreCase))
            {
                return T("配置缓存", "config cache");
            }

            return name;
        }

        private string[] DiscoverPackModIds()
        {
            try
            {
                PackResolution packs = PackLocator.Resolve(ConfigLoader.PacksPath, _language);
                if (packs == null || packs.Files == null || packs.Files.Length == 0)
                {
                    return Empty;
                }

                return PackFilter.DistinctModIds(packs.Files);
            }
            catch (Exception ex)
            {
                Log.Warn("panel: could not list pack files (" + ex.GetType().Name + ": " + ex.Message + "), showing loaded mods only");
                return Empty;
            }
        }

        /// <summary>
        /// caller → modId 归属：<see cref="RenderCoverage"/> 的 byCaller 里全是
        /// <c>类型全名.方法名</c>（如 <c>MuMech.MechJebModule…​.Update</c>），只能按 **最长 modId 前缀** 归属。
        /// <para>
        /// 已知局限（面板上如实显示为「其他」而不是硬凑一个 mod）：modId 是**程序集名**，而 caller 是**命名空间**，
        /// 两者对不上时（如 MechJeb2 的命名空间是 MuMech）就归属不了。要精确归属得在捕获时记下调用方的程序集，
        /// 那会动捕获热路径，v1.6 不做。
        /// </para>
        /// </summary>
        private Dictionary<string, long> BuildMissByMod(List<string> ids)
        {
            Dictionary<string, long> map = new Dictionary<string, long>(ids.Count + 1, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ids.Count; i++)
            {
                map[ids[i]] = 0;
            }

            _callerMiss.Clear();
            RenderCoverage.CopyCallerMiss(_callerMiss);

            for (int i = 0; i < _callerMiss.Count; i++)
            {
                string caller = _callerMiss[i].Key;
                long miss = _callerMiss[i].Value;
                if (miss <= 0 || string.IsNullOrEmpty(caller))
                {
                    continue;
                }

                string owner = Attribute(caller);
                long previous;
                map.TryGetValue(owner, out previous);
                map[owner] = previous + miss;
            }

            return map;
        }

        private string Attribute(string caller)
        {
            for (int i = 0; i < _prefixes.Count; i++)
            {
                string modId = _prefixes[i];
                if (caller.Length > modId.Length
                    && caller.StartsWith(modId, StringComparison.OrdinalIgnoreCase)
                    && caller[modId.Length] == '.')
                {
                    return modId;
                }
            }

            return OtherLabel;
        }

        // ------------------------------------------------------------------
        // IMGUI
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            if (!_visible || _prefs == null)
            {
                return;
            }

            try
            {
                _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, T("UI18n 翻译开关（按 mod）", "UI18n translation switch (per mod)"));
            }
            catch (Exception ex)
            {
                // 红线 6：UI 出错绝不影响游戏主循环，只记一次并把面板关掉。
                _visible = false;
                if (!_reported)
                {
                    _reported = true;
                    Log.Warn("panel: OnGUI failed (" + ex.GetType().Name + ": " + ex.Message + "), panel disabled for this session");
                }
            }
        }

        private void DrawWindow(int id)
        {
            if (!_built || _rows.Count == 0)
            {
                BuildRows();
            }

            float width = _windowRect.width - Pad * 2f;
            float y = 24f;

            // 汇总行
            GUI.Label(new Rect(Pad, y, width, 20f), Summary());
            y += 22f;
            GUI.Label(new Rect(Pad, y, width, 18f), T(
                "改动写入 PluginData/UI18n/prefs.json，重启游戏后生效（本局不热重载字典）",
                "Changes are written to PluginData/UI18n/prefs.json and take effect after a restart"));
            y += 24f;

            // 顶部按钮行
            float buttonWidth = (width - 3 * 6f) / 4f;
            if (GUI.Button(new Rect(Pad, y, buttonWidth, 22f), T("全开", "All on")))
            {
                SetAll(true);
            }

            if (GUI.Button(new Rect(Pad + buttonWidth + 6f, y, buttonWidth, 22f), T("全关", "All off")))
            {
                SetAll(false);
            }

            bool onlyMiss = GUI.Toggle(new Rect(Pad + (buttonWidth + 6f) * 2f, y, buttonWidth, 22f), _onlyMiss,
                                       T("只看未命中多的", "Only with misses"), GUI.skin.button);
            if (onlyMiss != _onlyMiss)
            {
                // 过滤是在 BuildRows 里做的（会真的把行从 _rows 里删掉），所以必须重建，
                // 而且**不能在这里重建** —— 本趟已经开始画控件了，中途换掉行数会让 IMGUI 的控件序列错位。
                // 标记「待重建」，下一趟 OnGUI 开头重建，玩家在这一帧内就能看到结果。
                _onlyMiss = onlyMiss;
                _built = false;
            }

            if (GUI.Button(new Rect(Pad + (buttonWidth + 6f) * 3f, y, buttonWidth, 22f), T("刷新统计", "Refresh")))
            {
                BuildRows();
            }

            y += 28f;

            // 列表：只画可视行，但控件要按顺序全画（跳过控件会让 IMGUI 的控件 id 在两趟之间错位）。
            Rect listRect = new Rect(Pad, y, width, _windowRect.height - y - FooterHeight - SubmissionBlockHeight);
            float contentHeight = Mathf.Max(listRect.height, _rows.Count * RowHeight + 4f);
            _scroll = GUI.BeginScrollView(listRect, _scroll, new Rect(0f, 0f, listRect.width - 20f, contentHeight));

            float firstVisible = Mathf.Floor(_scroll.y / RowHeight);
            float lastVisible = firstVisible + (listRect.height / RowHeight) + 1f;

            for (int i = 0; i < _rows.Count; i++)
            {
                if (i < firstVisible - 1f || i > lastVisible + 1f)
                {
                    continue;
                }

                DrawRow(_rows[i], i * RowHeight, listRect.width - 24f);
            }

            GUI.EndScrollView();

            // 底部「玩家投稿」区块（v1.6 M3，docs/10 §6）：与 mod 开关互不影响，勾选框本身就是同意书。
            DrawSubmissionBlock(_windowRect.height - FooterHeight - SubmissionBlockHeight + 2f, width);

            // 底部：保存 + 状态
            float footerY = _windowRect.height - FooterHeight + 6f;
            if (GUI.Button(new Rect(Pad, footerY, 220f, 24f), T("保存并提示重启", "Save (restart required)")))
            {
                SavePrefs();
            }

            if (GUI.Button(new Rect(Pad + 226f, footerY, 120f, 24f), T("重新读取", "Reload")))
            {
                PrefsRuntime.Load();
                _prefs = PrefsRuntime.Current;
                BuildRows();
                SetStatus(T("已重新读取 prefs.json", "prefs.json reloaded"));
            }

            if (_status.Length > 0 && Time.realtimeSinceStartup < _statusUntil)
            {
                GUI.Label(new Rect(Pad + 356f, footerY + 4f, _windowRect.width - Pad * 2f - 356f, 20f), _status);
            }
            else if (_prefs.DisabledCount > 0)
            {
                if (_disabledCountCached != _prefs.DisabledCount)
                {
                    // 只有数字变才重拼（这行原先每趟 OnGUI 都拼两串）。
                    _disabledCountCached = _prefs.DisabledCount;
                    _disabledLabel = T("已关闭 " + _prefs.DisabledCount + " 个，重启后生效",
                                       _prefs.DisabledCount + " disabled, effective after restart");
                }

                GUI.Label(new Rect(Pad + 356f, footerY + 4f, _windowRect.width - Pad * 2f - 356f, 20f), _disabledLabel);
            }

            GUI.DragWindow(new Rect(0f, 0f, _windowRect.width, 22f));
        }

        private void DrawRow(Row row, float y, float width)
        {
            bool enabled = GUI.Toggle(new Rect(4f, y, 16f, 18f), row.Enabled, GUIContent.none);
            if (enabled != row.Enabled)
            {
                row.Enabled = enabled;
                if (row.ModId.Length > 0)
                {
                    _prefs.SetEnabled(row.ModId, enabled);
                }
            }

            GUI.Label(new Rect(26f, y, width - 26f - 190f, 18f), row.Label);
            GUI.Label(new Rect(width - 188f, y, 184f, 18f), row.Counts, RightStyle());
        }

        private void SetAll(bool enabled)
        {
            List<string> ids = new List<string>(_rows.Count);
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].ModId.Length > 0)
                {
                    ids.Add(_rows[i].ModId);
                }
            }

            _prefs.SetAll(ids, enabled);

            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].Enabled = _rows[i].ModId.Length == 0 ? true : enabled;
            }

            SetStatus(enabled ? T("已全部打开（未保存）", "all enabled (not saved)") : T("已全部关闭（未保存）", "all disabled (not saved)"));
        }

        // ------------------------------------------------------------------
        // 玩家投稿（v1.6 M3，docs/10 §6）
        // ------------------------------------------------------------------

        /// <summary>
        /// 四行：①同意勾选框 + 当前状态 ②「允许单次长文本」 ③导出 / 打开文件夹 / 打开投稿页面 ④明细。
        /// <para>
        /// <b>绝不联网</b>：导出只是把 json 写进 <c>PluginData/UI18n/submissions/</c>；
        /// 「打开投稿页面」也只是把 URL 交给浏览器（玩家自己决定贴不贴）。
        /// </para>
        /// </summary>
        private void DrawSubmissionBlock(float y, float width)
        {
            SubmissionPrefs submission = _prefs != null ? _prefs.Submission : null;
            bool granted = submission != null && submission.Granted;

            bool next = GUI.Toggle(new Rect(Pad, y, width - 208f, 20f), granted,
                                   T("同意投稿：把没翻出来的英文原文打包给作者（不发存档、不联网）",
                                     "Allow submission: pack untranslated source strings for the author"));
            if (next != granted)
            {
                ApplySubmissionConsent(next);
            }

            GUI.Label(new Rect(Pad + width - 204f, y, 204f, 20f), SubmissionStatusText(), RightStyle());

            _allowLongSingletons = GUI.Toggle(new Rect(Pad, y + 22f, width, 20f), _allowLongSingletons,
                                              T("允许包含只出现一次的長文本（>120 字符，默认不含）",
                                                "Include long text seen only once (>120 chars)"));

            if (_submissionConfirm)
            {
                if (GUI.Button(new Rect(Pad, y + 44f, 112f, 22f), T("确认导出", "Confirm")))
                {
                    ExportSubmission();
                }

                if (GUI.Button(new Rect(Pad + 118f, y + 44f, 52f, 22f), T("取消", "No")))
                {
                    _submissionConfirm = false;
                    SetStatus(T("已取消导出", "export cancelled"));
                }
            }
            else if (GUI.Button(new Rect(Pad, y + 44f, 170f, 22f), T("导出投稿包…", "Export bundle...")))
            {
                PreviewSubmission();
            }

            if (GUI.Button(new Rect(Pad + 176f, y + 44f, 110f, 22f), T("打开文件夹", "Open folder")))
            {
                OpenSubmissionsFolder();
            }

            if (GUI.Button(new Rect(Pad + 292f, y + 44f, 150f, 22f), T("打开投稿页面", "Open submit page")))
            {
                OpenSubmissionPage();
            }

            // M4-b：把已导出的 bundle 变成一条预填 Issue。没导出过就先禁用，免得玩家以为已经提交了。
            bool reportable = _submissionBundle != null;
            bool previousEnabled = GUI.enabled;
            GUI.enabled = reportable;
            if (GUI.Button(new Rect(Pad + 458f, y + 44f, 150f, 22f), T("上报到 GitHub", "Report on GitHub")))
            {
                ReportSubmission();
            }

            GUI.enabled = previousEnabled;

            if (_submissionInfo.Length > 0)
            {
                GUI.Label(new Rect(Pad, y + 68f, width, 18f), _submissionInfo);
            }
        }

        /// <summary>右上角一句话状态（不占明细行）。</summary>
        private string SubmissionStatusText()
        {
            if (_prefs == null || _prefs.Submission == null || !_prefs.Submission.Granted)
            {
                return T("未同意 · 默认不投", "off (default)");
            }

            if (_submissionReported)
            {
                return T("已打开上报页", "report page opened");
            }

            if (_submissionPath.Length > 0)
            {
                return T("已导出", "exported");
            }

            return _submissionReady ? T("已备好，待确认", "ready, not exported") : T("已同意", "allowed");
        }

        /// <summary>
        /// 勾选 / 取消同意。§6.2：撤回 = 把这个值改回 false 并删掉 submissions 目录里的文件（玩家自己删，面板不删）。
        /// 写 prefs.json 后<b>不需要重启</b>（与 mod 开关不同：投稿不影响本局字典）。
        /// </summary>
        private void ApplySubmissionConsent(bool granted)
        {
            if (_prefs == null)
            {
                return;
            }

            SubmissionPrefs submission = _prefs.Submission;
            submission.Granted = granted;
            submission.PolicyVersion = SubmissionPolicy.PolicyVersion;

            if (granted)
            {
                submission.GrantedAt = SubmissionBuilder.FormatNow();
                submission.InstallId = InstallId.Ensure(submission.InstallId);
            }

            _submissionConfirm = false;
            _submissionReady = false;
            _submissionReported = false;

            string error;
            if (PrefsRuntime.Save(out error))
            {
                SetStatus(granted ? T("已同意投稿（随时可取消）", "submission allowed")
                                  : T("已撤回投稿同意", "submission revoked"));
                Log.Info("panel: submission consent=" + (granted ? "granted" : "revoked")
                         + " -> " + PrefsRuntime.FilePath);
            }
            else
            {
                SetStatus(T("保存失败：" + error, "save failed: " + error));
                Log.Warn("panel: submission consent save failed (" + error + ")");
            }
        }

        /// <summary>只算不写：先把「N 个 mod / M 条原文」摆给玩家看，点了确认才真的落盘。</summary>
        private void PreviewSubmission()
        {
            SubmissionExportResult result = SubmissionExporter.Preview(_submissionConsent, _language, _allowLongSingletons);
            if (!result.Ok)
            {
                SetSubmissionFailure(result);
                return;
            }

            _submissionConfirm = true;
            _submissionReady = true;
            _submissionPath = string.Empty;
            _submissionBundle = result.Bundle;
            _submissionReported = false;
            _submissionInfo = T(
                "将导出 " + result.Mods + " 个 mod / " + result.Entries + " 条原文；丢弃 " + result.Dropped
                + " 条（路径 " + result.DroppedPath + "、私有 " + result.DroppedPrivate + "、单次长文本 " + result.DroppedSingleton + "）",
                "Will export " + result.Mods + " mod(s) / " + result.Entries + " string(s); dropped " + result.Dropped);
            SetStatus(T("请确认导出内容", "confirm export"));
        }

        /// <summary>确认后落盘（文件永远在 <c>PluginData/UI18n/submissions/</c>，之后由玩家自己贴到 Issue）。</summary>
        private void ExportSubmission()
        {
            SubmissionExportResult result = SubmissionExporter.Export(_submissionConsent, _language, _allowLongSingletons);
            _submissionConfirm = false;

            if (!result.Ok)
            {
                SetSubmissionFailure(result);
                return;
            }

            _submissionReady = false;
            _submissionPath = result.FilePath;
            _submissionBundle = result.Bundle;
            _submissionInfo = T(
                "已导出 " + result.Entries + " 条原文（" + result.Mods + " 个 mod）到 PluginData/UI18n/submissions/，点「打开文件夹」取出后贴到投稿页面",
                "Exported " + result.Entries + " string(s) (" + result.Mods + " mods) to PluginData/UI18n/submissions/");

            if (_prefs != null && _prefs.Submission != null)
            {
                _prefs.Submission.LastSubmittedAt = SubmissionBuilder.FormatNow();
                _prefs.Submission.InstallId = InstallId.Ensure(_prefs.Submission.InstallId);

                string error;
                if (!PrefsRuntime.Save(out error))
                {
                    Log.Warn("panel: prefs save failed after submission (" + error + ")");
                }
            }

            SetStatus(T("已导出投稿包", "bundle exported"));
            Log.Info("panel: submission exported (id=" + result.Id + ", entries=" + result.Entries
                     + ", mods=" + result.Mods + ", dropped=" + result.Dropped + ")");
        }

        /// <summary>把导出器的 outcome 翻成一句人话；失败不弹窗、只写状态行（红线 6）。</summary>
        private void SetSubmissionFailure(SubmissionExportResult result)
        {
            _submissionConfirm = false;
            _submissionReady = false;
            _submissionBundle = null;

            string message;
            if (string.Equals(result.Outcome, SubmissionExportResult.OutcomeNotConsented, StringComparison.Ordinal))
            {
                message = T("先勾选上面的「同意投稿」", "tick the consent box first");
            }
            else if (string.Equals(result.Outcome, SubmissionExportResult.OutcomeNoSource, StringComparison.Ordinal))
            {
                message = T("本会话没有捕获文件（需要 CaptureMisses=True，并先在游戏里玩一会儿）",
                            "no capture file this session (need CaptureMisses=True and some play time)");
            }
            else if (string.Equals(result.Outcome, SubmissionExportResult.OutcomeNoEntries, StringComparison.Ordinal))
            {
                message = T("没有可投的原文（都被脱敏规则筛掉了）", "nothing to submit (all filtered by redaction)");
            }
            else
            {
                message = T("导出失败：" + result.Error, "export failed: " + result.Error);
            }

            _submissionInfo = message;
            SetStatus(message);
        }

        /// <summary>打开 <c>PluginData/UI18n/submissions/</c>（没有就建一个，免得浏览器报「找不到」）。</summary>
        private void OpenSubmissionsFolder()
        {
            try
            {
                string directory = SubmissionExporter.DirectoryPath;
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Application.OpenURL("file:///" + directory.Replace('\\', '/'));
                SetStatus(T("已打开投稿文件夹", "folder opened"));
            }
            catch (Exception ex)
            {
                SetStatus(T("打不开文件夹：" + ex.Message, "cannot open folder: " + ex.Message));
                Log.Warn("panel: open submissions folder failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        /// <summary>把投稿页面的 URL 交给系统浏览器。<b>不发起任何请求</b>（§6.9 零服务器）。</summary>
        private void OpenSubmissionPage()
        {
            try
            {
                Application.OpenURL(SubmissionPageUrl);
                SetStatus(T("已在浏览器打开投稿页面", "submit page opened in browser"));
            }
            catch (Exception ex)
            {
                SetStatus(T("打不开浏览器：" + ex.Message, "cannot open browser: " + ex.Message));
                Log.Warn("panel: open submission page failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        /// <summary>
        /// M4-b：把刚导出的 bundle 变成一条预填好的 GitHub Issue。
        /// <para>
        /// 只打开浏览器（<b>零服务器、零请求</b>）；bundle 文件已经在文件夹里，玩家把它拖进表单的
        /// <c>bundle</c> 字段即可 —— 接收侧脚本只认 user-attachments 链接，所以正文里绝不放 JSON。
        /// </para>
        /// </summary>
        private void ReportSubmission()
        {
            if (_submissionBundle == null)
            {
                _submissionInfo = T("请先导出投稿包，再点上报", "export the bundle first, then report");
                SetStatus(_submissionInfo);
                return;
            }

            SubmissionReport report = SubmissionIssue.Build(_submissionBundle);
            if (!report.CanReport)
            {
                _submissionInfo = T("这次没有可上报的内容（" + report.Reason + "）",
                                    "nothing to report (" + report.Reason + ")");
                SetStatus(_submissionInfo);
                return;
            }

            if (!SubmissionIssue.Open(report))
            {
                _submissionInfo = T("打不开浏览器，请打开投稿页面手动提交", "cannot open browser, submit manually");
                SetStatus(_submissionInfo);
                return;
            }

            _submissionReported = true;
            _submissionInfo = report.NeedsFileFallback
                ? T("已在浏览器打开上报页；正文太长塞不进 URL，请把 " + report.FileName + " 拖进表单的 bundle 字段",
                    "opened the issue page; body too long for the URL, drag " + report.FileName + " into the bundle field")
                : T("已在浏览器打开上报页；请把 " + report.FileName + " 拖进表单的 bundle 字段",
                    "opened the issue page; drag " + report.FileName + " into the bundle field");
            SetStatus(T("已备好上报页", "issue page ready"));

            if (report.Truncated)
            {
                Log.Info("submission: report trimmed to " + report.EntryCount + "/" + report.TotalEntries
                         + " (" + report.Remaining + " left for the next one)");
            }
        }

        private void SavePrefs()
        {
            string error;
            if (PrefsRuntime.Save(out error))
            {
                SetStatus(T("已保存，重启游戏生效", "saved, restart required"));
                Log.Info("panel: prefs saved (" + _prefs.ModCount + " mods, " + _prefs.DisabledCount + " disabled) -> " + PrefsRuntime.FilePath);
            }
            else
            {
                SetStatus(T("保存失败：" + error, "save failed: " + error));
                Log.Warn("panel: prefs save failed (" + error + ")");
            }
        }

        private void SetStatus(string text)
        {
            _status = text ?? string.Empty;
            _statusUntil = Time.realtimeSinceStartup + 6f;
        }

        /// <summary>汇总行最短刷新间隔（秒）。这些是调试读数，没必要每帧重拼。</summary>
        private const float SummaryRefreshSeconds = 0.5f;

        private string Summary()
        {
            float now = Time.realtimeSinceStartup;
            if (_summary != null && now - _summaryAt < SummaryRefreshSeconds)
            {
                return _summary;
            }

            long miss = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                miss += _rows[i].Miss;
            }

            _summary = T(
                "mods=" + _prefs.ModCount + "（关闭 " + _prefs.DisabledCount + "） · 本局已装条目=" + PackStats.TotalEntries
                + " · 未命中=" + miss + " · render ratio=" + RenderCoverage.Ratio,
                "mods=" + _prefs.ModCount + " (disabled " + _prefs.DisabledCount + ") · loaded entries=" + PackStats.TotalEntries
                + " · miss=" + miss + " · render ratio=" + RenderCoverage.Ratio);
            _summaryAt = now;
            return _summary;
        }

        private GUIStyle RightStyle()
        {
            if (_right == null)
            {
                _right = new GUIStyle(GUI.skin.label);
                _right.alignment = TextAnchor.MiddleRight;
            }

            return _right;
        }

        /// <summary>
        /// 中英双模：IMGUI 用的是 <c>GUI.skin.font</c>（KSP 自带位图字体），它**不一定**有汉字字形。
        /// 没有就整块退回英文 —— 显示成方块比显示英文更糟（玩家看不出点的是哪个 mod）。
        /// 判据只在第一次需要时测一次，之后走缓存。
        /// </summary>
        private string T(string chinese, string english)
        {
            return CjkAvailable() ? chinese : english;
        }

        private bool CjkAvailable()
        {
            if (_cjkChecked)
            {
                return _cjkOk;
            }

            _cjkChecked = true;
            try
            {
                Font font = GUI.skin != null ? GUI.skin.font : null;
                _cjkOk = font != null && font.HasCharacter('\u4E2D');
            }
            catch (Exception)
            {
                _cjkOk = false;
            }

            return _cjkOk;
        }

        /// <summary>
        /// launcher 按钮的图标：16×16 纯色。**必须有贴图** —— 传 null 会在 Setup 里留空图标（部分 KSP 版本直接 NRE）。
        /// 不加载任何游戏资源（红线 3：不碰游戏状态），自己生成一张。
        /// </summary>
        private static Texture2D BuildIcon()
        {
            Texture2D texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            texture.name = "UI18n.launcher";
            Color32 fill = new Color32(0x2F, 0x6F, 0xB0, 0xFF);

            Color32[] pixels = new Color32[16 * 16];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = fill;
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }
    }
}
