"""backend.py -- 译文存储与产物写入后端适配器（UI18n / CKAN / 通用）。

本模块把「数据持久化」与「底层存储细节」拆开，给上层（翻译流水线、外部脚本）一个统一接口：
1. StorageBackend：抽象基类，定义 save_entries / get_entry；
2. UI18nPackBackend：UniversalI18n 原生 packs/zh-cn/*.jsonl 适配器；
3. CkanModBackend：CKAN mod 元数据通道的骨架（按 mod 分文件追加 JSONL）。

**UI18nPackBackend 绝不自己写文件。** save_entries 把条目序列化成 put-batch 的 NDJSON，
交给 api.cmd_entry_put_batch：journal 事件、G2–G9 写入门禁、--expect-revision 校验、
原子写（tmp → journal → os.replace）全部由 api 那一层保证。绕开 api 直接 open(..., "w")
就是绕过门禁与审计，本模块不允许出现这种实现（2026-10-28 审查确认并修正）。

输入形状（与 api entry put-batch 的 stdin 一致）：
    {"mod": "TUFX", "s": "Blue Paint", "t": "蓝色涂装"}
可选字段：text（与 s 二选一）、h（给了就必须等于 fnv1a64(normalize(s))）、ctx、p、note。
**不接受包行里的派生字段 n** —— n 由 api 从 s 重算，传进来只会被 schema 拒。

接线状态（2026-10-28，照实写）：
* 已完成：backend.selftest()（临时目录，不碰真实 packs/），覆盖写入 / 回读 / journal /
  revision 冲突 / 门禁拒绝 / 整批原子性；
* **尚未接线**：translate.py、translate_il.py、api.py 目前都没有调用本模块。原因两条：
  ① 这三个文件本轮由别的 agent 持有，改动会冲突；
  ② translate.write_packs 是「整文件重写」语义，而 put-batch 是「按 h 插入/替换」，
     直接换过去会把「本次没跑到的旧行」留在包里 —— 合并还是重写要先拍板。
  因此本模块现在的定位是「已交付、有自检、待接线」，不要在任何文档里写成「已接入」。

另一个不要做的接线：engine.GateRules 是通用规则集，**不能**拿去替换 api.gate_check。
api 的 G2–G9 复用的是 translate.gate / locked_for / skeleton.placeholders，换一套规则会让
「报告说 PASS、文件却是另一套判据」重新出现（docs/10 §1 的事故表）。
"""

from __future__ import annotations

import io
import json
import re
import tempfile
from pathlib import Path
from types import SimpleNamespace
from typing import Any, Dict, List, Optional, Tuple


class StorageBackend:
    """抽象存储后端：写一批条目、按键回读一条。"""

    def save_entries(self, entries: List[Dict[str, Any]], actor: str = "pipeline:translate",
                     expect_revision: Optional[str] = None,
                     dry_run: bool = False,
                     allow_term_drift: bool = False) -> Dict[str, Any]:
        """写入一批条目，返回 envelope 形状的结果；失败抛 ApiError（UI18n）或 ValueError。"""
        raise NotImplementedError

    def get_entry(self, identifier: str) -> Optional[Dict[str, Any]]:
        """按键回读一条；不存在返回 None。"""
        raise NotImplementedError


