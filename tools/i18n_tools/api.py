r"""api.py -- 交互 API（docs/10）：译文唯一的写入口。

为什么要有这一层（docs/10 §1 的事故表）：packs/*.jsonl 过去是直接被文本编辑器和 agent 手写的，
代价真的发生过 —— 改 s 之后忘重算 h（运行期永远匹配不上）、占位符 <<1>> 少一个（进游戏
IndexOutOfRange 崩 UI）、同一包里同一个 h 出现两次（最后一行赢，行为不确定）、说不清哪条是模型
译的、两个 agent 各读整文件再写回（后写的吃掉前一个）。本模块把这些口子一次封掉：
写入必须带 门禁（G2–G9）+ revision 校验 + 审计事件；手改 JSON 的提交会被 journal_gate.py 红掉。

设计要点（改代码前先读这几条）：

1. 门禁**复用**既有实现，绝不重写。G2–G8 直接调 translate.gate()（它内部用 mask 的
   count_placeholders / count_formats / count_rich_tags / sentinel_multiset），G7 的锁定集合用
   translate.locked_for()（12 条专有约定 + 短语级官方术语），G9 用 skeleton.placeholders()。
   同一份代码既是生成门禁、也是写入门禁、也是 CI 门禁 —— 报告与文件同源，才不会有
   「报告说 PASS，文件被手改过」。
2. 原子写顺序：内存里改完 → 全量校验 → 写 <file>.tmp → 追加 journal 事件 → os.replace。
   journal 刻意放在 replace **之前**：docs/10 §5 规定「journal 追加失败视为写失败」，
   顺序反过来就会出现「文件已变、事件没写上」的窗口，那种状态下 journal_gate 与事实对不上。
3. revision = sha256(文件字节)[:12] + "@" + 行数（行数 = 非空行数）。写操作可以带
   --expect-revision，不匹配 → exit 3 并回传当前 revision。多 agent 并行时这是唯一的防覆盖手段。
4. --root 把全部读写关到一个根目录下（默认仓库根）：api 自检用它把真实 packs 完全隔离开，
   跑一整套写入测试，真实仓库一个字节都不会变。
5. 退出码是程序接口，语义冻结（docs/10 §3.4）：
   0 成功（含 --dry-run 全过）/ 2 用法错误 / 3 revision 冲突 / 4 门禁拒绝 /
   5 schema 非法 / 6 目标不存在 / 7 内部错误。

6. p（模板骨架标志）有**两种写法**：运行期 C# 的 JsonLineReader.IsPatternFlag 吃的是原始字符串，
   "p":1 与 "p":true 等价（存量 486 行是 translate_il.py 写的 "p":1，分布在 14 个 .il.jsonl 里）。
   所以读取侧容错（true / 1 / "1" / "true" 都当 True，见 coerce_pattern），写入侧只写布尔 true；
   validate 把旧表示算 notice（exit 0），--strict-p 才当 error；apply --normalize-p 走 put-batch
   同一条 commit 路径做一次性归一，并自证每包 h → (s,t,mod) 逐条不变、幂等（再跑 0 行）。

用法（**必须在 tools 目录下执行**：cd tools）：

    python -m i18n_tools.api pack list [--lang zh-cn] [--mod TUFX] [--json]
    python -m i18n_tools.api pack show --mod TUFX [--lang zh-cn] [--channel il|cfg] [--limit N] [--grep TEXT]
    python -m i18n_tools.api entry get (--h <h> | --text "<英文原文>") [--lang zh-cn] [--all]
    python -m i18n_tools.api entry search --text "<片段>" [--lang zh-cn] [--limit 50]
    python -m i18n_tools.api mod list [--json]
    python -m i18n_tools.api mod resolve (--assembly TUFX.dll | --pack TUFX.il)
    python -m i18n_tools.api journal tail [--n 20]
    python -m i18n_tools.api validate [--lang zh-cn] [--mod TUFX] [--strict] [--strict-p]
    python -m i18n_tools.api entry put --mod TUFX --lang zh-cn --text "Blue Paint" --t "蓝色涂装" --actor human:me
    python -m i18n_tools.api entry put-batch --actor pipeline:translate --stdin < batch.ndjson
    python -m i18n_tools.api entry delete --mod TUFX --lang zh-cn --h <h> --actor human:me [--reason why]
    python -m i18n_tools.api override put --en "<英文>" --zh "<中文>" --actor human:me [--note 依据]
    python -m i18n_tools.api glossary put --en "<英文>" --zh "<中文>" [--short] --actor human:me
    python -m i18n_tools.api mod put --mod TUFX --display TUFX [--assembly TUFX.dll] [--folder TUFX] [--ckan TUFX]
    python -m i18n_tools.api apply [--lang zh-cn] [--normalize-p] [--dry-run]
    python -m i18n_tools.api --selftest

全局 flag（写在动词前后都行）：--json 输出 envelope、--root 换仓库根、--actor 写操作必填。

envelope 约定（docs/10 §3.3）：单文件写操作严格按 §3.3 的字段；put-batch 这种一笔事务改多个文件时
额外带 files 明细（逐文件的 revision_before / revision_after / sha256 / bytes / journal 引用），
因为多文件共用一对 revision 说不清任何事。--json 的两种形态永远只有成功/失败两个顶层形状。

schema 与门禁之外的自家约束（docs/10 §4 末尾三条）：
  1. h 必须等于 fnv1a64(normalize(s))：同时给 --h 与 --s/--text 且不符 → exit 5；
  2. 同一包内 h 唯一（put 命中已存在行是**替换**不是追加，顺手把重复行收成一条）；
  3. t 为空 → 删除该行并记事件（不留空译文行，JsonLineReader 会判坏行）。

已知边界（写在这里是因为它会让人以为工具坏了）：
  * 一个 modId 可能同时有 <mod>.jsonl 与 <mod>.il.jsonl 两个包（实测 15 个 mod 是这样）。
    v1 的 entry put 动词面没有 --channel，所以目标文件按确定性规则选：
    ① h 已经出现在哪个包里 → 就是哪个包（改已有行永远落到原处）；
    ② 都没命中 → 优先 <mod>.jsonl（cfg 通道），只在它不存在时用 <mod>.il.jsonl；
    ③ 两个包都在且 h 都没命中 → 写 <mod>.jsonl 并给一条 warning（要写 IL 包请先命中已有行的 h）。
  * 本次不做（M2/M3 再说）：submission accept、manifest v2、运行时按 mod 开关。submit accept 这个
    动词位保留，调用会明确回 exit 2 并说明属 M3，不会被误当成能用。
"""
# === PART2 ===

# ---------------------------------------------------------------------------
# 依赖与输出卫生
# ---------------------------------------------------------------------------

import argparse
import contextlib
import datetime
import hashlib
import io
import json
import os
import re
import sys
import tempfile
from pathlib import Path

from .fnv import hash_text
from .textnorm import normalize
from . import skeleton as SK
from . import submission as SUB


def ensure_stdout():
    """Windows 控制台默认 GBK：不先切 UTF-8，打印中文会 UnicodeEncodeError 当场崩。

    另外 main() 在 --selftest 里会把 sys.stdout 换成 StringIO，那个对象没有 reconfigure，
    所以这里必须用 getattr 探测而不是直接调。
    """
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        with contextlib.suppress(Exception):
            reconfigure(encoding="utf-8", errors="replace")

# ---------------------------------------------------------------------------
# 退出码（docs/10 §3.4：这是程序接口，语义冻结，不许改）
# ---------------------------------------------------------------------------

EXIT_OK = 0
EXIT_USAGE = 2
EXIT_REVISION = 3
EXIT_GATE = 4
EXIT_SCHEMA = 5
EXIT_MISSING = 6
EXIT_INTERNAL = 7

#: 行内字段（schema 冻结，docs/10 §2）：多一个字段就要 exit 5，不然半年后没人记得它是什么。
ROW_KEYS = ("h", "n", "s", "t", "mod", "ctx", "p")
ROW_REQUIRED = ("h", "n", "s", "t", "mod")

#: journal 事件字段顺序（docs/10 §5），ts 在前是为了人读起来顺。
JOURNAL_KEYS = ("ts", "actor", "op", "lang", "mod", "h", "action", "file",
                "revision_before", "revision_after", "file_sha256_after",
                "bytes_after", "gates", "note")

#: 门禁编号（= translate.gate 的编号，不许改语义）
GATE_ORDER = ("G2", "G3", "G4", "G5", "G6", "G7", "G8", "G9")

#: actor 前缀：谁写的必须一眼看出来（手改 / 模型 / 流水线），审计与追责都靠它。
ACTOR_KINDS = ("human", "llm", "pipeline")

LANG = "zh-cn"

_H_RE = re.compile(r"^[0-9a-f]{16}$")
#: 两种包后缀（已知全集）。modId 的派生只剥这两个**精确**后缀，见 pack_mod 的注释。
_IL_SUFFIX = ".il.jsonl"
_CFG_SUFFIX = ".cfg.jsonl"
#: 去掉 .jsonl 之后还可能剩下这些 channel 后缀。
_CHANNELS = (".il", ".cfg")


class ApiError(Exception):
    """API 层的唯一异常类型；kind 决定退出码，message 直接给人看（中文）。"""

    EXIT = {
        "usage": EXIT_USAGE,
        "revision": EXIT_REVISION,
        "gate": EXIT_GATE,
        "schema": EXIT_SCHEMA,
        "missing": EXIT_MISSING,
        "internal": EXIT_INTERNAL,
    }
    #: 失败 envelope 里的 code（docs/10 §3.3 用字符串而不是数字，方便非 shell 调用方判）
    CODE = {
        "usage": "USAGE",
        "revision": "REVISION_CONFLICT",
        "gate": "GATE_REJECTED",
        "schema": "SCHEMA_INVALID",
        "missing": "NOT_FOUND",
        "internal": "INTERNAL",
    }

    def __init__(self, kind, message, details=None, code=None):
        super().__init__(message)
        self.kind = kind if kind in self.EXIT else "internal"
        self.message = message
        self.details = [str(item) for item in (details or [])]
        # 个别拒绝要报更具体的 code（docs/10 §6.6 的 submission-id-conflict 与
        # submission-id-mismatch），其余仍走 kind 的默认 code
        self._code = code

    @property
    def exit_code(self):
        return self.EXIT.get(self.kind, EXIT_INTERNAL)

    @property
    def code(self):
        return self._code or self.CODE.get(self.kind, "INTERNAL")


# ---------------------------------------------------------------------------
# 上下文与路径
# ---------------------------------------------------------------------------

def repo_root():
    """仓库根（默认 --root）：找 .git 向上退，退不出去就用 tools 的父目录。"""
    from .packindex import repo_root as _root
    return _root()


def default_root():
    return Path(repo_root())


class Ctx:
    """一次调用的上下文：根目录 + 语言 + 懒加载的术语表 + 待回传的 warning。

    所有路径都由 root 派生，所以 --root 指到临时目录时，读写完全不会碰到真实仓库
    （api --selftest 就是靠这一条做到「跑一整套写入测试，真实 packs 一个字节不动」）。
    """

    def __init__(self, root=None, lang=None):
        self.root = Path(root).resolve() if root else default_root()
        self.lang = lang or LANG
        self.warnings = []
        self._glossary = None
        self._glossary_tried = False

    # --- 路径 ---
    @property
    def packs_dir(self):
        return self.root / "packs"

    def lang_dir(self, lang=None):
        return self.packs_dir / (lang or self.lang)

    @property
    def journal_dir(self):
        return self.packs_dir / "journal"

    @property
    def overrides_path(self):
        return self.packs_dir / "overrides.jsonl"

    @property
    def glossary_path(self):
        return self.packs_dir / "glossary.jsonl"

    @property
    def conventions_path(self):
        return self.packs_dir / "conventions.jsonl"

    @property
    def mods_path(self):
        return self.packs_dir / "mods.json"

    @property
    def index_path(self):
        return self.packs_dir / "index.json"

    @property
    def pool_path(self):
        return self.packs_dir / "pool.jsonl"

    @property
    def reports_dir(self):
        return self.root / "reports"

    def rel(self, path):
        """相对仓库根的 posix 路径（journal / envelope 里一律写这种，git 与 CI 才认）。"""
        path = Path(path)
        try:
            return path.resolve().relative_to(self.root).as_posix()
        except (ValueError, OSError):
            return str(path)

    def warn(self, message):
        """warning 只累积、不阻断：进 envelope 的 warnings 与 stderr。"""
        self.warnings.append(message)

    # --- 术语表（G7 用；只在真正需要时付 0.11s） ---
    def glossary(self):
        if not self._glossary_tried:
            self._glossary_tried = True
            path = self.glossary_path
            if path.exists():
                tr = _translate()
                try:
                    self._glossary = tr.load_glossary(path)
                except Exception as exc:      # 术语表坏了不该让写入彻底停摆
                    self.warn("术语表读取失败（G7 将退化为只剩 12 条约定）：%s" % exc)
                    self._glossary = None
            else:
                self.warn("没有 packs/glossary.jsonl，G7 只判 12 条约定")
        return self._glossary


# ---------------------------------------------------------------------------
# 行 / 文件读写（字节级忠实：风格、换行、末尾换行都保持原样）
# ---------------------------------------------------------------------------

def sha256_hex(data):
    return hashlib.sha256(data).hexdigest()


def nonempty_lines(raw):
    return sum(1 for line in raw.split(b"\n") if line.strip())


def revision_of(raw):
    """revision = sha256(文件字节)[:12] + "@" + 非空行数；文件不存在 → missing@0。

    --expect-revision 比对的就是它：两个 agent 各读整个文件再写回时，后写的那一个会在这里被挡住。
    """
    if not raw:
        return "missing@0"
    return sha256_hex(raw)[:12] + "@" + str(nonempty_lines(raw))


def detect_style(raw):
    """原文件的书写风格：逗号/冒号后有没有空格、是不是纯 ASCII 的 \\u 转义。

    packs 里两种风格并存（0xx 是 ", " + ": "，IL 包是紧凑），整文件重写必须保持原风格，
    否则一次单行改动会在 diff 里变成 300 行 —— 评审就再也看不出到底改了什么。
    """
    if not raw:
        return {"spaced": False, "ascii": False}
    head = raw[:4096]
    first = head.split(b"\n", 1)[0]
    ascii_only = all(byte < 0x80 for byte in head)
    return {"spaced": b'": ' in first, "ascii": ascii_only and b"\\u" in head}


def dump_row(row, style):
    if style.get("spaced"):
        return json.dumps(row, ensure_ascii=style.get("ascii", False),
                          separators=(", ", ": "))
    return json.dumps(row, ensure_ascii=style.get("ascii", False), separators=(",", ":"))


def render_rows(rows, style):
    """行集 → 文件字节（末尾保留一个换行；空行集就是空文件）。"""
    if not rows:
        return b""
    return ("\n".join(dump_row(row, style) for row in rows) + "\n").encode("utf-8")


