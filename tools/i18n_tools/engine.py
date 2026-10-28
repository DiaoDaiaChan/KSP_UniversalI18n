"""engine.py -- 通用翻译引擎与门禁抽象（解耦核心）。

本模块独立于具体的 KSP/UI18n 文件存储路径与 JSONL 格式，供 UI18n 仓库主流程、CKAN Mod 翻译及外部脚本复用：
1. 数据结构契约：TranslationItem / TranslationBatch / GateResult；
2. 通用门禁：可配置的 G1–G9 规则集与自定义校验器；
3. 通用管道 TranslationEngine：纯内存/自定义源的处理循环，支持掩码、术语注入、重试与门禁。
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from typing import Any, Callable, Dict, List, Optional, Set, Tuple

from . import mask as masklib
from .fnv import hash_text
from .llm import LlmClient
from .textnorm import has_cjk, normalize


@dataclass
class TranslationItem:
    """通用的单条翻译单元。"""
    id: Any
    text: str
    role: str = "text"
    mod: str = "General"
    meta: Dict[str, Any] = field(default_factory=dict)
    masked: Optional[str] = None
    res: Any = None
    curated: Optional[str] = None
    keep_auto: bool = False


@dataclass
class TranslationBatch:
    """一组打包后的待译项。"""
    batch_id: int
    items: List[TranslationItem]
    bucket: str = "mid"
    mod: Optional[str] = None


@dataclass
class GateResult:
    """门禁校验产物。"""
    restored: str
    failed: List[str]
    notes: Dict[str, str]

    @property
    def passed(self) -> bool:
        return len(self.failed) == 0


class GateRules:
    """通用门禁配置与校验集合。"""

    CONVENTIONS: Tuple[Tuple[str, str], ...] = (
        ("Booster", "助推器"),
        ("Oxidizer", "氧化剂"),
        ("Throttle", "节流阀"),
        ("Gimbal", "万向节"),
        ("Specific Impulse", "比冲"),
        ("Apoapsis", "远地点"),
        ("Periapsis", "近地点"),
        ("Delta-V", "Δv"),
        ("Center of Mass", "质心"),
        ("Center of Pressure", "压心"),
        ("Reaction Wheel", "动量轮"),
        ("Monopropellant", "单组元推进剂"),
    )

    CONVENTION_ALIASES: Dict[str, Tuple[str, ...]] = {
        "Booster": ("助推",),
        "Oxidizer": ("富氧", "氧化"),
        "Throttle": ("节流", "油门"),
        "Gimbal": ("万向",),
        "Apoapsis": ("远拱点",),
        "Periapsis": ("近拱点",),
        "Delta-V": ("ΔV", "速度增量"),
        "Center of Mass": ("重心",),
        "Center of Pressure": ("压力中心",),
        "Reaction Wheel": ("反作用轮",),
        "Monopropellant": ("单组元", "单元推进剂"),
    }

    def __init__(self,
                 enabled_gates: Optional[Set[str]] = None,
                 conventions: Optional[Tuple[Tuple[str, str], ...]] = None,
                 convention_aliases: Optional[Dict[str, Tuple[str, ...]]] = None,
                 forbidden_terms: Optional[Dict[str, List[str]]] = None):
        self.enabled_gates = enabled_gates if enabled_gates is not None else {"G2", "G3", "G4", "G5", "G6", "G7", "G8"}
        self.conventions = conventions if conventions is not None else self.CONVENTIONS
        self.convention_aliases = convention_aliases if convention_aliases is not None else self.CONVENTION_ALIASES
        self.forbidden_terms = forbidden_terms or {}

    def check(self, item: TranslationItem, zh_raw: str, locked_pairs: Optional[List[Tuple[str, Any]]] = None) -> GateResult:
        source = item.text
        res = item.res
        failed: List[str] = []
        notes: Dict[str, str] = {}
        if not isinstance(zh_raw, str):
            zh_raw = "" if zh_raw is None else str(zh_raw)

        if "G5" in self.enabled_gates:
            want = masklib.sentinel_multiset(res.text) if res else {}
            got = masklib.sentinel_multiset(zh_raw)
            if want != got:
                failed.append("G5")
                notes["G5"] = f"哨兵多重集 期望{want} 实际{got}"
            if res is not None:
                invented = masklib.unknown_sentinels(zh_raw, res)
                if invented:
                    failed.append("G5")
                    notes.setdefault("G5", f"表外哨兵 {invented}")

        restored = masklib.restore(zh_raw, res) if res is not None else zh_raw
        if "G5" in self.enabled_gates and masklib.sentinel_leaks(restored):
            failed.append("G5")
            notes.setdefault("G5", "还原后残留哨兵")

        if "G2" in self.enabled_gates:
            if masklib.count_placeholders(source) != masklib.count_placeholders(restored):
                failed.append("G2")
                notes["G2"] = f"占位符 期望{masklib.count_placeholders(source)} 实际{masklib.count_placeholders(restored)}"

        if "G3" in self.enabled_gates:
            if masklib.count_formats(source) != masklib.count_formats(restored):
                failed.append("G3")
                notes["G3"] = f"格式符 期望{masklib.count_formats(source)} 实际{masklib.count_formats(restored)}"

        if "G4" in self.enabled_gates:
            if masklib.count_rich_tags(source) != masklib.count_rich_tags(restored):
                failed.append("G4")
                notes["G4"] = f"标签 期望{masklib.count_rich_tags(source)} 实际{masklib.count_rich_tags(restored)}"

        if "G6" in self.enabled_gates:
            if len(source) >= 9 and not source.lstrip().startswith("#"):
                ratio = len(restored) / len(source)
                if not (0.15 <= ratio <= 1.8):
                    failed.append("G6")
                    notes["G6"] = f"长度比 {ratio:.2f}"

        if "G7" in self.enabled_gates and locked_pairs:
            for en, want_zh in locked_pairs:
                if len(en) < 2 or not re.search(r"\b" + re.escape(en) + r"\b", source, re.IGNORECASE):
                    continue
                if not self._accept_term(en, want_zh, restored):
                    failed.append("G7")
                    notes["G7"] = f"术语 {en}={want_zh} 未出现在译文"
                    break

        if "G7" in self.enabled_gates and self.forbidden_terms:
            for en, bad_list in self.forbidden_terms.items():
                if re.search(r"\b" + re.escape(en) + r"\b", source, re.IGNORECASE):
                    for bad in bad_list:
                        if bad and bad in restored:
                            if "G7" not in failed:
                                failed.append("G7")
                            notes["G7"] = f"出现禁译词/歧义词: {bad} (术语: {en})"
                            break

        if "G8" in self.enabled_gates:
            if not restored.strip():
                failed.append("G8")
                notes["G8"] = "空译文"
            elif restored.strip() == source.strip():
                failed.append("G8")
                notes["G8"] = "译文等于原文"
            elif not has_cjk(restored):
                failed.append("G8")
                notes["G8"] = "译文无中文"

        return GateResult(restored=restored, failed=failed, notes=notes)

    def _accept_term(self, en: str, want_zh: Any, restored: str) -> bool:
        if not restored:
            return False
        wanted = want_zh if isinstance(want_zh, (tuple, list)) else (want_zh,)
        for want in wanted:
            if not want:
                continue
            if want in restored or want.lower() in restored.lower():
                return True
        for alt in self.convention_aliases.get(en, ()):
            if alt in restored or alt.lower() in restored.lower():
                return True
        return False


class TranslationEngine:
    """通用核心翻译引擎：无状态、参数化，支持灵活集成。"""

    SYSTEM_PROMPT_TEMPLATE = """你是一位专业的本地化翻译专家与技术工程师。