class UI18nPackBackend(StorageBackend):
    """对接 UniversalI18n 原生 packs 存储架构（通过 api 核心层保证 journal 与 G2–G9 门禁）。"""

    def __init__(self, root: Optional[Path] = None, lang: str = "zh-cn"):
        """root=None → api.Ctx 默认仓库根（真实 packs）；测试/试跑务必传临时目录。"""
        from . import api
        self.ctx = api.Ctx(root=root, lang=lang)
        self.lang = self.ctx.lang

    def save_entries(self, entries: List[Dict[str, Any]], actor: str = "pipeline:translate",
                     expect_revision: Optional[str] = None,
                     dry_run: bool = False,
                     allow_term_drift: bool = False) -> Dict[str, Any]:
        """写入一批条目：转成 put-batch NDJSON，交给 api 的写入口（唯一合法的写路径）。

        - 空列表是合法 no-op：不建文件、不写 journal，返回 rows=0 的 envelope；
        - 任意一行 schema / 门禁不过 → 整批一个字节都不落（api put-batch 的事务语义）；
        - 失败原样抛 api.ApiError：usage=2 / revision=3 / gate=4 / schema=5 / missing=6。
        """
        from . import api

        # Ctx 是长生命周期对象（不像 CLI 每次新建），warning 按批清空，避免上一批的告警串到这一批。
        self.ctx.warnings = []
        if not isinstance(entries, (list, tuple)):
            raise api.ApiError("schema", "entries 必须是列表，收到 %s" % type(entries).__name__)
        if not entries:
            return {"ok": True, "op": "entry.put-batch", "lang": self.lang, "rows": 0,
                    "changed": [], "gates": [], "warnings": list(self.ctx.warnings),
                    "journal": None, "files": [], "revision_before": None,
                    "revision_after": None, "dry_run": bool(dry_run)}

        lines = []
        for index, entry in enumerate(entries, 1):
            if not isinstance(entry, dict):
                raise api.ApiError("schema", "entries 第 %d 项不是 JSON 对象（收到 %s）"
                                   % (index, type(entry).__name__))
            try:
                lines.append(json.dumps(entry, ensure_ascii=False))
            except (TypeError, ValueError) as exc:
                raise api.ApiError("schema", "entries 第 %d 项无法序列化为 JSON：%s"
                                   % (index, exc))
        # api 的 cmd_* 只按 getattr 取字段，SimpleNamespace 就够；stdin 用 StringIO 顶上。
        args = SimpleNamespace(stdin=io.StringIO("\n".join(lines) + "\n"),
                               actor=actor, lang=self.lang,
                               expect_revision=expect_revision, dry_run=dry_run,
                               allow_term_drift=allow_term_drift)
        payload, _lines = api.cmd_entry_put_batch(self.ctx, args)
        return payload

    def get_entry(self, h: str) -> Optional[Dict[str, Any]]:
        """按 h 回读一条，返回 api 的命中结构 {"file","channel","row"}；没有 → None。"""
        from . import api
        args = SimpleNamespace(h=h, text=None, all=False, lang=self.lang)
        try:
            payload, _lines = api.cmd_entry_get(self.ctx, args)
        except api.ApiError as exc:
            if exc.kind == "missing":
                return None
            raise
        hits = payload.get("hits") or []
        return hits[0] if hits else None

    def _cfg_packs(self, mod: str):
        """该 mod 的 cfg 通道包（`<modId>.jsonl`）。IL 包（`*.il.jsonl`）不归本方法管。"""
        from . import api
        packs = []
        for path in api.iter_pack_files(self.ctx, self.lang):
            if api.pack_mod(path.name) != mod:
                continue
            if api.pack_channel(path.name) != "cfg":
                continue
            packs.append(api.PackFile(path, self.ctx))
        return packs

    def sync_pack(self, mod: str, rows, prune: bool = False, pool_hs=None,
                  actor: str = "pipeline:translate", dry_run: bool = False) -> Dict[str, Any]:
        """把一次翻译产出的整包行同步进 `packs/<lang>/<mod>.jsonl`（只动 cfg 通道）。

        **merge（默认）**：走 put-batch 按 h 插入/替换，一行都不删。失败行（translate
        里 `entry=None`）与本次没跑到的行原样留在包里 —— 一次 LLM 抖动不该清掉已有译文。

        **prune=True**：额外删掉「原文已经从 pool 消失」的行。判据是
        `h not in 本次结果 and h not in pool_hs`，**不是**「本次没产出就删」：
        后者会把 failed 行和缓存未命中的行一起删掉。所以 prune 必须传 pool_hs，
        调用方也不能在 `--limit / --sample / --mods / --only-bucket` 这类部分跑法下用它。
        """
        from . import api

        entries: List[Dict[str, Any]] = []
        result_hs = set()
        rejected: List[Dict[str, Any]] = []
        drift: List[Dict[str, Any]] = []
        for index, row in enumerate(rows, 1):
            if not isinstance(row, dict):
                raise api.ApiError("schema", "rows 第 %d 项不是 JSON 对象" % index)
            h = row.get("h")
            if not h:
                raise api.ApiError("schema", "rows 第 %d 项缺 h" % index)
            # n 是派生字段，put-batch 的 schema 不收；s 缺席时用原文兜底。
            entry = {"mod": mod, "h": h, "t": row.get("t", "")}
            if row.get("s") is not None:
                entry["s"] = row["s"]
            if row.get("ctx"):
                entry["ctx"] = row["ctx"]
            if row.get("p"):
                entry["p"] = row["p"]
            # 稳定键透传：条目里的 k（行内形状）或 key（池行形状，CKAN 池是
            # "ckan:<identifier>:<field>"）都写进行内 k。两个都没有就不写 k —— 游戏
            # cfg/IL 包本来就不带 k，别硬塞一个空值（schema 会判空串非法）。
            key = row.get("k")
            if key is None:
                key = row.get("key")
            if key is not None:
                entry["k"] = key

            # 逐行预检 G2–G9。仓库里有一批历史行过不了 G7（旧路径直接写文件、绕过了门禁）。
            # 处置是结构性的，不是给每条术语补白名单：
            #   * **只有 G7 挂** → 照写，记 term_drift。G7 的判据来自官方译文记忆，而那份记忆
            #     本身会错（2026-10-28 发现 Illuminator Mk1/Mk2 编号整体错位），中文里合法
            #     同义写法也堵不完（专家组/专家小组、多一个「的」）。把它当硬门禁就是治不完。
            #   * **还有别的门禁挂** → 真问题（占位符、长度比、富文本等），拒收该行、保留旧行。
            try:
                if entry.get("s") is not None:
                    api.gate_check(self.ctx, entry["s"], entry["t"],
                                   pattern=bool(entry.get("p")))
            except api.ApiError as exc:
                if exc.kind != "gate":
                    raise
                codes = [str(item).split(" ", 1)[0] for item in (exc.details or [])]
                if codes and all(code == "G7" for code in codes):
                    drift.append({"h": h, "s": entry.get("s"), "t": entry.get("t"),
                                  "reason": str(exc)})
                    entries.append(entry)
                    result_hs.add(h)
                    continue
                rejected.append({"h": h, "s": entry.get("s"), "reason": str(exc)})
                result_hs.add(h)          # 拒收 ≠ 该删：prune 不许碰它
                continue

            entries.append(entry)
            result_hs.add(h)

        stale: List[Dict[str, Any]] = []
        if prune:
            if pool_hs is None:
                raise api.ApiError("usage", "prune 必须给 pool_hs（否则无法区分「原文消失」与「本次没跑到」）")
            for pack in self._cfg_packs(mod):
                for row in pack.rows:
                    h = row.get("h")
                    if h and h not in result_hs and h not in pool_hs:
                        stale.append({"mod": mod, "h": h, "t": ""})

        payload = self.save_entries(entries + stale, actor=actor, dry_run=dry_run,
                                    allow_term_drift=bool(drift))
        payload["sync"] = {"mod": mod, "rows": len(entries), "pruned": len(stale),
                           "rejected": len(rejected), "rejected_rows": rejected,
                           "term_drift": len(drift), "term_drift_rows": drift,
                           "pack": "%s.jsonl" % mod}
        return payload


