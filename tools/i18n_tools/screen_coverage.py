r"""屏幕覆盖率：唯一一个「分母是玩家真正看到的文本」的指标。

为什么需要它
------------
`reports/coverage.md` 量的是 **pool 覆盖率**（分母 = 我们从 cfg/Localization 抽出来的候选池），
它与玩家体感无关：1.2 发布时 pool 覆盖率 70.93%，而同一时刻用户屏幕上的英文标签几乎一条都没翻出来。
本工具把分母换成 **运行时捕获到并真正渲染过的文本**（`PluginData/UI18n/capture-*.jsonl`），
分子换成 **游戏真正会加载的字典**（`packs/<lang>/*.jsonl`；注意 `packs/pool.jsonl` 运行时不加载，
见 `src/UI18n.Runtime/ConfigLoader.cs:15`）。

口径
----
- 分母 A = 全部 distinct 文本（含 path/progress/counter 这类永远不会翻的）。
- 分母 B = 「可译分母」= 捕获里 bucket != never 的 distinct 文本（＝ candidate + template）。
- 分子 = exact（字典里有完全相同的原文） + pattern（骨架行命中，仅当 pack 里有 `p:1` 行）。
- `reason=already-cjk` 的行本身不需要翻译，但因为分母 B 只排 never 桶，它们天然被排除。
- 同时交叉核对：重算 `fnv1a64(normalize(s))` 与捕获行里的 `h` 是否一致（不一致即 C#/Python 归一化契约破裂）。

用法
----
    cd tools
    python -m i18n_tools.screen_coverage --all 'D:/Kerbal Space Program_newmod/PluginData/UI18n' --packs ../packs/zh-cn --out ../reports/screen-coverage.md

    python -m i18n_tools.screen_coverage --selftest
    python -m i18n_tools.screen_coverage ... --fail-under 25    # 低于 25% 可译覆盖率则 exit 1（给 CI 用）

只读：不写任何 packs（只写 --out 指定的报告）。
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import tempfile
import time

from . import fnv, textnorm, skeleton

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_PACKS = os.path.join(REPO_ROOT, "packs", "zh-cn")
DEFAULT_OUT = os.path.join(REPO_ROOT, "reports", "screen-coverage.md")

NEVER = "never"


# --------------------------------------------------------------------------- 读取

def load_pack_hashes(packs_dir: str):
    """返回 (exact_hashes, pattern_hashes)。pattern 行靠 p 字段（1/true）识别。"""
    exact, pattern = set(), set()
    files = sorted(glob.glob(os.path.join(packs_dir, "*.jsonl")))
    for path in files:
        with open(path, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    row = json.loads(line)
                except ValueError:
                    continue
                h = row.get("h")
                if not isinstance(h, str):
                    continue
                flag = row.get("p")
                if flag in (1, True, "1", "true", "True"):
                    pattern.add(h)
                else:
                    exact.add(h)
    return exact, pattern, files


def load_pool_hashes(path: str):
    out = set()
    if not path or not os.path.exists(path):
        return out
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except ValueError:
                continue
            h = row.get("h")
            if isinstance(h, str):
                out.add(h)
    return out


def iter_capture_files(patterns):
    seen, out = set(), []
    for pat in patterns:
        for path in sorted(glob.glob(pat)):
            if not os.path.isfile(path) or path in seen:
                continue
            seen.add(path)
            out.append(path)
    return out


class TextAgg:
    """一条 distinct 文本的聚合信息（跨会话、跨 caller）。"""
    __slots__ = ("h", "text", "bucket", "sink", "count", "sessions", "callers", "reason", "template")

    def __init__(self, h, text, bucket, sink, reason, template):
        self.h = h
        self.text = text
        self.bucket = bucket
        self.sink = sink
        self.reason = reason
        self.template = template
        self.count = 0
        self.sessions = 0
        self.callers = {}


def collect(paths, quiet=False):
    agg = {}
    lines = bad = mismatch = 0
    for path in paths:
        try:
            fh = open(path, encoding="utf-8")
        except OSError as exc:
            if not quiet:
                print("skip %s (%s)" % (path, exc), file=sys.stderr)
            continue
        with fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                lines += 1
                try:
                    row = json.loads(line)
                except ValueError:
                    bad += 1
                    continue
                s = row.get("s")
                if not isinstance(s, str) or not s:
                    bad += 1
                    continue
                h = row.get("h")
                calc = fnv.hash_text(textnorm.normalize(s))
                if isinstance(h, str) and h != calc:
                    mismatch += 1
                if not isinstance(h, str) or not h:
                    h = calc
                item = agg.get(h)
                if item is None:
                    item = TextAgg(h, s, row.get("bucket") or "", row.get("sink") or "",
                                   row.get("reason") or "", bool(row.get("template")))
                    agg[h] = item
                cnt = row.get("count")
                if not isinstance(cnt, int) or cnt < 0:
                    cnt = 1
                item.count += cnt
                item.sessions += 1
                caller = row.get("caller") or ""
                item.callers[caller] = item.callers.get(caller, 0) + cnt
    return agg, lines, bad, mismatch


# --------------------------------------------------------------------------- 分析

def classify(agg, exact_h, pattern_h, pool_h):
    """返回 (stat, per_item)。per_item[h] = "exact"|"pattern"|"pool"|"missing"。"""
    stat = dict(exact=0, pattern=0, pool=0, missing=0)
    per = {}
    for h, item in agg.items():
        if h in exact_h:
            per[h] = "exact"
        elif h in pattern_h:
            per[h] = "pattern"
        elif h in pool_h:
            per[h] = "pool"
        else:
            per[h] = "missing"
        stat[per[h]] += 1
    return stat, per


def top_callers(agg, per, limit):
    """未覆盖（missing，且 bucket != never）按 caller 的调用次数聚合。"""
    rows = {}
    for h, item in agg.items():
        if per[h] != "missing" or not is_applicable(item):
            continue
        for caller, cnt in item.callers.items():
            cur = rows.get(caller)
            if cur is None:
                rows[caller] = [cnt, 1, item.text, item.sink, h]
            else:
                cur[0] += cnt
                cur[1] += 1
    out = sorted(rows.items(), key=lambda kv: (-kv[1][0], kv[0]))
    return out[:limit]


def top_texts(agg, per, limit, bucket=None):
    rows = [item for h, item in agg.items()
            if per[h] == "missing" and is_applicable(item) and (bucket is None or item.bucket == bucket)]
    rows.sort(key=lambda it: (-it.count, it.text))
    return rows[:limit]


NON_TEXT_REASONS = ("path", "url", "progress", "counter", "identifier", "too-long", "punct")


def is_applicable(item):
    """该条文本是否属于「该翻、能翻」的范围（＝覆盖率的分母）。

    排除三类：运行时判定的 never 桶；运行时已标注为非文案的 reason（路径/URL/进度/计数/标识符/超长/纯标点）；
    python 侧再兜一层纯数字与 URL 判断（旧会话的捕获行可能没带 reason）。
    """
    if item.bucket == NEVER:
        return False
    if item.reason in NON_TEXT_REASONS:
        return False
    text = item.text
    if len(text) < 2:
        return False
    if textnorm.is_pure_number(text) or textnorm.is_url_only(text):
        return False
    return True


def short(text, n=64):
    text = text.replace(chr(10), " ").replace("|", "/")
    return text if len(text) <= n else text[: n - 1] + "…"


# --------------------------------------------------------------------------- 报告

def build_report(stat, agg, per, paths, exact_h, pattern_h, pool_h, meta, top):
    total = len(agg)
    applicable = sum(1 for h in agg if is_applicable(agg[h]))
    covered = stat["exact"] + stat["pattern"]
    # 命中分两类：全部命中（含进度条/计数器）与「落在可译范围内」的命中。
    # 百分比必须与分母同口径 —— 分母是 applicable，分子也只能数 applicable 里的命中。
    exact_app = sum(1 for h in agg if per[h] == "exact" and is_applicable(agg[h]))
    pattern_app = sum(1 for h in agg if per[h] == "pattern" and is_applicable(agg[h]))
    covered_app = exact_app + pattern_app
    missing_app = sum(1 for h in agg if per[h] == "missing" and is_applicable(agg[h]))
    pct_all = 100.0 * covered / total if total else 0.0
    pct_app = 100.0 * covered_app / applicable if applicable else 0.0

    by_bucket = {}
    for h, it in agg.items():
        key = it.bucket or "(旧会话未声明)"
        slot = by_bucket.setdefault(key, [0, 0, 0, 0])  # total, exact, pattern, missing
        slot[0] += 1
        if per[h] == "exact":
            slot[1] += 1
        elif per[h] == "pattern":
            slot[2] += 1
        elif per[h] == "pool":
            pass
        else:
            slot[3] += 1

    L = []
    a = L.append
    a("# 屏幕覆盖率（screen coverage）")
    a("")
    a("> 分母是**运行时捕获到并真的渲染过的文本**，分子是**游戏真的会加载的字典**。")
    a("> 与 `reports/coverage.md`（分母 = 抽取出来的 pool）互补：那个看「我们抽了多少」，这个看「玩家看到多少变中文了」。")
    a("")
    a("| 项 | 值 |")
    a("| --- | --- |")
    a("| 生成时间 | %s |" % meta["generated"])
    a("| 会话文件 | %d 个 / %.1f MB |" % (meta["files"], meta["bytes"] / 1048576.0))
    a("| 捕获行 | %d（坏行 %d） |" % (meta["lines"], meta["bad"]))
    a("| distinct 文本 | **%d** |" % total)
    a("| 可译分母（排除 never + 运行时标注的 path/progress/counter/identifier 等非文案类别） | **%d** |" % applicable)
    a("| 字典条目 | exact %d / pattern %d |" % (len(exact_h), len(pattern_h)))
    a("| pool 交集（运行时不加载，仅供参考） | %d |" % len(pool_h & set(agg)))
    a("| 归一化契约核对（重算 h 与落盘 h 不一致） | %d |" % meta["mismatch"])
    a("")
    a("## 1. 覆盖率")
    a("")
    a("| 覆盖方式 | 命中 distinct | 其中在可译范围 | 占全部 %d | 占可译 %d |" % (total, applicable))
    a("| --- | --- | --- | --- | --- |")
    a("| exact（字典里有同一原文） | %d | %d | %.2f%% | %.2f%% |"
      % (stat["exact"], exact_app, 100.0 * exact_app / max(total, 1), 100.0 * exact_app / max(applicable, 1)))
    a("| pattern（骨架行命中） | %d | %d | %.2f%% | %.2f%% |"
      % (stat["pattern"], pattern_app, 100.0 * pattern_app / max(total, 1), 100.0 * pattern_app / max(applicable, 1)))
    a("| **合计** | **%d** | **%d** | **%.2f%%** | **%.2f%%** |"
      % (covered, covered_app, 100.0 * covered_app / max(total, 1), pct_app))
    a("| pool-only（运行时**不**加载） | %d | — | — | — |" % stat["pool"])
    a("| 未覆盖 missing（可译范围内） | %d | %d | %.2f%% | %.2f%% |"
      % (missing_app, missing_app, 100.0 * missing_app / max(total, 1), 100.0 * missing_app / max(applicable, 1)))
    a("| 不适配 / 不该翻（never + 非文案） | %d | — | %.2f%% | — |"
      % (total - applicable, 100.0 * (total - applicable) / max(total, 1)))
    a("")
    a("**可译覆盖率 = %.2f%%**（分子 %d / 分母 %d；分子 = exact %d + pattern %d，两者都只数落在可译范围内的命中）。"
      % (pct_app, covered_app, applicable, exact_app, pattern_app))
    a("")
    a("读法：`命中 distinct` 与 `其中在可译范围` 两列必须分开看 —— pattern 命中里绝大多数是进度条 / 计数器"
      "（运行时已标 `progress` / `counter`），翻它们没有意义，所以百分比只按可译范围内的那部分算。"
      "想知道骨架通道救回多少真文案，看 `pattern` 行的 `其中在可译范围` 那一列。")
    a("")
    a("## 2. 按捕获分桶")
    a("")
    a("| bucket | distinct | exact | pattern | missing |")
    a("| --- | --- | --- | --- | --- |")
    for key in sorted(by_bucket, key=lambda k: -by_bucket[k][0]):
        slot = by_bucket[key]
        a("| %s | %d | %d | %d | %d |" % (key, slot[0], slot[1], slot[2], slot[3]))
    a("")
    a("## 3. 未覆盖最多的调用点（前 %d）" % len(top))
    a("")
    a("这些就是「屏幕上还挂着英文」的地方，按调用次数降序。`caller` 直接对应代码里的方法名，可以据此定位 mod。")
    a("")
    a("| # | 调用次数 | distinct 串 | caller | sink | 样例 |")
    a("| --- | --- | --- | --- | --- | --- |")
    for i, (caller, slot) in enumerate(top, 1):
        a("| %d | %d | %d | `%s` | `%s` | `%s` |" % (i, slot[0], slot[1], caller or "(空)", slot[3] or "(空)", short(slot[2])))
    a("")
    a("## 4. 未覆盖的高频文本（前 %d）" % len(meta["hot"]))
    a("")
    a("| # | 调用次数 | bucket | 文本 |")
    a("| --- | --- | --- | --- |")
    for i, item in enumerate(meta["hot"], 1):
        a("| %d | %d | %s | `%s` |" % (i, item.count, item.bucket or "(空)", short(item.text)))
    a("")
    a("## 5. 怎么读")
    a("")
    a("- **可译覆盖率**是唯一该拿来对玩家承诺的数字；`reports/coverage.md` 的 pool 覆盖率只说明抽取端健康。")
    a("- `pattern` 列在 1.3 之前恒为 0（字典里没有 `p:1` 行），出现非零即骨架通道生效。")
    a("- `pool-only` 不算覆盖：`packs/pool.jsonl` 是生产端源产物，运行时不读（`src/UI18n.Runtime/ConfigLoader.cs:15`）。")
    a("- 归一化契约核对不为 0 说明 C# `TextNormalizer` 与 `i18n_tools/textnorm.py` 已经漂移，必须立即修。")
    a("")
    return chr(10).join(L)


# --------------------------------------------------------------------------- selftest

def selftest():
    ok = 0
    fail = []

    def check(name, cond):
        nonlocal ok
        if cond:
            ok += 1
        else:
            fail.append(name)

    tmp = tempfile.mkdtemp(prefix="u18n-sccov-")
    packs = os.path.join(tmp, "packs")
    os.makedirs(packs)
    cap = os.path.join(tmp, "cap")
    os.makedirs(cap)

    def h(text):
        return fnv.hash_text(textnorm.normalize(text))

    with open(os.path.join(packs, "A.jsonl"), "w", encoding="utf-8") as fh:
        fh.write(json.dumps({"h": h("Takeoff"), "n": "Takeoff", "s": "Takeoff", "t": "起飞", "mod": "A"}) + "\n")
        fh.write(json.dumps({"h": h("Mass Flow: # ton/s"), "n": "Mass Flow: # ton/s",
                             "s": "Mass Flow: # ton/s", "t": "质量流量：# 吨/秒", "mod": "A", "p": 1}) + "\n")
        fh.write("not json\n")

    rows = [
        {"h": h("Takeoff"), "s": "Takeoff", "sink": "GUI.Label", "caller": "C.A", "count": 10, "bucket": "candidate"},
        {"h": h("Mass Flow: 2.2 ton/s"), "s": "Mass Flow: 2.2 ton/s", "sink": "GUI.Label",
         "caller": "C.B", "count": 3, "bucket": "template", "template": True},
        {"h": h("Loading texture asset 1/9"), "s": "Loading texture asset 1/9", "sink": "TMP",
         "caller": "LoadingScreen.X", "count": 99, "bucket": "template", "reason": "progress", "template": True},
        {"h": "deadbeefdeadbeef", "s": "Bogus", "sink": "GUI.Label", "caller": "C.C", "count": 1, "bucket": "candidate"},
        {"h": h("C:/x/y.cfg"), "s": "C:/x/y.cfg", "sink": "GUI.Label", "caller": "C.D",
         "count": 5, "bucket": "never", "reason": "path"},
    ]
    with open(os.path.join(cap, "capture-1.jsonl"), "w", encoding="utf-8") as fh:
        for r in rows:
            fh.write(json.dumps(r) + "\n")

    exact_h, pattern_h, files = load_pack_hashes(packs)
    check("pack exact 计数", len(exact_h) == 1)
    check("pack pattern 计数", len(pattern_h) == 1)
    check("坏行被跳过不崩", len(files) == 1)

    paths = iter_capture_files([os.path.join(cap, "*.jsonl")])
    agg, lines, bad, mismatch = collect(paths, quiet=True)
    check("捕获行数", lines == 5)
    check("坏行 0", bad == 0)
    check("h 不一致被计数", mismatch == 1)
    check("distinct = 5", len(agg) == 5)

    # pattern 命中：翻译端只要把骨架查出来，运行端 fill_back 就成
    sk, nums = skeleton.skeleton("Mass Flow: 2.2 ton/s")
    check("骨架生成", sk == "Mass Flow: # ton/s" and nums == ["2.2"])
    check("骨架 hash 命中 pattern 集", fnv.hash_text(textnorm.normalize(sk)) in pattern_h)

    stat, per = classify(agg, exact_h, pattern_h, set())
    check("exact 命中 1", stat["exact"] == 1)
    check("pattern 未直接命中（原文哈希≠骨架哈希）", stat["pattern"] == 0)
    check("missing 4", stat["missing"] == 4)

    # 骨架补算：classify 只看精确哈希，pattern 的真实命中要靠 skeleton 再算一次
    pat_hit = 0
    for hh, item in agg.items():
        if per[hh] != "missing":
            continue
        s2, _ = skeleton.skeleton(item.text)
        if s2 is not None and fnv.hash_text(textnorm.normalize(s2)) in pattern_h:
            pat_hit += 1
    check("骨架二次判定命中 1", pat_hit == 1)

    applicable = sum(1 for hh in agg if is_applicable(agg[hh]))
    check("可译分母排掉 never/progress", applicable == 3)

    tops = top_callers(agg, per, 5)
    check("未覆盖 caller 不含 never 桶", all(c != "C.D" for c, _ in tops))
    check("未覆盖 caller 不含 progress 噪声", all(c != "LoadingScreen.X" for c, _ in tops))
    check("按调用次数排序", tops[0][0] == "C.B")

    hot = top_texts(agg, per, 3)
    check("高频文本排序", hot[0].count == 3 and hot[0].text.startswith("Mass Flow"))
    check("高频文本不含 progress 噪声", all("Loading texture" not in it.text for it in hot))

    md = build_report(stat, agg, per, paths, exact_h, pattern_h, set(),
                      {"generated": "t", "files": 1, "bytes": 1, "lines": lines, "bad": 0,
                       "mismatch": mismatch, "hot": hot}, tops)
    check("报告含标题", md.startswith("# 屏幕覆盖率"))
    check("报告含可译覆盖率", "可译覆盖率 =" in md)

    # 负向：packs 目录不存在时不应崩
    try:
        load_pack_hashes(os.path.join(tmp, "nope"))
        ok += 1
    except OSError:
        fail.append("packs 目录缺失应静默为空")

    print("selftest: %d passed, %d failed" % (ok, len(fail)))
    for name in fail:
        print("  FAIL %s" % name)
    return 0 if not fail else 1


# --------------------------------------------------------------------------- 主流程

def main(argv=None):
    ap = argparse.ArgumentParser(prog="screen_coverage", add_help=True,
                                 description="屏幕覆盖率：分母=运行时捕获到的文本，分子=游戏真正加载的字典")
    ap.add_argument("files", nargs="*", help="capture-*.jsonl 文件")
    ap.add_argument("--dir", action="append", default=[], help="捕获目录（可重复）")
    ap.add_argument("--all", action="append", default=[], help="捕获目录（可重复，等价 --dir）")
    ap.add_argument("--packs", default=DEFAULT_PACKS, help="字典目录（默认 packs/zh-cn）")
    ap.add_argument("--pool", default=os.path.join(REPO_ROOT, "packs", "pool.jsonl"), help="pool.jsonl（仅参考）")
    ap.add_argument("--out", default=DEFAULT_OUT, help="报告输出路径（写空字符串则不写文件）")
    ap.add_argument("--top", type=int, default=25, help="调用点表长度（默认 25）")
    ap.add_argument("--fail-under", type=float, default=None, help="可译覆盖率低于该百分比则 exit 1")
    ap.add_argument("--selftest", action="store_true", help="自检")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args(argv)

    if args.selftest:
        return selftest()

    pats = list(args.files)
    for d in list(args.dir) + list(args.all):
        pats.append(os.path.join(d, "capture-*.jsonl"))
    paths = iter_capture_files(pats)
    if not paths:
        print("没有找到任何 capture-*.jsonl（用 --all <dir> 指定）", file=sys.stderr)
        return 2

    total_bytes = sum(os.path.getsize(p) for p in paths)
    exact_h, pattern_h, pack_files = load_pack_hashes(args.packs)
    pool_h = load_pool_hashes(args.pool)

    t0 = time.time()
    agg, lines, bad, mismatch = collect(paths, quiet=args.quiet)
    stat, per = classify(agg, exact_h, pattern_h, pool_h)

    # 骨架二次判定：pattern 行在字典里存的是「骨架的哈希」，而捕获行存的是「带数字原文的哈希」，
    # 两者天然不相等，必须用 TextSkeleton.TryBuild + 骨架哈希再查一次。
    pat_bytes = 0
    if pattern_h:
        for h, item in agg.items():
            if per[h] != "missing":
                continue
            sk, _nums = skeleton.skeleton(item.text)
            if sk is not None and fnv.hash_text(textnorm.normalize(sk)) in pattern_h:
                per[h] = "pattern"
                pat_bytes += 1
        if pat_bytes:
            stat["missing"] -= pat_bytes
            stat["pattern"] += pat_bytes

    total = len(agg)
    applicable = sum(1 for h in agg if is_applicable(agg[h]))
    covered_app = sum(1 for h in agg if per[h] in ("exact", "pattern") and is_applicable(agg[h]))
    pct_app = 100.0 * covered_app / applicable if applicable else 0.0
    pct_all = 100.0 * (stat["exact"] + stat["pattern"]) / total if total else 0.0

    tops = top_callers(agg, per, args.top)
    hot = top_texts(agg, per, 10)

    meta = dict(generated=time.strftime("%Y-%m-%d %H:%M:%S"), files=len(paths), bytes=total_bytes,
                lines=lines, bad=bad, mismatch=mismatch, hot=hot)
    md = build_report(stat, agg, per, paths, exact_h, pattern_h, pool_h, meta, tops)

    if args.out:
        out = args.out if os.path.isabs(args.out) else os.path.abspath(args.out)
        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        with open(out, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(md + "\n")
        if not args.quiet:
            print("wrote %s (%d B)" % (out, len(md.encode("utf-8"))))

    print("screen coverage: files=%d lines=%d distinct=%d applicable=%d exact=%d pattern=%d poolOnly=%d missing=%d mismatch=%d -> applicable %.2f%%, all %.2f%% (%.1fs)"
          % (len(paths), lines, total, applicable, stat["exact"], stat["pattern"], stat["pool"],
             stat["missing"], mismatch, pct_app, pct_all, time.time() - t0))

    if args.fail_under is not None and pct_app < args.fail_under:
        print("FAIL: 可译覆盖率 %.2f%% < 门槛 %.2f%%" % (pct_app, args.fail_under), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
