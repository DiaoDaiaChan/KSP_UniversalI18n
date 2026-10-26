"""packs/overrides.jsonl 的生成器（provenance，不是运行时工具，不进流水线链路）。

用法：python reports/build_overrides.py
输入：packs/capture-candidates.jsonl（task-45 起的 capture 候选档；缺文件时退回 _scratch/capture-candidates-v3.jsonl）
      + reports/overrides_map.py（人工译文表）+ 现有 packs/overrides.jsonl（手写的 4 行原样保留在最前）
输出：packs/overrides.jsonl 全量重写（幂等：同输入连跑两次字节相同）
门禁：candidate/rescue 走 gate_reason（占位符多重集 + 富文本标签多重集）；pattern 额外比 skeleton.placeholders 的 # 个数（= G9 在构建期提前生效）
"""
import json, re, sys, io, os, collections
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
ROOT = r"C:\Users\43701\Documents\github\UniversalI18n"
sys.path.insert(0, ROOT + r"\tools")
sys.path.insert(0, ROOT + r"\reports")
from i18n_tools import skeleton as SK
from i18n_tools import mask as MK
from i18n_tools.textnorm import normalize
import overrides_map as Z

CAP = ROOT + r"\packs\capture-candidates.jsonl"
V3 = ROOT + r"\_scratch\capture-candidates-v3.jsonl"
OVR = ROOT + r"\packs\overrides.jsonl"

SRC = CAP if os.path.exists(CAP) else V3
rows = [json.loads(l) for l in open(SRC, encoding="utf-8") if l.strip()]
print("输入:", SRC, len(rows), "行", dict(collections.Counter(r.get("bucket") for r in rows)))
by_n = collections.defaultdict(list)
for r in rows:
    by_n[normalize(r["s"])].append(r)

def resolve(anchor, suffix=""):
    # 1) 精确
    hits = by_n.get(anchor)
    if suffix:
        hits = [r for r in (hits or []) if normalize(r["s"]).endswith(suffix)] if hits else None
    if hits:
        return hits[0], "exact"
    # 2) 唯一子串
    cand = []
    for n, rs in by_n.items():
        if anchor in n and (not suffix or n.endswith(suffix)):
            cand.append(rs[0])
    if len(cand) == 1:
        return cand[0], "substring"
    return (None, "ambiguous(%d)" % len(cand)) if cand else (None, "missing")

def gate_reason(src, zh):
    if not zh.strip():
        return "empty"
    if MK.count_placeholders(src) != MK.count_placeholders(zh):
        return "placeholder"
    if MK.count_rich_tags(src) != MK.count_rich_tags(zh):
        return "richtext"
    return ""

# 基线 = 手写的行（note 不是本脚本生成的两种前缀），必须原样保留；
# 本脚本生成的 candidate/pattern 行一律由 overrides_map.py 重新生成 —— 否则改表不生效（实测踩过）。
existing = [json.loads(l) for l in open(OVR, encoding="utf-8") if l.strip()]
GENERATED = ("捕获候选档", "骨架通道")
baseline = [r for r in existing if not r["note"].startswith(GENERATED)]
have = {normalize(r["en"]): r for r in baseline}
print("现有 overrides:", len(existing), "条 → 基线", len(baseline), "条（其余由译文表重算）")

new_rows, problems, covered_rows, detail = [], [], [], collections.Counter()
for anchor, suffix, zh in Z.CAND:
    row, how = resolve(anchor, suffix)
    if row is None:
        problems.append("candidate %-40s %s" % (anchor, how)); continue
    src = row["s"]
    why = gate_reason(src, zh)
    if why:
        problems.append("candidate %-40s 门禁 %s" % (anchor, why)); continue
    key = normalize(src)
    if key in have:
        detail["already"] += 1
        continue
    callers = row.get("callers") or ["?"]
    note = "捕获候选档（真实 UI 文案）| caller=%s | sessions=%s | count=%s" % (
        callers[0], row.get("sessions"), row.get("count"))
    new_rows.append({"en": src, "zh": zh, "note": note})
    have[key] = new_rows[-1]
    detail["cand_" + how] += 1
    covered_rows.append((key, len(by_n[key])))

# pattern：骨架键必须是候选档里真实出现过的骨架。
# 两种上游形态都吃：bucket=pattern（s 已是骨架，task-45 起）/ bucket=template（s 是实例，现算骨架）。
skel_rows = collections.defaultdict(list)
for r in rows:
    if r.get("bucket") == "pattern":
        skel_rows[r["s"]].append(r)
    elif r.get("bucket") == "template":
        sk, _nums = SK.skeleton(r["s"])
        if sk:
            skel_rows[sk].append(r)
print("骨架唯一键:", len(skel_rows))
for anchor, zh in Z.PATT:
    keys = [anchor] if anchor in skel_rows else [k for k in skel_rows if anchor in k]
    if len(keys) != 1:
        problems.append("pattern %-40s 命中 %d 个骨架" % (anchor, len(keys)))
        continue
    sk = keys[0]
    np_src, np_zh = SK.placeholders(sk), SK.placeholders(zh)
    if np_src != np_zh:
        problems.append("pattern %-40s G9 # 个数 %d != %d" % (anchor, np_zh, np_src))
        continue
    why = gate_reason(sk, zh)
    if why:
        problems.append("pattern %-40s 门禁 %s" % (anchor, why))
        continue
    key = normalize(sk)
    if key in have:
        detail["already"] += 1
        continue
    rs = skel_rows[sk]
    callers = collections.Counter((r.get("callers") or ["?"])[0] for r in rs)
    note = "骨架通道（pattern）| 骨架 %d 个实例 / %d 个源串 | caller=%s" % (
        sum(r.get("count", 1) for r in rs), len(rs), callers.most_common(1)[0][0])
    new_rows.append({"en": sk, "zh": zh, "note": note})
    have[key] = new_rows[-1]
    detail["patt"] += 1
    covered_rows.append((key, len(rs)))

print()
print("新增:", len(new_rows), dict(detail))
print("覆盖的候选源串条数(candidate):", sum(c for k, c in covered_rows if k in {normalize(r["en"]) for r in new_rows[:len(Z.CAND)]}) )
print("问题:", len(problems))
for p in problems:
    print("  !", p)

out = baseline + new_rows
with open(OVR, "w", encoding="utf-8", newline="\n") as f:
    for r in out:
        f.write(json.dumps(r, ensure_ascii=False, separators=(",", ":")) + "\n")
print("写盘 packs/overrides.jsonl:", len(out), "条")