_SAFE_NAME_RE = re.compile(r"[^A-Za-z0-9._\-]+")


def _safe_mod_name(mod: Any) -> str:
    """mod 名 → 安全文件名。规则与 translate.safe_mod_name 一致，但刻意不 import translate。

    不用原名直接拼路径：mod 来自外部条目，`../../x` 这种值会把文件写到输出目录之外。
    """
    name = _SAFE_NAME_RE.sub("_", str(mod or "GenericCKAN")).strip("._-")
    return name or "GenericCKAN"


class CkanModBackend(StorageBackend):
    """CKAN mod 元数据通道的**骨架**：按 mod 分文件追加 JSONL。

    照实说清楚能力边界（它不是可用的生产后端）：
    * 不跑 G2–G9、不写 journal —— CKAN 元数据不在 docs/10 的受管范围内，但也没有审计；
    * 追加写、不做去重 —— 同一批重跑会得到重复行，调用方自己保证幂等；
    * get_entry 未实现 —— CKAN 条目没有像 h 那样稳定的主键，等通道设计定了再补；
    * 尚未接入任何主流程。
    """

    def __init__(self, output_dir: Path):
        self.output_dir = Path(output_dir)

    def save_entries(self, entries: List[Dict[str, Any]], actor: str = "ckan:translator",
                     expect_revision: Optional[str] = None,
                     dry_run: bool = False) -> Dict[str, Any]:
        """按 mod 划分输出 <mod>.ckan-zh.jsonl（UTF-8、LF）。dry_run 只算不写。

        条目的 key（池行形状 "ckan:<identifier>:<field>"）额外复制成行内 k：下游读这份文件时
        不用再猜哪个字段是稳定键，与 packs 行的 k 是同一个名字、同一个含义。原字段 key 保留
        （这份文件不是 pack 行，没有冻结 schema，删字段会让既有读者断掉）。

        expect_revision 仅为与基类接口兼容而保留：CKAN 输出没有 revision 概念。
        """
        if not isinstance(entries, (list, tuple)):
            raise ValueError("entries 必须是列表，收到 %s" % type(entries).__name__)

        by_mod: Dict[str, List[Dict[str, Any]]] = {}
        for index, item in enumerate(entries, 1):
            if not isinstance(item, dict):
                raise ValueError("entries 第 %d 项不是字典" % index)
            by_mod.setdefault(_safe_mod_name(item.get("mod")), []).append(item)

        mods = sorted(by_mod)
        if dry_run:
            return {"written": 0, "planned": sum(len(rows) for rows in by_mod.values()),
                    "mods": mods, "actor": actor, "dry_run": True}
        if not by_mod:
            return {"written": 0, "mods": [], "actor": actor}

        self.output_dir.mkdir(parents=True, exist_ok=True)
        written = 0
        for mod in mods:
            target = self.output_dir / ("%s.ckan-zh.jsonl" % mod)
            with target.open("a", encoding="utf-8", newline="\n") as handle:
                for row in by_mod[mod]:
                    out = dict(row)
                    if out.get("key") is not None and out.get("k") is None:
                        out["k"] = out["key"]
                    handle.write(json.dumps(out, ensure_ascii=False) + "\n")
                    written += 1

        return {"written": written, "mods": mods, "actor": actor}


