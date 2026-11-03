using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    /// 游戏内「按 mod 开关翻译与诊断面板」（v1.6 M2-M6，docs/10 §9）。
    /// <para>
    /// 三条硬约束（红线）：
    /// <list type="number">
    /// <item>**只改状态、不热重载字典**：点保存只是写 <c>PluginData/UI18n/prefs.json</c>，
    /// 然后提示「重启游戏生效」。理由：字典在启动期一次性装好、17 个 patch 已按条目数决定装不装，
    /// 运行期换字典就要拆 patch + 重建索引，那是另一套架构，不是本任务的范围。</item>
    /// <item>**只在主线程读写**：所有状态（<see cref="Prefs"/>、行列表、<see cref="PackStats"/>、
    /// <see cref="RenderCoverage"/>）都在 <c>OnGUI</c> / <c>Update</c> 里访问，绝不跨线程。</item>
    /// <item>**绝不抛**（红线 6）：<c>OnGUI</c> 整体兜异常，出错只 Warn 一次并把面板自己关掉，
    /// 绝不因为一个 UI 异常影响游戏主循环。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed class PrefsPanel : MonoBehaviour
    {
        // ---- 版式（px）----
        private const float WindowWidth = 720f;
        private const float WindowHeight = 650f;
        private const float RowHeight = 22f;
        private const float FooterHeight = 44f;
        private const float Pad = 10f;

        private const string OtherLabel = "其他（无法归属）";
        private const string OtherLabelEn = "(unattributed)";
        private const string SubmissionPageUrl =
            "https://github.com/DiaoDaiaChan/KSP_UniversalI18n/issues/new?template=submit-texts.yml";
        private const string ProjectRepoUrl =
            "https://github.com/DiaoDaiaChan/KSP_UniversalI18n";

        /// <summary>KSP 的窗口 id 只要全局唯一即可；这里用固定的 32 位常量，避免 GetHashCode 跨会话变化。</summary>
        private const int WindowId = 0x55493131;

        private static readonly string[] Empty = new string[0];
        private static readonly string[] TabNamesZh = { "模组翻译开关", "运行统计与诊断", "词条投稿", "关于与设置" };
        private static readonly string[] TabNamesEn = { "Mod Translations", "Stats & Diagnostics", "Submissions", "About" };
        private static readonly string[] FilterModesZh = { "全部", "已启用", "已关闭", "有未命中", "原生" };
        private static readonly string[] FilterModesEn = { "All", "Active", "Disabled", "Misses", "Native" };

        private string _language = string.Empty;
        private Prefs _prefs;

        private readonly List<Row> _rows = new List<Row>(128);
        private readonly List<Row> _filteredRows = new List<Row>(128);
        private readonly List<string> _prefixes = new List<string>(128);
        private readonly List<KeyValuePair<string, long>> _callerMiss = new List<KeyValuePair<string, long>>(224);

        private Vector2 _scroll;
        private Rect _windowRect = new Rect(240f, 100f, WindowWidth, WindowHeight);
        private bool _visible;
        private bool _built;
        private bool _filterDirty = true;
        private int _selectedTab;
        private int _filterMode;
        private string _searchFilter = string.Empty;

        private string _status = string.Empty;
        private float _statusUntil;

        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private bool _iconCreatedLocally;
        private float _nextAddAttempt;
        private int _addAttempts;
        private bool _addGaveUp;
        private bool _reported;

        private bool _cjkChecked;
        private bool _cjkOk;

        // ---- IMGUI 样式缓存 ----
        private GUISkin _lastSkin;
        private GUIStyle _right;
        private GUIStyle _boldLabelStyle;
        private GUIStyle _badgeStyle;
        private GUIStyle _cardBoxStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _subTitleStyle;
        private GUIStyle _statusStyle;

        // ---- 每帧字符串的缓存 ----
        private string _displayNamesLoadedFor;
        private ModPolicies _policies;
        private string _policiesLoadedFor;
        private int _disabledCountCached = -1;
        private string _disabledLabel = string.Empty;

        // ---- 投稿区块状态 ----
        private string _submissionConsent = SubmissionPolicy.ConsentNone;
        private bool _allowLongSingletons;
        private bool _submissionConfirm;
        private bool _submissionReady;
        private string _submissionPath = string.Empty;
        private string _submissionInfo = string.Empty;
        private SubmissionBundle _submissionBundle;
        private bool _submissionReported;

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
                if (_iconCreatedLocally)
                {
                    Destroy(_icon);
                }
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

        private void OnButtonHover() { }
        private void OnButtonHoverOut() { }
        private void OnButtonEnable() { }
        private void OnButtonDisable() { _visible = false; }

        // ------------------------------------------------------------------
        // 数据模型
        // ------------------------------------------------------------------

        private sealed class Row
        {
            internal string ModId;
            internal string Display;
            internal int Entries;
            internal long Miss;
            internal bool Enabled;
            internal bool Native;
            internal string Label;
            internal string Counts;
        }

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

            _built = true;
            _filterDirty = true;
        }

        private void FillRowText(Row row)
        {
            row.Label = row.ModId.Length == 0
                ? row.Display
                : row.Display + "  (" + row.ModId + ")";

            if (row.Native)
            {
                row.Label += T("  [原生]", "  [Native]");
            }

            if (row.Entries <= 0 && row.Miss <= 0)
            {
                row.Counts = "—";
            }
            else
            {
                string entriesPart = row.Entries > 0 ? (row.Entries + T(" 词条", " entries")) : string.Empty;
                string missPart = row.Miss > 0 ? (T("未命中 ", "miss ") + row.Miss) : string.Empty;

                if (entriesPart.Length > 0 && missPart.Length > 0)
                {
                    row.Counts = entriesPart + " · " + missPart;
                }
                else if (entriesPart.Length > 0)
                {
                    row.Counts = entriesPart;
                }
                else
                {
                    row.Counts = missPart;
                }
            }
        }

        private void UpdateFilteredRows()
        {
            _filterDirty = false;
            _filteredRows.Clear();

            string search = (_searchFilter ?? string.Empty).Trim();
            bool hasSearch = search.Length > 0;

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                if (_filterMode == 1 && !row.Enabled) continue;
                if (_filterMode == 2 && row.Enabled) continue;
                if (_filterMode == 3 && row.Miss <= 0) continue;
                if (_filterMode == 4 && !row.Native) continue;

                if (hasSearch)
                {
                    if (row.ModId.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                        row.Display.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                }

                _filteredRows.Add(row);
            }
        }

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
        // IMGUI 渲染
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            if (!_visible || _prefs == null)
            {
                return;
            }

            try
            {
                if (Event.current != null && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                {
                    _visible = false;
                    Event.current.Use();
                    return;
                }

                GUISkin prevSkin = GUI.skin;
                if (HighLogic.Skin != null)
                {
                    GUI.skin = HighLogic.Skin;
                }

                if (Screen.width > 0 && Screen.height > 0)
                {
                    _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Mathf.Max(0f, Screen.width - _windowRect.width));
                    _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, Screen.height - 40f));
                }

                string windowTitle = T("Universal I18n · 宇宙汉化 (v1.6)", "Universal I18n (v1.6)");
                _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, windowTitle);

                GUI.skin = prevSkin;
            }
            catch (Exception ex)
            {
                _visible = false;
                if (!_reported)
                {
                    _reported = true;
                    Log.Warn("panel: OnGUI failed (" + ex.GetType().Name + ": " + ex.Message + "), panel disabled for this session");
                }
            }
        }

        private void EnsureStyles()
        {
            if (_lastSkin != GUI.skin)
            {
                _lastSkin = GUI.skin;
                _right = null;
                _boldLabelStyle = null;
                _badgeStyle = null;
                _cardBoxStyle = null;
                _titleStyle = null;
                _subTitleStyle = null;
                _statusStyle = null;
            }

            if (_right == null)
            {
                _right = new GUIStyle(GUI.skin.label);
                _right.alignment = TextAnchor.MiddleRight;
                _right.richText = true;
            }

            if (_boldLabelStyle == null)
            {
                _boldLabelStyle = new GUIStyle(GUI.skin.label);
                _boldLabelStyle.fontStyle = FontStyle.Bold;
                _boldLabelStyle.richText = true;
                _boldLabelStyle.alignment = TextAnchor.MiddleLeft;
            }

            if (_badgeStyle == null)
            {
                _badgeStyle = new GUIStyle(GUI.skin.label);
                _badgeStyle.alignment = TextAnchor.MiddleRight;
                _badgeStyle.richText = true;
            }

            if (_cardBoxStyle == null)
            {
                _cardBoxStyle = new GUIStyle(GUI.skin.box);
                _cardBoxStyle.padding = new RectOffset(10, 10, 10, 10);
            }

            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label);
                _titleStyle.fontSize = 18;
                _titleStyle.fontStyle = FontStyle.Bold;
                _titleStyle.richText = true;
            }

            if (_subTitleStyle == null)
            {
                _subTitleStyle = new GUIStyle(GUI.skin.label);
                _subTitleStyle.fontSize = 12;
                _subTitleStyle.fontStyle = FontStyle.Normal;
                _subTitleStyle.richText = true;
            }

            if (_statusStyle == null)
            {
                _statusStyle = new GUIStyle(GUI.skin.label);
                _statusStyle.richText = true;
                _statusStyle.alignment = TextAnchor.MiddleLeft;
            }
        }

        private void DrawWindow(int id)
        {
            if (HighLogic.Skin != null)
            {
                GUI.skin = HighLogic.Skin;
            }

            EnsureStyles();

            if (!_built || _rows.Count == 0)
            {
                BuildRows();
            }

            if (_filterDirty)
            {
                UpdateFilteredRows();
            }

            float width = _windowRect.width - Pad * 2f;
            float y = 24f;

            // 窗口右上角快速关闭按钮
            if (GUI.Button(new Rect(_windowRect.width - 26f, 3f, 20f, 18f), "✕"))
            {
                _visible = false;
                return;
            }

            // 顶部导航栏 (Tabs)
            string[] tabNames = CjkAvailable() ? TabNamesZh : TabNamesEn;
            int newTab = GUI.Toolbar(new Rect(Pad, y, width, 26f), _selectedTab, tabNames);
            if (newTab != _selectedTab)
            {
                _selectedTab = newTab;
            }

            y += 32f;
            float footerY = _windowRect.height - FooterHeight;

            switch (_selectedTab)
            {
                case 0:
                    DrawModTranslationsTab(y, width, footerY);
                    break;
                case 1:
                    DrawStatsTab(y, width, footerY);
                    break;
                case 2:
                    DrawSubmissionTab(y, width, footerY);
                    break;
                case 3:
                    DrawAboutTab(y, width, footerY);
                    break;
            }

            DrawFooter(footerY, width);

            GUI.DragWindow(new Rect(0f, 0f, _windowRect.width - 30f, 24f));
        }

        // ---- Tab 0: 模组翻译开关 ----
        private void DrawModTranslationsTab(float y, float width, float footerY)
        {
            float currentY = y;

            // 搜索栏与分类筛选
            GUI.Label(new Rect(Pad, currentY + 2f, 44f, 20f), T("搜索:", "Find:"));
            string newQuery = GUI.TextField(new Rect(Pad + 46f, currentY, 180f, 22f), _searchFilter ?? string.Empty);
            if (!string.Equals(newQuery, _searchFilter, StringComparison.Ordinal))
            {
                _searchFilter = newQuery;
                _filterDirty = true;
            }

            if (!string.IsNullOrEmpty(_searchFilter))
            {
                if (GUI.Button(new Rect(Pad + 230f, currentY, 22f, 22f), "✕"))
                {
                    _searchFilter = string.Empty;
                    _filterDirty = true;
                }
            }

            string[] filterModes = CjkAvailable() ? FilterModesZh : FilterModesEn;
            int newFilter = GUI.Toolbar(new Rect(Pad + 258f, currentY, width - 258f, 22f), _filterMode, filterModes);
            if (newFilter != _filterMode)
            {
                _filterMode = newFilter;
                _filterDirty = true;
            }

            currentY += 26f;

            // 批量操作按钮行
            float btnW = 70f;
            if (GUI.Button(new Rect(Pad, currentY, btnW, 22f), T("全开", "All on")))
            {
                SetAll(true);
            }

            if (GUI.Button(new Rect(Pad + btnW + 4f, currentY, btnW, 22f), T("全关", "All off")))
            {
                SetAll(false);
            }

            float filBtnW = 100f;
            if (GUI.Button(new Rect(Pad + (btnW + 4f) * 2f, currentY, filBtnW, 22f), T("仅开筛选", "On filtered")))
            {
                SetFiltered(true);
            }

            if (GUI.Button(new Rect(Pad + (btnW + 4f) * 2f + filBtnW + 4f, currentY, filBtnW, 22f), T("仅关筛选", "Off filtered")))
            {
                SetFiltered(false);
            }

            if (GUI.Button(new Rect(Pad + (btnW + 4f) * 2f + (filBtnW + 4f) * 2f, currentY, btnW, 22f), T("刷新", "Refresh")))
            {
                BuildRows();
            }

            float labelX = Pad + (btnW + 4f) * 2f + (filBtnW + 4f) * 2f + btnW + 6f;
            float labelW = width - (labelX - Pad);
            if (labelW > 60f)
            {
                string countInfo = T(
                    "显示 " + _filteredRows.Count + " / " + _rows.Count + " 个模组",
                    "Showing " + _filteredRows.Count + " / " + _rows.Count + " mods");
                GUI.Label(new Rect(labelX, currentY + 2f, labelW, 20f), countInfo, _right);
            }

            currentY += 26f;

            // 模组列表视图
            Rect listRect = new Rect(Pad, currentY, width, footerY - currentY - 6f);
            float contentHeight = Mathf.Max(listRect.height, _filteredRows.Count * RowHeight + 6f);
            _scroll = GUI.BeginScrollView(listRect, _scroll, new Rect(0f, 0f, listRect.width - 20f, contentHeight));

            if (_filteredRows.Count == 0)
            {
                GUI.Label(new Rect(20f, 40f, listRect.width - 40f, 24f),
                    T("未找到匹配的模组（可尝试清空搜索栏或切换筛选条件）",
                      "No matching mods found (try clearing search or changing filters)"),
                    _boldLabelStyle);
            }
            else
            {
                float firstVisible = Mathf.Floor(_scroll.y / RowHeight);
                float lastVisible = firstVisible + (listRect.height / RowHeight) + 1f;

                for (int i = 0; i < _filteredRows.Count; i++)
                {
                    if (i < firstVisible - 1f || i > lastVisible + 1f)
                    {
                        continue;
                    }

                    DrawRow(_filteredRows[i], i * RowHeight, listRect.width - 24f);
                }
            }

            GUI.EndScrollView();
        }

        private void DrawRow(Row row, float y, float width)
        {
            bool enabled = GUI.Toggle(new Rect(4f, y + 1f, 18f, 18f), row.Enabled, GUIContent.none);
            if (enabled != row.Enabled)
            {
                row.Enabled = enabled;
                if (row.ModId.Length > 0)
                {
                    _prefs.SetEnabled(row.ModId, enabled);
                    _disabledCountCached = -1;
                    _filterDirty = true;
                }
            }

            string labelText = row.Label;
            if (!row.Enabled)
            {
                labelText = "<color=#888888>" + labelText + T(" (已关闭)", " (disabled)") + "</color>";
            }
            else if (row.Native)
            {
                labelText = "<color=#4DD0E1>" + labelText + "</color>";
            }

            GUI.Label(new Rect(26f, y, width - 26f - 200f, 18f), labelText, _boldLabelStyle);

            string countText = row.Counts;
            if (row.Miss > 0)
            {
                countText = "<color=#FFB74D>" + countText + "</color>";
            }
            else
            {
                countText = "<color=#B0BEC5>" + countText + "</color>";
            }

            GUI.Label(new Rect(width - 198f, y, 194f, 18f), countText, _badgeStyle);
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

            _disabledCountCached = -1;
            _filterDirty = true;
            SetStatus(enabled ? T("已全部打开（未保存）", "all enabled (not saved)") : T("已全部关闭（未保存）", "all disabled (not saved)"));
        }

        private void SetFiltered(bool enabled)
        {
            List<string> ids = new List<string>(_filteredRows.Count);
            for (int i = 0; i < _filteredRows.Count; i++)
            {
                if (_filteredRows[i].ModId.Length > 0)
                {
                    ids.Add(_filteredRows[i].ModId);
                    _filteredRows[i].Enabled = enabled;
                }
            }

            _prefs.SetAll(ids, enabled);
            _disabledCountCached = -1;
            _filterDirty = true;
            SetStatus(enabled ? T("已打开筛选出的模组（未保存）", "filtered mods enabled (not saved)")
                              : T("已关闭筛选出的模组（未保存）", "filtered mods disabled (not saved)"));
        }

        // ---- Tab 1: 运行统计与诊断 ----
        private void DrawStatsTab(float y, float width, float footerY)
        {
            float currentY = y;

            // 卡片 1: 核心指标总览
            float card1H = 92f;
            GUI.Box(new Rect(Pad, currentY, width, card1H), GUIContent.none, _cardBoxStyle);

            GUI.Label(new Rect(Pad + 12f, currentY + 8f, width - 24f, 22f),
                T("<b>核心统计总览 (Core Statistics)</b>", "<b>Core Statistics Overview</b>"), _boldLabelStyle);

            long totalMiss = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                totalMiss += _rows[i].Miss;
            }

            string col1 = T(
                "当前语言: " + (string.IsNullOrEmpty(_language) ? "zh-cn" : _language) + "\n"
                + "总模组数: " + _prefs.ModCount + " (启用: " + (_prefs.ModCount - _prefs.DisabledCount) + ", 关闭: " + _prefs.DisabledCount + ")",
                "Language: " + (string.IsNullOrEmpty(_language) ? "zh-cn" : _language) + "\n"
                + "Total Mods: " + _prefs.ModCount + " (Active: " + (_prefs.ModCount - _prefs.DisabledCount) + ", Off: " + _prefs.DisabledCount + ")");

            string col2 = T(
                "已装载翻译词条: " + PackStats.TotalEntries + "\n"
                + "未归属文本词条: " + PackStats.Get("UI18n-Unattributed"),
                "Loaded Entries: " + PackStats.TotalEntries + "\n"
                + "Unattributed: " + PackStats.Get("UI18n-Unattributed"));

            string col3 = T(
                "渲染捕获比率: " + RenderCoverage.Ratio + "\n"
                + "累计未命中调用: " + totalMiss,
                "Render Ratio: " + RenderCoverage.Ratio + "\n"
                + "Total Misses: " + totalMiss);

            float thirdW = (width - 24f) / 3f;
            GUI.Label(new Rect(Pad + 12f, currentY + 32f, thirdW, 52f), col1, _boldLabelStyle);
            GUI.Label(new Rect(Pad + 12f + thirdW, currentY + 32f, thirdW, 52f), col2, _boldLabelStyle);
            GUI.Label(new Rect(Pad + 12f + thirdW * 2f, currentY + 32f, thirdW, 52f), col3, _boldLabelStyle);

            currentY += card1H + 10f;

            // 卡片 2: 未命中排行榜 Top 10
            float card2H = 220f;
            GUI.Box(new Rect(Pad, currentY, width, card2H), GUIContent.none, _cardBoxStyle);

            GUI.Label(new Rect(Pad + 12f, currentY + 8f, width - 24f, 22f),
                T("<b>未命中排行 Top 10 (调用未汉化频次最高模组)</b>", "<b>Top 10 Misses Leaderboard</b>"), _boldLabelStyle);

            List<Row> missesRank = new List<Row>(_rows);
            missesRank.RemoveAll(delegate(Row r) { return r.Miss <= 0; });
            missesRank.Sort(delegate(Row a, Row b) { return b.Miss.CompareTo(a.Miss); });

            if (missesRank.Count == 0)
            {
                GUI.Label(new Rect(Pad + 16f, currentY + 40f, width - 32f, 24f),
                    T("<color=#81C784>当前无未命中调用，所有受支持模组均正常翻译！</color>",
                      "<color=#81C784>No misses detected, all supported mods are localized!</color>"), _boldLabelStyle);
            }
            else
            {
                float rowTop = currentY + 32f;
                int maxShow = Mathf.Min(8, missesRank.Count);
                for (int i = 0; i < maxShow; i++)
                {
                    Row r = missesRank[i];
                    string rankNum = "#" + (i + 1);
                    string modName = r.Display + " (" + (string.IsNullOrEmpty(r.ModId) ? OtherLabel : r.ModId) + ")";
                    string missCount = r.Miss + T(" 次未命中", " misses");

                    GUI.Label(new Rect(Pad + 16f, rowTop, 36f, 20f), "<color=#FFB74D>" + rankNum + "</color>", _boldLabelStyle);
                    GUI.Label(new Rect(Pad + 56f, rowTop, width - 230f, 20f), modName, _boldLabelStyle);
                    GUI.Label(new Rect(Pad + width - 170f, rowTop, 150f, 20f), "<color=#FF8A65>" + missCount + "</color>", _right);
                    rowTop += 21f;
                }
            }

            currentY += card2H + 10f;

            // 卡片 3: 运行环境与字体支持
            float card3H = 88f;
            GUI.Box(new Rect(Pad, currentY, width, card3H), GUIContent.none, _cardBoxStyle);

            GUI.Label(new Rect(Pad + 12f, currentY + 8f, width - 24f, 22f),
                T("<b>运行时与字体诊断 (Runtime & Font Status)</b>", "<b>Runtime & Font Status</b>"), _boldLabelStyle);

            string fontStatus = CjkAvailable()
                ? T("<color=#81C784>正常 (当前 GUI 字体支持 CJK 汉字)</color>", "<color=#81C784>OK (CJK glyphs supported)</color>")
                : T("<color=#E57373>警告 (当前 GUI 字体缺少 CJK 字符，部分界面将退回英文)</color>", "<color=#E57373>Warning (CJK missing, falling back to English)</color>");

            string fontName = GUI.skin != null && GUI.skin.font != null ? GUI.skin.font.name : T("默认位图字体", "Default Bitmap Font");

            GUI.Label(new Rect(Pad + 12f, currentY + 32f, width - 24f, 20f),
                T("CJK 汉字支持: ", "CJK Support: ") + fontStatus, _boldLabelStyle);

            string envLine = T("GUI 字体: " + fontName + "  |  场景: " + HighLogic.LoadedScene + "  |  平台: " + Application.platform,
                               "Font: " + fontName + "  |  Scene: " + HighLogic.LoadedScene + "  |  Platform: " + Application.platform);
            GUI.Label(new Rect(Pad + 12f, currentY + 54f, width - 24f, 20f), envLine, _boldLabelStyle);

            currentY += card3H + 12f;

            if (GUI.Button(new Rect(Pad, currentY, 240f, 26f), T("复制诊断报告到剪贴板", "Copy Diagnostics Report")))
            {
                CopyDiagnosticsReport(totalMiss);
            }
        }

        private void CopyDiagnosticsReport(long totalMiss)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("## UniversalI18n Diagnostic Report");
                sb.AppendLine("- Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("- Language: " + (string.IsNullOrEmpty(_language) ? "zh-cn" : _language));
                sb.AppendLine("- Total Mods: " + _prefs.ModCount + " (Disabled: " + _prefs.DisabledCount + ")");
                sb.AppendLine("- Total Loaded Entries: " + PackStats.TotalEntries);
                sb.AppendLine("- Total Misses: " + totalMiss);
                sb.AppendLine("- Render Ratio: " + RenderCoverage.Ratio);
                sb.AppendLine("- Scene: " + HighLogic.LoadedScene);
                sb.AppendLine("- CJK Available: " + CjkAvailable());
                sb.AppendLine("- Unity Version: " + Application.unityVersion);
                sb.AppendLine("- Platform: " + Application.platform);
                GUIUtility.systemCopyBuffer = sb.ToString();
                SetStatus(T("诊断报告已复制到剪贴板！", "Report copied to clipboard!"));
            }
            catch (Exception ex)
            {
                SetStatus(T("复制失败: " + ex.Message, "Copy failed: " + ex.Message));
            }
        }

        // ---- Tab 2: 词条投稿向导 ----
        private void DrawSubmissionTab(float y, float width, float footerY)
        {
            float currentY = y;

            // 隐私与工作流说明
            float bannerH = 110f;
            GUI.Box(new Rect(Pad, currentY, width, bannerH), GUIContent.none, _cardBoxStyle);

            GUI.Label(new Rect(Pad + 12f, currentY + 8f, width - 24f, 22f),
                T("<b>零服务器隐私投稿工作流 (Privacy-First Community Submissions)</b>",
                  "<b>Privacy-First Community Submissions</b>"), _boldLabelStyle);

            string bannerDesc = T(
                "• <b>零服务器、零联网、零外发</b>：导出仅在本地生成脱敏 json，不向任何第三方发起网络请求。\n"
                + "• <b>零个人隐私泄露</b>：严格过滤本地路径、玩家命名、存档数据与敏感文本，仅提取未翻译的静态英文短句。\n"
                + "• <b>开源共建</b>：通过 GitHub Issue 模板提交，所有玩家均可在社区公开监督并共同完善汉化词典。",
                "• <b>Zero Server / Zero Network</b>: Exports JSON locally. Never connects to any third-party server.\n"
                + "• <b>Privacy First</b>: Redacts local paths, user names, and save data. Only collects untranslated English strings.\n"
                + "• <b>Community Driven</b>: Submit via GitHub Issues where all contributions are transparent and public.");

            GUI.Label(new Rect(Pad + 12f, currentY + 30f, width - 24f, 72f), bannerDesc, _boldLabelStyle);

            currentY += bannerH + 10f;

            // 授权与配置卡片
            float configH = 92f;
            GUI.Box(new Rect(Pad, currentY, width, configH), GUIContent.none, _cardBoxStyle);

            GUI.Label(new Rect(Pad + 12f, currentY + 8f, width - 24f, 22f),
                T("<b>投稿授权与选项 (Consent & Options)</b>", "<b>Consent & Options</b>"), _boldLabelStyle);

            SubmissionPrefs submission = _prefs != null ? _prefs.Submission : null;
            bool granted = submission != null && submission.Granted;

            bool next = GUI.Toggle(new Rect(Pad + 16f, currentY + 34f, width - 240f, 22f), granted,
                T("同意投稿：允许打包未命中的模组英文原文供作者补充汉化",
                  "Allow submission: Pack untranslated source strings for the author"));
            if (next != granted)
            {
                ApplySubmissionConsent(next);
            }

            GUI.Label(new Rect(Pad + width - 210f, currentY + 34f, 196f, 22f),
                SubmissionStatusText(), _right);

            _allowLongSingletons = GUI.Toggle(new Rect(Pad + 16f, currentY + 60f, width - 32f, 22f), _allowLongSingletons,
                T("允许包含单次出现的长文本（>120 字符，默认过滤以防偶然内容泄露）",
                  "Include long text seen only once (>120 chars, default filtered)"));

            currentY += configH + 10f;

            // 导出与上报操作区
            float actionH = 160f;
            GUI.Box(new Rect(Pad, currentY, width, actionH), GUIContent.none, _cardBoxStyle);

            GUI.Label(new Rect(Pad + 12f, currentY + 8f, width - 24f, 22f),
                T("<b>导出与上报操作 (Export & Report Actions)</b>", "<b>Export & Report Actions</b>"), _boldLabelStyle);

            float btnRowY = currentY + 34f;
            if (_submissionConfirm)
            {
                if (GUI.Button(new Rect(Pad + 16f, btnRowY, 130f, 26f), T("确认立即导出", "Confirm Export")))
                {
                    ExportSubmission();
                }

                if (GUI.Button(new Rect(Pad + 152f, btnRowY, 80f, 26f), T("取消", "Cancel")))
                {
                    _submissionConfirm = false;
                    SetStatus(T("已取消导出", "Export cancelled"));
                }
            }
            else
            {
                if (GUI.Button(new Rect(Pad + 16f, btnRowY, 170f, 26f), T("预览并生成投稿包…", "Preview & Export...")))
                {
                    PreviewSubmission();
                }
            }

            if (GUI.Button(new Rect(Pad + 236f, btnRowY, 130f, 26f), T("打开投稿文件夹", "Open Folder")))
            {
                OpenSubmissionsFolder();
            }

            bool reportable = _submissionBundle != null;
            bool prevEnabled = GUI.enabled;
            GUI.enabled = reportable;
            if (GUI.Button(new Rect(Pad + 372f, btnRowY, 160f, 26f), T("在浏览器中上报…", "Report on GitHub...")))
            {
                ReportSubmission();
            }
            GUI.enabled = prevEnabled;

            if (GUI.Button(new Rect(Pad + 538f, btnRowY, 140f, 26f), T("查看投稿模板", "Submit Page")))
            {
                OpenSubmissionPage();
            }

            float infoBoxY = currentY + 68f;
            GUI.Label(new Rect(Pad + 16f, infoBoxY, width - 32f, 20f),
                T("<b>操作日志与状态明细:</b>", "<b>Status & Operation Details:</b>"), _boldLabelStyle);

            string detailText = _submissionInfo.Length > 0
                ? _submissionInfo
                : T("提示：先勾选「同意投稿」，在游戏中游玩数分钟后点击「预览并生成投稿包」。",
                    "Hint: Tick 'Allow submission', play for a few minutes, then click 'Preview & Export'.");

            GUI.Label(new Rect(Pad + 16f, infoBoxY + 22f, width - 32f, 60f),
                "<color=#B0BEC5>" + detailText + "</color>", _boldLabelStyle);
        }

        // ---- Tab 3: 关于与说明 ----
        private void DrawAboutTab(float y, float width, float footerY)
        {
            float currentY = y;

            float headerH = 76f;
            GUI.Box(new Rect(Pad, currentY, width, headerH), GUIContent.none, _cardBoxStyle);

            if (_icon != null)
            {
                GUI.DrawTexture(new Rect(Pad + 14f, currentY + 10f, 56f, 56f), _icon);
            }

            float textX = Pad + 80f;
            GUI.Label(new Rect(textX, currentY + 8f, width - 90f, 24f),
                T("Universal I18n · 宇宙汉化", "Universal I18n"), _titleStyle);

            GUI.Label(new Rect(textX, currentY + 34f, width - 90f, 18f),
                T("<color=#81D4FA>Kerbal Space Program 游戏与模组无侵入本地化引擎 (v1.6)</color>",
                  "<color=#81D4FA>Kerbal Space Program High-Performance Localization Engine (v1.6)</color>"), _subTitleStyle);

            GUI.Label(new Rect(textX, currentY + 52f, width - 90f, 18f),
                "<color=#90A4AE>Core 核心架构 · Harmony 2.x 运行时 · 80+ 流行模组字典内置</color>", _boldLabelStyle);

            currentY += headerH + 10f;

            float faqH = 150f;
            GUI.Box(new Rect(Pad, currentY, width, faqH), GUIContent.none, _cardBoxStyle);

            GUI.Label(new Rect(Pad + 12f, currentY + 8f, width - 24f, 22f),
                T("<b>架构设计与设计原则 (Architecture Principles)</b>", "<b>Architecture Principles</b>"), _boldLabelStyle);

            string faqText = T(
                "<b>Q: 为什么开关模组后必须重启游戏才能生效？</b>\n"
                + "UI18n 为追求极致的游戏帧率与绝对零 GC 垃圾，在游戏启动引导阶段一次性完成所有字典预载与 17 处 Harmony 补丁编译装载。运行时字典为完全不可变的只读索引树。若在运行期热拆拔字典，将导致剧烈帧率波动并破坏 Unity 补丁链，因此配置持久化写入 PluginData/UI18n/prefs.json，于下次启动加载。\n\n"
                + "<b>Q: 如何添加新的模组汉化？</b>\n"
                + "UI18n 采用开箱即用的 JSONL 词典切片格式，可前往 GitHub 仓库贡献词条或使用 Tab 2 词条投稿向导导出未命中原文。",
                "<b>Q: Why is game restart required after changing mod switches?</b>\n"
                + "For maximum frame rates and zero GC pressure, UI18n preloads all dictionaries and compiles all 17 Harmony patches once at boot. Runtime dictionaries are fully immutable read-only indices. Hot-reloading would introduce frame drops and patch chain corruption. Changes are saved to PluginData/UI18n/prefs.json for the next launch.\n\n"
                + "<b>Q: How to add translations for new mods?</b>\n"
                + "Contribute JSONL word entries on GitHub or use Tab 2 (Submissions) to export missed strings.");

            GUI.Label(new Rect(Pad + 12f, currentY + 28f, width - 24f, 116f), faqText, _boldLabelStyle);

            currentY += faqH + 10f;

            float linkH = 80f;
            GUI.Box(new Rect(Pad, currentY, width, linkH), GUIContent.none, _cardBoxStyle);

            GUI.Label(new Rect(Pad + 12f, currentY + 8f, width - 24f, 22f),
                T("<b>快捷入口与开源社区 (Quick Access & Links)</b>", "<b>Quick Access & Links</b>"), _boldLabelStyle);

            if (GUI.Button(new Rect(Pad + 16f, currentY + 34f, 200f, 26f), T("打开配置目录 (PluginData)", "Open Config Directory")))
            {
                try
                {
                    string dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/UI18n/PluginData/UI18n");
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    Application.OpenURL("file:///" + dir.Replace('\\', '/'));
                }
                catch (Exception ex)
                {
                    SetStatus("打开目录失败: " + ex.Message);
                }
            }

            if (GUI.Button(new Rect(Pad + 224f, currentY + 34f, 200f, 26f), T("访问 GitHub 开源仓库", "Visit GitHub Repository")))
            {
                Application.OpenURL(ProjectRepoUrl);
            }

            if (GUI.Button(new Rect(Pad + 432f, currentY + 34f, 200f, 26f), T("提交 Issue 或反馈", "Submit Issue / Feedback")))
            {
                Application.OpenURL(ProjectRepoUrl + "/issues");
            }
        }

        // ---- 底部操作栏 ----
        private void DrawFooter(float footerY, float width)
        {
            if (GUI.Button(new Rect(Pad, footerY + 6f, 180f, 26f), T("保存配置 (重启生效)", "Save (restart required)")))
            {
                SavePrefs();
            }

            if (GUI.Button(new Rect(Pad + 186f, footerY + 6f, 110f, 26f), T("重新读取", "Reload")))
            {
                PrefsRuntime.Load();
                _prefs = PrefsRuntime.Current;
                BuildRows();
                SetStatus(T("已重新读取 prefs.json", "prefs.json reloaded"));
            }

            float statusX = Pad + 304f;
            float statusW = width - 304f;

            if (_status.Length > 0 && Time.realtimeSinceStartup < _statusUntil)
            {
                GUI.Label(new Rect(statusX, footerY + 8f, statusW, 22f), _status, _statusStyle);
            }
            else if (_prefs.DisabledCount > 0)
            {
                if (_disabledCountCached != _prefs.DisabledCount)
                {
                    _disabledCountCached = _prefs.DisabledCount;
                    _disabledLabel = T(
                        "<color=#FFB74D>已关闭 " + _prefs.DisabledCount + " 个模组，重启游戏后生效</color>",
                        "<color=#FFB74D>" + _prefs.DisabledCount + " mods disabled, restart required</color>");
                }

                GUI.Label(new Rect(statusX, footerY + 8f, statusW, 22f), _disabledLabel, _statusStyle);
            }
            else
            {
                GUI.Label(new Rect(statusX, footerY + 8f, statusW, 22f),
                    T("<color=#81C784>所有模组翻译已正常启用</color>", "<color=#81C784>All mod translations active</color>"), _statusStyle);
            }
        }

        // ------------------------------------------------------------------
        // 投稿逻辑
        // ------------------------------------------------------------------

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

        // ------------------------------------------------------------------
        // 图标资源
        // ------------------------------------------------------------------

        private Texture2D BuildIcon()
        {
            _iconCreatedLocally = false;

            // 1. 优先尝试从 KSP GameDatabase 获取高精度贴图（若游戏已装载）
            try
            {
                if (GameDatabase.Instance != null)
                {
                    Texture2D tex = GameDatabase.Instance.GetTexture("UI18n/Textures/icon", false);
                    if (tex == null)
                    {
                        tex = GameDatabase.Instance.GetTexture("UI18n/icon", false);
                    }
                    if (tex != null)
                    {
                        Log.Info("panel: loaded launcher icon from GameDatabase");
                        return tex;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("panel: GameDatabase.GetTexture failed: " + ex.Message);
            }

            // 2. 内置 38x38 航天级矢量重绘图标（包含深空蓝球、青色公转轨道环、卫星与中英文符号）
            try
            {
                Texture2D texture = new Texture2D(38, 38, TextureFormat.RGBA32, false);
                texture.name = "UI18n.launcher";
                Color32[] pixels = new Color32[38 * 38];
                for (int i = 0; i < IconPixels38.Length && i < pixels.Length; i++)
                {
                    uint v = IconPixels38[i];
                    pixels[i] = new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24));
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                _iconCreatedLocally = true;
                Log.Info("panel: generated 38x38 high-res launcher icon from embedded data");
                return texture;
            }
            catch (Exception ex)
            {
                Log.Warn("panel: failed to generate 38x38 icon: " + ex.Message);
            }

            // 3. 终极兜底：16x16 纯色贴图
            try
            {
                Texture2D fallback = new Texture2D(16, 16, TextureFormat.RGBA32, false);
                fallback.name = "UI18n.launcher.fallback";
                Color32 fill = new Color32(0x2F, 0x6F, 0xB0, 0xFF);
                Color32[] fallbackPixels = new Color32[16 * 16];
                for (int i = 0; i < fallbackPixels.Length; i++) fallbackPixels[i] = fill;
                fallback.SetPixels32(fallbackPixels);
                fallback.Apply(false, false);
                _iconCreatedLocally = true;
                return fallback;
            }
            catch
            {
                return null;
            }
        }

private static readonly uint[] IconPixels38 = new uint[38 * 38] {
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0600D4FFu, 0x1200D4FEu, 0x1800D4FFu, 0x2200D2FFu, 0x2200D2FFu, 0x2200D2FFu, 0x2200D2FFu, 0x1900D6FEu, 0x1500CEFEu, 0x0800DFFFu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0100FFFFu, 0x1500CEFEu, 0x2600D0FEu, 0x491BC3FBu, 0x882DB9F7u, 0xB532B5F5u, 0xBA34B4F4u, 0xD636B5F5u, 0xDB36B4F5u, 0xBB33B5F4u, 0xB532B5F5u, 0x932EB7F6u, 0x5521C0F9u, 0x2700D1FEu, 0x1B00CFFEu, 0x0400BFFFu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x1200D4FEu, 0x2F0ACEFEu, 0x7C2BBBF6u, 0xCE33B3F2u, 0xEC33ABEAu, 0xF433A6E2u, 0xF9319DD9u, 0xFF2B84B7u, 0xFF2981B4u, 0xFF2981B4u, 0xFF2A83B7u, 0xFA309AD4u, 0xF532A5E2u, 0xED33ABE9u, 0xD534B1F0u, 0x932EB7F6u, 0x380DC8FAu, 0x1700D2FEu, 0x0300AAFFu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0300AAFFu, 0x2200D2FFu, 0x6C28BAF7u, 0xD434B2F1u, 0xF133A8E5u, 0xFD2C8EC4u, 0xFF1D5C85u, 0xFF1A4468u, 0xFF0B1C33u, 0xFF071429u, 0xFF071429u, 0xFF071429u, 0xFF071429u, 0xFF0B1B32u, 0xFF183C5Eu, 0xFF1C557Bu, 0xFF2880B2u, 0xF432A6E2u, 0xDA34B1F2u, 0x882DB9F7u, 0x2D0BCBFEu, 0x0700DAFEu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0700DAFEu, 0x2C0BCAFEu, 0xA431B7F5u, 0xEE33AAE8u, 0xFC2E92CAu, 0xFF17486Cu, 0xFF08162Du, 0xFF163351u, 0xFF091931u, 0xFF07152Bu, 0xFF07152Bu, 0xFF07152Bu, 0xFF07152Bu, 0xFF07152Bu, 0xFF07152Bu, 0xFF08182Eu, 0xFF153251u, 0xFF091931u, 0xFF113757u, 0xFD2985B9u, 0xF133A8E5u, 0xBB33B5F5u, 0x3C15C7FAu, 0x0B00D0FEu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0600D4FFu, 0x3C15C7FAu, 0xC033B5F4u, 0xF333A6E3u, 0xFF226C99u, 0xFF0A1B33u, 0xFF08172Du, 0xFF122C48u, 0xFF0F2540u, 0xFF08172Du, 0xFF193D55u, 0xFF2D6782u, 0xFF306C88u, 0xFF306C88u, 0xFF306C88u, 0xFF1C425Au, 0xFF08172Du, 0xFF0D2038u, 0xFF15314Fu, 0xFF08172Du, 0xFF08172Du, 0xFF1A5278u, 0xF632A4E0u, 0xD434B2F1u, 0x4E1DC0FBu, 0x0B00D0FEu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0100FFFFu, 0x2B0BC9FEu, 0xB532B5F5u, 0xF925B4E7u, 0xFF147599u, 0xFF064861u, 0xFF064861u, 0xFF0B304Bu, 0xFF153455u, 0xFF0F2841u, 0xFF4192B0u, 0xFF469CBCu, 0xFF32728Fu, 0xFF306D89u, 0xFF306D89u, 0xFF306D89u, 0xFF4497B6u, 0xFF50B1D2u, 0xFF1C435Du, 0xFF14304Fu, 0xFF0D233Eu, 0xFF081830u, 0xFF081830u, 0xFF123A5Bu, 0xF72F9AD5u, 0xD434B2F1u, 0x3C15C7FAu, 0x0700DAFEu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x2B00DBFEu, 0xCF11D6FBu, 0xFE04DFFCu, 0xFF08BCDAu, 0xFF03A9C3u, 0xFF03A9C3u, 0xFF03A9C3u, 0xFF03CBE5u, 0xFF1ACEEBu, 0xFF4ACBECu, 0xFF1C91ADu, 0xFF07435Du, 0xFF091932u, 0xFF091932u, 0xFF091932u, 0xFF091932u, 0xFF091932u, 0xFF102943u, 0xFF4498B7u, 0xFF295D7Bu, 0xFF163355u, 0xFF091932u, 0xFF091932u, 0xFF091932u, 0xFF153F61u, 0xF632A4E0u, 0xBB33B5F5u, 0x2D0BCBFEu, 0x0100FFFFu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x1300D6FEu, 0xD704E0FDu, 0xF41DC5F3u, 0xFF2374A4u, 0xFF091A34u, 0xFF091A34u, 0xFF091A34u, 0xFF0A1D37u, 0xFF18385Cu, 0xFF154560u, 0xFF0B5D78u, 0xFF057A94u, 0xFF02B0CAu, 0xFF02C2DBu, 0xFF05738Eu, 0xFF082640u, 0xFF091A34u, 0xFF091A34u, 0xFF091A34u, 0xFF091A34u, 0xFF183A55u, 0xFF173759u, 0xFF0C1F3Bu, 0xFF091A34u, 0xFF091A34u, 0xFF091A34u, 0xFF1C5882u, 0xF133A8E6u, 0x7C2BBBF6u, 0x1500CEFEu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x9F00E2FEu, 0xD91CCAF9u, 0xFC309CD7u, 0xFF0E2946u, 0xFF0A1C36u, 0xFF0A1C36u, 0xFF0A1C36u, 0xFF102846u, 0xFF153151u, 0xFF0A1C36u, 0xFF0A1C36u, 0xFF0A1C36u, 0xFF0A1C36u, 0xFF09344Eu, 0xFF05819Bu, 0xFF01C8E2u, 0xFF0493ACu, 0xFF093A54u, 0xFF0A1C36u, 0xFF0A1C36u, 0xFF0A1C36u, 0xFF132D4Cu, 0xFF132D4Cu, 0xFF0A1C36u, 0xFF0A1C36u, 0xFF0A1C36u, 0xFF0A1C36u, 0xFD2A87BCu, 0xD534B1F0u, 0x380DC8FAu, 0x0400BFFFu, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x0D00C4FEu, 0xE003E0FDu, 0xF12CB2EBu, 0xFF1B567Fu, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF143354u, 0xFF102946u, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF0A233Eu, 0xFF06647Eu, 0xFF02BCD6u, 0xFF03A4BFu, 0xFF09354Fu, 0xFF0A1D38u, 0xFF0D2541u, 0xFF17375Au, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF0A1D38u, 0xFF16456Bu, 0xF233A7E5u, 0x8D2FB8F5u, 0x1800D4FFu, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x1C00D1FEu, 0xED07DEFDu, 0xFC27A3DAu, 0xFF0C223Fu, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF193B5Fu, 0xFF0C223Fu, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF07536Fu, 0xFF02BCD7u, 0xFF067691u, 0xFF0A1E3Au, 0xFF1B3F64u, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFF0A1E3Au, 0xFD2A88BDu, 0xCF33B2F1u, 0x2600D0FEu, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x300ACFFFu, 0xF511D1F8u, 0xFF1998C2u, 0xFF0B1F3Cu, 0xFF0B1F3Cu, 0xFF0B1F3Cu, 0xFF0B1F3Cu, 0xFF0B1F3Cu, 0xFF1A3F64u, 0xFF0C203Eu, 0xFF0B1F3Cu, 0xFF0B1F3Cu, 0xFF0B1F3Cu, 0xFF0B1F3Cu, 0xFF0B1F3Cu, 0xFF0B1F3Cu, 0xFF3C443Du, 0xFF484D3Du, 0xFF0B1F3Cu, 0xFF0B1F3Cu, 0xFF0B2542u, 0xFF05829Eu, 0xFF03B1CCu, 0xFF165579u, 0xFF0D2240u, 0xFF0B1F3Cu, 0xFF56583Eu, 0xFF23313Cu, 0xFF0B1F3Cu, 0xFF1E608Cu, 0xEC34ACEBu, 0x4F1DC1FBu, 0x0600D4FFu, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x0A00CCFFu, 0x6625BBF7u, 0xF426B7ECu, 0xFF07BAD8u, 0xFF0B203Fu, 0xFF0B203Fu, 0xFF0B203Fu, 0xFF0B203Fu, 0xFF0D2343u, 0xFF1A3D65u, 0xFF0B203Fu, 0xFF0B203Fu, 0xFF0B203Fu, 0xFF0B203Fu, 0xFF0B203Fu, 0xFF0B203Fu, 0xFF0B203Fu, 0xFF807840u, 0xFFFFD740u, 0xFFDCBD40u, 0xFF565840u, 0xFF0B203Fu, 0xFF0B203Fu, 0xFF094867u, 0xFF11C3D7u, 0xFF6FA87Au, 0xFFFFD740u, 0xFFFFD740u, 0xFF908440u, 0xFF0B203Fu, 0xFF123458u, 0xF433A7E4u, 0x882DB9F7u, 0x1400CCFFu, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x1100D2FFu, 0x822DBAF7u, 0xF632A5E3u, 0xFF05ABC8u, 0xFF095674u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF0C2241u, 0xFF0F2949u, 0xFF193C63u, 0xFF0C2241u, 0xFF0C2241u, 0xFF0C2241u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF0C2241u, 0xFF0C2241u, 0xFF0C2241u, 0xFF172A41u, 0xFF726E41u, 0xFFFFD740u, 0xFFC6AC40u, 0xFF303D41u, 0xFF3D4641u, 0xFFDDC045u, 0xFFFFD740u, 0xFFB0C466u, 0xFF243946u, 0xFF0C2241u, 0xFF0C2241u, 0xFF0C2241u, 0xF7309CD8u, 0x9F30B6F5u, 0x1800D4FFu, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x1500CEFEu, 0x932EB7F6u, 0xF932A3E0u, 0xFF0D3759u, 0xFF03B8D3u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF0C2343u, 0xFF102C4Fu, 0xFF183A60u, 0xFF0C2343u, 0xFF0C2343u, 0xFF0C2343u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF0C2343u, 0xFF0C2343u, 0xFF0C2343u, 0xFF0C2343u, 0xFF0C2343u, 0xFF243542u, 0xFFC6AC41u, 0xFFFFD740u, 0xFFFFD740u, 0xFFFFD740u, 0xFF948B49u, 0xFF086280u, 0xFF04A1BDu, 0xFF0C2949u, 0xFF0C2343u, 0xFF0C2343u, 0xF92D8FC7u, 0xB532B5F5u, 0x1E00CCFFu, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x1B00CFFEu, 0xA833B7F5u, 0xFB3099D5u, 0xFF153459u, 0xFF11587Cu, 0xFF06B1CEu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF1C3F68u, 0xFF1D456Du, 0xFF153459u, 0xFF153459u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF153459u, 0xFF153459u, 0xFF153459u, 0xFF153459u, 0xFF153459u, 0xFF153459u, 0xFF203C58u, 0xFFFFD740u, 0xFFFFD740u, 0xFFFFD740u, 0xFFB7A84Eu, 0xFF1D426Bu, 0xFF153459u, 0xFF0F6385u, 0xFF07A7C4u, 0xFF14395Eu, 0xFF153459u, 0xFF2B86BDu, 0xC935B5F4u, 0x2200D2FFu, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x1900D6FEu, 0xA02FB7F5u, 0xFB32A1DDu, 0xFF15365Bu, 0xFF15365Bu, 0xFF0F6587u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF15365Bu, 0xFF15365Bu, 0xFF15365Bu, 0xFF15365Bu, 0xFF15365Bu, 0xFF15365Bu, 0xFF958E4Cu, 0xFFFFD740u, 0xFFA4994Au, 0xFFA4994Au, 0xFFFFD740u, 0xFF4A6163u, 0xFF15365Bu, 0xFF15365Bu, 0xFF0E6E90u, 0xFF08A3C1u, 0xFF15365Bu, 0xFF2B87BEu, 0xC133B6F4u, 0x2200D2FFu, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x1400CCFFu, 0x8D2FB8F5u, 0xF832A4E2u, 0xFF102F52u, 0xFF0E2749u, 0xFF0E2749u, 0xFF0A607Fu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF1A3E66u, 0xFF0E2749u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF0E2749u, 0xFF0E2749u, 0xFF0E2749u, 0xFF0E2749u, 0xFF0E2749u, 0xFF0E2749u, 0xFF263848u, 0xFFFFD740u, 0xFFFFD740u, 0xFF192F49u, 0xFF263848u, 0xFFFFD740u, 0xFFDDBF44u, 0xFF0E2749u, 0xFF0E2749u, 0xFF0E2749u, 0xFF0691AFu, 0xFF09708Fu, 0xF82E95CFu, 0xAF32B6F6u, 0x1C00D1FEu, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x0F00DDFFu, 0x7C2BBBF6u, 0xF532A6E4u, 0xFF143B62u, 0xFF0E284Bu, 0xFF0E284Bu, 0xFF0E284Bu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF146D92u, 0xFF0E284Bu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF0E284Bu, 0xFF0E284Bu, 0xFF0E284Bu, 0xFF0E284Bu, 0xFF0E284Bu, 0xFF0E284Bu, 0xFF737147u, 0xFFFFD740u, 0xFF737147u, 0xFF0E284Bu, 0xFF0E284Bu, 0xFF979151u, 0xFFFFD740u, 0xFF585E48u, 0xFF0E284Bu, 0xFF0E284Bu, 0xFF0E2E51u, 0xFF03C5E0u, 0xF82DA6E0u, 0x9F30B6F5u, 0x1700D2FEu, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x0700DAFEu, 0x5521C0F9u, 0xEF34AAE9u, 0xFF206291u, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFFDCBE42u, 0xFFFFD740u, 0xFF273B4Bu, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFF3E5A67u, 0xFFFFD740u, 0xFFDCBE42u, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFF0F2A4Du, 0xFF0E89ABu, 0xF917C9F3u, 0x762BBCF8u, 0x0F00DDFFu, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x2600D0FEu, 0xD734AFEFu, 0xFF2983BAu, 0xFF0F2B50u, 0xFF0F2B50u, 0xFF0F2B50u, 0xFF0F2B50u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF0A6789u, 0xFF0F2B50u, 0xFF0F2B50u, 0xFF0F2B50u, 0xFF273C4Eu, 0xFF747349u, 0xFF747349u, 0xFFFFD740u, 0xFFFFD740u, 0xFF747349u, 0xFF747349u, 0xFF747349u, 0xFF7C845Eu, 0xFFFFD740u, 0xFFFFD740u, 0xFF827D49u, 0xFF747349u, 0xFF273C4Eu, 0xFF2278A9u, 0xFC06E0FDu, 0x3D14C8FAu, 0x0300AAFFu, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x1700D2FEu, 0x9930B7F4u, 0xF933A4E2u, 0xFF16426Du, 0xFF102C52u, 0xFF102C52u, 0xFF102C52u, 0xFF102C52u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF0F3C62u, 0xFF069FBDu, 0xFF05A9C7u, 0xFF0D5276u, 0xFF102C52u, 0xFF273D50u, 0xFF74744Bu, 0xFF74744Bu, 0xFF74744Bu, 0xFF74744Bu, 0xFF74744Bu, 0xFFFFD740u, 0xFFFFD740u, 0xFF999658u, 0xFF74744Bu, 0xFF74744Bu, 0xFF74744Bu, 0xFF74744Bu, 0xFF294054u, 0xFA2F97D4u, 0xF704E2FDu, 0x2F00D9FEu, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x0700DAFEu, 0x4F1DC1FBu, 0xEB35ADEDu, 0xFF287DB3u, 0xFF102D54u, 0xFF102D54u, 0xFF102D54u, 0xFF102D54u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF102D54u, 0xFF102D54u, 0xFF0C5E82u, 0xFF04B5D2u, 0xFF04B5D2u, 0xFF0A6E90u, 0xFF102D54u, 0xFF102D54u, 0xFF102D54u, 0xFF102D54u, 0xFF344651u, 0xFFFFD740u, 0xFFFFD740u, 0xFF1A426Du, 0xFF102D54u, 0xFF102D54u, 0xFF102D54u, 0xFF6A7252u, 0xFFC7C560u, 0xF57AC0B1u, 0xED02E4FEu, 0x1800DFFFu, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x1B00CFFEu, 0xA431B7F5u, 0xFA32A4E2u, 0xFF174470u, 0xFF112F56u, 0xFF112F56u, 0xFF112F56u, 0xFF153760u, 0xFF1C446Fu, 0xFF112F56u, 0xFF112F56u, 0xFF112F56u, 0xFF112F56u, 0xFF0D5479u, 0xFF079AB9u, 0xFF02CBE6u, 0xFF088AABu, 0xFF0F3F65u, 0xFF112F56u, 0xFF1C3755u, 0xFF415152u, 0xFF536660u, 0xFF173B65u, 0xFF112F56u, 0xFF112F56u, 0xFF2F4655u, 0xFFFFE650u, 0xFFFFE650u, 0xFEF8E455u, 0x9F00E2FEu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0400BFFFu, 0x3D14C8FAu, 0xE334AFEFu, 0xFD2E95CFu, 0xFF133760u, 0xFF113058u, 0xFF113058u, 0xFF123159u, 0xFF1E4977u, 0xFF2F6E94u, 0xFF215177u, 0xFF113058u, 0xFF113058u, 0xFF113058u, 0xFF113058u, 0xFF0F4067u, 0xFF0980A2u, 0xFF02CBE7u, 0xFF03C5E2u, 0xFF18B1CFu, 0xFF2EA5C8u, 0xFF176E97u, 0xFF123860u, 0xFF113058u, 0xFF113058u, 0xFF265065u, 0xFFF1E258u, 0xFFFFE650u, 0xFCF1E559u, 0x1600DCFEu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x1400CCFFu, 0x6625BBF7u, 0xEB35ADEDu, 0xFF2C89C2u, 0xFF13355Fu, 0xFF11315Au, 0xFF11315Au, 0xFF183F6Au, 0xFF1F4D79u, 0xFF47A0C5u, 0xFF357BA1u, 0xFF1A446Cu, 0xFF11315Au, 0xFF11315Au, 0xFF11315Au, 0xFF11315Au, 0xFF14365Fu, 0xFF368BB0u, 0xFF47B6D9u, 0xFF179BBDu, 0xFF08B6D5u, 0xFF01D5F1u, 0xFF01DBF5u, 0xFF01DBF5u, 0xFF05D4F2u, 0xFB45D7D2u, 0xD7A2DA94u, 0x3891DF9Au, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x1B00CFFEu, 0x822DBAF7u, 0xED34ADECu, 0xFF2C89C3u, 0xFF143964u, 0xFF12325Cu, 0xFF14355Fu, 0xFF1E4A79u, 0xFF153963u, 0xFF357BA2u, 0xFF4FB2D7u, 0xFF4BA9CEu, 0xFF47A1C5u, 0xFF47A1C5u, 0xFF4EAED3u, 0xFF56C0E4u, 0xFF33779Eu, 0xFF12325Cu, 0xFF1E4978u, 0xFF153863u, 0xFF12325Cu, 0xFF143660u, 0xFF2678AEu, 0xF334A8E7u, 0xAF32B6F6u, 0x2300D3FEu, 0x0100FFFFu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0100FFFFu, 0x2200D2FFu, 0x822DBAF7u, 0xEB35AEEDu, 0xFD2E95D0u, 0xFF1A4F80u, 0xFF12335Fu, 0xFF173D6Bu, 0xFF1B4474u, 0xFF12335Fu, 0xFF12335Fu, 0xFF1F4F79u, 0xFF245882u, 0xFF245882u, 0xFF1D4A74u, 0xFF12335Fu, 0xFF12335Fu, 0xFF183E6Du, 0xFF1A4473u, 0xFF12335Fu, 0xFF16406Fu, 0xFF2C8AC4u, 0xEE34ACEBu, 0xA431B7F5u, 0x2D0BCBFEu, 0x0400BFFFu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0100FFFFu, 0x1B00CFFEu, 0x6625BBF7u, 0xE335B0EFu, 0xF833A6E5u, 0xFF2981B9u, 0xFF194979u, 0xFF1E4979u, 0xFF173C6Bu, 0xFF133561u, 0xFF133561u, 0xFF133561u, 0xFF133561u, 0xFF133561u, 0xFF133561u, 0xFF163B68u, 0xFF1E4A7Bu, 0xFF17416Eu, 0xFF2572A9u, 0xFA32A5E3u, 0xE935B0EFu, 0x822DBAF7u, 0x2200D2FFu, 0x0300AAFFu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x1400CCFFu, 0x3D14C8FAu, 0x9930B7F4u, 0xEA35AFEEu, 0xF833A6E5u, 0xFF2D8EC9u, 0xFF2878AFu, 0xFF205C8Eu, 0xFF174170u, 0xFF133663u, 0xFF133663u, 0xFF153D6Bu, 0xFF205B8Eu, 0xFF2872A9u, 0xFF2B88C2u, 0xFA32A5E3u, 0xED34ADECu, 0xA931B8F5u, 0x4F1DC1FBu, 0x1800D4FFu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0400BFFFu, 0x1900D6FEu, 0x491BC3FBu, 0x932EB7F6u, 0xD133B0F0u, 0xED34ADECu, 0xF535AAE9u, 0xF935A8E8u, 0xF935A8E8u, 0xF935A8E8u, 0xF935A8E8u, 0xF634A9E9u, 0xEE34ACECu, 0xD735B0EFu, 0x9F30B6F5u, 0x5A21C0F9u, 0x1F00CDFEu, 0x0700DAFEu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0600D4FFu, 0x1400CCFFu, 0x2500CEFEu, 0x491BC3FBu, 0x7128BBF5u, 0x892EBAF7u, 0x8E30BAF7u, 0x8E30BAF7u, 0x8A2EBAF7u, 0x762BBCF8u, 0x5521C0F9u, 0x2600D0FEu, 0x1900D6FEu, 0x0800DFFFu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x0600D4FFu, 0x0B00D0FEu, 0x1200D4FEu, 0x1700D2FEu, 0x1700D2FEu, 0x1400CCFFu, 0x0B00D0FEu, 0x0700DAFEu, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
            0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 
        };
    }
}
