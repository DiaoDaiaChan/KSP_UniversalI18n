"""T11 报告层：把一次运行写成 reports/translation-<ts>.json，并在 stdout 打一份人类可读摘要。

设计口径：

- **密钥永不入报告**：payload 里本来就不该有 key，但写盘前仍过一遍 redact()（llm.py 的兜底
  规则会把 Bearer / sk- 形状的串打成 sk-***），宁可误伤不可泄漏。
- 报告是**审计物**：八门禁各自的拦截数与样例必须落盘，否则"通过率 97%"这种数字无法复核。
- 通过率口径 = (ok + official) / (ok + official + failed)，**keep 不计入分母**：keep 是"判定
  无需翻译"（缩写、单位、纯型号），不是流水线的成功或失败。keep 单独报数，且按 00 §4.1 要落库
  （写成 t == s，DictEntry.IsKeep 命中后终止下沉查找），所以它既不算成功也不算失败。
- official = 官方译文记忆直用（零 token），口径上等同 ok，但要单独报数以便核对 glossary 收益。
"""

from __future__ import annotations

import json
import time
from pathlib import Path

from .llm import redact

#: 八门禁的中文短名（打印用）
GATE_NAMES = {
    "G1": "条数/id 集合一致",
    "G2": "KSP 占位符 <<n>> 多重集",
    "G3": "格式符 {n} %s",
    "G4": "富文本标签逐字节",
    "G5": "哨兵 <<M>> 多重集/无残留",
    "G6": "长度比 0.15–1.8",
    "G7": "术语锁定逐字一致",
    "G8": "非空/非原文/含中文",
}


def _pct(part, whole, digits=2):
    if not whole:
        return "n/a"
    return ("{:." + str(digits) + "f}%").format(100.0 * part / whole)


def write_report(payload, reports_dir=None) -> Path:
    """写 reports/translation-<YYYYmmdd-HHMMSS>.json。返回写入路径。"""
    reports_dir = Path(reports_dir)
    reports_dir.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    path = reports_dir / "translation-{}.json".format(stamp)
    # 同名（同秒重复跑）时加序号，避免覆盖上一份审计物
    index = 1
    while path.exists():
        path = reports_dir / "translation-{}-{}.json".format(stamp, index)
        index += 1
    text = json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=False, default=str)
    path.write_text(redact(text), encoding="utf-8", newline="\n")
    return path