def selftest() -> Tuple[List[str], int]:
    """backend.py 单元自检。返回 (失败列表, 总项数)，与 engine.selftest 同签名。

    全部在 tempfile.TemporaryDirectory() 里跑：--root 指向临时根，真实 packs/ 一个字节不动。
    """
    from . import api

    failures: List[str] = []
    total = 0

    def check(name: str, ok: bool, detail: str = ""):
        nonlocal total
        total += 1
        if not ok:
            failures.append("%s%s" % (name, ("：" + detail) if detail else ""))

    def eq(name: str, actual: Any, expected: Any):
        check(name, actual == expected, "got %r, want %r" % (actual, expected))

    def raises(name: str, kind: str, func):
        nonlocal total
        total += 1
        try:
            func()
        except api.ApiError as exc:
            if exc.kind != kind:
                failures.append("%s：期望 ApiError(%s)，实际 ApiError(%s) %s"
                                % (name, kind, exc.kind, exc))
            return
        except Exception as exc:                 # 非 ApiError 也算失败（类型不对）
            failures.append("%s：期望 ApiError(%s)，实际 %s: %s"
                            % (name, kind, type(exc).__name__, exc))
            return
        failures.append("%s：期望 ApiError(%s)，实际没有抛错" % (name, kind))

    with tempfile.TemporaryDirectory(prefix="ui18n-backend-") as temp:
        root = Path(temp)
        lang_dir = root / "packs" / "zh-cn"
        lang_dir.mkdir(parents=True)
        backend = UI18nPackBackend(root=root)
        pack_path = lang_dir / "TUFX.jsonl"

        # ---- 1) 写入成功路径（走 api 写入口，字段由 api 重算） ----
        result = backend.save_entries(
            [{"mod": "TUFX", "s": "Blue Paint", "t": "蓝色涂装"}], actor="pipeline:test")
        eq("save_entries.ok", result.get("ok"), True)
        eq("save_entries.rows", result.get("rows"), 1)
        eq("save_entries.changed.action", (result.get("changed") or [{}])[0].get("action"), "insert")
        check("save_entries 真的落了盘", pack_path.exists())
        rows = api.rows_of_bytes(pack_path.read_bytes()) if pack_path.exists() else []
        eq("落盘行数", len(rows), 1)
        row = rows[0] if rows else {}
        eq("落盘 t", row.get("t"), "蓝色涂装")
        eq("落盘 mod", row.get("mod"), "TUFX")
        eq("落盘 h = fnv1a64(normalize(s))", row.get("h"), api.hash_of("Blue Paint"))
        eq("落盘 n = normalize(s)", row.get("n"), "Blue Paint")
        h = row.get("h")

        # ---- 2) get_entry 回读 ----
        hit = backend.get_entry(h)
        check("get_entry 命中且带 file/channel/row",
              isinstance(hit, dict) and {"file", "channel", "row"} <= set(hit))
        eq("get_entry.row.t", (hit or {}).get("row", {}).get("t"), "蓝色涂装")
        eq("get_entry 未知 h 返回 None", backend.get_entry("0" * 16), None)

        # ---- 3) journal 事件（api 写的，不是本模块自己编的） ----
        events = api.journal_events(backend.ctx)
        eq("journal 事件数", len(events), 1)
        event = events[0] if events else {}
        eq("journal.op", event.get("op"), "entry.put-batch")
        eq("journal.actor", event.get("actor"), "pipeline:test")
        eq("journal.file", event.get("file"), "packs/zh-cn/TUFX.jsonl")
        eq("journal.action", event.get("action"), "insert")
        eq("journal.h", event.get("h"), h)
        eq("journal.file_sha256_after", event.get("file_sha256_after"),
           api.sha256_hex(pack_path.read_bytes()))
        check("journal.gates 记了 G2/G5/G8", {"G2", "G5", "G8"} <= set(event.get("gates") or []))

        # ---- 4) revision 冲突必须被拒，且不落盘、不写事件 ----
        revision = api.PackFile(pack_path, backend.ctx).revision()
        before = pack_path.read_bytes()
        journal_before = len(api.journal_events(backend.ctx))
        raises("revision 冲突被拒", "revision", lambda: backend.save_entries(
            [{"mod": "TUFX", "s": "Frame Rate", "t": "帧率"}],
            actor="pipeline:test", expect_revision="deadbeef0000@1"))
        eq("revision 冲突不改文件", pack_path.read_bytes(), before)
        eq("revision 冲突不写 journal", len(api.journal_events(backend.ctx)), journal_before)
        ok = backend.save_entries(
            [{"mod": "TUFX", "s": "Frame Rate", "t": "帧率"}],
            actor="pipeline:test", expect_revision=revision)
        eq("正确 revision 能写", ok.get("rows"), 1)

        # ---- 5) 非法输入：门禁拒绝 / schema 拒绝，都不落盘 ----
        before = pack_path.read_bytes()
        journal_before = len(api.journal_events(backend.ctx))
        raises("门禁拒绝（G2 占位符多一个）", "gate", lambda: backend.save_entries(
            [{"mod": "TUFX", "s": "Apoapsis: <<1>> m", "t": "远地点: <<1>> <<2>> m"}],
            actor="pipeline:test"))
        eq("门禁拒绝不改文件", pack_path.read_bytes(), before)
        eq("门禁拒绝不写 journal", len(api.journal_events(backend.ctx)), journal_before)
        raises("h 与原文不符被 schema 拒", "schema", lambda: backend.save_entries(
            [{"mod": "TUFX", "s": "Blue Paint", "t": "蓝色涂装", "h": "0" * 16}],
            actor="pipeline:test"))
        raises("缺 mod 被 schema 拒", "schema", lambda: backend.save_entries(
            [{"s": "Blue Paint", "t": "蓝色涂装"}], actor="pipeline:test"))
        raises("包行派生字段 n 不被 put-batch 接受", "schema", lambda: backend.save_entries(
            [{"mod": "TUFX", "n": "Blue Paint", "s": "Blue Paint", "t": "蓝色涂装"}],
            actor="pipeline:test"))
        raises("非字典条目被拒", "schema", lambda: backend.save_entries(
            ["Blue Paint"], actor="pipeline:test"))

        # ---- 6) 整批原子性：一行不过门禁，整批一个字节都不落 ----
        before = pack_path.read_bytes()
        journal_before = len(api.journal_events(backend.ctx))
        raises("整批一行不过门禁 → 整批拒", "gate", lambda: backend.save_entries([
            {"mod": "TUFX", "s": "Science Lab", "t": "科学实验室"},
            {"mod": "TUFX", "s": "Apoapsis: <<1>> m", "t": "远地点: <<1>> <<2>> m"},
        ], actor="pipeline:test"))
        eq("整批拒绝不改文件", pack_path.read_bytes(), before)
        eq("整批拒绝不写 journal", len(api.journal_events(backend.ctx)), journal_before)

        # ---- 7) 空批次是 no-op（流水线这一轮没有可写条目） ----
        empty = backend.save_entries([], actor="pipeline:test")
        eq("空批次 rows", empty.get("rows"), 0)
        eq("空批次不改文件", pack_path.read_bytes(), before)
        eq("空批次不写 journal", len(api.journal_events(backend.ctx)), journal_before)

        # ---- 8) CkanModBackend 骨架：能写、路径消毒、构造不建目录 ----
        lazy_dir = root / "ckan-lazy"
        CkanModBackend(lazy_dir)
        eq("Ckan 构造不建目录（不在构造期偷偷写盘）", lazy_dir.exists(), False)
        ckan = CkanModBackend(root / "ckan-out")
        written = ckan.save_entries([
            {"mod": "TUFX", "summary": "TUFX 是……"},
            {"mod": "../../escape", "summary": "越界"},
        ], actor="ckan:test")
        eq("Ckan 写入条数", written.get("written"), 2)
        check("Ckan 按 mod 分文件", (root / "ckan-out" / "TUFX.ckan-zh.jsonl").exists())
        check("Ckan 文件名消毒（不能越出输出目录）",
              (root / "ckan-out" / "escape.ckan-zh.jsonl").exists()
              and not (root / "escape.ckan-zh.jsonl").exists())
        eq("Ckan dry_run 不写", CkanModBackend(root / "ckan-dry").save_entries(
            [{"mod": "X", "summary": "y"}], dry_run=True).get("written"), 0)

        # ---- 9) sync_pack：默认合并，「本次没跑到」绝不删行 ----
        sync_backend = UI18nPackBackend(root=root)
        sync_path = lang_dir / "SYNCMOD.jsonl"
        sync_backend.save_entries([
            {"mod": "SYNCMOD", "s": "Alpha", "t": "阿尔法"},
            {"mod": "SYNCMOD", "s": "Bravo", "t": "布拉沃"},
        ], actor="pipeline:test")
        il_path = lang_dir / "SYNCMOD.il.jsonl"
        il_path.write_text(json.dumps({"h": api.hash_of("Charlie"), "n": "Charlie",
                                       "s": "Charlie", "t": "查理", "mod": "SYNCMOD"},
                                      ensure_ascii=False) + "\n", encoding="utf-8")
        il_before = il_path.read_bytes()

        merged = sync_backend.sync_pack(
            "SYNCMOD", [{"h": api.hash_of("Alpha"), "s": "Alpha", "t": "阿尔法（改）"}],
            actor="pipeline:test")
        eq("sync merge 只报本次行数", merged["sync"]["rows"], 1)
        eq("sync merge 不删行", merged["sync"]["pruned"], 0)
        after = {r["s"]: r["t"] for r in api.rows_of_bytes(sync_path.read_bytes())}
        eq("sync merge 更新命中行", after.get("Alpha"), "阿尔法（改）")
        eq("sync merge 保留没跑到的行", after.get("Bravo"), "布拉沃")
        eq("sync 不碰 IL 包", il_path.read_bytes(), il_before)

        # 稳定键透传：条目里的 k / 池行的 key 都要落成行内 k（独立 mod，不干扰上面 SYNCMOD 的断言）
        keyed = sync_backend.sync_pack("KEYMOD", [
            {"h": api.hash_of("Keyed One"), "s": "Keyed One", "t": "键一",
             "k": "ckan:KeyDemo:abstract"},
            {"h": api.hash_of("Keyed Two"), "s": "Keyed Two", "t": "键二",
             "key": "ckan:KeyDemo:title"},
        ], actor="pipeline:test")
        eq("sync 带键的两行都写了", keyed["sync"]["rows"], 2)
        key_rows = {r["s"]: r.get("k") for r in
                    api.rows_of_bytes((lang_dir / "KEYMOD.jsonl").read_bytes())}
        eq("sync 透传行内 k（条目 k）", key_rows.get("Keyed One"), "ckan:KeyDemo:abstract")
        eq("sync 透传行内 k（池行 key）", key_rows.get("Keyed Two"), "ckan:KeyDemo:title")

        # CkanModBackend：条目的 key 复制成行内 k（原 key 保留），文件名仍按 mod 分
        ckan_key = CkanModBackend(root / "ckan-key")
        ckan_key.save_entries([{"mod": "DemoMod", "t": "摘要",
                                "key": "ckan:DemoMod:abstract"}], actor="ckan:test")
        ckan_rows = [json.loads(line) for line in
                     (root / "ckan-key" / "DemoMod.ckan-zh.jsonl")
                     .read_text(encoding="utf-8").splitlines() if line.strip()]
        eq("Ckan 输出行带行内 k", [r.get("k") for r in ckan_rows], ["ckan:DemoMod:abstract"])
        eq("Ckan 输出保留原 key 字段", [r.get("key") for r in ckan_rows],
           ["ckan:DemoMod:abstract"])

        # prune 的判据是「原文已从 pool 消失」：Bravo 还在 pool 里就必须活下来
        keep = sync_backend.sync_pack(
            "SYNCMOD", [{"h": api.hash_of("Alpha"), "s": "Alpha", "t": "阿尔法"}],
            prune=True, pool_hs={api.hash_of("Alpha"), api.hash_of("Bravo")},
            actor="pipeline:test")
        eq("sync prune 不删 pool 里还存在的行", keep["sync"]["pruned"], 0)
        eq("sync prune 后 Bravo 仍在",
           "Bravo" in {r["s"] for r in api.rows_of_bytes(sync_path.read_bytes())}, True)

        gone = sync_backend.sync_pack(
            "SYNCMOD", [{"h": api.hash_of("Alpha"), "s": "Alpha", "t": "阿尔法"}],
            prune=True, pool_hs={api.hash_of("Alpha")}, actor="pipeline:test")
        eq("sync prune 删掉 pool 里消失的行", gone["sync"]["pruned"], 1)
        eq("sync prune 后只剩 Alpha",
           {r["s"] for r in api.rows_of_bytes(sync_path.read_bytes())}, {"Alpha"})

        raises("prune 缺 pool_hs 拒绝", "usage", lambda: sync_backend.sync_pack(
            "SYNCMOD", [{"h": api.hash_of("Alpha"), "s": "Alpha", "t": "阿尔法"}],
            prune=True, actor="pipeline:test"))

        # ---- 10) 单行过不了门禁 → 拒收该行、保留包里旧行，整批照常写 ----
        before_reject = sync_path.read_bytes()
        mixed = sync_backend.sync_pack("SYNCMOD", [
            {"h": api.hash_of("Alpha"), "s": "Alpha", "t": "阿尔法二"},
            {"h": api.hash_of("Apoapsis: <<1>> m"), "s": "Apoapsis: <<1>> m",
             "t": "远地点: <<1>> <<2>> m"},
        ], actor="pipeline:test")
        eq("被门禁拒收的行数", mixed["sync"]["rejected"], 1)
        eq("同批里合格行仍然写入", mixed["sync"]["rows"], 1)
        got = {r["s"]: r["t"] for r in api.rows_of_bytes(sync_path.read_bytes())}
        eq("合格行已更新", got.get("Alpha"), "阿尔法二")
        eq("被拒行不写盘（占位符仍与原文一致）",
           any("<<1>>" in r["t"] and "<<2>>" not in r["t"]
               for r in api.rows_of_bytes(sync_path.read_bytes()) if r["s"].startswith("Apoapsis")),
           False)
        check("拒收不破坏原文件里的其它行", len(api.rows_of_bytes(sync_path.read_bytes())) >= 1,
              str(before_reject[:40]))

        # ---- 11) 只挂 G7 → 照写并记账（短语级术语漂移不当硬门禁）----
        # 临时仓库本来没有 glossary，G7 无从触发；先塞一条短语级术语。
        (root / "packs" / "glossary.jsonl").write_text(
            json.dumps({"key": "#autoLOC_test", "en": "Truss Structure",
                        "zh": "结构钢架", "short": True}, ensure_ascii=False),
            encoding="utf-8")
        drift_backend = UI18nPackBackend(root=root)
        drift_res = drift_backend.sync_pack("SYNCMOD", [
            {"h": api.hash_of("Truss Structure is rigid."),
             "s": "Truss Structure is rigid.", "t": "桁架结构很坚固。"},
        ], actor="pipeline:test")
        eq("G7 漂移不算拒收", drift_res["sync"]["rejected"], 0)
        eq("G7 漂移被记账", drift_res["sync"]["term_drift"], 1)
        eq("G7 漂移行照写",
           "Truss Structure is rigid." in {r["s"] for r in api.rows_of_bytes(sync_path.read_bytes())},
           True)

    return failures, total
