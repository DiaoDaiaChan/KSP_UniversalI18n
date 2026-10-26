using System;
using System.Collections.Generic;
using System.Text;

// 命名空间为什么不叫 UI18n.Core.Prefs：类名就叫 Prefs，而 UI18n.Core 下若同时存在
// 命名空间 Prefs 与类型 Prefs，任何 UI18n.Core.* 里的简单名 "Prefs" 都会先绑到命名空间
// （CS0118 "是命名空间，不能当作类型用"），Core / Runtime / 测试三处都要写全限定名。
// 文件路径保持任务书要求的 src/UI18n.Core/Prefs/，命名空间用 Settings 避开这个坑。
namespace UI18n.Core.Settings
{
    /// <summary>
    /// 单个 mod 的开关记录（<c>prefs.json</c> 里 <c>"mods":{"&lt;modId&gt;":{"enabled":true}}</c> 的一条）。
    /// <para>
    /// 为什么不用裸 <c>Dictionary&lt;string,bool&gt;</c>：每条记录还要留一个 <see cref="Unknown"/>
    /// 袋子装将来新增的键（例如 M3 的「只对我关掉的文件不投稿」），格式升级时老版本运行时不能把它们吃掉。
    /// </para>
    /// </summary>
    public sealed class ModPreference
    {
        /// <summary>是否启用该 mod 的包（true = 正常加载）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>本条目里未被本版本识别的键（原样保存 JSON 片段，写回时按名字 ordinal 排序）。</summary>
        public IDictionary<string, string> Unknown
        {
            get { return _unknown; }
        }

        private readonly Dictionary<string, string> _unknown = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>建一条默认（启用）记录。</summary>
        public ModPreference()
        {
        }

        /// <summary>按给定开关建记录。</summary>
        public ModPreference(bool enabled)
        {
            Enabled = enabled;
        }

        /// <summary>深拷贝（面板编辑用副本，取消编辑不影响已加载状态）。</summary>
        public ModPreference Clone()
        {
            ModPreference copy = new ModPreference(Enabled);
            foreach (KeyValuePair<string, string> pair in _unknown)
            {
                copy._unknown[pair.Key] = pair.Value;
            }

            return copy;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Enabled ? "enabled" : "disabled";
        }
    }

    /// <summary>
    /// 投稿同意状态（<c>prefs.json</c> 的 <c>submission</c> 段）。
    /// <para>
    /// M3 才真正用到，这里先把字段落地并把「未知子键」留住：同意是**玩家状态**，
    /// 一旦因为版本升级被清掉就会出现「没同意却上传」的合规事故，所以宁可写得多一点。
    /// </para>
    /// </summary>
    public sealed class SubmissionPrefs
    {
        /// <summary>是否已同意投稿（默认 false：不同意就不投稿，D2）。</summary>
        public bool Granted { get; set; }

        /// <summary>同意时依据的政策版本（0 = 未记录）。</summary>
        public int PolicyVersion { get; set; }

        /// <summary>最近一次投稿时间（ISO 8601，空串 = 从未投稿）。</summary>
        public string LastSubmittedAt { get; set; } = string.Empty;

        /// <summary>
        /// 同意时间（ISO 8601，空串 = 未记录）。写进 bundle 的 <c>consent.grantedAt</c>，
        /// 同时是「玩家在面板里做过明确决定」的证据（见 SubmissionPolicy.HasRecordedDecision）。
        /// </summary>
        public string GrantedAt { get; set; } = string.Empty;

        /// <summary>
        /// 客户端安装标识（32 位小写 hex，见 <c>UI18n.Core.Submission.InstallId</c>）。
        /// <para>
        /// 只用于服务端本地去重，<b>不是</b>硬件指纹：不许由 MAC / 用户名 / 路径 / 机器名派生（§6.2）。
        /// 旧档没有这个键时由运行时生成一次并写回（<c>Ensure</c>）。
        /// </para>
        /// </summary>
        public string InstallId { get; set; } = string.Empty;

        /// <summary>本段里未被本版本识别的键（原样 JSON 片段）。</summary>
        public IDictionary<string, string> Unknown
        {
            get { return _unknown; }
        }