def print_summary(payload) -> None:
    """把 payload 打成人类可读摘要（stdout，UTF-8）。"""
    stats = payload.get("stats") or {}
    build = payload.get("build") or {}
    written = payload.get("written") or {}
    ok = stats.get("items_ok", 0)
    official = stats.get("items_official", 0)
    rescue = stats.get("items_rescue", 0)
    keep = stats.get("items_keep", 0)
    failed = stats.get("items_failed", 0)
    total = ok + official + rescue + keep + failed
    tried = ok + official + rescue + failed

    print("")
    print("=" * 74)
    print("翻译摘要  {}{}".format(payload.get("started", "?"),
                                 "  [DRY-RUN 未写盘]" if payload.get("dry_run") else ""))
    print("=" * 74)
    print("输入    : pool {} 行 → 待译 {} 条 / 跳过 {} 条".format(
        payload.get("pool_rows", 0), build.get("items", 0),
        sum((build.get("skipped") or {}).values())))
    print("批次    : {} 批 / 并发 {} / 字符预算 {} / 拆分 {} 次 / 单条重试 {} 次 / 单条补发 {} 次".format(
        payload.get("batches", 0), payload.get("jobs", 0),
        payload.get("max_chars", 0), stats.get("splits", 0), stats.get("retried", 0),
        stats.get("single_retries", 0)))
    print("结果    : 共处理 {} 条 → ok {} / official {} / rescue {} / keep {} / failed {}".format(
        total, ok, official, rescue, keep, failed))
    print("通过率  : {} （(ok+official+rescue)/(ok+official+rescue+failed)，keep 不计入分母）".format(
        _pct(ok + official + rescue, tried)))
    if build.get("keeps_auto"):
        print("零 token: {} 条遮蔽后只剩不可译物，直接标 keep（04 §5.5，未送翻）".format(
            build["keeps_auto"]))
    curated = payload.get("curated") or {}
    if curated:
        print("官方直用: 命中 {} 条 = 整句 {} + 键引用救回 {}（救回开关 {}，套用官方中文记忆、非模型输出）"
              " / 门禁不过退回翻译 {} 条".format(
                  curated.get("used", 0), curated.get("exact_used", 0),
                  curated.get("keyref_rescue", 0),
                  "开" if payload.get("keyref_rescue") else "关", curated.get("rejected", 0)))
        for row in (curated.get("rescue_examples") or [])[:3]:
            print("          救回样例 {} → {}（{}）".format(
                row.get("key"), _clip(row.get("zh"), 40), row.get("mod")))
    print("耗时    : {}  调用 {} 次 / 平均 {:.2f}s / 最慢 {:.2f}s".format(
        _fmt_elapsed(payload.get("elapsed_s", stats.get("elapsed_s"))),
        stats.get("calls", 0), stats.get("latency_avg_s", 0.0), stats.get("latency_max_s", 0.0)))
    print("模型    : {}  promptV={}  glossaryV={}".format(
        payload.get("model"), payload.get("promptV"), payload.get("glossaryV")))
    print("术语表  : {}".format(payload.get("glossary") or "-"))

    tokens = stats.get("tokens") or {}
    prompt_tokens = tokens.get("prompt", 0)
    completion_tokens = tokens.get("completion", 0)
    calls = stats.get("calls", 0)
    print("token   : prompt {} + completion {} = {} / {} 次调用".format(
        prompt_tokens, completion_tokens, prompt_tokens + completion_tokens, calls))
    if total:
        print("         每条均摊 {:.1f} prompt + {:.1f} completion".format(
            prompt_tokens / total, completion_tokens / total))
        print("         每 10 条 {:.0f} prompt + {:.0f} completion".format(
            prompt_tokens / total * 10, completion_tokens / total * 10))
    print("缓存    : 命中 {} 条".format(stats.get("cache_hits", 0)))
    if stats.get("mixed_lang"):
        print("半译    : {} 条（译文残留 ≥3 个英文单词，需人工复核；不算失败）".format(stats["mixed_lang"]))
        top = stats.get("mixed_lang_top_mods") or []
        if top:
            print("          半译最多的 mod：" + "  ".join(
                "{}={}".format(row["mod"], row["count"]) for row in top))
    if stats.get("term_drift"):
        print("术语漂移: {} 处（只计数不拦截，§9 走 term-drift 复核）".format(stats["term_drift"]))
        for row in (stats.get("drift_top") or [])[:10]:
            print("          {:<26} 期望 {:<10} 出现 {:>4} 次  例 {}".format(
                row.get("term", ""), row.get("want", ""), row.get("count", 0),
                _clip((row.get("zh_samples") or ["-"])[0], 46)))
    if build.get("keeps_auto") or keep:
        for row in (stats.get("keep_examples") or [])[:3]:
            print("keep    : id={} mod={} s==t {!r}（{}）".format(
                row.get("id"), row.get("mod"), _clip(row.get("s"), 40), row.get("why", "")))
    if stats.get("terms_injected") or stats.get("items_locked"):
        print("术语命中: 注入 {} 条（去重 {} 种）/ 短语锁触发 {} 种，覆盖 {} 条".format(
            stats.get("terms_injected", 0), stats.get("terms_distinct", 0),
            stats.get("locked_triggered", 0), stats.get("items_locked", 0)))
    reasons = stats.get("finish_reasons") or {}
    if reasons:
        print("停止原因: " + "  ".join("{}={}".format(key, value) for key, value in sorted(reasons.items())))

    # 八门禁：逐条报拦截数（0 也要打出来，"这条门禁没触发"本身是信息）
    print("")
    print("-- 八门禁拦截 " + "-" * 58)
    gates = stats.get("gates") or {}
    examples = stats.get("gate_examples") or {}
    for name in ("G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8"):
        count = gates.get(name, 0)
        mark = " " if count == 0 else "!"
        print(" {} {:<2} {:<26} {:>5}".format(mark, name, GATE_NAMES.get(name, ""), count))
        for example in (examples.get(name) or [])[:3]:
            print("      id={} mod={} {}".format(example.get("id"), example.get("mod"), example.get("why", "")))
            print("        源 {}".format(_clip(example.get("src"))))
            print("        译 {}".format(_clip(example.get("zh"))))

    # 未译条目：不写库、运行时回退原文，但必须列清是哪几条被哪条门禁拦下
    untranslated = payload.get("untranslated") or {}
    if untranslated:
        by_gate = untranslated.get("by_gate") or {}
        print("")
        print("-- 未译条目 untranslated " + "-" * 50)
        print(" 共 {} 条不写库（运行时 TryGet miss → 回退原文，安全终态）".format(
            untranslated.get("count", 0)))
        print(" 清单 {}".format(untranslated.get("path", "-")))
        for name in ("G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8"):
            if by_gate.get(name):
                print(" ! {:<2} {:<26} {:>5}".format(name, GATE_NAMES.get(name, ""), by_gate[name]))

    # 跳过原因
    skipped = build.get("skipped") or {}
    if skipped:
        print("")
        print("-- 跳过原因 " + "-" * 60)
        for reason, count in sorted(skipped.items(), key=lambda kv: -kv[1]):
            print("   {:<14} {:>6}".format(reason, count))

    # 按 mod：只打前 15 个（全量在 JSON 里）
    by_mod = payload.get("by_mod") or {}
    if by_mod:
        print("")
        print("-- 按 mod（前 15，全量见 JSON）" + "-" * 43)
        print("   {:<26} {:>6} {:>6} {:>6}".format("mod", "ok", "keep", "failed"))
        ordered = sorted(by_mod.items(), key=lambda kv: -(kv[1].get("ok", 0) + kv[1].get("keep", 0)))
        for mod, row in ordered[:15]:
            print("   {:<26} {:>6} {:>6} {:>6}".format(
                mod[:26], row.get("ok", 0), row.get("keep", 0), row.get("failed", 0)))
        if len(ordered) > 15:
            print("   … 另外 {} 个 mod".format(len(ordered) - 15))

    # 写盘
    files = written.get("files") or {}
    print("")
    if written.get("written"):
        print("写盘    : {} 个文件 / {} 条 → packs/zh-cn/".format(len(files), written.get("total", 0)))
        top = sorted(files.items(), key=lambda kv: -kv[1])[:10]
        print("          最大文件：" + "  ".join("{}={}".format(name, count) for name, count in top))
    else:
        print("写盘    : dry-run，未写 packs/zh-cn/（本次会产生 {} 条 / {} 个文件）".format(
            written.get("total", 0), len(files)))

    failed_rows = stats.get("failed") or []
    if failed_rows:
        print("")
        print("-- 失败明细（最多 300 条，报告里全存）" + "-" * 36)
        for row in failed_rows[:10]:
            print("   id={} mod={} gates={}".format(row.get("id"), row.get("mod"), row.get("gates")))
            print("      源 {}".format(_clip(row.get("src"))))
            print("      译 {}".format(_clip(row.get("zh"))))
            notes = row.get("notes") or {}
            if notes:
                print("      因 {}".format(json.dumps(notes, ensure_ascii=False)[:180]))
        if len(failed_rows) > 10:
            print("   … 另外 {} 条".format(len(failed_rows) - 10))
    print("=" * 74)


def _clip(text, limit=110):
    if not text:
        return "-"
    text = str(text).replace("\n", "\\n").replace("\r", "\\r")
    return text if len(text) <= limit else text[:limit] + "…"


def _fmt_elapsed(seconds):
    if not seconds:
        return "n/a"
    seconds = float(seconds)
    if seconds < 90:
        return "{:.1f}s".format(seconds)
    return "{:.1f}min".format(seconds / 60.0)


def main(argv=None):
    """独立入口：python -m i18n_tools.report <report.json> 打印摘要（不联网）。"""
    import argparse
    import sys

    from . import ensure_stdout

    ensure_stdout()
    parser = argparse.ArgumentParser(prog="i18n_tools.report", description="打印翻译报告摘要")
    parser.add_argument("path", help="reports/translation-*.json")
    args = parser.parse_args(argv)
    payload = json.loads(Path(args.path).read_text(encoding="utf-8"))
    print_summary(payload)
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