你正在进行文本的高质量英译中汉化。

【输出格式】
只输出 JSON：{"items":[{"id":<id>,"zh":"<译文>"}]}
不输出解释、不输出 markdown 代码围栏、不在 JSON 前后加任何字符。
id 必须与输入一一对应，不增不减。

【输入格式】
输入是 JSON：{"batch":[{"id":1,"t":"原文","role":"角色/字段","mod":"领域/来源"}]}
t 是要翻译的英文原文，role 与 mod 只用于判断语境，不要翻译也不要回显。

【绝对不可改动】
- 占位符 <<1>> <<2>>：数量和编号必须完全一致，位置可随中文语序调整。
- 格式符 {0} {1} %s %d：数量和顺序不可变。
- 富文本标签 <b></b> <i></i> <color=...></color>：标签与属性值逐字节保留。
- 哨兵 <<M0>> <<M1>>：原样保留，不得翻译、不得删除、不得改编号。
- 专业缩写与单位按领域惯例保持原样。

【强制术语表】
__GLOSSARY__

【风格】
- 面向最终用户，简洁、专业、通顺，不加无意义感叹号。
- 专有名词、公司名、型号代号保留规范写法。
"""

    def __init__(self,
                 llm_client: Optional[LlmClient] = None,
                 gates: Optional[GateRules] = None,
                 glossary_provider: Optional[Callable[[List[str]], List[Tuple[str, str]]]] = None,
                 log: Callable[[str], None] = print):
        self.client = llm_client
        self.gates = gates or GateRules()
        self.glossary_provider = glossary_provider
        self.log = log

    def prepare_item(self, item_id: Any, text: str, role: str = "text", mod: str = "General", meta: Optional[Dict[str, Any]] = None) -> TranslationItem:
        res = masklib.mask(text)
        return TranslationItem(
            id=item_id,
            text=text,
            role=role,
            mod=mod,
            meta=meta or {},
            masked=res.text,
            res=res,
        )

    def translate_batch(self, items: List[TranslationItem], temperature: float = 0.2) -> List[Dict[str, Any]]:
        if not items:
            return []

        glossary_pairs: List[Tuple[str, str]] = []
        if self.glossary_provider:
            glossary_pairs = self.glossary_provider([it.masked or it.text for it in items])

        glossary_text = "\n".join(f"- {en} = {zh}" for en, zh in glossary_pairs) if glossary_pairs else "（按通用专业技术惯例翻译）"
        system_content = self.SYSTEM_PROMPT_TEMPLATE.replace("__GLOSSARY__", glossary_text)

        user_content = json.dumps({
            "batch": [
                {"id": it.id, "t": it.masked or it.text, "role": it.role, "mod": it.mod}
                for it in items
            ]
        }, ensure_ascii=False)

        messages = [
            {"role": "system", "content": system_content},
            {"role": "user", "content": user_content},
        ]

        if self.client is None:
            raw_zh_map = {it.id: "[STUB] " + (it.masked or it.text) for it in items}
        else:
            resp = self.client.chat(messages, temperature=temperature)
            raw_zh_map = self._extract_translations(resp.content)

        results = []
        for it in items:
            raw_zh = raw_zh_map.get(it.id, "")
            gate_res = self.gates.check(it, raw_zh, locked_pairs=glossary_pairs)
            results.append({
                "id": it.id,
                "source": it.text,
                "translation": gate_res.restored,
                "passed": gate_res.passed,
                "failed_gates": gate_res.failed,
                "notes": gate_res.notes,
                "item": it,
            })
        return results

    def _extract_translations(self, content: str) -> Dict[Any, str]:
        cleaned = content.strip()
        if "```" in cleaned:
            cleaned = re.sub(r"^```[a-zA-Z]*\n", "", cleaned)
            cleaned = re.sub(r"\n```$", "", cleaned)
        out: Dict[Any, str] = {}
        try:
            data = json.loads(cleaned)
            rows = data.get("items") or data.get("batch") or data
            if isinstance(rows, list):
                for row in rows:
                    if isinstance(row, dict) and "id" in row and "zh" in row:
                        out[row["id"]] = str(row["zh"])
        except Exception:
            for m in re.finditer(r'\{[^{}]*"id"\s*:\s*([^,}\s]+)[^{}]*"zh"\s*:\s*"([^"]+)"', content):
                try:
                    raw_id = m.group(1).strip().strip('"')
                    zh = m.group(2)
                    out[raw_id] = zh
                except Exception:
                    continue
        return out


def selftest() -> Tuple[List[str], int]:
    """engine.py 单元自检。"""
    failures: List[str] = []
    total = 0

    def eq(name: str, actual: Any, expected: Any):
        nonlocal total
        total += 1
        if actual != expected:
            failures.append(f"{name}: got {actual!r}, want {expected!r}")

    # 1. TranslationItem & prepare_item
    eng = TranslationEngine()
    item = eng.prepare_item("item-1", "Toggle ModuleEnginesFX [<<1>>]", mod="NearFutureSolar")
    eq("prepare_item.id", item.id, "item-1")
    eq("prepare_item.mod", item.mod, "NearFutureSolar")
    eq("prepare_item.masked", bool("<<M0>>" in (item.masked or "") and "<<1>>" in (item.masked or "")), True)

    # 2. GateRules check
    gates = GateRules(forbidden_terms={"Trim": ["修剪", "整理"]})
    res_pass = gates.check(item, "切换 <<M0>> [<<1>>]")
    eq("gates.check pass", res_pass.passed, True)
    eq("gates.check unmask", "ModuleEnginesFX" in res_pass.restored, True)

    # 3. Forbidden term violation
    item_trim = eng.prepare_item("item-2", "Trim Controls")
    res_forbid = gates.check(item_trim, "修剪控制")
    eq("gates.check forbidden fail", res_forbid.passed, False)
    eq("gates.check forbidden gate G7", any(f.startswith("G7") for f in res_forbid.failed), True)

    # 4. JSON extraction error tolerance
    broken_json = 'Some thoughts\n```json\n[{"id": "a", "zh": "测试A"}, {"id": "b", "zh": "测试B"}]\n```'
    extracted = eng._extract_translations(broken_json)
    eq("extract translations count", len(extracted), 2)
    eq("extract translations item a", extracted.get("a"), "测试A")

    return failures, total