        private readonly Dictionary<string, string> _unknown = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>深拷贝。</summary>
        public SubmissionPrefs Clone()
        {
            SubmissionPrefs copy = new SubmissionPrefs();
            copy.Granted = Granted;
            copy.PolicyVersion = PolicyVersion;
            copy.LastSubmittedAt = LastSubmittedAt;
            copy.GrantedAt = GrantedAt;
            copy.InstallId = InstallId;
            foreach (KeyValuePair<string, string> pair in _unknown)
            {
                copy._unknown[pair.Key] = pair.Value;
            }

            return copy;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Granted ? "granted" : "not-granted";
        }
    }

    /// <summary>
    /// 玩家状态文件的内存模型（<c>PluginData/UI18n/prefs.json</c>，契约见 docs/10 §10）。
    /// <para>
    /// 为什么单独一个文件而不是塞进 <c>Config.cfg</c>：<c>Config.cfg</c> 是 mod 自带的模板，
    /// 升级时会被整包覆盖（玩家状态会被抹掉）；prefs.json 是玩家数据，只由本运行时写。
    /// </para>
    /// <para>
    /// 三条硬语义（写错就会被当成 bug 报）：
    /// <list type="number">
    /// <item><b>没有记录 = 启用</b>（<see cref="IsEnabled"/>）。新装的 mod 不需要玩家点一下才生效，
    /// 只有显式 <c>"enabled":false</c> 才是关。</item>
    /// <item><b>未知字段原样保留</b>（<see cref="Unknown"/>）：老运行时读到新版本写的键不能丢，
    /// 否则升级一次就把玩家的投稿同意状态清掉了。</item>
    /// <item><b>大小写不敏感</b>：modId 来自文件名，Windows 上大小写不可靠；内部一律
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> 比较，写回时保留首次出现的原始拼写。</item>
    /// </list>
    /// </para>
    /// <para>本类纯粹是数据 + 纯函数式判定，<b>不做 IO、不抛异常、不依赖 UnityEngine</b>（T1 契约）。</para>
    /// </summary>
    public sealed class Prefs
    {
        /// <summary>当前 schema 标识（写文件用这个字符串）。</summary>
        public const string SchemaId = "ui18n-prefs/1";

        /// <summary>schema 的数字写法（docs/10 §10 早期示例写的是 <c>"schema":1</c>，读的时候两种都要认）。</summary>
        public const int SchemaNumber = 1;

        /// <summary>默认语言。</summary>
        public const string DefaultLanguage = "zh-cn";

        /// <summary>默认显示模式：显示译文。</summary>
        public const string ModeTranslated = "translated";

        /// <summary>默认显示模式：显示原文（保留给 M3 的「我先只看原文」）。</summary>
        public const string ModeOriginal = "original";

        /// <summary>默认显示模式值。</summary>
        public const string DefaultModeValue = ModeTranslated;

        private readonly Dictionary<string, ModPreference> _mods =
            new Dictionary<string, ModPreference>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _unknown = new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly List<string> _warnings = new List<string>();

        /// <summary>schema 标识。读到 <c>1</c> 会归一成 <see cref="SchemaId"/>，读到未知字符串则原样保留（前向兼容）。</summary>
        public string Schema { get; set; } = SchemaId;

        /// <summary>译文语言（小写连字符，例如 <c>zh-cn</c>）。</summary>
        public string Language { get; set; } = DefaultLanguage;

        /// <summary>显示模式（<see cref="ModeTranslated"/> / <see cref="ModeOriginal"/>）。</summary>
        public string DefaultMode { get; set; } = DefaultModeValue;

        /// <summary>投稿同意状态。</summary>
        public SubmissionPrefs Submission
        {
            get { return _submission; }
            set { _submission = value ?? new SubmissionPrefs(); }
        }

        private SubmissionPrefs _submission = new SubmissionPrefs();

        /// <summary>顶层未被本版本识别的键（原样 JSON 片段，写回时按名字 ordinal 排序）。</summary>
        public IDictionary<string, string> Unknown
        {
            get { return _unknown; }
        }

        /// <summary>
        /// 解析期的非致命告警（例如某条 mod 记录不是对象）。Runtime 汇总成一行 Warn，
        /// 不逐条刷屏（红线 6：失败只 log 不抛，但必须能被计数）。
        /// </summary>
        public IList<string> Warnings
        {
            get { return _warnings; }
        }

        /// <summary>建一份默认状态：无任何 mod 记录 ⇒ 全部启用。</summary>
        public static Prefs CreateDefault()
        {
            return new Prefs();
        }

        /// <summary>有记录的 mod 数量（不含 default = 启用 的隐含项）。</summary>
        public int ModCount
        {
            get { return _mods.Count; }
        }

