"""覆盖度核算（provenance）：packs/overrides.jsonl 对捕获候选档 / 骨架档的覆盖率。

用法：python reports/coverage_overrides.py
输入：packs/overrides.jsonl + packs/capture-candidates.jsonl（task-45 起 bucket=candidate/pattern，
      pattern 行的 s 已是骨架）；文件不存在时退回 _scratch/capture-candidates-v3.jsonl 的
      candidate/template 老桶（骨架现算）。
输出：stdout（数字已抄进 reports/DELIVERY.md §13.2）+ _scratch/t46-coverage.json
"""
import json, re, sys, io, collections
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
ROOT = r"C:\Users\43701\Documents\github\UniversalI18n"
sys.path.insert(0, ROOT + r"\tools")
from i18n_tools import skeleton as SK
from i18n_tools.textnorm import normalize

ovr = [json.loads(l) for l in open(ROOT + r"\packs\overrides.jsonl", encoding="utf-8") if l.strip()]
table = {normalize(r["en"]): r for r in ovr}
pat_keys = {k for k in table if table[k]["note"].startswith("骨架通道")}
print("overrides:", len(ovr), "pattern:", len(pat_keys))

src = ROOT + r"\packs\capture-candidates.jsonl"
try:
    rows = [json.loads(l) for l in open(src, encoding="utf-8") if l.strip()]
except FileNotFoundError:
    src = ROOT + r"\_scratch\capture-candidates-v3.jsonl"
    rows = [json.loads(l) for l in open(src, encoding="utf-8") if l.strip()]
print("input:", src, len(rows), "行", dict(collections.Counter(r.get("bucket") for r in rows)))

# ---- candidate 覆盖 ----
cand = [r for r in rows if r.get("bucket") == "candidate"]
cand_cov = [r for r in cand if normalize(r["s"]) in table]
uniq = {normalize(r["s"]) for r in cand}
uniq_cov = {k for k in uniq if k in table}
print("candidate 行 %d / 唯一 %d -> 覆盖行 %d (%.1f%%) / 唯一覆盖 %d" % (
      len(cand), len(uniq), len(cand_cov), 100.0 * len(cand_cov) / max(1, len(cand)), len(uniq_cov)))

# ---- 骨架覆盖（pattern 桶的 s 已是骨架；老桶 template 现算骨架）----
skel_rows = []
for r in rows:
    if r.get("bucket") == "pattern":
        skel_rows.append((r, r["s"]))
    elif r.get("bucket") == "template":
        sk, _nums = SK.skeleton(r["s"])
        if sk:
            skel_rows.append((r, sk))
covered_rows = sum(1 for _r, sk in skel_rows if normalize(sk) in pat_keys)
covered_misses = sum(_r.get("count", 1) for _r, sk in skel_rows if normalize(sk) in pat_keys)
total_misses = sum(_r.get("count", 1) for _r, sk in skel_rows)
uniq_skel = {sk for _r, sk in skel_rows}
print("骨架行 %d / 唯一骨架 %d -> 覆盖行 %d (%.1f%%) / 覆盖会话次数 %d (%.1f%%)" % (
      len(skel_rows), len(uniq_skel), covered_rows, 100.0 * covered_rows / max(1, len(skel_rows)),
      covered_misses, 100.0 * covered_misses / max(1, total_misses)))
print("覆盖的唯一骨架 %d / %d" % (len({s for s in uniq_skel if normalize(s) in pat_keys}), len(uniq_skel)))
uncovered = collections.Counter(sk for _r, sk in skel_rows if normalize(sk) not in pat_keys)
print("未覆盖骨架 top12:", uncovered.most_common(12))

# ---- candidate 丢弃分类（行级 + 唯一级）----
def drop_reason(r):
    s = r["s"]; callers = r.get("callers") or ["?"]; st = s.strip()
    if "[" in s and "]" in s: return "material"
    if callers[0].startswith("ProfilerUI.DrawPluginsContent"): return "typename"
    if len(re.sub(r"<[^<>]*>", "", s).split()) <= 1 and len(s) <= 40: return "single-token"
    if re.match(r"^(SSC|STDN|MSFN|KSAT|US)\s*-", st): return "station"
    if st.endswith(" Kerman"): return "kerbal-name"
    if re.match(r"^(Loading|Searching)", st): return "loading"
    if st.startswith("Kopernicus:"): return "kopernicus"
    if st.startswith("ModuleManager:"): return "mm"
    return ""
dc = collections.Counter(); du = collections.Counter(); seen = set()
for r in cand:
    why = drop_reason(r) or "keep"
    dc[why] += 1
    k = normalize(r["s"])
    if k not in seen:
        seen.add(k); du[why] += 1
print("candidate 分类(行):", dict(dc))
print("candidate 分类(唯一):", dict(du))

out = {"input": src,
       "overrides": {"rows": len(ovr), "pattern": len(pat_keys)},
       "candidate": {"rows": len(cand), "uniq": len(uniq), "covered_rows": len(cand_cov),
                     "covered_uniq": len(uniq_cov), "classes_rows": dict(dc), "classes_uniq": dict(du)},
       "skeleton": {"rows": len(skel_rows), "uniq_skel": len(uniq_skel),
                    "covered_rows": covered_rows, "covered_misses": covered_misses,
                    "total_misses": total_misses}}
json.dump(out, open(ROOT + r"\_scratch\t46-coverage.json", "w", encoding="utf-8"),
          ensure_ascii=False, indent=1)
print("-> _scratch/t46-coverage.json")