def rows_of_bytes(raw, label="packs 行"):
    """解析 JSONL；坏行直接 exit 5 —— 绝不把坏行当成好行继续写（那会把坏行抹掉）。"""
    rows = []
    text = raw.decode("utf-8") if isinstance(raw, (bytes, bytearray)) else str(raw or "")
    for lineno, line in enumerate(text.splitlines(), 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
        except ValueError as exc:
            raise ApiError("schema", "%s 第 %d 行不是合法 JSON：%s" % (label, lineno, exc))
        if not isinstance(row, dict):
            raise ApiError("schema", "%s 第 %d 行不是 JSON 对象" % (label, lineno))
        rows.append(row)
    return rows


def normalize_source(source):
    return normalize(source)


def hash_of(source):
    """行的 h：fnv1a64(normalize(s))（fnv.hash_text 不做归一化，归一化这一步必须在这里做）。"""
    return hash_text(normalize(source))


class FileBase:
    """一个文件在内存里的待写状态：原字节 + 目标字节 + 待记事件。"""

    def __init__(self, path, ctx):
        self.ctx = ctx
        self.path = Path(path)
        self.existed = self.path.exists()
        self.raw = self.path.read_bytes() if self.existed else b""
        self.style = detect_style(self.raw)
        self.events = []

    @property
    def rel(self):
        return self.ctx.rel(self.path)

    @property
    def mod(self):
        return None

    def revision(self):
        return revision_of(self.raw)

    def data(self):
        raise NotImplementedError

    def add_event(self, action, h=None, gates=None, note=""):
        self.events.append((h, action, list(gates or []), note or ""))

    def changed(self):
        return [{"file": self.rel, "h": h, "action": action}
                for (h, action, _gates, _note) in self.events]

    def all_gates(self):
        seen = []
        for (_h, _action, gates, _note) in self.events:
            for gate in gates:
                if gate not in seen:
                    seen.append(gate)
        return [gate for gate in GATE_ORDER if gate in seen]


class LinesFile(FileBase):
    """JSONL 资源文件（包、overrides、glossary）：行集进出，坏行不放过。"""

    def __init__(self, path, ctx, label=None):
        FileBase.__init__(self, path, ctx)
        self.rows = rows_of_bytes(self.raw, label or ctx.rel(self.path))

    def data(self):
        return render_rows(self.rows, self.style)


class PackFile(LinesFile):
    """一个包文件。文件不存在也能构造（put 会把它建出来）。"""

    def __init__(self, path, ctx):
        LinesFile.__init__(self, path, ctx, ctx.rel(path))

    @property
    def mod(self):
        return pack_mod(self.path.name)

    @property
    def channel(self):
        return pack_channel(self.path.name)

    def find(self, h):
        for index, row in enumerate(self.rows):
            if row.get("h") == h:
                return index
        return -1

    def find_text(self, source):
        target = normalize(source)
        for index, row in enumerate(self.rows):
            if row.get("n") == target:
                return index
        return -1


class DocFile(FileBase):
    """单文档 JSON（packs/mods.json）：缩进 2 写盘，diff 里仍是一行一个 modId。"""

    def __init__(self, path, ctx, default):
        FileBase.__init__(self, path, ctx)
        if self.existed and self.raw.strip():
            try:
                self.doc = json.loads(self.raw.decode("utf-8"))
            except ValueError as exc:
                raise ApiError("schema", "%s 不是合法 JSON：%s" % (ctx.rel(path), exc))
            if not isinstance(self.doc, dict):
                raise ApiError("schema", "%s 顶层必须是 JSON 对象" % ctx.rel(path))
        else:
            self.doc = json.loads(json.dumps(default))

    def data(self):
        return (json.dumps(self.doc, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def write_tmp(path, data):
    """写 <file>.tmp（同目录，保证 os.replace 是原子改名而不是跨盘拷贝）。"""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    try:
        with open(tmp, "wb") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
    except OSError as exc:
        with contextlib.suppress(OSError):
            tmp.unlink()
        raise ApiError("internal", "写临时文件失败 %s：%s" % (str(tmp), exc))
    return tmp


# ---------------------------------------------------------------------------
# journal（只追加；docs/10 §5）
# ---------------------------------------------------------------------------

def now_iso():
    return datetime.datetime.now().astimezone().isoformat(timespec="seconds")


def journal_path(ctx, ts):
    return ctx.journal_dir / ((ts or now_iso())[:10] + ".jsonl")


def journal_append(ctx, event):
    """追加一条事件，返回 packs/journal/<date>.jsonl#L<n>（行号 1 起）。

    只追加、永不改写历史行 —— journal_gate 会在 CI 上校验这一点（删改历史行 = 红）。
    """
    ts = event.get("ts") or now_iso()
    payload = dict(event)
    payload["ts"] = ts
    ordered = {key: payload[key] for key in JOURNAL_KEYS if key in payload}
    for key in payload:
        if key not in ordered:
            ordered[key] = payload[key]
    path = journal_path(ctx, ts)
    path.parent.mkdir(parents=True, exist_ok=True)
    line = json.dumps(ordered, ensure_ascii=False, separators=(",", ":"))
    before = 0
    if path.exists():
        before = nonempty_lines(path.read_bytes())
    try:
        with open(path, "a", encoding="utf-8", newline="\n") as handle:
            handle.write(line + "\n")
            handle.flush()
            os.fsync(handle.fileno())
    except OSError as exc:
        raise ApiError("internal", "journal 追加失败（journal 写不进去就整笔写失败）：%s" % exc)
    return "%s#L%d" % (ctx.rel(path), before + 1)


def make_event(actor, op, lang=None, mod=None, h=None, action=None, file=None,
               revision_before=None, revision_after=None, file_sha256_after=None,
               bytes_after=None, gates=None, note=""):
    return {
        "ts": now_iso(),
        "actor": actor,
        "op": op,
        "lang": lang,
        "mod": mod,
        "h": h,
        "action": action,
        "file": file,
        "revision_before": revision_before,
        "revision_after": revision_after,
        "file_sha256_after": file_sha256_after,
        "bytes_after": bytes_after,
        "gates": list(gates or []),
        "note": note or "",
    }


def journal_events(ctx, limit=None):
    """按日期升序读全部 journal 事件（每项带 _file 与 _line，方便回指）。"""
    events = []
    if not ctx.journal_dir.exists():
        return events
    for path in sorted(ctx.journal_dir.glob("*.jsonl")):
        raw = path.read_bytes()
        for lineno, line in enumerate(raw.decode("utf-8").splitlines(), 1):
            if not line.strip():
                continue
            try:
                event = json.loads(line)
            except ValueError:
                continue
            if isinstance(event, dict):
                event = dict(event)
                event["_file"] = ctx.rel(path)
                event["_line"] = lineno
                events.append(event)
    if limit is not None:
        events = events[-limit:]
    return events


def commit(ctx, files, actor, op, dry_run=False):
    """把一批文件的改动落地（顺序：tmp → journal → replace，见模块 docstring）。

    返回给 envelope 用的字典：changed / files 明细 / journal 引用 / gates / warnings。
    任何一个文件为空事件都跳过；dry_run 时只算不写（连 journal 都不写）。
    """
    staged = [item for item in files if item.events]
    if not staged:
        raise ApiError("usage", "没有要写入的变更（事件为空）")
    plan = []
    for item in staged:
        data = item.data()
        plan.append({
            "file": item,
            "data": data,
            "sha256": sha256_hex(data),
            "revision_after": revision_of(data),
            "revision_before": item.revision(),
            "refs": [],
        })
    gates = []
    for step in plan:
        for gate in step["file"].all_gates():
            if gate not in gates:
                gates.append(gate)
    gates = [gate for gate in GATE_ORDER if gate in gates]
    files_out = []
    if dry_run:
        for step in plan:
            item = step["file"]
            files_out.append({
                "file": item.rel,
                "revision_before": step["revision_before"],
                "revision_after": step["revision_after"],
                "sha256": step["sha256"],
                "bytes": len(step["data"]),
                "journal": None,
            })
        return {
            "changed": [entry for step in plan for entry in step["file"].changed()],
            "files": files_out,
            "journal": None,
            "gates": gates,
            "warnings": list(ctx.warnings),
            "dry_run": True,
        }

    tmps = []
    try:
        for step in plan:
            tmps.append(write_tmp(step["file"].path, step["data"]))
        for step in plan:
            item = step["file"]
            for (h, action, item_gates, note) in item.events:
                event = make_event(
                    actor, op, lang=ctx.lang, mod=item.mod, h=h, action=action,
                    file=item.rel, revision_before=step["revision_before"],
                    revision_after=step["revision_after"],
                    file_sha256_after=step["sha256"],
                    bytes_after=len(step["data"]), gates=item_gates, note=note,
                )
                step["refs"].append(journal_append(ctx, event))
        for tmp, step in zip(tmps, plan):
            os.replace(tmp, step["file"].path)
        tmps = []
    except OSError as exc:
        raise ApiError("internal", "落盘失败（已回滚临时文件）：%s" % exc)
    finally:
        for tmp in tmps:
            with contextlib.suppress(OSError):
                tmp.unlink()

    for step in plan:
        item = step["file"]
        files_out.append({
            "file": item.rel,
            "revision_before": step["revision_before"],
            "revision_after": step["revision_after"],
            "sha256": step["sha256"],
            "bytes": len(step["data"]),
            "journal": step["refs"][0] if step["refs"] else None,
        })
    refs = [ref for step in plan for ref in step["refs"]]
    return {
        "changed": [entry for step in plan for entry in step["file"].changed()],
        "files": files_out,
        "journal": refs[0] if refs else None,
        "journals": refs,
        "gates": gates,
        "warnings": list(ctx.warnings),
    }
# === PART3 ===

# ---------------------------------------------------------------------------
# 包路径与 modId
# ---------------------------------------------------------------------------

def iter_pack_files(ctx, lang=None):
    """<root>/packs/<lang>/*.jsonl 按文件名升序（确定性：同一状态每次扫出同样顺序）。"""
    directory = ctx.lang_dir(lang)
    if not directory.is_dir():
        return []
    return sorted(directory.glob("*.jsonl"))


def pack_mod(name):
    """包文件名 → modId（docs/10 口径，与 packindex._mod_of_name 同源）。

    规则只有三步：①去末尾 .jsonl；②若剩下部分以 **精确** 后缀 .il 或 .cfg 结尾，只去掉它；
    ③其余原样就是 modId。97 个包文件 → 82 个 modId，其中 15 个 modId 同时有 cfg 与 IL 两个包。

    为什么要强调「精确后缀」：modId 本身可以含点号（实测 AtmosphereAutopilot.UI.il.jsonl 的
    modId 是 AtmosphereAutopilot.UI），所以「剥掉最后一个点号段」是错的；而 .il / .cfg 是已知
    全集的 channel 后缀，剥它们不会有歧义。
    """
    base = str(name)
    for suffix in (".jsonl",) + _CHANNELS:
        if base.endswith(suffix):
            base = base[: -len(suffix)]
    return base


def pack_channel(name):
    """<mod>.il.jsonl → "il"；<mod>.jsonl 与 <mod>.cfg.jsonl 都算 "cfg" 通道。"""
    return "il" if str(name).endswith(_IL_SUFFIX) else "cfg"


def pack_paths(ctx, mod, lang=None):
    """该 modId 的所有包文件：cfg 在前、IL 在后，同 channel 内按文件名升序。"""
    found = [path for path in iter_pack_files(ctx, lang) if pack_mod(path.name) == mod]
    return sorted(found, key=lambda path: (pack_channel(path.name) != "cfg", path.name))


def scan(ctx, lang=None):
    """扫描包目录（复用 packindex.scan_packs，索引与 API 的统计口径必须同源）。"""
    from .packindex import scan_packs
    lang = lang or ctx.lang
    if not ctx.lang_dir(lang).is_dir():
        return []
    return scan_packs(str(ctx.packs_dir), lang)


def choose_target(ctx, mod, h, lang=None):
    """entry put 的目标文件规则（见模块 docstring 的「已知边界」三条）。

    ① h 已在某个包里 → 就写那个包（改已有行永远落回原处，不会把 IL 包的条目挪进 cfg 包）；
    ② 都没命中：优先 <mod>.jsonl，只有它不存在才用 <mod>.il.jsonl；
    ③ 两个包都在且都没命中 → 写 cfg 包并给 warning。
    """
    candidates = pack_paths(ctx, mod, lang)
    for path in candidates:
        pack = PackFile(path, ctx)
        if pack.find(h) >= 0:
            return pack
    if not candidates:
        return PackFile(ctx.lang_dir(lang) / (mod + ".jsonl"), ctx)
    if len(candidates) > 1:
        ctx.warn("%s 同时有 %s 与 %s，h=%s 两个包里都没有 → 按约定写 cfg 包；"
                 "要写 IL 包请用 IL 包里已有的 h"
                 % (mod, candidates[0].name, candidates[1].name, h))
    return PackFile(candidates[0], ctx)


def choose_target_cached(ctx, cache, primary, mod, h, lang=None):
    """put-batch 用：同一批里对同一 mod 只打开一次文件，否则后面的行会覆盖前面的改动。"""
    for pack in cache.values():
        if pack.mod == mod and pack.find(h) >= 0:
            return pack
    if mod in primary:
        return primary[mod]
    pack = choose_target(ctx, mod, h, lang)
    cache[pack.rel] = pack
    primary[mod] = pack
    return pack


# ---------------------------------------------------------------------------
# 参数与 schema 校验
# ---------------------------------------------------------------------------

def need_actor(args):
    """写操作的身份证：human:<名字> / llm:<模型> / pipeline:<脚本>。缺了就没法追责。"""
    actor = str(getattr(args, "actor", "") or "").strip()
    if not actor:
        raise ApiError("usage", "写操作必须带 --actor（human:<名字> / llm:<模型> / pipeline:<脚本>），"
                                "缺了它 journal 就说不清是谁改的")
    if ":" not in actor:
        raise ApiError("usage", "--actor 必须带前缀冒号：human:<名字> / llm:<模型> / pipeline:<脚本>")
    kind, name = actor.split(":", 1)
    if kind not in ACTOR_KINDS:
        raise ApiError("usage", "--actor 前缀只能是 human / llm / pipeline，收到 %r" % kind)
    if not name.strip():
        raise ApiError("usage", "--actor 冒号后面不能是空的")
    return actor


def need_value(value, flag):
    if value is None or str(value) == "":
        raise ApiError("usage", "缺少 %s" % flag)
    return str(value)


#: p 字段的旧表示（历史产物：translate_il.py 与 v1.3 运行期文档写的是 "p":1）。
#: 读取侧一律当 True —— 运行期 C# 的 IsPatternFlag 也只吃 "1" 与 "true" 两种原始字符串。
PATTERN_TRUE_STRINGS = ("1", "true")
PATTERN_FALSE_STRINGS = ("0", "false", "")


def coerce_pattern(value, label="行"):
    """p 字段的读取侧容错（docs/10 §2）：true / 1 / "1" / "true" 都是 True。

    为什么必须有它：486 行既有骨架行是 "p":1（translate_il.py 的历史写法），而 M1 的写入侧
    从今天起只写布尔 true。两套表示必须能同时读，否则 validate / apply 在真实仓库上永远红。
    """
    if value is None:
        return False
    if isinstance(value, bool):
        return value
    if isinstance(value, int):
        if value in (0, 1):
            return bool(value)
        raise ApiError("schema", "%s：p 只能是 true / 1 / \"1\" / \"true\"（收到整数 %r）"
                       % (label, value))
    if isinstance(value, str):
        low = value.strip().lower()
        if low in PATTERN_TRUE_STRINGS:
            return True
        if low in PATTERN_FALSE_STRINGS:
            return False
    raise ApiError("schema", "%s：p 只能是 true / 1 / \"1\" / \"true\"（收到 %r）" % (label, value))


def pattern_is_legacy(value):
    """p 用了旧表示（不是布尔）→ validate 记 notice；apply --normalize-p 负责归一。"""
    return value is not None and not isinstance(value, bool)


def check_row_schema(row, label, notices=None):
    """一条包行的 schema（docs/10 §2 冻结）：字段集合、类型、h 与 n 的一致性。

    notices 传列表时，p 用旧表示（"p":1 / "1" / "true"）会往里记一条 —— validate 把它算
    notice 而不是 error（存量 486 行是历史写法，为了它把仓库判红挡住 apply 是本末倒置）。
    """
    if not isinstance(row, dict):
        raise ApiError("schema", "%s 不是 JSON 对象" % label)
    extra = [key for key in row if key not in ROW_KEYS]
    if extra:
        raise ApiError("schema", "%s：未知字段 %s（schema 冻结，只允许 %s）"
                       % (label, ", ".join(sorted(extra)), ", ".join(ROW_KEYS)))
    missing = [key for key in ROW_REQUIRED if key not in row]
    if missing:
        raise ApiError("schema", "%s：缺字段 %s" % (label, ", ".join(missing)))
    for key in ("n", "s", "t", "mod"):
        if not isinstance(row[key], str):
            raise ApiError("schema", "%s：字段 %s 必须是字符串" % (label, key))
    if "ctx" in row and not isinstance(row["ctx"], str):
        raise ApiError("schema", "%s：ctx 必须是字符串" % label)
    if "p" in row:
        coerce_pattern(row["p"], label)          # 非法值 → exit 5（true / 1 / "1" / "true" 都收）
        if notices is not None and pattern_is_legacy(row["p"]):
            notices.append("%s：p 用了旧表示 %r（等价 true；apply --normalize-p 可归一）"
                           % (label, row["p"]))
    if not isinstance(row["h"], str) or not _H_RE.match(row["h"]):
        raise ApiError("schema", "%s：h 必须是 16 位小写十六进制，收到 %r" % (label, row["h"]))
    if not row["t"].strip():
        raise ApiError("schema", "%s：t 不能为空（删一行请用 entry delete）" % label)
    expected_n = normalize_source(row["s"])
    if row["n"] != expected_n:
        raise ApiError("schema", "%s：n 必须等于 normalize(s)（期望 %r，实际 %r）"
                       % (label, expected_n, row["n"]))
    expected_h = hash_of(row["s"])
    if row["h"] != expected_h:
        raise ApiError("schema", "%s：h 与原文不符（期望 %s，实际 %s）—— 改过 s 却忘了重算 h"
                       % (label, expected_h, row["h"]))


# ---------------------------------------------------------------------------
# 门禁（G2–G9；实现一律调既有代码，见模块 docstring 第 1 条）
# ---------------------------------------------------------------------------

_TR = None


def _translate():
    """懒加载 translate：它顶部 import .llm/.report，0.07s 的代价只在真要跑门禁时付。"""
    global _TR
    if _TR is None:
        from . import translate as module
        _TR = module
    return _TR


def gates_for(ctx, source, zh, pattern=False, subset=None):
    """跑门禁，返回 {"ran": [...], "failed": [...], "notes": {...}, "restored": str}。

    G2–G8 直接调 translate.gate（内部就是 mask 的四个计数器 + locked_for），本函数只补两件事：

      1. **G8 的 keep 例外**：短标签原样抄回（型号 / 标识符 / 配置键）是正确行为，
         translate.echo_keep 判真就把 G8 从 failed 里摘掉 —— 那一行以 t==s 落库，
         运行时 DictEntry.IsKeep 直接终止下沉，正是设计要的结果。
      2. **G9**：只对 p:true（模板骨架）行判 # 占位符个数，用 skeleton.placeholders
         （TMP 标签里的 # 不算，避免把 color=#FF0000 当成占位符）。

    subset 只用于人工入口（override / glossary）：那两个文件是手调过的短词与专名，
    G6 长度比与 G7 术语必然误报，只跑机械上不能错的 G2/G3/G4/G5。
    """
    tr = _translate()
    locked = tr.locked_for(source, ctx.glossary())
    restored, failed, notes = tr.gate({"row": {"text": source}, "res": None}, zh, locked)
    failed = list(failed)
    notes = dict(notes)
    if "G8" in failed and tr.echo_keep(source, restored):
        failed = [code for code in failed if code != "G8"]
        notes.pop("G8", None)
    ran = ["G5", "G2", "G3", "G4", "G6", "G7", "G8"]
    if pattern:
        ran.append("G9")
        want = SK.placeholders(source)
        got = SK.placeholders(restored)
        if want != got:
            failed.append("G9")
            notes["G9"] = "# 占位符 期望%d 实际%d" % (want, got)
    if subset:
        keep = set(subset)
        ran = [code for code in ran if code in keep]
        failed = [code for code in failed if code in keep]
        notes = {code: text for code, text in notes.items() if code in keep}
    ran = [code for code in GATE_ORDER if code in ran]
    failed = [code for code in GATE_ORDER if code in failed]
    return {"ran": ran, "failed": failed, "notes": notes, "restored": restored}


#: 人工入口（override / glossary）只跑机械门禁：手调过的短词与专名，G6 长度比与 G7 术语必然误报。
MANUAL_GATES = ("G2", "G3", "G4", "G5")


def gate_check(ctx, source, zh, pattern=False, subset=None):
    """门禁闸门：不过就 exit 4，details 里逐条给「Gx 说明」。返回跑过的门禁编号。"""
    result = gates_for(ctx, source, zh, pattern=pattern, subset=subset)
    if result["failed"]:
        details = ["%s %s" % (code, result["notes"].get(code, "")) for code in result["failed"]]
        raise ApiError("gate", "门禁拒绝（%s）：%s" % ("、".join(result["failed"]), details[0]),
                       details)
    return result["ran"]


# ---------------------------------------------------------------------------
# 读动词
# ---------------------------------------------------------------------------

def cmd_pack_list(ctx, args):
    lang = getattr(args, "lang", None) or ctx.lang
    entries = scan(ctx, lang)
    want = getattr(args, "mod", None)
    if want:
        entries = [entry for entry in entries if entry["mod"] == want]
    packs = [{
        "mod": entry["mod"],
        "channel": pack_channel(entry["file"]),
        "file": entry["file"],
        "rows": entry["rows"],
        "bytes": entry["bytes"],
        "keepRows": entry.get("keepRows", 0),
        "sha256": entry.get("sha256"),
    } for entry in entries]
    payload = {
        "op": "pack.list", "ok": True, "lang": lang,
        "packCount": len(packs),
        "totalRows": sum(item["rows"] for item in packs),
        "totalBytes": sum(item["bytes"] for item in packs),
        "packs": packs,
    }
    lines = ["%-22s %-3s %6d 行 %8d B  %s" % (item["mod"], item["channel"], item["rows"],
                                              item["bytes"], item["file"]) for item in packs]
    lines.append("合计 %d 个包 / %d 行 / %d 字节" % (payload["packCount"], payload["totalRows"],
                                                 payload["totalBytes"]))
    return payload, lines


def cmd_pack_show(ctx, args):
    mod = need_value(getattr(args, "mod", None), "--mod")
    lang = getattr(args, "lang", None) or ctx.lang
    want_channel = getattr(args, "channel", None)
    paths = pack_paths(ctx, mod, lang)
    if want_channel:
        paths = [path for path in paths if pack_channel(path.name) == want_channel]
    if not paths:
        raise ApiError("missing", "没有这个包：mod=%s lang=%s channel=%s" % (mod, lang, want_channel or "*"))
    limit = getattr(args, "limit", None) or 0
    grep = getattr(args, "grep", None)
    packs, lines, total = [], [], 0
    for path in paths:
        pack = PackFile(path, ctx)
        rows = pack.rows
        if grep:
            rows = [row for row in rows if grep in row.get("s", "") or grep in row.get("t", "")]
        if limit:
            rows = rows[:limit]
        total += len(rows)
        packs.append({"file": pack.rel, "rows": len(rows), "revision": pack.revision(),
                      "items": rows})
        lines.append("%s  %d 行  revision=%s" % (pack.rel, len(rows), pack.revision()))
        for row in rows:
            lines.append("  %s  %s => %s" % (row.get("h", ""), row.get("s", ""), row.get("t", "")))
    payload = {"op": "pack.show", "ok": True, "lang": lang, "mod": mod,
               "rows": total, "packs": packs}
    return payload, lines


def cmd_entry_get(ctx, args):
    lang = getattr(args, "lang", None) or ctx.lang
    want_h = getattr(args, "h", None)
    text = getattr(args, "text", None)
    if not want_h and text is None:
        raise ApiError("usage", "entry get 需要 --h 或 --text")
    hits = []
    for path in iter_pack_files(ctx, lang):
        pack = PackFile(path, ctx)
        for row in pack.rows:
            if want_h and row.get("h") != want_h:
                continue
            if text is not None and row.get("s") != text and row.get("n") != normalize_source(text):
                continue
            hits.append({"file": pack.rel, "channel": pack.channel, "row": row})
    if not hits:
        raise ApiError("missing", "没有命中：%s" % (want_h or text))
    if not getattr(args, "all", False):
        hits = hits[:1]
    payload = {"op": "entry.get", "ok": True, "lang": lang, "count": len(hits), "hits": hits}
    lines = ["%s  %s => %s  (%s)" % (hit["row"].get("h", ""), hit["row"].get("s", ""),
                                     hit["row"].get("t", ""), hit["file"]) for hit in hits]
    return payload, lines


def cmd_entry_search(ctx, args):
    needle = need_value(getattr(args, "text", None), "--text")
    lang = getattr(args, "lang", None) or ctx.lang
    limit = getattr(args, "limit", None) or 50
    hits = []
    for path in iter_pack_files(ctx, lang):
        pack = PackFile(path, ctx)
        for row in pack.rows:
            if needle in row.get("s", "") or needle in row.get("t", ""):
                hits.append({"file": pack.rel, "row": row})
                if len(hits) >= limit:
                    break
        if len(hits) >= limit:
            break
    payload = {"op": "entry.search", "ok": True, "lang": lang, "needle": needle,
               "count": len(hits), "hits": hits}
    lines = ["%s  %s => %s  (%s)" % (hit["row"].get("h", ""), hit["row"].get("s", ""),
                                     hit["row"].get("t", ""), hit["file"]) for hit in hits]
    lines.append("共 %d 条（上限 %d）" % (len(hits), limit))
    return payload, lines


def load_mods(ctx, required=False):
    """读 packs/mods.json；不存在时按 required 决定是报错还是当空表。"""
    path = ctx.mods_path
    if not path.exists():
        if required:
            raise ApiError("missing", "没有 packs/mods.json（先跑一次 mod put 或用 mods.json 引导）")
        return {"schema": 1, "mods": []}
    doc = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(doc, dict):
        raise ApiError("schema", "packs/mods.json 顶层必须是 JSON 对象")
    mods = doc.get("mods")
    if not isinstance(mods, list):
        raise ApiError("schema", "packs/mods.json 的 mods 必须是数组")
    return doc


def cmd_mod_list(ctx, args):
    doc = load_mods(ctx, required=False)
    entries = scan(ctx, ctx.lang)
    counts = {}
    for entry in entries:
        counts[entry["mod"]] = counts.get(entry["mod"], 0) + entry["rows"]
    mods = []
    for item in doc.get("mods") or []:
        item = dict(item)
        item["rows"] = counts.get(item.get("modId"), 0)
        mods.append(item)
    known = {item.get("modId") for item in mods}
    unregistered = sorted(mod for mod in counts if mod not in known)
    if unregistered:
        ctx.warn("有 %d 个包里的 modId 还没登记进 packs/mods.json：%s"
                 % (len(unregistered), ", ".join(unregistered[:8]) + ("…" if len(unregistered) > 8 else "")))
    payload = {"op": "mod.list", "ok": True, "count": len(mods), "mods": mods,
               "unregistered": unregistered}
    lines = ["%-24s %-28s %6d 行  ckan=%s" % (item.get("modId"), item.get("displayName"),
                                              item.get("rows", 0), item.get("ckan"))
             for item in mods]
    lines.append("合计 %d 个 mod（未登记 %d 个）" % (len(mods), len(unregistered)))
    return payload, lines


def _mod_key(target):
    """把 --assembly / --pack 的入参归一成可比的名字：去路径、去 .jsonl / .il。"""
    name = Path(str(target).replace("\\", "/")).name
    for suffix in (".jsonl",) + _CHANNELS:
        if name.endswith(suffix):
            name = name[: -len(suffix)]
    return name


def cmd_mod_resolve(ctx, args):
    doc = load_mods(ctx, required=True)
    assembly = getattr(args, "assembly", None)
    pack = getattr(args, "pack", None)
    if not assembly and not pack:
        raise ApiError("usage", "mod resolve 需要 --assembly <X.dll> 或 --pack <mod[.il]>")
    wanted = _mod_key(assembly or pack)
    plain = Path(str(assembly or pack).replace("\\", "/")).name
    for item in doc.get("mods") or []:
        names = set()
        names.add(str(item.get("modId") or ""))
        names.add(str(item.get("displayName") or ""))
        if item.get("ckan"):
            names.add(str(item["ckan"]))
        for key in ("assemblies", "folders", "aliases"):
            for value in item.get(key) or []:
                names.add(str(value))
                names.add(Path(str(value).replace("\\", "/")).name)
        if wanted in names or plain in names:
            payload = {"op": "mod.resolve", "ok": True, "input": assembly or pack, "mod": item}
            lines = ["%s -> modId=%s displayName=%s" % (assembly or pack, item.get("modId"),
                                                       item.get("displayName"))]
            return payload, lines
    raise ApiError("missing", "mods.json 里没有能解析 %r 的条目（用 api mod put 登记它）"
                   % (assembly or pack))


def cmd_journal_tail(ctx, args):
    count = getattr(args, "n", None) or 20
    events = journal_events(ctx, limit=int(count))
    payload = {"op": "journal.tail", "ok": True, "count": len(events), "events": events}
    lines = []
    for event in events:
        lines.append("%s  %-10s %-18s %s  %s  %s" % (event.get("ts"), event.get("actor"),
                                                     event.get("op"), event.get("file"),
                                                     event.get("action") or "-",
                                                     event.get("h") or "-"))
    if not lines:
        lines.append("（还没有 journal 事件）")
    return payload, lines


def cmd_validate(ctx, args):
    """全量体检：schema、h/n/s 一致性、同包 h 唯一、mod 身份；--strict 再加机械门禁。

    故意在 --strict 里**不跑 G6/G7/G8**：术语与长度要不要接受得人判，而 1.5 万行逐条跑
    locked_for（800 个术语 × 3 正则）会把 validate 变成分钟级。这三条由生成流程与 CI 保证。
    """
    lang = getattr(args, "lang", None) or ctx.lang
    only_mod = getattr(args, "mod", None)
    strict = bool(getattr(args, "strict", False))
    strict_p = bool(getattr(args, "strict_p", False))
    problems = []
    notices = []             # p 用旧表示（"p":1）的行 —— 默认只是 notice，--strict-p 才当 error
    stats = {"packs": 0, "rows": 0, "keepRows": 0, "unregistered": 0}
    mods = None
    if ctx.mods_path.exists():
        try:
            mods = load_mods(ctx, required=False)
        except ApiError as exc:
            problems.append("packs/mods.json: " + exc.message)
    known = {item.get("modId") for item in (mods or {}).get("mods") or []}
    for path in iter_pack_files(ctx, lang):
        pack = None
        try:
            pack = PackFile(path, ctx)
        except ApiError as exc:
            problems.append(exc.message)
            continue
        if only_mod and pack.mod != only_mod:
            continue
        stats["packs"] += 1
        if mods is not None and pack.mod not in known:
            stats["unregistered"] += 1
            if strict:
                problems.append("%s：modId %s 没登记进 packs/mods.json" % (pack.rel, pack.mod))
        seen = {}
        for lineno, row in enumerate(pack.rows, 1):
            stats["rows"] += 1
            label = "%s 第 %d 行" % (pack.rel, lineno)
            try:
                check_row_schema(row, label, notices)
            except ApiError as exc:
                problems.append(exc.message)
                continue
            if row.get("t") == row.get("s"):
                stats["keepRows"] += 1
            if row["h"] in seen:
                problems.append("%s：h 重复（与第 %d 行同 h），运行期只能靠最后一行赢" %
                                (label, seen[row["h"]]))
            else:
                seen[row["h"]] = lineno
            if strict:
                result = gates_for(ctx, row["s"], row["t"],
                                   pattern=coerce_pattern(row.get("p"), label),
                                   subset=("G2", "G4", "G9"))
                for code in result["failed"]:
                    problems.append("%s：%s %s" % (label, code, result["notes"].get(code, "")))
    if strict_p:
        problems.extend(notices)        # --strict-p：旧表示按 error 算（归一后拿它自证）
    payload = {"op": "validate", "ok": not problems, "lang": lang,
               "packs": stats["packs"], "rows": stats["rows"], "keepRows": stats["keepRows"],
               "strict": strict, "strictP": strict_p, "pLegacy": len(notices),
               "problems": problems, "notices": notices}
    lines = ["检查 %d 个包 / %d 行（keep %d）%s" % (stats["packs"], stats["rows"],
                                                 stats["keepRows"], "，含机械门禁" if strict else "")]
    if notices:
        lines.append("notice: p 旧表示 %d 行（等值，可 apply --normalize-p 归一）" % len(notices))
    if problems:
        for problem in problems[:40]:
            lines.append("  [问题] " + problem)
        if len(problems) > 40:
            lines.append("  …… 另有 %d 条" % (len(problems) - 40))
        raise ApiError("schema", "validate 发现 %d 个问题" % len(problems), lines)
    lines.append("全部通过")
    return payload, lines
# === PART4 ===

# ---------------------------------------------------------------------------
# 写动词
# ---------------------------------------------------------------------------

def source_for(ctx, args, lang=None):
    """把 --text / --s / --h 归一成 (原文, h)，并把「h 与原文不符」这条 schema 校验做掉。"""
    text = getattr(args, "text", None)
    raw_s = getattr(args, "s", None)
    if text is not None and raw_s is not None and text != raw_s:
        raise ApiError("usage", "--text 与 --s 指的是同一个东西（原文），不能给两个不同的值")
    source = text if text is not None else raw_s
    want_h = getattr(args, "h", None)
    if source is None:
        if not want_h:
            raise ApiError("usage", "必须给 --text（原文）或 --h")
        for path in iter_pack_files(ctx, lang):
            pack = PackFile(path, ctx)
            index = pack.find(want_h)
            if index >= 0:
                return pack.rows[index]["s"], want_h
        raise ApiError("schema", "给了 --h=%s 但库里没有这一行，也没有 --text 可以校验它"
                       % want_h)
    computed = hash_of(source)
    if want_h and want_h != computed:
        raise ApiError("schema", "--h 与原文不符：给的是 %s，按 fnv1a64(normalize(s)) 算出 %s"
                       % (want_h, computed))
    return source, computed


def build_row(ctx, mod, source, zh, h, ctx_note=None, pattern=False):
    """构造一行并跑门禁，返回 (row, keep, gates)；zh 为空串表示删除，返回 row=None。"""
    if zh == "":
        return None, False, []
    row = {"h": h, "n": normalize_source(source), "s": source, "t": zh, "mod": mod}
    if ctx_note:
        row["ctx"] = ctx_note
    if pattern:
        row["p"] = True
    gates = gate_check(ctx, source, zh, pattern=pattern)
    keep = zh.strip() == source.strip() or _translate().echo_keep(source, zh)
    return row, keep, gates


def apply_put(pack, row, gates, note=""):
    """把一行放进包：同 h 是**替换**不是追加（顺手把重复 h 收成一条）。返回 action。"""
    h = row["h"]
    if pack.find(h) >= 0:
        new_rows, placed = [], False
        for existing in pack.rows:
            if existing.get("h") == h:
                if not placed:
                    new_rows.append(row)
                    placed = True
                continue
            new_rows.append(existing)
        pack.rows = new_rows
        action = "replace"
    else:
        pack.rows.append(row)
        action = "insert"
    pack.add_event(action, h=h, gates=gates, note=note)
    return action


def revision_check(pack, expect):
    """--expect-revision 不匹配 → exit 3，并把当前 revision 回传（调用方立刻能重读重试）。"""
    if not expect:
        return
    current = pack.revision()
    if str(expect) != current:
        raise ApiError("revision", "revision 冲突：期望 %s，当前 %s（先重读再写）"
                       % (expect, current), [current])


def envelope(ctx, op, result, lang=None, mod=None):
    """成功 envelope（docs/10 §3.3）。多文件写操作额外带 files 明细（见模块 docstring）。"""
    files = result.get("files") or []
    return {
        "ok": True,
        "op": op,
        "lang": lang,
        "mod": mod,
        "revision_before": files[0]["revision_before"] if files else None,
        "revision_after": files[0]["revision_after"] if files else None,
        "changed": result.get("changed") or [],
        "gates": result.get("gates") or [],
        "warnings": result.get("warnings") or [],
        "journal": result.get("journal"),
        "files": files,
        "dry_run": bool(result.get("dry_run")),
    }


def _put_lines(payload, result):
    lines = []
    for item in result.get("changed") or []:
        lines.append("%-8s %s  %s" % (item["action"], item["file"], item["h"] or "-"))
    for item in result.get("files") or []:
        lines.append("%s  %s -> %s  %d 字节" % (item["file"], item["revision_before"],
                                              item["revision_after"], item["bytes"]))
    if result.get("journal"):
        lines.append("journal: " + str(result["journal"]))
    if result.get("dry_run"):
        lines.append("（--dry-run：没有写盘、也没有写 journal）")
    for message in result.get("warnings") or []:
        lines.append("warning: " + message)
    return lines


def cmd_entry_put(ctx, args):
    actor = need_actor(args)
    mod = need_value(getattr(args, "mod", None), "--mod")
    lang = getattr(args, "lang", None) or ctx.lang
    if getattr(args, "t", None) is None:
        raise ApiError("usage", "entry put 必须给 --t（空字符串表示删掉这一行）")
    source, h = source_for(ctx, args, lang)
    pattern = bool(getattr(args, "p", False))
    row, keep, gates = build_row(ctx, mod, source, args.t, h,
                                 ctx_note=getattr(args, "ctx", None), pattern=pattern)
    target = choose_target(ctx, mod, h, lang)
    revision_check(target, getattr(args, "expect_revision", None))
    note = getattr(args, "note", "") or ""
    if keep:
        note = ("keep（译文与原文相同，运行时按 IsKeep 终止下沉）" + ("；" + note if note else ""))
    if row is None:
        index = target.find(h)
        if index < 0:
            raise ApiError("missing", "要删的行不存在：%s 里没有 h=%s" % (target.rel, h))
        del target.rows[index]
        target.add_event("delete", h=h, note=note)
    else:
        apply_put(target, row, gates, note)
    result = commit(ctx, [target], actor, "entry.put",
                    dry_run=bool(getattr(args, "dry_run", False)))
    payload = envelope(ctx, "entry.put", result, lang=lang, mod=mod)
    return payload, _put_lines(payload, result)


def cmd_entry_put_batch(ctx, args):
    """NDJSON 走 stdin：一文件一笔事务，**全过才写**（任何一行不过门禁，整批一个字节都不落）。"""
    actor = need_actor(args)
    lang = getattr(args, "lang", None) or ctx.lang
    stdin = getattr(args, "stdin", None) or sys.stdin
    raw = stdin.read()
    rows_in = [line for line in raw.splitlines() if line.strip()]
    if not rows_in:
        raise ApiError("usage", "put-batch 的 stdin 是空的（NDJSON：一行一条）")
    allowed = ("mod", "h", "text", "s", "t", "ctx", "p", "note")
    cache, primary, prepares = {}, {}, []
    for lineno, line in enumerate(rows_in, 1):
        try:
            item = json.loads(line)
        except ValueError as exc:
            raise ApiError("schema", "stdin 第 %d 行不是合法 JSON：%s" % (lineno, exc))
        if not isinstance(item, dict):
            raise ApiError("schema", "stdin 第 %d 行不是 JSON 对象" % lineno)
        extra = [key for key in item if key not in allowed]
        if extra:
            raise ApiError("schema", "stdin 第 %d 行有未知字段 %s（只允许 %s）"
                           % (lineno, ", ".join(sorted(extra)), ", ".join(allowed)))
        mod = item.get("mod")
        if not mod:
            raise ApiError("schema", "stdin 第 %d 行缺 mod" % lineno)
        if "t" not in item:
            raise ApiError("schema", "stdin 第 %d 行缺 t（要删这一行请写 t 为空串）" % lineno)
        probe = argparse.Namespace(text=item.get("text"), s=item.get("s"), h=item.get("h"))
        source, h = source_for(ctx, probe, lang)
        row, keep, gates = build_row(ctx, mod, source, item["t"], h,
                                     ctx_note=item.get("ctx"),
                                     pattern=coerce_pattern(item.get("p"),
                                                            "stdin 第 %d 行" % lineno))
        pack = choose_target_cached(ctx, cache, primary, mod, h, lang)
        note = item.get("note") or ""
        if keep:
            note = ("keep" + ("；" + note if note else ""))
        prepares.append((pack, row, h, gates, note, lineno))
    seen = {}
    for (_pack, _row, h, _gates, _note, lineno) in prepares:
        if h in seen:
            ctx.warn("stdin 里 h=%s 出现多次（第 %d 行会覆盖第 %d 行）" % (h, lineno, seen[h]))
        seen[h] = lineno
    expect = getattr(args, "expect_revision", None)
    for pack in cache.values():
        revision_check(pack, expect)
    for (pack, row, h, gates, note, lineno) in prepares:
        if row is None:
            index = pack.find(h)
            if index < 0:
                raise ApiError("missing", "stdin 第 %d 行要删的 h=%s 在 %s 里不存在"
                               % (lineno, h, pack.rel))
            del pack.rows[index]
            pack.add_event("delete", h=h, note=note)
        else:
            apply_put(pack, row, gates, note)
    result = commit(ctx, list(cache.values()), actor, "entry.put-batch",
                    dry_run=bool(getattr(args, "dry_run", False)))
    payload = envelope(ctx, "entry.put-batch", result, lang=lang, mod=None)
    payload["rows"] = len(prepares)
    lines = ["整批 %d 行全部通过门禁（一文件一笔事务：全过才写）" % len(prepares)]
    lines.extend(_put_lines(payload, result))
    return payload, lines


def cmd_entry_delete(ctx, args):
    actor = need_actor(args)
    mod = need_value(getattr(args, "mod", None), "--mod")
    h = need_value(getattr(args, "h", None), "--h")
    lang = getattr(args, "lang", None) or ctx.lang
    target = choose_target(ctx, mod, h, lang)
    index = target.find(h)
    if index < 0:
        raise ApiError("missing", "%s 里没有 h=%s" % (target.rel, h))
    revision_check(target, getattr(args, "expect_revision", None))
    del target.rows[index]
    target.add_event("delete", h=h, note=getattr(args, "reason", "") or "")
    result = commit(ctx, [target], actor, "entry.delete",
                    dry_run=bool(getattr(args, "dry_run", False)))
    payload = envelope(ctx, "entry.delete", result, lang=lang, mod=mod)
    return payload, _put_lines(payload, result)


def cmd_override_put(ctx, args):
    """人工覆盖（docs/10 §12 D6）：只跑机械门禁 G2/G3/G4/G5，长度比与术语交给人判。"""
    actor = need_actor(args)
    en = need_value(getattr(args, "en", None), "--en")
    zh = need_value(getattr(args, "zh", None), "--zh")
    if not en.strip() or not zh.strip():
        raise ApiError("usage", "--en / --zh 不能为空")
    gates = gate_check(ctx, en, zh, subset=MANUAL_GATES)
    res = LinesFile(ctx.overrides_path, ctx, "packs/overrides.jsonl")
    note = getattr(args, "note", "") or ""
    index = None
    for position, row in enumerate(res.rows):
        if row.get("en") == en:
            index = position
            break
    if index is None:
        res.rows.append({"en": en, "zh": zh, "note": note})
        action = "insert"
    else:
        row = dict(res.rows[index])
        row["zh"] = zh
        if note:
            row["note"] = note
        res.rows[index] = row
        action = "replace"
    res.add_event(action, gates=gates, note=note)
    result = commit(ctx, [res], actor, "override.put",
                    dry_run=bool(getattr(args, "dry_run", False)))
    payload = envelope(ctx, "override.put", result, lang=ctx.lang, mod=None)
    return payload, _put_lines(payload, result)


def cmd_glossary_put(ctx, args):
    """术语表追加/覆盖：同 en 已存在就是替换（术语表也是只追加的账，不改 key）。"""
    actor = need_actor(args)
    en = need_value(getattr(args, "en", None), "--en")
    zh = need_value(getattr(args, "zh", None), "--zh")
    short = bool(getattr(args, "short", False))
    gates = gate_check(ctx, en, zh, subset=MANUAL_GATES)
    res = LinesFile(ctx.glossary_path, ctx, "packs/glossary.jsonl")
    index = None
    for position, row in enumerate(res.rows):
        if row.get("en") == en:
            index = position
            break
    if index is None:
        res.rows.append({"key": en, "en": en, "zh": zh, "short": short})
        action = "insert"
    else:
        row = dict(res.rows[index])
        row["zh"] = zh
        row["short"] = short
        res.rows[index] = row
        action = "replace"
    res.add_event(action, gates=gates, note="short=%s" % ("true" if short else "false"))
    result = commit(ctx, [res], actor, "glossary.put",
                    dry_run=bool(getattr(args, "dry_run", False)))
    payload = envelope(ctx, "glossary.put", result, lang=ctx.lang, mod=None)
    return payload, _put_lines(payload, result)


def cmd_mod_put(ctx, args):
    """登记 / 更新一个 mod 身份（mods.json）。modId 是稳定键，永不改名。"""
    actor = need_actor(args)
    mod = need_value(getattr(args, "mod", None), "--mod")
    display = getattr(args, "display", None) or mod
    doc = DocFile(ctx.mods_path, ctx, {"schema": 1, "mods": []})
    mods = doc.doc.get("mods")
    if not isinstance(mods, list):
        raise ApiError("schema", "packs/mods.json 的 mods 必须是数组")
    entry = None
    for item in mods:
        if isinstance(item, dict) and item.get("modId") == mod:
            entry = item
            break
    action = "replace" if entry is not None else "insert"
    if entry is None:
        entry = {"modId": mod, "displayName": display, "ckan": None,
                 "assemblies": [], "folders": [], "aliases": []}
        mods.append(entry)
    entry["displayName"] = display
    if getattr(args, "ckan", None):
        entry["ckan"] = args.ckan
    for field in ("assemblies", "folders", "aliases"):
        values = getattr(args, field, None) or []
        bucket = entry.get(field)
        if bucket is None:
            bucket = []
            entry[field] = bucket
        if not isinstance(bucket, list):
            raise ApiError("schema", "packs/mods.json 里 %s 的 %s 必须是数组" % (mod, field))
        for value in values:
            if value and value not in bucket:
                bucket.append(str(value))
    mods.sort(key=lambda item: str(item.get("modId") or "") if isinstance(item, dict) else "")
    doc.doc["schema"] = doc.doc.get("schema") or 1
    doc.add_event(action, note=getattr(args, "note", "") or "")
    result = commit(ctx, [doc], actor, "mod.put",
                    dry_run=bool(getattr(args, "dry_run", False)))
    payload = envelope(ctx, "mod.put", result, lang=None, mod=mod)
    payload["modEntry"] = entry
    return payload, _put_lines(payload, result)


def _semantic_map(pack):
    """一个包的 h → (s, t, mod) 映射：归一 p 只许改写法，这张表必须逐条相等。"""
    return {row.get("h"): (row.get("s"), row.get("t"), row.get("mod")) for row in pack.rows}


def normalize_pattern_flags(ctx, actor, lang=None, dry_run=False):
    """把 p 的旧表示（"p":1 / "1" / "true"）归一成布尔 true。

    为什么需要它：486 行既有骨架行是 translate_il.py 的历史写法 "p":1，运行期 C# 两种写法都吃，
    但 M1 的写入侧从今天起只写布尔。归一让存量与新写入收敛，且它走的是 entry put-batch 同一条
    commit 路径（写 tmp → 追加 journal → os.replace），所以每一笔都有 journal 背书。
    幂等：已经全是布尔 → rows=0，不写盘、不写 journal。
    自证：归一后从磁盘重读，逐包比 h → (s,t,mod)，不等就整笔报内部错误（绝不悄悄改语义）。
    """
    files, counts, before = [], {}, {}
    for path in iter_pack_files(ctx, lang):
        pack = PackFile(path, ctx)
        legacy = [row for row in pack.rows if pattern_is_legacy(row.get("p"))]
        if not legacy:
            continue
        before[pack.rel] = _semantic_map(pack)
        for index, row in enumerate(pack.rows):
            if pattern_is_legacy(row.get("p")):
                row["p"] = coerce_pattern(row["p"], "%s 第 %d 行" % (pack.rel, index + 1))
        counts[pack.rel] = len(legacy)
        pack.add_event("normalize-p", note="p 旧表示 %d 行 → 布尔 true（s/t/mod/h 一个字符都没动）"
                       % len(legacy))
        files.append(pack)
    rows = sum(counts.values())
    if not files:
        return {"op": "apply.normalize-p", "ok": True, "rows": 0, "files": [],
                "mapsEqual": True, "changed": [], "gates": [], "journal": None,
                "warnings": list(ctx.warnings)}
    result = commit(ctx, files, actor, "apply.normalize-p", dry_run=dry_run)
    after = {}
    for pack in files:
        after[pack.rel] = _semantic_map(pack if dry_run else PackFile(pack.path, ctx))
    equal = after == before
    if not equal:
        raise ApiError("internal", "归一后 h→(s,t,mod) 映射不一致（归一必须只改 p 的写法）")
    result.update({"op": "apply.normalize-p", "rows": rows, "mapsEqual": equal})
    return result


def cmd_apply(ctx, args):
    """重算 packs/index.json 与 reports/coverage.md（委托 packindex，口径同源）。

    --normalize-p：顺手把 p 的旧表示归一成布尔（一笔事务 + journal，并自证语义不变）。
    --dry-run：只报告会发生什么；不归一、不重算、不写盘、不写 journal。
    """
    actor = need_actor(args)
    lang = getattr(args, "lang", None) or ctx.lang
    normalize_p = bool(getattr(args, "normalize_p", False))
    dry_run = bool(getattr(args, "dry_run", False))
    checked, _checked = cmd_validate(
        ctx, argparse.Namespace(lang=lang, mod=None, strict=False, strict_p=False))
    norm = None
    if normalize_p:
        norm = normalize_pattern_flags(ctx, actor, lang=lang, dry_run=dry_run)
        if not dry_run:
            cmd_validate(ctx, argparse.Namespace(lang=lang, mod=None, strict=False,
                                                strict_p=True))   # 归一后严格模式必须绿
    payload = {"op": "apply", "ok": True, "lang": lang, "actor": actor, "dryRun": dry_run,
               "index": ctx.rel(ctx.index_path),
               "coverage": ctx.rel(ctx.reports_dir / "coverage.md"),
               "packs": checked["packs"], "rows": checked["rows"], "pLegacy": checked["pLegacy"],
               "normalizeP": norm, "packCount": None, "totalRows": None, "totalBytes": None,
               "journal": None, "changed": [], "gates": [], "warnings": list(ctx.warnings)}
    lines = ["validate: %d 个包 / %d 行，p 旧表示 %d 行"
             % (checked["packs"], checked["rows"], checked["pLegacy"])]
    if norm is not None:
        if norm["rows"]:
            lines.append("normalize-p: %d 行归一为布尔 true（%d 个文件，语义不变 %s）"
                         % (norm["rows"], len(norm["files"]),
                            "通过" if norm["mapsEqual"] else "失败"))
            for item in norm["files"]:
                lines.append("  %s  %s -> %s  %d 字节"
                             % (item["file"], item["revision_before"], item["revision_after"],
                                item["bytes"]))
            if norm.get("journal"):
                lines.append("journal: " + str(norm["journal"]))
        else:
            lines.append("normalize-p: 0 行需要归一（幂等）")
    if dry_run:
        lines.append("（--dry-run：没有写盘、没有写 journal；index 与覆盖率也未重算）")
        lines.extend("warning: " + message for message in ctx.warnings)
        return payload, lines
    from . import packindex as PK
    argv = ["--packs-dir", str(ctx.packs_dir), "--index", str(ctx.index_path),
            "--coverage", str(ctx.reports_dir / "coverage.md"),
            "--pool", str(ctx.pool_path), "--lang", lang, "--quiet"]
    code = 0
    try:
        code = PK.main(argv)
    except SystemExit as exc:
        code = int(exc.code or 0)
    if code:
        raise ApiError("internal", "packindex 重算失败（exit %d）" % code)
    doc = {}
    if ctx.index_path.exists():
        doc = json.loads(ctx.index_path.read_text(encoding="utf-8"))
    payload.update({"packCount": doc.get("packCount"), "totalRows": doc.get("totalRows"),
                    "totalBytes": doc.get("totalBytes")})
    lines.append("已重算 %s：%s 个包 / %s 行 / %s 字节"
                 % (payload["index"], payload["packCount"], payload["totalRows"],
                    payload["totalBytes"]))
    lines.extend("warning: " + message for message in ctx.warnings)
    return payload, lines


# ---------------------------------------------------------------------------
# 投稿回路（M3，docs/10 §6，2026-10-26 冻结）
#
# 这一组动词的职责只有一件事：**让玩家的投稿能安全落进仓库**。三条硬约束：
#   1. submission 不走 G1-G9 —— 它没有 t，长度比 / 术语表 / echo keep / 骨架 # 数都无从判定；
#   2. 幂等优先于报错：同 id 同内容 -> exit 0 + already:true，一个文件都不写、不写 journal
#      （否则每次重跑 CI 都会在 journal 里留一堆噪声行）；
#   3. 同 id 不同内容 -> exit 4（id 是内容派生的，同 id 不同内容只能是人改过包里的文件）。
# ---------------------------------------------------------------------------

SUBMISSION_DIR_REL = "packs/submission"
SUBMISSION_INDEX_REL = "packs/submission/index.json"
CANDIDATES_REL = "packs/candidates.jsonl"


def read_bytes_or_empty(path):
    """读文件字节；不存在当空（新仓库里 packs/submission 还不存在是正常状态）。"""
    try:
        return Path(path).read_bytes()
    except FileNotFoundError:
        return b""


def pack_hashes(ctx, lang=None):
    """packs/<lang>/ 里出现过的全部 h（归并 candidates 时判 covered）。

    为什么不复用 PackFile：那是给写入用的（要保风格、要能改行、要算 revision）；
    这里只要 1.4 万个 h，逐行 json 解析一遍 0.2 s，也不怕某个包的写法有变化。
    """
    hashes = set()
    for path in iter_pack_files(ctx, lang):
        for line in path.read_bytes().decode("utf-8", "replace").splitlines():
            if not line.strip():
                continue
            try:
                row = json.loads(line)
            except ValueError:
                continue
            h = row.get("h") if isinstance(row, dict) else None
            if isinstance(h, str):
                hashes.add(h)
    return hashes


def commit_submission(ctx, built, actor, dry_run=False):
    """M3 的提交路径：写全部 tmp -> 追加**一条**事件 -> 逐个 os.replace。

    为什么不复用 commit()：§6.7 的事件形状是「一笔投稿一条事件、事件里带 files[]」，
    而 commit() 的口径是「每个文件各自一条事件」。原子性照旧 —— journal 写不进去就整笔失败，
    任何一个 tmp 写失败都不会改名，不会留下半套投稿目录。
    """
    steps, files = [], []
    for step in built["files"]:
        path = ctx.root / step["path"]
        before = read_bytes_or_empty(path)
        if before == step["data"]:
            continue
        steps.append(step)
        files.append({"file": step["path"], "revision_before": revision_of(before),
                      "revision_after": revision_of(step["data"]),
                      "bytes": len(step["data"]), "rows": step.get("rows", 0),
                      "sha256": step["sha256"]})
    result = {"changed": [step["path"] for step in steps], "files": files, "gates": [],
              "warnings": list(ctx.warnings), "journal": None, "dry_run": bool(dry_run)}
    if not steps or dry_run:
        return result
    tmps = []
    try:
        for step in steps:
            target = ctx.root / step["path"]
            tmps.append((write_tmp(target, step["data"]), target))
    except ApiError:
        for tmp, _target in tmps:
            with contextlib.suppress(OSError):
                tmp.unlink()
        raise
    payload = dict(built["event"])
    payload["ts"] = now_iso()
    payload["actor"] = actor
    result["journal"] = journal_append(ctx, payload)
    for tmp, target in tmps:
        os.replace(str(tmp), str(target))
    return result


def submission_index(ctx):
    """读 packs/submission/index.json（不存在 = 空索引，不是错误）。"""
    raw = read_bytes_or_empty(ctx.root / SUBMISSION_INDEX_REL)
    if not raw.strip():
        return {"schema": 1, "submissions": {}}
    try:
        doc = json.loads(raw.decode("utf-8"))
    except ValueError as exc:
        raise ApiError("schema", "%s 不是合法 JSON：%s" % (SUBMISSION_INDEX_REL, exc))
    if not isinstance(doc, dict):
        raise ApiError("schema", "%s 顶层必须是对象" % SUBMISSION_INDEX_REL)
    if not isinstance(doc.get("submissions"), dict):
        doc["submissions"] = {}
    doc.setdefault("schema", 1)
    return doc


def cmd_submit_accept(ctx, args):
    """收一份玩家投稿：校验 -> 算 id -> 幂等/冲突 -> 按 mod 拆 -> 归并 candidates。"""
    actor = need_actor(args)
    bundle_arg = getattr(args, "bundle", None)
    use_stdin = bool(getattr(args, "stdin", False))
    dry_run = bool(getattr(args, "dry_run", False))
    if use_stdin and bundle_arg:
        raise ApiError("usage", "--bundle 与 --stdin 只能给一个（两个都给我不知道该信谁）")
    if not use_stdin and not bundle_arg:
        raise ApiError("usage", "submit accept 要 --bundle <投稿.json>，或者 --stdin 从管道读")
    if use_stdin:
        stream = getattr(sys.stdin, "buffer", None)
        raw = stream.read() if stream is not None else sys.stdin.read().encode("utf-8")
        where = "<stdin>"
    else:
        path = Path(bundle_arg)
        if not path.is_absolute():
            path = Path.cwd() / path
        try:
            raw = path.read_bytes()
        except OSError as exc:
            raise ApiError("missing", "读不到投稿文件 %s：%s" % (bundle_arg, exc)) from None
        where = str(bundle_arg)
    try:
        bundle = json.loads(raw.decode("utf-8-sig"))
    except (ValueError, UnicodeDecodeError) as exc:
        raise ApiError("schema", "%s 不是合法 JSON：%s" % (where, exc)) from None
    accepted_at = now_iso()
    index_raw = read_bytes_or_empty(ctx.root / SUBMISSION_INDEX_REL)
    try:
        built = SUB.plan(bundle, pack_hashes(ctx, ctx.lang),
                         read_bytes_or_empty(ctx.root / CANDIDATES_REL),
                         index_raw, accepted_at, actor)
    except SUB.BundleError as exc:
        raise ApiError(exc.kind, exc.message, exc.details, code=exc.code) from None
    for message in built["warnings"]:
        ctx.warn(message)
    sid = built["id"]
    already, stored_sha, stored_mods = SUB.existing_state(
        index_raw, read_bytes_or_empty(ctx.root / SUBMISSION_DIR_REL / sid / SUB.META_NAME), sid)
    if already:
        if stored_sha == built["event"]["bundle_sha256"]:
            payload = {"ok": True, "op": "submit.accept", "lang": built["event"]["lang"],
                       "submission_id": sid, "already": True, "bundle_sha256": stored_sha,
                       "changed": [], "files": [], "journal": None, "dry_run": dry_run,
                       "rows": built["rows"], "mods": built["mods"],
                       "warnings": list(ctx.warnings)}
            return payload, ["已收过投稿 %s（内容 sha256 相同）：一个文件都没写、也没有写 journal"
                             % sid]
        raise ApiError(
            "gate",
            "投稿 id 冲突：%s 已经收过别的内容（存储 %s / 本次 %s）；id 是内容派生的，"
            "同 id 不同内容说明有人改过投稿里的文件"
            % (sid, (stored_sha or "?")[:16], built["event"]["bundle_sha256"][:16]),
            ["已存 bundle sha256：" + str(stored_sha or "-"),
             "本次 bundle sha256：" + built["event"]["bundle_sha256"],
             "已存的 mods：" + (", ".join(stored_mods) or "-"),
             "本次的 mods：" + (", ".join(built["mods"]) or "-")]
            + (["mods 差异：" + ", ".join(sorted(set(stored_mods) ^ set(built["mods"])))]
               if set(stored_mods) ^ set(built["mods"]) else []),
            code="submission-id-conflict")
    result = commit_submission(ctx, built, actor, dry_run=dry_run)
    payload = envelope(ctx, "submit.accept", result, lang=built["event"]["lang"], mod=None)
    payload.update({"submission_id": sid, "already": False,
                    "bundle_sha256": built["event"]["bundle_sha256"],
                    "rows": built["rows"], "mods": built["mods"],
                    "candidates": built["event"]["candidates"],
                    "submission_files": [step["path"] for step in built["files"][:-1]]})
    stats = built["stats"]
    lines = ["投稿 %s：%d 行 / %d 个 mod" % (sid, built["rows"], len(built["mods"]))]
    for step in built["files"]:
        lines.append("  %s  %d 字节" % (step["path"], len(step["data"])))
    lines.append("candidates：新增 %d 行、累加 %d 行（总 %d 行）"
                 % (stats["inserted"], stats["updated"], stats["rows_after"]))
    if result.get("journal"):
        lines.append("journal: " + str(result["journal"]))
    if dry_run:
        lines.append("（--dry-run：没有写盘、也没有写 journal）")
    lines.extend("warning: " + message for message in ctx.warnings)
    return payload, lines


def cmd_submit_list(ctx, args):
    """列已收下的投稿（读侧；没有 index 就是空列表，不算错）。"""
    doc = submission_index(ctx)
    items = []
    for sid in sorted(doc["submissions"]):
        entry = doc["submissions"][sid]
        if not isinstance(entry, dict):
            continue
        items.append({"id": sid, "createdAt": entry.get("createdAt"),
                      "language": entry.get("language"), "rows": entry.get("rows"),
                      "bytes": entry.get("bytes"), "mods": entry.get("mods") or []})
    items.sort(key=lambda item: (str(item.get("createdAt") or ""), item["id"]), reverse=True)
    limit = int(getattr(args, "limit", 0) or 0)
    if limit > 0:
        items = items[:limit]
    payload = {"ok": True, "op": "submit.list", "count": len(items), "submissions": items}
    lines = ["已收投稿 %d 份" % len(items)]
    for item in items:
        lines.append("%s  %s  %s 行  %d 个 mod"
                     % (item["id"], item.get("createdAt") or "-", item.get("rows"),
                        len(item["mods"])))
    return payload, lines


def cmd_submit_show(ctx, args):
    """看一份投稿：index 汇总 + meta.json + 每个 mod 文件（--entries 才把行读出来）。"""
    sid = need_value(getattr(args, "id", None), "--id")
    doc = submission_index(ctx)
    entry = doc["submissions"].get(sid)
    if not isinstance(entry, dict):
        raise ApiError("missing", "没有 id=%s 的投稿（%s 里查不到）" % (sid, SUBMISSION_INDEX_REL))
    base = ctx.root / SUBMISSION_DIR_REL / sid
    meta_raw = read_bytes_or_empty(base / SUB.META_NAME)
    meta = {}
    if meta_raw.strip():
        try:
            meta = json.loads(meta_raw.decode("utf-8"))
        except ValueError as exc:
            raise ApiError("schema", "%s/%s/meta.json 不是合法 JSON：%s"
                           % (SUBMISSION_DIR_REL, sid, exc)) from None
    files = []
    for item in entry.get("files") or []:
        if not isinstance(item, dict):
            continue
        info = dict(item)
        if getattr(args, "entries", False):
            raw = read_bytes_or_empty(base / str(item.get("file") or ""))
            rows = []
            for line in raw.decode("utf-8", "replace").splitlines():
                if not line.strip():
                    continue
                try:
                    rows.append(json.loads(line))
                except ValueError:
                    pass
            info["entries"] = rows
        files.append(info)
    payload = {"ok": True, "op": "submit.show", "id": sid, "index": entry,
               "meta": meta, "files": files}
    lines = ["投稿 %s  %s  %s 行  %d 个 mod"
             % (sid, entry.get("createdAt") or "-", entry.get("rows"),
                len(entry.get("mods") or []))]
    lines.append("meta: " + (("%d 个字段" % len(meta)) if meta else "缺 meta.json"))
    for item in files:
        lines.append("  %s  %s 行  %s 字节"
                     % (item.get("file"), item.get("rows"), item.get("bytes")))
    return payload, lines


#: 动词表：(一级, 二级) → 实现。v1 冻结，加动词要先改 docs/10。
VERBS = {
    ("pack", "list"): cmd_pack_list,
    ("pack", "show"): cmd_pack_show,
    ("entry", "get"): cmd_entry_get,
    ("entry", "search"): cmd_entry_search,
    ("entry", "put"): cmd_entry_put,
    ("entry", "put-batch"): cmd_entry_put_batch,
    ("entry", "delete"): cmd_entry_delete,
    ("override", "put"): cmd_override_put,
    ("glossary", "put"): cmd_glossary_put,
    ("mod", "list"): cmd_mod_list,
    ("mod", "resolve"): cmd_mod_resolve,
    ("mod", "put"): cmd_mod_put,
    ("journal", "tail"): cmd_journal_tail,
    ("validate", None): cmd_validate,
    ("apply", None): cmd_apply,
    ("submit", "accept"): cmd_submit_accept,
    ("submit", "list"): cmd_submit_list,
    ("submit", "show"): cmd_submit_show,
}


# ---------------------------------------------------------------------------
# 命令行
# ---------------------------------------------------------------------------

def _globals():
    """全局 flag：用 SUPPRESS 挂到每一级 parser 上，这样写在动词前后都行且不互相覆盖。"""
    parent = argparse.ArgumentParser(add_help=False)
    parent.add_argument("--json", action="store_true", default=argparse.SUPPRESS,
                        help="输出 JSON envelope（docs/10 §3.3）")
    parent.add_argument("--root", default=argparse.SUPPRESS,
                        help="仓库根（默认当前仓库；selftest 用它把读写隔离到临时目录）")
    parent.add_argument("--actor", default=argparse.SUPPRESS,
                        help="写操作必填：human:<名字> / llm:<模型> / pipeline:<脚本>")
    return parent


def build_parser():
    parent = _globals()
    parser = argparse.ArgumentParser(
        prog="python -m i18n_tools.api",
        description="UI18n 交互 API：packs 的唯一写入口（docs/10）。写操作必须带 --actor，"
                    "全部过 G2–G9 门禁并记 journal。",
        parents=[parent],
    )
    parser.add_argument("--selftest", action="store_true", help="自测（真实 packs 一个字节都不碰）")
    sub = parser.add_subparsers(dest="verb")

    pack = sub.add_parser("pack", parents=[parent], help="看包清单 / 包内容")
    pack_sub = pack.add_subparsers(dest="action")
    item = pack_sub.add_parser("list", parents=[parent], help="列出所有包")
    item.add_argument("--lang", default=None)
    item.add_argument("--mod", default=None)
    item.set_defaults(func=cmd_pack_list)
    item = pack_sub.add_parser("show", parents=[parent], help="看一个包的行")
    item.add_argument("--mod", required=True)
    item.add_argument("--lang", default=None)
    item.add_argument("--channel", choices=("il", "cfg"), default=None)
    item.add_argument("--limit", type=int, default=0)
    item.add_argument("--grep", default=None)
    item.set_defaults(func=cmd_pack_show)

    entry = sub.add_parser("entry", parents=[parent], help="查 / 写条目")
    entry_sub = entry.add_subparsers(dest="action")
    item = entry_sub.add_parser("get", parents=[parent], help="按 h 或原文取一条")
    item.add_argument("--h", default=None)
    item.add_argument("--text", default=None)
    item.add_argument("--lang", default=None)
    item.add_argument("--all", action="store_true", help="命中多处时全部输出")
    item.set_defaults(func=cmd_entry_get)
    item = entry_sub.add_parser("search", parents=[parent], help="按片段搜原文/译文")
    item.add_argument("--text", required=True)
    item.add_argument("--lang", default=None)
    item.add_argument("--limit", type=int, default=50)
    item.set_defaults(func=cmd_entry_search)
    item = entry_sub.add_parser("put", parents=[parent], help="写一条（幂等：同 h 是替换）")
    item.add_argument("--mod", required=True)
    item.add_argument("--lang", default=None)
    item.add_argument("--h", default=None)
    item.add_argument("--text", default=None, help="英文原文（等价于包里的 s）")
    item.add_argument("--s", default=None, help="英文原文（与 --text 等价，别名）")
    item.add_argument("--t", default=None, help="译文；空字符串 = 删掉这一行")
    item.add_argument("--ctx", default=None, help="上下文提示（可选，写进行里的 ctx）")
    item.add_argument("--p", action="store_true", help="模板骨架行（写 p:true，G9 生效）")
    item.add_argument("--note", default=None)
    item.add_argument("--expect-revision", dest="expect_revision", default=None)
    item.add_argument("--dry-run", dest="dry_run", action="store_true")
    item.set_defaults(func=cmd_entry_put)
    item = entry_sub.add_parser("put-batch", parents=[parent], help="NDJSON 走 stdin，整批一笔事务")
    item.add_argument("--lang", default=None)
    item.add_argument("--note", default=None)
    item.add_argument("--expect-revision", dest="expect_revision", default=None)
    item.add_argument("--dry-run", dest="dry_run", action="store_true")
    item.set_defaults(func=cmd_entry_put_batch)
    item = entry_sub.add_parser("delete", parents=[parent], help="删一条")
    item.add_argument("--mod", required=True)
    item.add_argument("--lang", default=None)
    item.add_argument("--h", required=True)
    item.add_argument("--reason", default=None)
    item.add_argument("--expect-revision", dest="expect_revision", default=None)
    item.add_argument("--dry-run", dest="dry_run", action="store_true")
    item.set_defaults(func=cmd_entry_delete)

    override = sub.add_parser("override", parents=[parent], help="人工覆盖")
    override_sub = override.add_subparsers(dest="action")
    item = override_sub.add_parser("put", parents=[parent], help="写一条 packs/overrides.jsonl")
    item.add_argument("--en", required=True)
    item.add_argument("--zh", required=True)
    item.add_argument("--note", default=None)
    item.add_argument("--dry-run", dest="dry_run", action="store_true")
    item.set_defaults(func=cmd_override_put)

    glossary = sub.add_parser("glossary", parents=[parent], help="术语表")
    glossary_sub = glossary.add_subparsers(dest="action")
    item = glossary_sub.add_parser("put", parents=[parent], help="写一条 packs/glossary.jsonl")
    item.add_argument("--en", required=True)
    item.add_argument("--zh", required=True)
    item.add_argument("--short", action="store_true")
    item.add_argument("--dry-run", dest="dry_run", action="store_true")
    item.set_defaults(func=cmd_glossary_put)

    mod = sub.add_parser("mod", parents=[parent], help="mod 身份表")
    mod_sub = mod.add_subparsers(dest="action")
    item = mod_sub.add_parser("list", parents=[parent], help="列出已登记 mod")
    item.set_defaults(func=cmd_mod_list)
    item = mod_sub.add_parser("resolve", parents=[parent], help="按程序集/包名解析 modId")
    item.add_argument("--assembly", default=None)
    item.add_argument("--pack", default=None)
    item.set_defaults(func=cmd_mod_resolve)
    item = mod_sub.add_parser("put", parents=[parent], help="登记 / 更新一个 mod")
    item.add_argument("--mod", required=True)
    item.add_argument("--display", default=None)
    item.add_argument("--assembly", dest="assemblies", action="append", default=None)
    item.add_argument("--folder", dest="folders", action="append", default=None)
    item.add_argument("--alias", dest="aliases", action="append", default=None)
    item.add_argument("--ckan", default=None)
    item.add_argument("--note", default=None)
    item.add_argument("--dry-run", dest="dry_run", action="store_true")
    item.set_defaults(func=cmd_mod_put)

    journal = sub.add_parser("journal", parents=[parent], help="审计事件")
    journal_sub = journal.add_subparsers(dest="action")
    item = journal_sub.add_parser("tail", parents=[parent], help="看最后 N 条事件")
    item.add_argument("--n", type=int, default=20)
    item.set_defaults(func=cmd_journal_tail)

    item = sub.add_parser("validate", parents=[parent], help="全量体检")
    item.add_argument("--lang", default=None)
    item.add_argument("--mod", default=None)
    item.add_argument("--strict", action="store_true", help="加机械门禁 G2/G4/G9")
    item.add_argument("--strict-p", dest="strict_p", action="store_true",
                      help="p 的旧表示（1 / \"1\" / \"true\"）也判失败（归一后拿它自证）")
    item.set_defaults(func=cmd_validate)

    item = sub.add_parser("apply", parents=[parent], help="重算 index.json 与覆盖率报告")
    item.add_argument("--lang", default=None)
    item.add_argument("--normalize-p", dest="normalize_p", action="store_true",
                      help="把 p 的旧表示归一成布尔 true（一笔事务 + journal）")
    item.add_argument("--dry-run", dest="dry_run", action="store_true",
                      help="只报告会发生什么：不归一、不重算、不写盘、不写 journal")
    item.set_defaults(func=cmd_apply)

    item = sub.add_parser("submit", parents=[parent], help="投稿回路（M3：收 / 列 / 看）")
    submit_sub = item.add_subparsers(dest="action")
    item = submit_sub.add_parser("accept", parents=[parent],
                                 help="收一份投稿 json（校验 + 幂等 + 按 mod 拆 + 归并 candidates）")
    item.add_argument("--bundle", default=None, help="投稿文件路径（相对路径按当前目录解析）")
    item.add_argument("--stdin", action="store_true", help="从标准输入读投稿 json")
    item.add_argument("--dry-run", dest="dry_run", action="store_true",
                      help="只报告会写哪些文件：不写盘、不写 journal")
    item.set_defaults(func=cmd_submit_accept)
    item = submit_sub.add_parser("list", parents=[parent], help="列已收下的投稿")
    item.add_argument("--limit", type=int, default=0, help="只列最近 N 份（0 = 全部）")
    item.set_defaults(func=cmd_submit_list)
    item = submit_sub.add_parser("show", parents=[parent], help="看一份投稿")
    item.add_argument("--id", required=True)
    item.add_argument("--entries", action="store_true", help="连每行原文一起读出来")
    item.add_argument("--limit", type=int, default=0)
    item.set_defaults(func=cmd_submit_show)
    return parser


def _emit(payload, lines, as_json):
    if as_json:
        print(json.dumps(payload, ensure_ascii=False))
        return
    for line in lines:
        print(line)


def _fail(error, as_json):
    if as_json:
        print(json.dumps({"ok": False,
                          "error": {"code": error.code, "message": error.message,
                                    "details": error.details}}, ensure_ascii=False))
    else:
        sys.stderr.write("[api] %s: %s\n" % (error.code, error.message))
        for detail in error.details[:20]:
            sys.stderr.write("  - %s\n" % detail)
    return error.exit_code


def run(argv=None):
    """跑一次 API，返回退出码（main 的同义函数，方便进程内调用与测试）。"""
    return main(argv)


def main(argv=None):
    ensure_stdout()
    argv = list(sys.argv[1:] if argv is None else argv)
    if "--selftest" in argv:
        failures, total = selftest()
        for (name, detail) in failures:
            sys.stderr.write("[api selftest] 失败：%s  %s\n" % (name, detail))
        if failures:
            print("[api selftest] %d 项失败 / 共 %d 项" % (len(failures), total))
            return EXIT_INTERNAL
        print("[api selftest] 全部通过（%d 项）" % total)
        return EXIT_OK
    parser = build_parser()
    try:
        args = parser.parse_args(argv)
    except SystemExit:
        return EXIT_USAGE
    func = getattr(args, "func", None)
    if func is None:
        parser.print_help(sys.stderr)
        return EXIT_USAGE
    as_json = bool(getattr(args, "json", False))
    ctx = Ctx(root=getattr(args, "root", None))
    try:
        payload, lines = func(ctx, args)
    except ApiError as exc:
        return _fail(exc, as_json)
    except SystemExit as exc:
        return EXIT_USAGE if exc.code not in (0, None) else EXIT_OK
    except (OSError, ValueError, KeyError) as exc:
        return _fail(ApiError("internal", "内部错误：%s: %s" % (type(exc).__name__, exc)), as_json)
    _emit(payload, lines, as_json)
    return EXIT_OK



# ---------------------------------------------------------------------------
# 自检（--selftest）
#
# 全部在 tempfile.TemporaryDirectory() 里跑：--root 把读写都关进临时根，真实仓库一个字节不动。
# 最后再回头比对真实 packs/zh-cn 的 sha256 快照 —— 这是「--root 隔离真的有效」的硬证据。
# ---------------------------------------------------------------------------

def _h(text):
    return hash_text(normalize(text))


def _row(source, zh, mod="TUFX", pattern=False):
    row = {"h": _h(source), "n": normalize(source), "s": source, "t": zh, "mod": mod}
    if pattern:
        row["p"] = True
    return row


def _put_text(path, text):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(text.encode("utf-8"))


def _pack_text(rows, spaced):
    sep = (", ", ": ") if spaced else (",", ":")
    return "\n".join(json.dumps(row, ensure_ascii=False, separators=sep) for row in rows) + "\n"


def _fixture(root):
    """造一个完整的小仓库：两种书写风格 + 双 channel + 带点号的 modId + 术语表 + 覆盖表。"""
    packs = root / "packs"
    lang_dir = packs / "zh-cn"
    lang_dir.mkdir(parents=True, exist_ok=True)
    _put_text(lang_dir / "TUFX.jsonl", _pack_text([
        _row("Blue Paint", "蓝色涂装"),
        _row("Throttle Valve Assembly For The Engine", "发动机节流阀组件"),
    ], spaced=True))
    _put_text(lang_dir / "TUFX.il.jsonl", _pack_text([
        _row("Frame Rate", "帧率"),
    ], spaced=False))
    _put_text(lang_dir / "AtmosphereAutopilot.UI.il.jsonl", _pack_text([
        _row("Target Attitude", "目标姿态", mod="AtmosphereAutopilot.UI"),
    ], spaced=False))
    _put_text(packs / "glossary.jsonl", _pack_text([
        {"key": "Kerbal", "en": "Kerbal", "zh": "坎巴拉人", "short": False},
        {"key": "Oxidizer", "en": "Oxidizer", "zh": "氧化剂", "short": True},
    ], spaced=False))
    _put_text(packs / "overrides.jsonl", _pack_text([
        {"en": "Science", "zh": "科学", "note": ""},
    ], spaced=False))
    return packs


def _call(root, argv, stdin_text=None):
    """进程内跑一次 API：返回 (退出码, 解析后的 JSON envelope 或 None, stderr 文本)。"""
    out, err = io.StringIO(), io.StringIO()
    saved = (sys.stdout, sys.stderr, sys.stdin)
    sys.stdout, sys.stderr = out, err
    if stdin_text is not None:
        sys.stdin = io.StringIO(stdin_text)
    try:
        code = main(["--root", str(root), "--json"] + list(argv))
    finally:
        sys.stdout, sys.stderr, sys.stdin = saved
    payload = None
    text = out.getvalue().strip()
    if text:
        try:
            payload = json.loads(text.splitlines()[-1])
        except ValueError:
            payload = None
    return code, payload, err.getvalue()


def _snapshot(packs):
    """包目录 + journal 的字节快照（原子性/隔离都拿它比）。"""
    state = {}
    for path in sorted(packs.rglob("*")):
        if path.is_file():
            state[path.relative_to(packs).as_posix()] = sha256_hex(path.read_bytes())
    return state


def _journal_lines(root):
    path_dir = root / "packs" / "journal"
    if not path_dir.is_dir():
        return 0
    return sum(nonempty_lines(path.read_bytes()) for path in path_dir.glob("*.jsonl"))


def _semantic_state(lang_dir):
    """包目录 → {文件名: {h: (s, t, mod)}}：归一 p 前后逐条比，证明只改了写法。"""
    state = {}
    for path in sorted(Path(lang_dir).glob("*.jsonl")):
        rows = {}
        for row in rows_of_bytes(path.read_bytes(), path.name):
            rows[row.get("h")] = (row.get("s"), row.get("t"), row.get("mod"))
        state[path.name] = rows
    return state


def selftest():
    """返回 (失败列表, 总项数)；失败列表元素是 (名字, 细节)。"""
    failures = []
    total = 0

    def check(name, condition, detail=""):
        nonlocal total
        total += 1
        if not condition:
            failures.append((name, str(detail)))

    def reject(name, source, zh, want, pattern=False, ctx=None):
        """门禁必须拒绝：断言 want 出现在 failed 里（并打印实际命中集合，方便对照）。"""
        result = gates_for(ctx, source, zh, pattern=pattern)
        check(name, want in result["failed"], "failed=%s" % (result["failed"],))

    real = default_root()
    real_dir = real / "packs" / "zh-cn"
    real_before = {}
    if real_dir.is_dir():
        real_before = {path.name: sha256_hex(path.read_bytes()) for path in real_dir.glob("*.jsonl")}
    journal_before = _journal_lines(real) if (real / "packs" / "journal").is_dir() else 0

    with tempfile.TemporaryDirectory(prefix="ui18n-api-") as temp:
        root = Path(temp)
        packs = _fixture(root)
        cfg_path = packs / "zh-cn" / "TUFX.jsonl"
        il_path = packs / "zh-cn" / "TUFX.il.jsonl"
        ctx = Ctx(root=root)

        # ---- 1) 退出码表与 modId 派生（纯函数） ----
        for kind, want in (("usage", 2), ("revision", 3), ("gate", 4),
                           ("schema", 5), ("missing", 6), ("internal", 7)):
            check("退出码 %s -> %d" % (kind, want), ApiError(kind, "x").exit_code == want,
                  ApiError(kind, "x").exit_code)
        for name, want in (("TUFX.jsonl", "TUFX"), ("TUFX.il.jsonl", "TUFX"),
                           ("TUFX.cfg.jsonl", "TUFX"),
                           ("AtmosphereAutopilot.UI.il.jsonl", "AtmosphereAutopilot.UI"),
                           ("X.il.il.jsonl", "X.il")):
            check("pack_mod(%s) == %s" % (name, want), pack_mod(name) == want, pack_mod(name))
        check("pack_channel(.il.jsonl) == il", pack_channel("TUFX.il.jsonl") == "il")
        check("pack_channel(.jsonl) == cfg", pack_channel("TUFX.jsonl") == "cfg")
        check("pack_channel(.cfg.jsonl) == cfg", pack_channel("TUFX.cfg.jsonl") == "cfg")
        check("normalize 折叠空白后 h 不变", hash_of("Blue  Paint\n") == hash_of("Blue Paint"),
              hash_of("Blue  Paint\n"))
        check("h 是 16 位小写十六进制", bool(_H_RE.match(hash_of("Blue Paint"))), hash_of("Blue Paint"))
        check("revision 形如 <12hex>@<行数>",
              revision_of(cfg_path.read_bytes()).endswith("@2"),
              revision_of(cfg_path.read_bytes()))
        check("不存在的文件 revision = missing@0", revision_of(b"") == "missing@0")

        # ---- 2) 读动词 ----
        code, payload, _err = _call(root, ["pack", "list"])
        check("pack list exit 0", code == 0, code)
        check("pack list 3 个包", (payload or {}).get("packCount") == 3, (payload or {}).get("packCount"))
        check("pack list 4 行", (payload or {}).get("totalRows") == 4, (payload or {}).get("totalRows"))
        mods = sorted(item["mod"] for item in (payload or {}).get("packs", []))
        check("pack list 里同 modId 两个 channel 并存",
              mods == ["AtmosphereAutopilot.UI", "TUFX", "TUFX"], mods)
        code, payload, _err = _call(root, ["pack", "show", "--mod", "TUFX", "--channel", "il"])
        check("pack show --channel il 只看到 IL 包",
              code == 0 and [p["file"] for p in payload["packs"]] == ["packs/zh-cn/TUFX.il.jsonl"],
              code)
        code, payload, _err = _call(root, ["entry", "get", "--h", _h("Blue Paint")])
        check("entry get 按 h 命中", code == 0 and payload["hits"][0]["row"]["t"] == "蓝色涂装", code)
        code, _payload, _err = _call(root, ["entry", "get", "--h", "0" * 16])
        check("entry get 未命中 exit 6", code == 6, code)
        code, payload, _err = _call(root, ["entry", "search", "--text", "Paint"])
        check("entry search 命中 1 条", code == 0 and payload["count"] == 1, code)
        code, payload, _err = _call(root, ["journal", "tail"])
        check("journal tail 起手 0 条", code == 0 and payload["count"] == 0, code)

        # ---- 3) 写操作的身份与参数 ----
        code, payload, _err = _call(root, ["entry", "put", "--mod", "TUFX", "--text", "Frame Time",
                                           "--t", "帧时间"])
        check("缺 --actor exit 2", code == 2 and payload["error"]["code"] == "USAGE", code)
        code, _p, _e = _call(root, ["entry", "put", "--mod", "TUFX", "--text", "Frame Time",
                                    "--t", "帧时间", "--actor", "robot:x"])
        check("--actor 前缀非法 exit 2", code == 2, code)
        code, _p, _e = _call(root, ["entry", "frobnicate"])
        check("未知二级动词 exit 2", code == 2, code)
        code, _p, _e = _call(root, ["submit", "accept", "--actor", "human:x"])
        check("submit accept 缺 --bundle 是用法错（exit 2）", code == 2, code)

        # ---- 4) entry put：新行、h 现算、风格保持、journal ----
        before_res = revision_of(cfg_path.read_bytes())
        code, payload, _err = _call(root, ["entry", "put", "--mod", "TUFX", "--text", "Frame Time",
                                           "--t", "帧时间", "--actor", "llm:selftest"])
        check("entry put 新行 exit 0", code == 0, code)
        check("entry put envelope revision_before 对", (payload or {}).get("revision_before") == before_res,
              (payload or {}).get("revision_before"))
        check("entry put 给了「两个包都没命中」的 warning",
              any("cfg" in item for item in (payload or {}).get("warnings") or []),
              (payload or {}).get("warnings"))
        rows_now = rows_of_bytes(cfg_path.read_bytes())
        check("put 后 cfg 包 3 行", len(rows_now) == 3, len(rows_now))
        new_row = [row for row in rows_now if row["s"] == "Frame Time"]
        check("put 写入的 h 是现算的", bool(new_row) and new_row[0]["h"] == _h("Frame Time"),
              new_row and new_row[0]["h"])
        check("put 保持原文件书写风格", (b'": ' in cfg_path.read_bytes().split(b"\n")[0]))
        events = journal_events(ctx)
        check("journal 记了 1 条事件", len(events) == 1, len(events))
        check("journal action=insert", events and events[0]["action"] == "insert",
              events and events[0].get("action"))
        check("journal file_sha256_after 与磁盘一致",
              events and events[0]["file_sha256_after"] == sha256_hex(cfg_path.read_bytes()))
        check("journal actor 原样落盘", events and events[0]["actor"] == "llm:selftest")
        check("journal 字段顺序固定", events and list(events[0])[:len(JOURNAL_KEYS)] == list(JOURNAL_KEYS),
              events and list(events[0]))

        # ---- 5) 同 h 是替换（幂等），且改已有行永远落回原处 ----
        code, payload, _err = _call(root, ["entry", "put", "--mod", "TUFX", "--text", "Frame Rate",
                                           "--t", "帧数（FPS）", "--actor", "human:selftest"])
        check("h 已在 IL 包 → 写回 IL 包", code == 0, code)
        il_rows = rows_of_bytes(il_path.read_bytes())
        check("IL 包里那条被替换而不是新增",
              len(il_rows) == 1 and il_rows[0]["t"] == "帧数（FPS）", il_rows)
        check("cfg 包没被顺手改一行",
              len(rows_of_bytes(cfg_path.read_bytes())) == 3)
        code, payload, _err = _call(root, ["entry", "put", "--mod", "TUFX", "--text", "Frame Rate",
                                           "--t", "帧数（FPS）", "--actor", "human:selftest"])
        check("同内容再 put 一次仍是 1 行（不追加重复）",
              len(rows_of_bytes(il_path.read_bytes())) == 1)

        # ---- 6) revision 冲突 / schema 错 ----
        live = revision_of(cfg_path.read_bytes())
        code, payload, _err = _call(root, ["entry", "put", "--mod", "TUFX", "--text", "Frame Time",
                                           "--t", "帧时间", "--actor", "human:selftest",
                                           "--expect-revision", "deadbeefcafe@9"])
        check("revision 冲突 exit 3", code == 3, code)
        check("revision 冲突回传当前 revision",
              (payload or {}).get("error", {}).get("details") == [live],
              (payload or {}).get("error", {}).get("details"))
        code, payload, _err = _call(root, ["entry", "put", "--mod", "TUFX", "--text", "Blue Paint",
                                           "--h", "0" * 16, "--t", "蓝色涂装",
                                           "--actor", "human:selftest"])
        check("h 与原文不符 exit 5", code == 5 and payload["error"]["code"] == "SCHEMA_INVALID", code)

        # ---- 7) 门禁 G2/G3/G4/G6/G7/G8/G9 各一条拒绝 ----
        reject("G2 占位符 <<n>> 数量不对就拒绝", "Fuel: <<1>> units", "燃料：单位", "G2", ctx=ctx)
        reject("G4 富文本标签缺失就拒绝", "<b>Bold</b> text here", "粗体文本", "G4", ctx=ctx)
        reject("G6 长度比越界就拒绝", "This is a long sentence about rocketry.", "好", "G6", ctx=ctx)
        reject("G7 官方术语被改就拒绝", "Oxidizer", "助燃剂", "G7", ctx=ctx)
        reject("G8 原文照抄就拒绝", "Throttle Valve Assembly For The Engine",
               "Throttle Valve Assembly For The Engine", "G8", ctx=ctx)
        reject("G9 模板占位符数量不对就拒绝", "Tank #1 holds #2 units", "罐子装东西", "G9",
               pattern=True, ctx=ctx)
        good = gates_for(ctx, "Frame Time", "帧时间")
        check("正常译文所有门禁都过", good["failed"] == [], good["failed"])

        # ---- 8) keep 行：原文照抄但 echo_keep 判真 → 允许，且 note 标 keep ----
        code, payload, _err = _call(root, ["entry", "put", "--mod", "TUFX", "--text", "NASA",
                                           "--t", "NASA", "--actor", "human:selftest"])
        check("keep 行（t == s 的短标签）允许写入", code == 0, code)
        keep_rows = [row for row in rows_of_bytes(cfg_path.read_bytes()) if row.get("s") == "NASA"]
        check("keep 行以 t == s 落库", bool(keep_rows) and keep_rows[0]["t"] == "NASA", keep_rows)
        keep_note = [event["note"] for event in journal_events(ctx) if event.get("h") == _h("NASA")]
        check("keep 行的 journal note 标了 keep",
              bool(keep_note) and "keep" in keep_note[-1], keep_note)

        # ---- 9) entry delete ----
        code, _payload, _err = _call(root, ["entry", "delete", "--mod", "TUFX", "--h", _h("NASA"),
                                            "--actor", "human:selftest", "--reason", "自检清理"])
        check("entry delete exit 0", code == 0, code)
        check("delete 后 NASA 行没了",
              not [row for row in rows_of_bytes(cfg_path.read_bytes()) if row.get("s") == "NASA"])
        code, _payload, _err = _call(root, ["entry", "delete", "--mod", "TUFX", "--h", "1" * 16,
                                            "--actor", "human:selftest"])
        check("delete 不存在的行 exit 6", code == 6, code)

        # ---- 10) --dry-run 不写盘、不记 journal ----
        snap_before = _snapshot(packs)
        code, payload, _err = _call(root, ["entry", "put", "--mod", "TUFX", "--text", "Dry Run Text",
                                           "--t", "演练文本", "--actor", "human:selftest",
                                           "--dry-run"])
        check("--dry-run exit 0", code == 0, code)
        check("--dry-run 一个字节都没写", _snapshot(packs) == snap_before)
        check("--dry-run 不产生 journal 事件",
              (payload or {}).get("journal") is None and _journal_lines(root) == len(journal_events(ctx)))

        # ---- 11) put-batch：全过才写 ----
        lines_before = _journal_lines(root)
        batch_ok = "\n".join([
            json.dumps({"mod": "TUFX", "text": "Frame Time", "t": "帧时间"}, ensure_ascii=False),
            json.dumps({"mod": "AtmosphereAutopilot.UI", "text": "Roll Rate", "t": "滚转角速度"},
                       ensure_ascii=False),
        ])
        code, payload, _err = _call(root, ["entry", "put-batch", "--actor", "pipeline:selftest"],
                                    stdin_text=batch_ok)
        check("put-batch 全过 exit 0", code == 0, code)
        check("put-batch 写了两个文件", len((payload or {}).get("files") or []) == 2,
              (payload or {}).get("files"))
        check("put-batch 两个文件都真落盘了",
              any(row.get("s") == "Roll Rate" for row in
                  rows_of_bytes((packs / "zh-cn" / "AtmosphereAutopilot.UI.il.jsonl").read_bytes())))
        check("put-batch 每行一条 journal 事件",
              _journal_lines(root) == lines_before + 2, _journal_lines(root))

        snap_before = _snapshot(packs)
        journals_before = _journal_lines(root)
        batch_bad = "\n".join([
            json.dumps({"mod": "TUFX", "text": "Half Pass One", "t": "半通过一"}, ensure_ascii=False),
            json.dumps({"mod": "TUFX", "text": "<b>Half Pass Two</b>", "t": "半通过二"},
                       ensure_ascii=False),
        ])
        code, payload, _err = _call(root, ["entry", "put-batch", "--actor", "pipeline:selftest"],
                                    stdin_text=batch_bad)
        check("put-batch 半过 → exit 4", code == 4, code)
        check("put-batch 半过 → 整批一个字节都没写（原子性）", _snapshot(packs) == snap_before)
        check("put-batch 半过 → 没有 journal 事件", _journal_lines(root) == journals_before)
        batch_schema = json.dumps({"mod": "TUFX", "text": "X Y", "t": "甲", "zz": 1})
        code, _payload, _err = _call(root, ["entry", "put-batch", "--actor", "pipeline:selftest"],
                                     stdin_text=batch_schema)
        check("put-batch 未知字段 exit 5", code == 5, code)
        code, _payload, _err = _call(root, ["entry", "put-batch", "--actor", "pipeline:selftest"],
                                     stdin_text="  \n")
        check("put-batch 空 stdin exit 2", code == 2, code)

        # ---- 12) override / glossary ----
        code, _p, _e = _call(root, ["override", "put", "--en", "Science", "--zh", "科学点",
                                    "--actor", "human:selftest"])
        check("override put 覆盖已有 en exit 0", code == 0, code)
        overrides = rows_of_bytes((packs / "overrides.jsonl").read_bytes())
        check("override put 是替换不是追加（仍 1 行）",
              len(overrides) == 1 and overrides[0]["zh"] == "科学点", overrides)
        code, _p, _e = _call(root, ["override", "put", "--en", "<b>Bold</b>", "--zh", "粗体",
                                    "--actor", "human:selftest"])
        check("override put 也过机械门禁（G4 拒绝）", code == 4, code)
        code, _p, _e = _call(root, ["glossary", "put", "--en", "Throttle", "--zh", "节流阀",
                                    "--short", "--actor", "human:selftest"])
        check("glossary put exit 0", code == 0, code)
        glossary_rows = rows_of_bytes((packs / "glossary.jsonl").read_bytes())
        check("glossary 现在是 3 条", len(glossary_rows) == 3, len(glossary_rows))
        check("glossary 新条目 short 标记保留",
              [row for row in glossary_rows if row["en"] == "Throttle"][0]["short"] is True,
              glossary_rows)

        # ---- 13) mods.json：put / list / resolve ----
        _call(root, ["mod", "put", "--mod", "TUFX", "--display", "TexturesUnlimited",
                     "--assembly", "TUFX.dll", "--folder", "000_TexturesUnlimited",
                     "--ckan", "TexturesUnlimited", "--actor", "pipeline:selftest"])
        code, payload, _err = _call(root, ["mod", "put", "--mod", "AtmosphereAutopilot.UI",
                                           "--display", "AtmosphereAutopilot UI",
                                           "--assembly", "AtmosphereAutopilot.dll",
                                           "--actor", "pipeline:selftest"])
        check("mod put 第二个 exit 0", code == 0, code)
        doc = json.loads((packs / "mods.json").read_text(encoding="utf-8"))
        ids = [item["modId"] for item in doc["mods"]]
        check("mods.json 按 modId 排序", ids == sorted(ids), ids)
        check("mods.json 带点号的 modId 原样保留",
              "AtmosphereAutopilot.UI" in ids, ids)
        code, payload, _err = _call(root, ["mod", "list"])
        check("mod list 看到 2 个 mod 且行数非零",
              code == 0 and payload["count"] == 2
              and sum(item["rows"] for item in payload["mods"]) == 6,
              code)
        code, payload, _err = _call(root, ["mod", "resolve", "--assembly", "TUFX.dll"])
        check("mod resolve 按程序集命中", code == 0 and payload["mod"]["modId"] == "TUFX", code)
        code, payload, _err = _call(root, ["mod", "resolve", "--pack", "AtmosphereAutopilot.UI.il"])
        check("mod resolve 带点号的包名原样解析",
              code == 0 and payload["mod"]["modId"] == "AtmosphereAutopilot.UI", code)
        code, _payload, _err = _call(root, ["mod", "resolve", "--assembly", "Nope.dll"])
        check("mod resolve 未登记 exit 6", code == 6, code)

        # ---- 14) validate：干净 → 0；坏行 → 5 ----
        code, payload, _err = _call(root, ["validate"])
        check("validate 干净包 exit 0", code == 0 and payload["rows"] == 6, (code, payload))
        code, payload, _err = _call(root, ["validate", "--strict"])
        check("validate --strict 也过（G2/G4/G9）", code == 0, (code, payload))
        good_cfg = cfg_path.read_bytes()
        broken = _pack_text([_row("Blue Paint", "蓝色涂装"),
                             {"h": "0" * 16, "n": "Blue Paint", "s": "Blue Paint",
                              "t": "蓝色涂装", "mod": "TUFX"}], spaced=True)
        _put_text(cfg_path, broken)
        code, payload, _err = _call(root, ["validate"])
        check("validate 发现 h 不符 exit 5", code == 5, code)
        check("validate 的问题里点出 h", "h" in json.dumps(payload, ensure_ascii=False), payload)
        cfg_path.write_bytes(good_cfg)

        # ---- 14b) p 字段：旧表示容错 + 写入用布尔 + normalize-p 幂等 ----
        # 存量的 486 行是 translate_il.py 写的 "p":1；这里造一个只含旧表示的小根，
        # 把「读得进 / 写得对 / 归一零语义变化 / 幂等 / strict-p 自证」一次跑完。
        with tempfile.TemporaryDirectory(prefix="ui18n-api-p-") as proot:
            root_p = Path(proot)
            lang_p = root_p / "packs" / "zh-cn"
            lang_p.mkdir(parents=True, exist_ok=True)
            skel = lang_p / "SkeletonMod.il.jsonl"

            def _prow(text, zh, p=None):
                row = {"h": _h(text), "n": normalize(text), "s": text, "t": zh,
                       "mod": "SkeletonMod"}
                if p is not None:
                    row["p"] = p
                return row

            _put_text(skel, _pack_text([
                _prow("Skeleton Alpha", "骨架甲", 1),
                _prow("Skeleton Beta", "骨架乙", "1"),
                _prow("Skeleton Gamma", "骨架丙", "true"),
                _prow("Skeleton Delta", "骨架丁", True),
                _prow("Skeleton Epsilon", "骨架戊"),
            ], spaced=False))
            code, payload, _err = _call(root_p, ["validate"])
            check("p 旧表示 1 / \"1\" / \"true\" 都当 True（validate exit 0）", code == 0,
                  (code, payload))
            check("validate 把 3 行旧表示记成 notice", bool(payload) and payload["pLegacy"] == 3,
                  payload)
            check("notice 里点了 apply --normalize-p",
                  "apply --normalize-p" in json.dumps(payload, ensure_ascii=False), payload)
            code, payload, _err = _call(root_p, ["validate", "--strict-p"])
            check("validate --strict-p 对旧表示 exit 5", code == 5, code)
            _put_text(lang_p / "BadP.il.jsonl", _pack_text([
                {"h": _h("Skeleton Zeta"), "n": normalize("Skeleton Zeta"),
                 "s": "Skeleton Zeta", "t": "骨架己", "mod": "BadP", "p": 2}], spaced=False))
            code, payload, _err = _call(root_p, ["validate"])
            check("p:2 这种非法值 exit 5", code == 5, (code, payload))
            (lang_p / "BadP.il.jsonl").unlink()
            code, payload, _err = _call(root_p, ["entry", "put", "--mod", "SkeletonMod",
                                                 "--text", "Brand New Skeleton",
                                                 "--t", "全新骨架", "--p",
                                                 "--actor", "pipeline:selftest"])
            check("entry put --p exit 0", code == 0, (code, payload))
            data = skel.read_bytes()
            raw = data.decode("utf-8")
            written = [row for row in rows_of_bytes(data, "SkeletonMod.il.jsonl")
                       if row.get("s") == "Brand New Skeleton"]
            check("写入侧写的是布尔 true（不是 1 / \"1\"）",
                  len(written) == 1 and written[0].get("p") is True, written)
            check("写入的字节里就是 \"p\":true", "\"p\":true" in raw, raw[-180:])
            state_before = _semantic_state(lang_p)
            code, payload, _err = _call(root_p, ["apply", "--normalize-p",
                                                 "--actor", "pipeline:selftest"])
            check("apply --normalize-p exit 0", code == 0, (code, payload))
            check("归一了 3 行", bool(payload) and payload["normalizeP"]["rows"] == 3, payload)
            check("归一自证 h→(s,t,mod) 映射相等",
                  bool(payload) and payload["normalizeP"]["mapsEqual"], payload)
            check("落盘重读映射仍逐条相等（只有 p 的写法变了）",
                  _semantic_state(lang_p) == state_before, sorted(_semantic_state(lang_p))[:3])
            raw = skel.read_bytes().decode("utf-8")
            check("归一后文件里旧表示清零",
                  "\"p\":1" not in raw and "\"p\":\"1\"" not in raw, raw[:300])
            code, payload, _err = _call(root_p, ["validate", "--strict-p"])
            check("归一后 --strict-p 转绿（自证）", code == 0, (code, payload))
            code, payload, _err = _call(root_p, ["apply", "--normalize-p",
                                                 "--actor", "pipeline:selftest"])
            check("再跑一次 normalize-p 幂等（0 行）",
                  code == 0 and payload["normalizeP"]["rows"] == 0, payload)
            journals = "".join(path.read_text(encoding="utf-8")
                               for path in (root_p / "packs" / "journal").glob("*.jsonl"))
            check("normalize-p 写了 journal", "apply.normalize-p" in journals, journals[:200])
            shot = _snapshot(root_p / "packs")
            code, payload, _err = _call(root_p, ["apply", "--normalize-p", "--dry-run",
                                                 "--actor", "pipeline:selftest"])
            check("apply --dry-run exit 0", code == 0, (code, payload))
            check("apply --dry-run 一个字节都不写",
                  _snapshot(root_p / "packs") == shot,
                  sorted(set(shot) ^ set(_snapshot(root_p / "packs")))[:5])

        # ---- 16) 投稿回路（M3）：收一次 + 二次幂等 + 七类反例 ----
        sample_path = Path(__file__).with_name("fixtures") / "submission-sample.json"
        sample = json.loads(sample_path.read_text(encoding="utf-8"))
        cand_path = packs / "candidates.jsonl"
        seed_h = _h("Vessel Integrity Check")
        seed = json.dumps({"h": seed_h, "s": "Vessel Integrity Check",
                           "src": "Vessel Integrity Check", "bucket": "candidate",
                           "reason": "sentence", "channel": "capture", "caller": "",
                           "sink": "", "count": 3, "mod": "TUFX",
                           "firstSeen": "2026-10-02T12:00:00+08:00", "covered": False},
                          ensure_ascii=False, separators=(",", ":"))
        _put_text(cand_path, seed + "\n")
        covered_now = set()
        for path in (packs / "zh-cn").glob("*.jsonl"):
            for line in path.read_text(encoding="utf-8").splitlines():
                if line.strip():
                    covered_now.add(json.loads(line)["h"])
        counter = [0]

        def accept(bundle, dry=False):
            counter[0] += 1
            path = root / ("bundle-%d.json" % counter[0])
            _put_text(path, json.dumps(bundle, ensure_ascii=False, indent=2) + "\n")
            argv = ["submit", "accept", "--actor", "pipeline:selftest",
                    "--bundle", str(path)]
            if dry:
                argv.append("--dry-run")
            return _call(root, argv)

        def with_id(bundle):
            copy = json.loads(json.dumps(bundle))
            copy["id"] = SUB.bundle_id(copy)
            return copy

        def cand_rows():
            rows = {}
            for line in cand_path.read_text(encoding="utf-8").splitlines():
                if line.strip():
                    row = json.loads(line)
                    rows[row["h"]] = row
            return rows

        shot_0 = _snapshot(packs)
        journal_0 = _journal_lines(root)
        code, payload, _err = accept(sample, dry=True)
        check("submit accept --dry-run exit 0", code == 0, (code, payload))
        check("submit accept --dry-run 一个字节都不写", _snapshot(packs) == shot_0,
              sorted(set(shot_0) ^ set(_snapshot(packs)))[:5])
        check("--dry-run 也把要写的 5 个文件报出来",
              len((payload or {}).get("changed") or []) == 5, (payload or {}).get("changed"))

        code, payload, _err = accept(sample)
        sid = (payload or {}).get("submission_id")
        check("submit accept exit 0", code == 0, (code, payload))
        check("收下的 id 与内容派生的 id 一致", sid == sample["id"], (sid, sample["id"]))
        check("accept 写了 5 个文件（meta + 2 个 mod + index + candidates）",
              len((payload or {}).get("changed") or []) == 5, (payload or {}).get("changed"))
        sub_dir = packs / "submission" / str(sid)
        check("投稿目录按 id 建", sub_dir.is_dir(), str(sub_dir))
        meta_text = (sub_dir / "meta.json").read_text(encoding="utf-8")
        meta = json.loads(meta_text)
        check("meta.json 不带 actor.installId（客户端本地去重不上报）",
              "installId" not in meta_text and "installId" not in meta.get("actor", {}),
              meta.get("actor"))
        check("meta.json 抄了脱敏计数与同意版本",
              meta.get("redactions", {}).get("paths") == 2
              and meta.get("consent", {}).get("policyVersion") == 1, meta.get("redactions"))
        mod_files = sorted(path.name for path in sub_dir.glob("*.jsonl"))
        check("按 mod 拆成两个文件", mod_files == ["FerramAerospaceResearch.jsonl", "TUFX.jsonl"],
              mod_files)
        sub_rows = []
        for path in sub_dir.glob("*.jsonl"):
            for line in path.read_text(encoding="utf-8").splitlines():
                if line.strip():
                    sub_rows.append(json.loads(line))
        check("投稿共 5 行", len(sub_rows) == 5, len(sub_rows))
        check("投稿行里没有 t 字段（这不是语言包）",
              all("t" not in row for row in sub_rows), sub_rows[:1])
        check("投稿行字段都在白名单里",
              all(set(row) <= set(SUB.ROW_FIELDS) for row in sub_rows),
              sorted({key for row in sub_rows for key in row} - set(SUB.ROW_FIELDS)))
        index_doc = json.loads((packs / "submission" / "index.json").read_text(encoding="utf-8"))
        check("index.json 记了这份投稿", sid in (index_doc.get("submissions") or {}),
              list(index_doc.get("submissions") or {}))
        events = [event for event in journal_events(ctx) if event.get("op") == "submit.accept"]
        check("journal 追加了一条 submit.accept", _journal_lines(root) == journal_0 + 1,
              _journal_lines(root))
        check("事件带 submission_id / bundle_sha256 / 4 个 files",
              len(events) == 1 and events[0].get("submission_id") == sid
              and len(events[0].get("files") or []) == 4
              and len(str(events[0].get("bundle_sha256"))) == 64, events and events[-1])
        check("事件里 candidates 分支带 file_sha256_after（journal_gate 靠它认账）",
              isinstance(((events[0].get("candidates") or {}).get("file_sha256_after")), str),
              events[0].get("candidates"))

        rows_now = cand_rows()
        blue = rows_now.get(_h("Blue Paint")) or {}
        check("已覆盖的 h 在候选表里 covered:true / reason=covered",
              blue.get("covered") is True and blue.get("reason") == "covered", blue)
        fresh = rows_now.get(_h("Aerodynamic Failure")) or {}
        check("新 h 的 channel=submit 且 src 指向投稿",
              fresh.get("channel") == "submit"
              and fresh.get("src") == "submission/" + str(sid), fresh)
        proc = rows_now.get(_h("Loading texture asset 1950/5740 (buffer=45MB)")) or {}
        check("进度串按现有分类器落 template 桶", proc.get("bucket") == "template", proc)
        seen = rows_now.get(seed_h) or {}
        check("已在表里的 h 不插行、count 累加（3 -> 5）", seen.get("count") == 5, seen)
        check("已在表里的行保留首次通道（capture 不被改成 submit）",
              seen.get("channel") == "capture", seen.get("channel"))
        check("候选表从 1 行变 5 行", len(rows_now) == 5, len(rows_now))
        check("已覆盖判断与盘上包一致（Blue Paint 在包里）", _h("Blue Paint") in covered_now,
              sorted(covered_now)[:3])

        shot_1 = _snapshot(packs)
        code, payload, _err = accept(sample)
        check("同 id 同内容二次 accept exit 0 且 already:true",
              code == 0 and (payload or {}).get("already") is True, (code, payload))
        check("幂等 accept 一个字节都不写", _snapshot(packs) == shot_1,
              sorted(set(shot_1) ^ set(_snapshot(packs)))[:5])
        check("幂等 accept 不写 journal", _journal_lines(root) == journal_0 + 1,
              _journal_lines(root))

        index_path = packs / "submission" / "index.json"
        index_text = index_path.read_text(encoding="utf-8")
        tampered = json.loads(index_text)
        tampered["submissions"][sid]["bundleSha256"] = "0" * 64
        _put_text(index_path, json.dumps(tampered, ensure_ascii=False, indent=2) + "\n")
        code, payload, _err = accept(sample)
        check("同 id 但内容不同 exit 4", code == 4, (code, payload))
        check("冲突的 error.code 是 submission-id-conflict",
              ((payload or {}).get("error") or {}).get("code") == "submission-id-conflict",
              (payload or {}).get("error"))
        check("冲突把双方完整 sha256 都报出来",
              any("0" * 64 in str(item) for item in
                  ((payload or {}).get("error") or {}).get("details") or []),
              ((payload or {}).get("error") or {}).get("details"))
        _put_text(index_path, index_text)
        check("冲突与还原后字节回到原样", _snapshot(packs) == shot_1,
              sorted(set(shot_1) ^ set(_snapshot(packs)))[:5])

        index_path.unlink()
        code, payload, _err = accept(sample)
        check("index.json 被删也能判幂等（退回 meta.json 的 bundleSha256）",
              code == 0 and (payload or {}).get("already") is True, (code, payload))
        check("缺 index 的幂等同样不写盘", _snapshot(packs).get("submission/index.json") is None
              and _journal_lines(root) == journal_0 + 1, _journal_lines(root))
        _put_text(index_path, index_text)

        no_consent = json.loads(json.dumps(sample))
        no_consent["consent"]["granted"] = False
        code, payload, _err = accept(with_id(no_consent), dry=True)
        check("consent.granted=false exit 5",
              code == 5 and ((payload or {}).get("error") or {}).get("code")
              == "submission-consent-missing", (code, payload))

        bad_id = json.loads(json.dumps(sample))
        bad_id["entries"][0]["s"] = "Blue Paint (edited)"
        code, payload, _err = accept(bad_id, dry=True)
        check("id 与内容不符 exit 5",
              code == 5 and ((payload or {}).get("error") or {}).get("code")
              == "submission-id-mismatch", (code, payload))

        stray = json.loads(json.dumps(sample))
        stray["entries"].append({"h": _h("Stray Line"), "s": "Stray Line", "mod": "NotDeclared"})
        code, payload, _err = accept(with_id(stray), dry=True)
        check("entries[].mod 不在 mods[] 里 exit 4（内容不合格不是格式坏）", code == 4,
              (code, payload))

        empty = json.loads(json.dumps(sample))
        empty["entries"] = []
        code, payload, _err = accept(with_id(empty), dry=True)
        check("entries 为空 exit 4", code == 4, (code, payload))

        missing = json.loads(json.dumps(sample))
        missing.pop("createdAt")
        code, payload, _err = accept(with_id(missing), dry=True)
        check("缺 createdAt exit 5", code == 5, (code, payload))

        bad_schema = json.loads(json.dumps(sample))
        bad_schema["schema"] = "ui18n-submission/2"
        code, payload, _err = accept(bad_schema, dry=True)
        check("schema 版本不对 exit 5", code == 5, (code, payload))

        check("六个反例都没写盘", _snapshot(packs) == shot_1,
              sorted(set(shot_1) ^ set(_snapshot(packs)))[:5])

        code, payload, _err = _call(root, ["submit", "list"])
        check("submit list exit 0 且只有 1 份",
              code == 0 and (payload or {}).get("count") == 1, (code, payload))
        code, payload, _err = _call(root, ["submit", "show", "--id", str(sid), "--entries"])
        check("submit show --entries 读出 2 个文件 / 5 行",
              code == 0 and len((payload or {}).get("files") or []) == 2
              and sum(len(item.get("entries") or [])
                      for item in (payload or {}).get("files") or []) == 5, (code, payload))
        check("submit show 的 meta 也不带 installId",
              "installId" not in json.dumps((payload or {}).get("meta"), ensure_ascii=False))
        code, payload, _err = _call(root, ["submit", "show", "--id", "0000000000000000"])
        check("submit show 查不到 exit 6", code == 6, (code, payload))

        # ---- 15) --root 隔离：临时根变了，真实仓库没变 ----
        check("临时根确实被写过（隔离测试本身有效）",
              any(path.name == "TUFX.jsonl" for path in (packs / "zh-cn").glob("*.jsonl")))
        check("临时根里有 journal", _journal_lines(root) > 0, _journal_lines(root))

    if real_before:
        real_after = {path.name: sha256_hex(path.read_bytes()) for path in real_dir.glob("*.jsonl")}
        check("真实 packs/zh-cn 一个字节都没变（--root 隔离有效）", real_after == real_before,
              "变化的文件：%s" % sorted(set(real_before) ^ set(real_after))[:5])
        check("真实 journal 没被写入",
              _journal_lines(real) == journal_before, _journal_lines(real))
    else:
        check("（提示）真实仓库不在预期位置，跳过隔离断言", True)

    return failures, total


if __name__ == "__main__":
    raise SystemExit(main())