        /// <summary>显式关掉的 mod 数量。</summary>
        public int DisabledCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<string, ModPreference> pair in _mods)
                {
                    if (!pair.Value.Enabled)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>所有有记录的 modId，ordinal 升序（日志与面板都用它，保证输出确定性）。</summary>
        public string[] GetModIds()
        {
            string[] ids = new string[_mods.Count];
            _mods.Keys.CopyTo(ids, 0);
            Array.Sort(ids, StringComparer.Ordinal);
            return ids;
        }

        /// <summary>显式关掉的 modId，ordinal 升序。</summary>
        public string[] DisabledModIds()
        {
            List<string> ids = new List<string>();
            foreach (KeyValuePair<string, ModPreference> pair in _mods)
            {
                if (!pair.Value.Enabled)
                {
                    ids.Add(pair.Key);
                }
            }

            ids.Sort(StringComparer.Ordinal);
            return ids.ToArray();
        }

        /// <summary>是否有该 mod 的记录（不区分大小写）。</summary>
        public bool Contains(string modId)
        {
            return !string.IsNullOrEmpty(modId) && _mods.ContainsKey(modId);
        }

        /// <summary>
        /// 该 mod 的显式开关：<c>null</c> = 没有记录（调用方按「启用」处理）。
        /// </summary>
        public bool? GetExplicit(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return null;
            }

            ModPreference pref;
            return _mods.TryGetValue(modId, out pref) ? pref.Enabled : (bool?)null;
        }

        /// <summary>
        /// <b>核心语义</b>：没有记录 = 启用；显式 <c>false</c> 才是关。
        /// <paramref name="modId"/> 为 null / 空串时返回 true（不认识的东西一律不拦，
        /// 宁可多加载一个包，也不要因为一个空字符串把整个字典关掉）。
        /// </summary>
        public bool IsEnabled(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return true;
            }

            ModPreference pref;
            return !_mods.TryGetValue(modId, out pref) || pref.Enabled;
        }

        /// <summary>设置开关（新建记录或改既有记录）。<paramref name="modId"/> 为空白时忽略。</summary>
        public void SetEnabled(string modId, bool enabled)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return;
            }

            ModPreference pref;
            if (!_mods.TryGetValue(modId, out pref))
            {
                _mods[modId] = new ModPreference(enabled);
                return;
            }

            pref.Enabled = enabled;
        }

        /// <summary>取记录（没有则 null），供面板读写未知子键。</summary>
        public ModPreference Get(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return null;
            }

            ModPreference pref;
            return _mods.TryGetValue(modId, out pref) ? pref : null;
        }

        /// <summary>
        /// 批量设置（面板的「全开 / 全关」用）。<paramref name="modIds"/> 为 null 时作用于已有记录。
        /// </summary>
        public void SetAll(IEnumerable<string> modIds, bool enabled)
        {
            if (modIds == null)
            {
                List<string> existing = new List<string>(_mods.Keys);
                for (int i = 0; i < existing.Count; i++)
                {
                    SetEnabled(existing[i], enabled);
                }

                return;
            }

            foreach (string id in modIds)
            {
                SetEnabled(id, enabled);
            }
        }

        /// <summary>深拷贝（面板编辑副本、测试隔离用）。</summary>
        public Prefs Clone()
        {
            Prefs copy = new Prefs();
            copy.Schema = Schema;
            copy.Language = Language;
            copy.DefaultMode = DefaultMode;
            copy._submission = Submission.Clone();
            foreach (KeyValuePair<string, ModPreference> pair in _mods)
            {
                copy._mods[pair.Key] = pair.Value.Clone();
            }

            foreach (KeyValuePair<string, string> pair in _unknown)
            {
                copy._unknown[pair.Key] = pair.Value;
            }

            for (int i = 0; i < _warnings.Count; i++)
            {
                copy._warnings.Add(_warnings[i]);
            }

            return copy;
        }

        /// <summary>一行诊断：<c>prefs: schema=ui18n-prefs/1, language=zh-cn, mods=97, disabled=4</c>。</summary>
        public string Describe()
        {
            StringBuilder sb = new StringBuilder(96);
            sb.Append("prefs: schema=").Append(string.IsNullOrEmpty(Schema) ? "(none)" : Schema);
            sb.Append(", language=").Append(string.IsNullOrEmpty(Language) ? "(none)" : Language);
            sb.Append(", mods=").Append(ModCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(", disabled=").Append(DisabledCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }
    }
}
