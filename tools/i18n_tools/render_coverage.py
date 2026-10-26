r"""render 覆盖率：唯一一个「分母是游戏真的渲染出来的文本」的运行时自证指标（task-62 / docs/08 D-T4）。

为什么还要第三个覆盖率
----------------------
- pool 覆盖率（reports/coverage.md）= 我们抽出来的候选池翻了多少 → 预热指标，与玩家体感无关。
- 屏幕覆盖率（reports/screen-coverage.md）= 捕获到的文本里字典能接住多少 → 事后统计，依赖捕获开关。
- render 覆盖率（本文件）= **游戏自己数的**：本次会话渲染出口一共见过多少条不同的原文，其中多少条当场变成了中文。
  它不需要捕获开关、不需要采样、不会多算也不会漏算，是发版时唯一可以对玩家承诺的数字。

口径
----
- 运行时在 Translator 的**首次路径**（某个原文第一次进来）记一次：
  hit = 首次就拿到译文，miss = 首次没拿到。
- distinct = hit + miss：同一原文重复渲染不重复计；TextCache 被清空后重现的由运行时 HashSet 挡住。
- ratio = hit / distinct，固定 4 位小数（结果见"0.xxxx"，正文里同时给百分比）。
- bySink：是哪个渲染出口产生的（uGUI / IMGUI / TMPro / ...；空值归入 (none)）。
- byCaller：未命中的调用点，只在「miss 且该串首次出现」时取一次调用栈（见 CaptureCaller）。
- 数据来源：<KSP 根>/PluginData/UI18n/render-<UTC stamp>.json，由 UI18nAddon 在
  「场景切换（= 读档完成）」「每 300 秒」「退出」三个时机落盘；先写 .tmp 再替换，不会读到半截文件。

跨会话合并
----------
hit / miss / bySink / byCaller 都是**相加**（它们本来就是互不重叠的计数），distinct 也相加 ——
但要说清楚：合并后的 distinct 是「各会话 distinct 之和」，**不是去重并集**。文件里只有计数没有哈希，
跨会话无法去重，所以跨会话的 ratio 是「按 total distinct 加权的平均」，不是某个真实会话的读数。
同一个 stamp 有多份文件时只保留 mtime 最新的那份（重复落盘 / 拷来拷去的产物）。

用法
----
    cd tools
    python -m i18n_tools.render_coverage --all "D:/Kerbal Space Program_newmod/PluginData/UI18n"
    python -m i18n_tools.render_coverage --all <dir> --out ../reports/render-coverage.md --top 20
    python -m i18n_tools.render_coverage <单个 render-*.json> --quiet
    python -m i18n_tools.render_coverage --selftest

只读：不写任何 packs，只写 --out 指定的报告（--out "" 则只打印一行摘要）。
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import tempfile
import time

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_OUT = os.path.join(REPO_ROOT, "reports", "render-coverage.md")

SINK_NONE = "(none)"
CALLER_OTHER = "(other)"

# 报告里引用别的产物时，找不到就写这个，不猜数字
MISSING = "（未生成）"


def _c(text):
    """markdown 行内代码。python 源码里不出现反引号，统一走这里。"""
    return chr(96) + str(text) + chr(96)


def _cell(value):
    """表格单元格：竖线与换行会破坏表格，转义掉。"""
    text = str(value).replace("|", "\\|")
    return text.replace("\r", " ").replace("\n", " ")


def repo_root():
    return REPO_ROOT


def default_render_dir():
    """render-*.json 所在目录：<KSP 根>/PluginData/UI18n。

    优先级：环境变量 UI18N_CAPTURE_DIR（与捕获目录同一个地方，抓包脚本复用它）-> 从 GameData 推 -> 仓库旁。
    """
    env = os.environ.get("UI18N_CAPTURE_DIR")
    if env:
        return env
    try:
        from .harvest import default_gamedata
        gamedata = default_gamedata()
    except Exception:
        gamedata = None
    if gamedata:
        return os.path.join(os.path.dirname(os.path.abspath(gamedata)), "PluginData", "UI18n")
    return os.path.join(repo_root(), "_scratch", "capture")


# --------------------------------------------------------------------------- 解析

def _as_int(value):
    """严格的非负整数：bool 不算（True 是 1 会让统计悄悄变味），负数/字符串一律 None。"""
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    if value < 0:
        return None
    return value


def _as_str(value):
    return value if isinstance(value, str) else ""


def parse_session(path):
    """读一份 render-*.json。

    返回 (session, None) 或 (None, 原因)。任何异常都当成「这份文件不可用」，绝不抛给调用方。
    """
    try:
        with open(path, encoding="utf-8-sig") as fh:
            raw = fh.read()
    except OSError as exc:
        return None, "读取失败: %s" % exc
    try:
        data = json.loads(raw)
    except ValueError as exc:
        return None, "不是合法 JSON: %s" % exc
    if not isinstance(data, dict):
        return None, "顶层不是对象"

    hit = _as_int(data.get("hit"))
    miss = _as_int(data.get("miss"))
    if hit is None or miss is None:
        return None, "hit/miss 缺失或不是非负整数"

    problems = []
    declared = _as_int(data.get("distinct"))
    if declared is None:
        problems.append("distinct 缺失")
    elif declared != hit + miss:
        problems.append("distinct=%d 与 hit+miss=%d 不符" % (declared, hit + miss))

    computed = (float(hit) / (hit + miss)) if (hit + miss) else 0.0
    # 真 schema 里 ratio 是 JSON 数字（"ratio":0.9882）；手写/旧文件可能是字符串，两种都接受
    raw_ratio = data.get("ratio")
    if isinstance(raw_ratio, bool) or not isinstance(raw_ratio, (str, int, float)):
        problems.append("ratio 缺失")
        declared_ratio = ""
    elif isinstance(raw_ratio, str):
        declared_ratio = raw_ratio.strip()
        if declared_ratio != "%.4f" % computed:
            problems.append("ratio=%s 与 hit/miss 算出的 %.4f 不符" % (declared_ratio, computed))
    else:
        declared_ratio = "%.4f" % raw_ratio
        if declared_ratio != "%.4f" % computed:
            problems.append("ratio=%r 与 hit/miss 算出的 %.4f 不符" % (raw_ratio, computed))

    by_sink = {}
    raw_sink = data.get("bySink")
    if isinstance(raw_sink, dict):
        for key, val in raw_sink.items():
            if not isinstance(val, dict):
                continue
            sh = _as_int(val.get("hit"))
            sm = _as_int(val.get("miss"))
            if sh is None and sm is None:
                continue
            by_sink[str(key)] = {"hit": sh or 0, "miss": sm or 0}
    sink_hit = sum(v["hit"] for v in by_sink.values())
    sink_miss = sum(v["miss"] for v in by_sink.values())
    if by_sink and (sink_hit != hit or sink_miss != miss):
        problems.append("bySink 合计 %d/%d 与 hit/miss %d/%d 不符" % (sink_hit, sink_miss, hit, miss))

    by_caller = {}
    raw_caller = data.get("byCaller")
    if isinstance(raw_caller, dict):
        for key, val in raw_caller.items():
            # 真 schema 是 {"<caller>":{"miss":n}}；也接受手写的 {"<caller>": n}
            if isinstance(val, dict):
                cm = _as_int(val.get("miss"))
            else:
                cm = _as_int(val)
            if cm is None:
                continue
            by_caller[str(key)] = cm
    caller_miss = sum(by_caller.values())
    if by_caller and caller_miss != miss:
        problems.append("byCaller 合计 %d 与 miss %d 不符" % (caller_miss, miss))

    stamp = _as_str(data.get("stamp")).strip()
    if not stamp:
        base = os.path.basename(path)
        if base.startswith("render-") and base.endswith(".json"):
            stamp = base[len("render-"):-len(".json")]
        else:
            stamp = time.strftime("%Y%m%d-%H%M%S", time.localtime(os.path.getmtime(path)))

    return {
        "path": path,
        "file": os.path.basename(path),
        "stamp": stamp,
        "version": _as_str(data.get("version")),
        "hit": hit,
        "miss": miss,
        "distinct": hit + miss,
        "ratio": computed,
        "ratio_declared": declared_ratio,
        "bySink": by_sink,
        "byCaller": by_caller,
        "problems": problems,
        "mtime": os.path.getmtime(path),
        "bytes": os.path.getsize(path),
    }, None


def iter_render_files(patterns):
    """展开 --dir / --all / 位置参数 -> 文件列表（去重、稳定排序）。"""
    seen, out = set(), []
    for pattern in patterns:
        for path in sorted(glob.glob(pattern)):
            if not os.path.isfile(path):
                continue
            full = os.path.abspath(path)
            if full in seen:
                continue
            seen.add(full)
            out.append(full)
    return out


def load_sessions(paths, quiet=False):
    """读一批文件，按 stamp 去重（同 stamp 保留 mtime 最新的）。

    返回 (sessions, errors, duplicates)：errors 是 [(path, 原因)]，duplicates 是 [(丢掉的 path, 保留的 path)]。
    """
    sessions, errors = [], []
    for path in paths:
        session, err = parse_session(path)
        if session is None:
            errors.append((path, err))
            continue
        sessions.append(session)

    best, duplicates = {}, []
    for session in sessions:
        old = best.get(session["stamp"])
        if old is None:
            best[session["stamp"]] = session
            continue
        keep, drop = (session, old) if session["mtime"] >= old["mtime"] else (old, session)
        best[session["stamp"]] = keep
        duplicates.append((drop["path"], keep["path"]))

    merged = sorted(best.values(), key=lambda s: (s["stamp"], s["file"]))
    if not quiet:
        for path, why in errors:
            print("skip %s: %s" % (path, why), file=sys.stderr)
    return merged, errors, duplicates


# --------------------------------------------------------------------------- 合并与报告

def ratio_of(hit, miss):
    total = hit + miss
    return (float(hit) / total) if total else 0.0


def merge_sessions(sessions):
    """把多份会话计数相加。distinct 也相加（增量语义，不是并集，见模块 docstring）。"""
    total = {"hit": 0, "miss": 0, "distinct": 0, "sessions": len(sessions),
             "bySink": {}, "byCaller": {}}
    for session in sessions:
        total["hit"] += session["hit"]
        total["miss"] += session["miss"]
        total["distinct"] += session["distinct"]
        for key, val in session["bySink"].items():
            slot = total["bySink"].setdefault(key, {"hit": 0, "miss": 0})
            slot["hit"] += val["hit"]
            slot["miss"] += val["miss"]
        for key, val in session["byCaller"].items():
            total["byCaller"][key] = total["byCaller"].get(key, 0) + val
    total["ratio"] = ratio_of(total["hit"], total["miss"])
    return total


def top_callers(total, limit):
    """未命中 top N：miss 降序、同数按名字升序（保证报告逐字节可复现）。"""
    items = sorted(total["byCaller"].items(), key=lambda kv: (-kv[1], kv[0]))
    return items[:limit] if limit and limit > 0 else items


def sink_rows(total):
    rows = []
    for key, val in total["bySink"].items():
        rows.append((key, val["hit"], val["miss"], val["hit"] + val["miss"],
                     ratio_of(val["hit"], val["miss"])))
    rows.sort(key=lambda r: (-r[3], r[0]))
    return rows


def pool_figures(coverage_path=None):
    """从 reports/coverage.md 抓 pool 口径（可覆盖 X 条 / 已覆盖 Y 条 / Z%），抓不到就 None。

    不硬编码这些数字：pool 会随字典增长，报告里必须显示当时的值，否则两个口径的对比会变成谎话。
    """
    path = coverage_path or os.path.join(repo_root(), "reports", "coverage.md")
    if not os.path.exists(path):
        return None
    try:
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
    except OSError:
        return None
    import re
    match = re.search(r"可覆盖英文串\s*([\d,]+)\s*条[，,]\s*已被翻译包覆盖\s*([\d,]+)\s*条[，,]\s*覆盖率\s*([\d.]+)%", text)
    if not match:
        return None
    return {"total": int(match.group(1).replace(",", "")),
            "covered": int(match.group(2).replace(",", "")),
            "percent": match.group(3),
            "source": os.path.relpath(path, repo_root())}


def _table(headers, rows):
    out = ["| " + " | ".join(headers) + " |",
           "|" + "|".join(["---"] * len(headers)) + "|"]
    for row in rows:
        out.append("| " + " | ".join(_cell(v) for v in row) + " |")
    return out


def build_report(sessions, errors, duplicates, total, pool, top, generated_at=None):
    """生成 markdown 报告。纯函数（除时间戳），selftest 直接调用它断言。"""
    lines = []
    add = lines.append
    add("# render 覆盖率（运行时自证）")
    add("")
    add("本文件由 " + _c("python -m i18n_tools.render_coverage") + " 生成，**不要手改**。")
    add("")
    add("生成时间：" + (generated_at or time.strftime("%Y-%m-%d %H:%M:%S")))
    add("数据来源：" + _c("<KSP 根>/PluginData/UI18n/render-*.json") + "（每份 = 一次游戏会话）")
    add("本次读到 **%d** 份会话文件（另有 %d 份无法解析、%d 份因 stamp 重复被丢弃）。"
        % (len(sessions), len(errors), len(duplicates)))
    add("")
    add("## 结论")
    add("")
    add("**本次会话期间，玩家屏幕上出现的 " + "{:,}".format(total["distinct"]) +
        " 条不同英文文本中，有 " + "{:,}".format(total["hit"]) + " 条当场变成了中文，render 覆盖率 "
        + "%.4f（%.2f%%）。**" % (total["ratio"], total["ratio"] * 100.0))
    add("")
    add("- hit = " + "{:,}".format(total["hit"]) + "（首次见到就有译文）")
    add("- miss = " + "{:,}".format(total["miss"]) + "（首次见到没译文，进未命中捕获通道）")
    add("- distinct = hit + miss = " + "{:,}".format(total["distinct"]))
    add("- ratio = hit / distinct = " + "{:,}".format(total["hit"]) + " / "
        + "{:,}".format(total["distinct"]) + " = %.4f" % total["ratio"])
    add("")
    if total["sessions"] > 1:
        add("> 跨会话合并：distinct 是各会话 distinct **之和**（不是去重并集）—— 文件里只有计数没有哈希，"
            "跨会话无法去重。所以上面的 ratio 是按 total distinct 加权的平均，不是某一局的读数。")
        add("")

    add("## 与另外两个覆盖率口径的关系")
    add("")
    rows = []
    if pool:
        rows.append(("pool 覆盖率", "{:,}".format(pool["total"]), "{:,}".format(pool["covered"]),
                     pool["percent"] + "%", pool["source"] + "（抽出来的候选池）"))
    else:
        rows.append(("pool 覆盖率", MISSING, MISSING, MISSING, "先跑 python -m i18n_tools.packindex"))
    screen = os.path.join(repo_root(), "reports", "screen-coverage.md")
    if os.path.exists(screen):
        rows.append(("屏幕覆盖率", "捕获里可译的 distinct", "字典能接住的", "见报告",
                     "reports/screen-coverage.md（依赖捕获开关）"))
    else:
        rows.append(("屏幕覆盖率", MISSING, MISSING, MISSING, "reports/screen-coverage.md"))
    rows.append(("render 覆盖率", "{:,}".format(total["distinct"]), "{:,}".format(total["hit"]),
                 "%.2f%%" % (total["ratio"] * 100.0), "本报告（运行时自己数，唯一可对玩家承诺的口径）"))
    lines.extend(_table(["口径", "分母", "分子", "值", "来源"], rows))
    add("")
    add("三个分母**不一样，不能相加也不能互相除**：")
    add("")
    add("- pool 的分母是「我们抽出来的候选池」，里面含大量玩家一辈子看不到的 cfg 文本；")
    add("- 屏幕覆盖率的分母是「捕获到并判为可译的文本」，依赖捕获开关与采样；")
    add("- render 的分母是「本次会话真的渲染过、且走了 Translator 的不同原文」——它最小，也最接近体感。")
    add("")

    add("## 逐会话")
    add("")
    if sessions:
        rows = []
        for session in sessions:
            rows.append((session["stamp"], session["version"] or "", "{:,}".format(session["hit"]),
                         "{:,}".format(session["miss"]), "{:,}".format(session["distinct"]),
                         "%.4f" % session["ratio"], session["file"]))
        lines.extend(_table(["stamp (UTC)", "version", "hit", "miss", "distinct", "ratio", "文件"], rows))
    else:
        add("没有可用的会话文件。")
    add("")

    add("## 按渲染出口（bySink）")
    add("")
    rows = sink_rows(total)
    if rows:
        table = []
        for key, hit, miss, distinct, ratio in rows:
            table.append((key, "{:,}".format(hit), "{:,}".format(miss), "{:,}".format(distinct),
                          "%.4f" % ratio, "%.2f%%" % (ratio * 100.0)))
        lines.extend(_table(["sink", "hit", "miss", "distinct", "ratio", "覆盖率"], table))
        add("")
        add(_c(SINK_NONE) + " = 补丁没带 sink 名的旧路径；" + _c(CALLER_OTHER) + " = byCaller 超出上限后的合并桶。")
    else:
        add("没有 bySink 数据（运行时版本早于 task-62）。")
    add("")

    add("## 未命中 top %d（按调用点）" % len(top_callers(total, top)))
    add("")
    if total["byCaller"]:
        rows = []
        for index, (caller, count) in enumerate(top_callers(total, top), start=1):
            share = (100.0 * count / total["miss"]) if total["miss"] else 0.0
            rows.append((index, _c(caller), "{:,}".format(count), "%.1f%%" % share))
        lines.extend(_table(["#", "调用点", "miss", "占全部未命中"], rows))
        add("")
        add("这些调用点就是下一轮最该补的字典条目来源（配合 " + _c("reports/screen-coverage.md")
            + " 的原文列表一起看）。")
    else:
        add("没有未命中记录 —— 要么本次会话全命中，要么运行时没开 miss 计数。")
    add("")

    add("## 已知边界")
    add("")
    add("1. 只统计走了 " + _c("Translator.Translate") + " 的渲染出口；直接读字典或未接入补丁的出口看不见。")
    add("2. 主菜单与加载界面也算（它们同样渲染），所以「刚进游戏还没读档」就有读数，属正常。")
    add("3. 判据是**首次**：某串第一次没翻出来、后来补上也不会改判（同一会话内字典不热更新）。")
    add("4. 上限：distinct 65,536 条、byCaller 200 个；到顶时运行时会打一行 " + _c("[UI18n] render coverage: ... 已达上限")
        + " 的警告，之后的读数仍然有效但不再增长。")
    add("5. 跨会话 distinct 是相加，不是并集（见上）。")
    add("6. 读数与「补丁装上的时刻」有关：本表里有会话在加载屏阶段就结束，加载屏的进度串"
        "（" + _c("Loading texture asset 1950/5740 (buffer=45MB)") + " 这类，见 "
        + _c("reports/loading-screen-coverage.md") + "）一局就能贡献几百条 distinct，"
        "所以整局 ratio 会被拉低；只统计主菜单/飞行阶段才是玩家实际体感。"
        "这也是同一版本不同会话 ratio 能从 0.35 跳到 0.99 的原因，别把某一次的 ratio 当成版本结论。")
    add("")
    if errors:
        add("## 无法解析的文件")
        add("")
        rows = [(os.path.basename(path), why) for path, why in errors]
        lines.extend(_table(["文件", "原因"], rows))
        add("")
    if duplicates:
        add("## stamp 重复（保留 mtime 最新的一份）")
        add("")
        rows = [(os.path.basename(drop), os.path.basename(keep)) for drop, keep in duplicates]
        lines.extend(_table(["丢弃", "保留"], rows))
        add("")
    problems = [(session["file"], p) for session in sessions for p in session["problems"]]
    add("## 自洽检查")
    add("")
    if problems:
        add("发现 **%d** 处计数不自洽（运行时写盘 bug 或文件被改过），逐条列出：" % len(problems))
        add("")
        lines.extend(_table(["文件", "问题"], [(f, p) for f, p in problems]))
    else:
        add("全部会话文件自洽：distinct == hit + miss、ratio 与 hit/miss 一致、bySink/byCaller 合计与 hit/miss 一致。")
    add("")
    return "\n".join(lines)


# --------------------------------------------------------------------------- 自检

def selftest():
    ok = 0
    fail = []

    def check(name, cond):
        nonlocal ok
        if cond:
            ok += 1
        else:
            fail.append(name)

    tmp = tempfile.mkdtemp(prefix="render-cov-")
    try:
        a_path = os.path.join(tmp, "render-20260101-000000.json")
        b_path = os.path.join(tmp, "render-20260102-000000.json")
        dup_path = os.path.join(tmp, "render-20260101-000000.json.copy")
        bad_path = os.path.join(tmp, "render-20260103-000000.json")
        noise_path = os.path.join(tmp, "capture-20260101-000000.jsonl")

        with open(a_path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(json.dumps({
                "stamp": "20260101-000000", "version": "1.4.0+test",
                "hit": 8, "miss": 2, "distinct": 10, "ratio": 0.8,
                "bySink": {"uGUI": {"hit": 6, "miss": 1}, "IMGUI": {"hit": 2, "miss": 1}},
                "byCaller": {"A.B": {"miss": 1}, "C.D": {"miss": 1}},
            }, ensure_ascii=False))
        with open(b_path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(json.dumps({
                "stamp": "20260102-000000", "version": "1.4.0+test",
                "hit": 3, "miss": 7, "distinct": 10, "ratio": "0.3000",  # 字符串形式也接受
                "bySink": {"TMPro": {"hit": 3, "miss": 7}},
                "byCaller": {"A.B": {"miss": 5}, "C.D": {"miss": 1}, "E.F": {"miss": 1}},
            }, ensure_ascii=False))
        # 同 stamp 的第二份（更晚的 mtime、不同的读数）：应被丢弃，读数保留旧的 8/2
        with open(dup_path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(json.dumps({
                "stamp": "20260101-000000", "version": "1.4.0+dup",
                "hit": 1, "miss": 1, "distinct": 2, "ratio": "0.5000",
                "bySink": {}, "byCaller": {},
            }, ensure_ascii=False))
        # a 是「退出时最后写的那份」（更新、更完整）-> 去重时保留它；.copy 是旧的重复落盘
        os.utime(a_path, (1735689700, 1735689700))
        os.utime(dup_path, (1735689600, 1735689600))
        # 计数不自洽 + ratio 写错：应被接受但记进 problems
        with open(bad_path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(json.dumps({
                "stamp": "20260103-000000", "version": "1.4.0+bad",
                "hit": 2, "miss": 1, "distinct": 99, "ratio": 0.9,
                "bySink": {"uGUI": {"hit": 2, "miss": 1}},
                "byCaller": {"A.B": {"miss": 1}},
            }, ensure_ascii=False))
        # 非 render 文件（捕获日志）不应被 glob 抓到
        with open(noise_path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("{}\n")

        sessions, errors, duplicates = load_sessions([a_path, b_path, dup_path, bad_path], quiet=True)
        check("同 stamp 去重（4 个文件 -> 3 个会话）", len(sessions) == 3)
        check("去重保留 mtime 最新的那份",
              [s for s in sessions if s["stamp"] == "20260101-000000"][0]["hit"] == 8)
        check("丢弃的是同 stamp 的旧那份", len(duplicates) == 1 and duplicates[0][0] == os.path.abspath(dup_path))
        check("无法解析的文件被跳过且不崩", len(errors) == 0)
        check("会话按 stamp 升序",
              [s["stamp"] for s in sessions] == ["20260101-000000", "20260102-000000", "20260103-000000"])
        check("读到了 version", sessions[0]["version"] == "1.4.0+test")

        total = merge_sessions([s for s in sessions if s["stamp"] != "20260103-000000"])
        check("合并 hit=11", total["hit"] == 11)
        check("合并 miss=9", total["miss"] == 9)
        check("合并 distinct=20", total["distinct"] == 20)
        check("合并 ratio=0.55", "%.4f" % total["ratio"] == "0.5500")
        check("bySink 合并 uGUI 6/1", total["bySink"]["uGUI"] == {"hit": 6, "miss": 1})
        check("bySink 合并 TMPro 3/7", total["bySink"]["TMPro"] == {"hit": 3, "miss": 7})
        check("byCaller 合并 A.B=6", total["byCaller"]["A.B"] == 6)
        check("byCaller 合并 C.D=2", total["byCaller"]["C.D"] == 2)
        check("byCaller 合并 E.F=1", total["byCaller"]["E.F"] == 1)

        tops = top_callers(total, 10)
        check("top 排序：miss 降序", [c for c, _ in tops] == ["A.B", "C.D", "E.F"])
        check("top 截断生效", len(top_callers(total, 2)) == 2)

        sinks = sink_rows(total)
        check("sink 表按 distinct 降序", sinks[0][0] == "TMPro")
        check("sink 表带 ratio", "%.4f" % sinks[0][4] == "0.3000")

        bad = [s for s in sessions if s["stamp"] == "20260103-000000"]
        check("不自洽的会话仍被接受", len(bad) == 1)
        check("抓出 distinct 不符", any("distinct=99" in p for p in bad[0]["problems"]))
        check("抓出 ratio 写错（数字形式）", any("ratio=0.9" in p for p in bad[0]["problems"]))
        check("数字型 ratio 与字符串型都能读", [s for s in sessions if s["stamp"] == "20260101-000000"][0]["ratio_declared"] == "0.8000")
        check("自洽的会话没有 problems", [s for s in sessions if s["stamp"] == "20260101-000000"][0]["problems"] == [])

        md = build_report(sessions, errors, duplicates, total, None, 20, generated_at="T")
        check("报告有标题", md.startswith("# render 覆盖率"))
        check("报告有结论段", "render 覆盖率 0.5500" in md)
        check("报告说明跨会话 distinct 不是并集", "不是去重并集" in md)
        check("报告点明 ratio 受加载屏影响", "加载屏的进度串" in md)
        check("报告有 bySink 节", "## 按渲染出口" in md)
        check("报告有未命中 top 节", "## 未命中 top" in md)
        check("报告有已知边界", "## 已知边界" in md)
        check("报告列出不自洽", "计数不自洽" in md)
        check("报告提到 pool 未生成", MISSING in md)

        fake_pool = os.path.join(tmp, "coverage.md")
        with open(fake_pool, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("本安装可覆盖英文串 12,792 条，已被翻译包覆盖 9,397 条，覆盖率 73.46%。\n")
        pool = pool_figures(fake_pool)
        check("抓到 pool 口径", pool is not None and pool["total"] == 12792 and pool["covered"] == 9397)
        check("抓到 pool 百分比", pool["percent"] == "73.46")
        md2 = build_report(sessions, errors, duplicates, total, pool, 20, generated_at="T")
        check("报告并列 pool 口径", "73.46%" in md2 and "pool 覆盖率" in md2)

        # 坏文件 + 空目录的行为
        broken = os.path.join(tmp, "render-broken.json")
        with open(broken, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("{not json")
        parsed, err = parse_session(broken)
        check("坏 JSON 返回原因不抛", parsed is None and err is not None)
        parsed, err = parse_session(os.path.join(tmp, "nope.json"))
        check("文件不存在返回原因不抛", parsed is None and err is not None)
        check("空目录没有文件", iter_render_files([os.path.join(tmp, "empty", "*.json")]) == [])
        check("非 render-*.json 不被 glob 抓到",
              all("capture-" not in p for p in iter_render_files([os.path.join(tmp, "render-*.json")])))

        out = os.path.join(tmp, "out.md")
        rc = main(["--dir", tmp, "--out", out, "--quiet"])
        check("main 在含坏文件时不失败", rc == 0)
        check("报告已写出", os.path.exists(out))
        with open(out, encoding="utf-8") as fh:
            written = fh.read()
        check("写出的报告含结论", "render 覆盖率" in written)
        check("写出的报告按仓库根解析路径外仍可写", written.startswith("# render 覆盖率"))

        rc = main(["--glob", os.path.join(tmp, "empty", "*.json"), "--out", "", "--quiet"])
        check("没有文件时 exit 2", rc == 2)

        check("空计数不除零", ratio_of(0, 0) == 0.0)
        check("ratio_of 正常", "%.4f" % ratio_of(3, 1) == "0.7500")
    finally:
        for name in os.listdir(tmp):
            try:
                os.remove(os.path.join(tmp, name))
            except OSError:
                pass
        try:
            os.rmdir(tmp)
        except OSError:
            pass

    print("selftest: %d passed, %d failed" % (ok, len(fail)))
    for name in fail:
        print("  FAIL %s" % name)
    # 与 check_overrides / candidates 等一致：返回 (失败项, 总项数)，供 tools/i18n_tools/selftest.py 汇总
    return fail, ok + len(fail)


# --------------------------------------------------------------------------- 主流程

def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="render_coverage", add_help=True,
        description="render 覆盖率：分母=游戏真的渲染过的不同原文（运行时自证）")
    parser.add_argument("files", nargs="*", help="render-*.json 文件")
    parser.add_argument("--dir", action="append", default=[], help="render-*.json 所在目录（可重复）")
    parser.add_argument("--all", action="append", default=[], help="同 --dir（可重复）")
    parser.add_argument("--glob", default="", help="自定义 glob（一般不用）")
    parser.add_argument("--out", default=DEFAULT_OUT, help="报告输出路径（默认 reports/render-coverage.md，空字符串=不写）")
    parser.add_argument("--coverage", default="", help="pool 口径来源（默认 reports/coverage.md）")
    parser.add_argument("--top", type=int, default=20, help="未命中调用点表长度（默认 20）")
    parser.add_argument("--selftest", action="store_true", help="自检")
    parser.add_argument("--quiet", action="store_true", help="不打印逐文件信息")
    args = parser.parse_args(argv)

    if args.selftest:
        failures, _total = selftest()
        return 0 if not failures else 1

    patterns = list(args.files)
    if args.glob:
        patterns.append(args.glob)
    for directory in list(args.dir) + list(args.all):
        patterns.append(os.path.join(directory, "render-*.json"))
    if not patterns:
        patterns.append(os.path.join(default_render_dir(), "render-*.json"))

    paths = iter_render_files(patterns)
    if not paths:
        print("没有找到任何 render-*.json（用 --dir <PluginData/UI18n> 指定；先把游戏跑一盘）", file=sys.stderr)
        return 2

    started = time.time()
    sessions, errors, duplicates = load_sessions(paths, quiet=args.quiet)
    if not sessions:
        print("读到了 %d 个文件但没有一份可用（见上面的 skip 原因）" % len(paths), file=sys.stderr)
        return 2

    total = merge_sessions(sessions)
    pool = pool_figures(args.coverage or None)
    md = build_report(sessions, errors, duplicates, total, pool, args.top)

    if args.out:
        out = args.out if os.path.isabs(args.out) else os.path.join(repo_root(), args.out)
        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        with open(out, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(md + "\n")
        if not args.quiet:
            print("wrote %s (%d B)" % (out, len(md.encode("utf-8"))))

    print("render coverage: sessions=%d hit=%d miss=%d distinct=%d ratio=%.4f (%.2f%%) bad=%d dup=%d (%.2fs)"
          % (len(sessions), total["hit"], total["miss"], total["distinct"], total["ratio"],
             total["ratio"] * 100.0, len(errors), len(duplicates), time.time() - started))
    return 0


if __name__ == "__main__":
    sys.exit(main())
