"""selftest.py -- 全流水线自检：`python -m i18n_tools.selftest`，退出码 0 即通过。

自检**不读真实 GameData**，全部用临时目录里的假 mod 复现真实结构（真实数据的验收另有
`python -m i18n_tools.harvest --verify ..\\_scratch\\corpus\\pool.jsonl`，它要与既有产物逐字节一致）。

覆盖的坑（每个都是本项目真实踩过的，见 README 已知坑）：
  1. FNV-1a 64 三个官方向量 + 16 位小写 hex 形式；
  2. 归一化：CRLF / 制表 / NBSP / 全角 都要折成单个半角空格；
  3. pure-number 必须收紧，不能把 "<<1>> <<2>>" 这类占位符文本当纯数字丢掉；
  4. cfg 解析：节点名单独一行、左大括号在下一行；CRLF 文件；https 协议头不能被当注释；
     单行补丁节点 @AGENT[...] { title = #$name$ } 的右大括号不能吃进值里；
  5. Localization 判定 = 路径里有 Localization 目录段（大小写不敏感），文件名带 Localization 不算，
     且**按 mod 求语言并集**（语言分文件是常态，同一 mod 的 en-us / zh-cn 在不同文件里）；
  6. cfg 文本字段：title/description/displayName 的大小写变体与 xxx_title 后缀变体、tags 走跳过；
  7. glossary：英文只从 cfg 行尾注释取（dictionary.cfg 自己的中文行绝不能被当成英文），
     注释键要补 # 才能与字典键配对；
  8. 产物自检：packs/pool.jsonl 与既有真值逐字节一致（有真值文件时才检查）。
  9. 包索引：packs/index.json 的排序确定性、字段齐全、sha256/rows/keepRows 与真实文件一致、
     幂等（除 generatedAt 外逐字节相同）、--check 正例 exit 0 与四种负例 exit 1。
  10. IL 采集：sink 白名单与 PatchRegistry 一致（Debug.Log 绝不算 sink）、真解 IL 的偏移、
      签名里的 CLASS/VALUETYPE 与 CMOD_* 都按压缩 TypeDefOrRefEncoded 读（读错会让实参整体
      错位、字面量全丢）、候选行去重排序与 h 口径、落盘幂等。
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import sys
import tempfile

from . import (api, candidates, capture_ingest, cfgparse, check_overrides, ensure_stdout,
                fnv, glossary, golden_gate, harvest, harvest_cfgcache, ilmeta, ilstrings,
                journal_gate, merge_overrides, packindex, render_coverage, textnorm)

_CHECKED = 0
_FAILED: list[str] = []


def check(name, ok, detail=""):
    global _CHECKED
    _CHECKED += 1
    if ok:
        print("  [ok]   " + name)
    else:
        print("  [FAIL] " + name + ("  " + detail if detail else ""))
        _FAILED.append(name)


def eq(name, got, want):
    check(name, got == want, "got=%r want=%r" % (got, want))


def _write(path, text, crlf=False):
    """写 fixture 文件；crlf=True 时把换行换成 CRLF（复刻真实 cfg 的换行）。"""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if crlf:
        text = text.replace("\n", "\r\n")
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)


# ---------------------------------------------------------------------------
# 1. FNV
# ---------------------------------------------------------------------------
def test_fnv():
    print("[1] FNV-1a 64")
    for text, want in fnv.VECTORS:
        eq("fnv1a64(%r)" % text, fnv.hash_int(text), want)
    eq("hash_text(foobar)", fnv.hash_text("foobar"), "85944171f73967e8")
    eq("hash_text 长度", len(fnv.hash_text("任意中文")), 16)
    eq("hash_text 小写", fnv.hash_text("任意中文"), fnv.hash_text("任意中文").lower())
    eq("UTF-8 而非 UTF-16（中文非 ASCII 也能算）", fnv.hash_text("引擎"), fnv.hash_text("引擎"))


# ---------------------------------------------------------------------------
# 2. 归一化与判据
# ---------------------------------------------------------------------------
def test_textnorm():
    print("[2] 归一化 / 过滤判据 / 特征位")
    eq("CRLF + 制表折单空格", textnorm.normalize("  S1 SRB\r\n\t KD25k  "), "S1 SRB KD25k")
    eq("NBSP 折单空格", textnorm.normalize("a" + chr(160) * 2 + "b"), "a b")
    eq("全角归一半角（NFKC）", textnorm.normalize("ＡＢＣ１２３"), "ABC123")
    eq("不折叠大小写", textnorm.normalize("Title"), "Title")
    quotes = chr(34) + "x" + chr(34) + " " + chr(8220) + "y" + chr(8221)
    eq("不统一引号", textnorm.normalize(quotes), quotes)
    check("pure-number 1,234.5", textnorm.is_pure_number("1,234.5"))
    check("pure-number 50%", textnorm.is_pure_number("50%"))
    check("pure-number 不吞占位符 <<1>> <<2>>", not textnorm.is_pure_number("<<1>> <<2>>"))
    check("pure-number 不吞 1 of 3", not textnorm.is_pure_number("1 of 3"))
    check("pure-number 不吞 V2", not textnorm.is_pure_number("V2"))
    check("already-cjk 命中中文", textnorm.has_cjk("固体燃料助推器"))
    check("already-cjk 不误判英文", not textnorm.has_cjk("Solid Fuel Booster"))
    sample = "Hold <<1>> m <b>x</b> 3s https://a.b/c"
    feats = textnorm.features(sample)
    eq("features.len", feats["len"], len(sample))
    check("features.ph", feats["ph"])
    check("features.rt", feats["rt"])
    check("features.dg", feats["dg"])
    check("features.url", feats["url"])
    check("features.path 不误吃 URL", not textnorm.features("https://a.b/c")["path"])
    eq("len_bucket(2)", textnorm.len_bucket(2), "2-10")
    eq("len_bucket(11)", textnorm.len_bucket(11), "11-30")
    eq("len_bucket(500)", textnorm.len_bucket(500), "200+")


# ---------------------------------------------------------------------------
# 3. cfg 解析
# ---------------------------------------------------------------------------
def test_cfgparse():
    print("[3] cfg 解析")
    text = "PART\n{\n\ttitle = Foo //#autoLOC_1 = Foo\n\turl = https://example.com/a\n}\n"
    root = cfgparse.parse_text(text)
    part = root.child("PART")
    check("节点名单独一行 + 左大括号下一行", part is not None)
    if part is not None:
        eq("行尾双斜杠注释被剥掉", part.get("title"), "Foo")
        eq("https 协议头不当注释", part.get("url"), "https://example.com/a")
    crlf = "Localization\r\n{\r\n\ten-us\r\n\t{\r\n\t\tk = v\r\n\t}\r\n}\r\n"
    root2 = cfgparse.parse_text(crlf)
    loc = root2.child("Localization")
    check("CRLF 文件能认出 Localization（曾经的 CRLF bug）", loc is not None)
    if loc is not None:
        eq("CRLF 文件里语言节点名干净", [c.name for c in loc.children], ["en-us"])
    inline = "@AGENT[*]:HAS[~title] { title = #$name$ }\n"
    root3 = cfgparse.parse_text(inline)
    vals = [v for node in root3.walk() for (_k, v, _ln) in node.values]
    check("单行补丁节点不把右大括号吃进值里",
          all(not v.rstrip().endswith("}") for v in vals), repr(vals))
    deep = "A\n{\n" + "".join("\tB\n\t{\n" for _ in range(3)) + "".join("\t}\n" for _ in range(3)) + "}\n"
    root4 = cfgparse.parse_text(deep)
    eq("多层嵌套节点数（root + A + 3B）", sum(1 for _ in root4.walk()), 5)
    eq("解析统计记账", cfgparse.parse_text("X\n{\n\ta = 1\n}\n").child("X").get("a"), "1")


# ---------------------------------------------------------------------------
# 4. 全流水线（假 GameData）
# ---------------------------------------------------------------------------
def build_fake_gamedata(base):
    gd = os.path.join(base, "GameData")
    _write(os.path.join(gd, "FakeAlpha", "Localization", "en-us.cfg"),
           "Localization\n{\n\ten-us\n\t{\n\t\t#a_one = Alpha One\n\t\t#a_two = Alpha Two\n\t}\n}\n")
    _write(os.path.join(gd, "FakeBeta", "Localization", "en-us.cfg"),
           "Localization\n{\n\ten-us\n\t{\n\t\t#b_one = Beta One\n\t\t#b_three = Beta Three\n\t}\n}\n")
    _write(os.path.join(gd, "FakeBeta", "Localization", "zh-cn.cfg"),
           "Localization\n{\n\tzh-cn\n\t{\n\t\t#b_one = 贝塔一\n\t}\n}\n")
    _write(os.path.join(gd, "FakeBeta", "Parts", "beta.cfg"),
           "PART\n{\n\ttitle = Beta Part\n\tdescription = A beta part\n\ttags = beta part test\n}\n")
    _write(os.path.join(gd, "FakeGamma", "Localization", "en-us.cfg"),
           "Localization\n{\n\ten-us\n\t{\n\t\t#g_one = Gamma One\n\t}\n}\n", crlf=True)
    _write(os.path.join(gd, "FakeGamma", "Localization", "zh-cn.cfg"),
           "Localization\n{\n\tzh-cn\n\t{\n\t\t#g_one = 伽马一\n\t}\n}\n", crlf=True)
    _write(os.path.join(gd, "FakeGamma", "configs", "Gamma-Localization.cfg"),
           "PART\n{\n\ttitle = Gamma Non Loc Title\n}\n")
    _write(os.path.join(gd, "FakeGamma", "PatchManager", "Lang", "en-us.cfg"),
           "Localization\n{\n\ten-us\n\t{\n\t\t#should_ignore = Should Ignore Me\n\t}\n}\n")
    _write(os.path.join(gd, "FakeDelta", "parts", "delta.cfg"),
           "PART\n{\n\ttitle = Delta Title One\n\tTitle = Delta Caps Title\n"
           "\tdisplayName = Delta Display\n\treal_title = Delta Real Title\n"
           "\t!real_description = Delta Real Desc\n\tDescription = Delta Caps Desc\n"
           "\ttags = delta tag words\n}\n")
    return gd


def test_pipeline(base):
    print("[4] 全流水线（假 GameData，harvest.main 端到端）")
    gd = build_fake_gamedata(base)
    out = os.path.join(base, "pool.jsonl")
    inv_out = os.path.join(base, "inventory.json")
    rc = harvest.main(["--gamedata", gd, "--out", out, "--inventory", inv_out, "--quiet"])
    eq("harvest.main 退出码", rc, 0)
    with open(inv_out, encoding="utf-8") as fh:
        inv = json.load(fh)
    eq("cfgFiles", inv["cfgFiles"], 9)
    eq("locFiles（只认 Localization 目录段）", inv["locFiles"], 5)
    eq("modsWithLocalization", inv["modsWithLocalization"], 3)
    eq("locKeysTotal（按 mod 求并集）", inv["locKeysTotal"], 5)
    eq("locKeysCovered", inv["locKeysCovered"], 2)
    eq("locKeysMissingZh", inv["locKeysMissingZh"], 3)
    eq("cfgFieldHits（含 tags 与后缀变体）", inv["cfgFieldHits"], 11)
    eq("poolSize", inv["poolSize"], 12)
    eq("poolBreakdown.loc", inv["poolBreakdown"]["loc"], 3)
    eq("poolBreakdown.cfg", inv["poolBreakdown"]["cfg"], 9)
    eq("skip.search-tags", inv["skip"].get("search-tags"), 2)
    eq("其它过滤器不应命中", sorted(k for k in inv["skip"] if k != "search-tags"), [])
    with open(out, encoding="utf-8") as fh:
        rows = [json.loads(line) for line in fh.read().split("\n")]
    eq("pool 行数 = inventory.poolSize", len(rows), inv["poolSize"])
    eq("id 连续", [r["id"] for r in rows], list(range(1, len(rows) + 1)))
    texts = [r["text"] for r in rows]
    check("Lang 目录里的键不进池",
          "#should_ignore" not in texts and "Should Ignore Me" not in texts)
    check("Localization 目录里的缺译键进池（按 key 记，text 是英文原文）",
          any(r.get("key") == "#a_one" and r["text"] == "Alpha One" for r in rows))
    check("名字带 Localization 的普通 cfg 进池", "Gamma Non Loc Title" in texts)
    check("tags 值不进池", "beta part test" not in texts and "delta tag words" not in texts)
    check("后缀变体 real_title 归一为 title",
          any(r["text"] == "Delta Real Title" and r.get("field") == "title" for r in rows))
    check("后缀变体 !real_description 归一为 description",
          any(r["text"] == "Delta Real Desc" and r.get("field") == "description" for r in rows))
    check("Title 变体保留原写法", any(r.get("field") == "Title" for r in rows))
    check("loc 行有 key 无 field", all(("key" in r) != ("field" in r) for r in rows))
    check("h 全是 16 位 hex", all(len(r["h"]) == 16 and int(r["h"], 16) >= 0 for r in rows))
    check("len 与 text 一致", all(r["len"] == len(r["text"]) for r in rows))
    with open(out, "rb") as fh:
        tail = fh.read()[-1:]
    check("末尾无换行（与既有产物一致）", tail != b"\n")


# ---------------------------------------------------------------------------
# 5. glossary（假 Squad）
# ---------------------------------------------------------------------------
def test_glossary(base):
    print("[5] glossary（假 Squad：英文只从行尾注释取）")
    gd = os.path.join(base, "gd_glossary")
    _write(os.path.join(gd, "Squad", "Localization", "dictionary.cfg"),
           "Localization\n{\n\tzh-cn\n\t{\n\t\t#autoLOC_1001 = 引擎\n"
           "\t\t#autoLOC_1002 = 燃料箱\n\t\t#autoLOC_1003 = 只有中文\n\t}\n}\n")
    _write(os.path.join(gd, "Squad", "Parts", "engine.cfg"),
           "PART\n{\n\ttitle = #autoLOC_1001 //#autoLOC_1001 = Engine\n"
           "\tdescription = #autoLOC_1002 //#autoLOC_1002 = Fuel Tank\n}\n")
    out = os.path.join(base, "glossary.jsonl")
    rc = glossary.main(["--gamedata", gd, "--out", out, "--quiet"])
    eq("glossary.main 退出码", rc, 0)
    with open(out, encoding="utf-8") as fh:
        rows = [json.loads(line) for line in fh.read().split("\n")]
    eq("只导双语行（默认不导只有中文的键）", len(rows), 2)
    by_key = {r["key"]: r for r in rows}
    check("配对键补回 #", "#autoLOC_1001" in by_key)
    if "#autoLOC_1001" in by_key:
        eq("en 取自行尾注释", by_key["#autoLOC_1001"]["en"], "Engine")
        eq("zh 取自 dictionary.cfg", by_key["#autoLOC_1001"]["zh"], "引擎")
        eq("short 判据（Engine：1 词 6 字符）", by_key["#autoLOC_1001"]["short"], True)
        eq("字段顺序 key/en/zh/short", list(by_key["#autoLOC_1001"].keys()),
           ["key", "en", "zh", "short"])
    check("字典中文行没被当成英文", all(not textnorm.has_cjk(r["en"]) for r in rows))
    out_all = os.path.join(base, "glossary_all.jsonl")
    rc2 = glossary.main(["--gamedata", gd, "--out", out_all, "--include-zh-only", "--quiet"])
    eq("--include-zh-only 退出码", rc2, 0)
    with open(out_all, encoding="utf-8") as fh:
        rows_all = [json.loads(line) for line in fh.read().split("\n")]
    eq("--include-zh-only 导出全部字典键", len(rows_all), 3)
    check("只有中文的键 en 为空串",
          all(r["en"] == "" for r in rows_all if r["key"] == "#autoLOC_1003"))


# ---------------------------------------------------------------------------
# 7. 包索引 packs/index.json（假 packs：顺序即契约 / 幂等 / --check 正负例）
# ---------------------------------------------------------------------------
def _pack_row(text_n, text_s, text_t, mod):
    """按定稿包的真实行 schema 造一行：{"h","mod","n","s","t"}，h = FNV-1a64(n)。

    字段名与顺序复刻 packs/zh-cn/*.jsonl（json.dumps 默认分隔符、键序 h/mod/n/s/t），
    这样 fixture 与真实产物同构，测出来的行为才对得上。
    """
    row = {"h": fnv.hash_text(text_n), "mod": mod, "n": text_n, "s": text_s, "t": text_t}
    return json.dumps(row, ensure_ascii=False)


def build_fake_packs(base):
    """三个包文件，mod 名故意让 ordinal 序与 case-insensitive 序不同。

    ordinal 升序      = Mmm, ZZZ, aaa（大写字母码点更小）
    case-insensitive = aaa, Mmm, ZZZ
    只有按 ordinal 排才会得到 ["Mmm","ZZZ","aaa"]；这是"顺序即契约"的守卫。
    """
    packs = os.path.join(base, "packs")
    lang_dir = os.path.join(packs, "zh-cn")
    files = {
        "aaa": [("Alpha", "Alpha", "阿尔法")],                        # 1 行 / keep 1
        "Mmm": [("Bravo", "Bravo", "布拉沃"),                         # 2 行 / keep 1
                ("Charlie", "Charlie v2", "查理")],
        "ZZZ": [("Delta", "Delta v2", "德尔塔"),                      # 3 行 / keep 0
                ("Echo", "Echo v2", "回声"),
                ("Foxtrot", "Foxtrot v2", "狐步")],
    }
    for mod, items in files.items():
        _write(os.path.join(lang_dir, mod + ".jsonl"),
               "\n".join(_pack_row(n, s, t, mod) for n, s, t in items) + "\n")
    pool = [
        {"h": fnv.hash_text("Alpha"), "text": "Alpha", "src": "cfg", "mod": "aaa"},
        {"h": fnv.hash_text("Bravo"), "text": "Bravo", "src": "loc", "mod": "Mmm"},
        {"h": fnv.hash_text("Charlie"), "text": "Charlie", "src": "cfg", "mod": "Mmm"},
        {"h": fnv.hash_text("Zulu"), "text": "Zulu", "src": "loc", "mod": "ZZZ"},
    ]
    _write(os.path.join(packs, "pool.jsonl"),
           "\n".join(json.dumps(r, ensure_ascii=False) for r in pool) + "\n")
    return packs


def _strip_generated_at(raw):
    """抹平 generatedAt 那一行，用来证明"除 generatedAt 外逐字节相同"。"""
    out = []
    for line in raw.split(b"\n"):
        out.append(b'"generatedAt": "<X>",' if b'"generatedAt"' in line else line)
    return b"\n".join(out)


def test_packindex(base):
    print("[7] 包索引 packs/index.json（假 packs）")
    packs = build_fake_packs(base)
    entries = packindex.scan_packs(packs, "zh-cn")
    eq("包文件数", len(entries), 3)
    eq("排序 = mod 名 ordinal 升序（不是 case-insensitive）",
       [e["mod"] for e in entries], ["Mmm", "ZZZ", "aaa"])
    eq("条目字段顺序固定", list(entries[0].keys()), list(packindex.ENTRY_FIELDS))
    for e in entries:
        path = os.path.join(packs, e["file"].replace("/", os.sep))
        with open(path, "rb") as fh:
            raw = fh.read()
        rows = [json.loads(x) for x in raw.decode("utf-8").split("\n") if x.strip()]
        eq("sha256(%s) 与真实文件一致" % e["mod"], e["sha256"], hashlib.sha256(raw).hexdigest())
        eq("bytes(%s) 与文件大小一致" % e["mod"], e["bytes"], len(raw))
        eq("rows(%s) 与文件行数一致" % e["mod"], e["rows"], len(rows))
        eq("keepRows(%s) 与 t==s 行数一致" % e["mod"], e["keepRows"],
           sum(1 for r in rows if r["t"] == r["s"]))
    doc = packindex.build_index(entries, "zh-cn", "2026-01-01T00:00:00Z")
    eq("顶层字段顺序", list(doc.keys()),
       ["schema", "lang", "generatedAt", "totalRows", "totalBytes", "packCount", "packs"])
    eq("schema == 1", doc["schema"], 1)
    eq("lang == zh-cn", doc["lang"], "zh-cn")
    eq("packCount == len(packs)", doc["packCount"], len(doc["packs"]))
    eq("totalRows == sum(rows)", doc["totalRows"], sum(e["rows"] for e in entries))
    eq("totalBytes == sum(bytes)", doc["totalBytes"], sum(e["bytes"] for e in entries))
    want_bytes = sum(os.path.getsize(os.path.join(packs, "zh-cn", m + ".jsonl"))
                     for m in ("aaa", "Mmm", "ZZZ"))
    eq("假 packs 三条汇总", (doc["totalRows"], doc["totalBytes"], doc["packCount"]),
       (6, want_bytes, 3))

    # 幂等：同样输入两次生成，只有 generatedAt 那一行能变
    a = os.path.join(base, "index_a.json")
    b = os.path.join(base, "index_b.json")
    packindex.write_index(a, packindex.build_index(packindex.scan_packs(packs), "zh-cn",
                                                  "2026-01-01T00:00:00Z"))
    packindex.write_index(b, packindex.build_index(packindex.scan_packs(packs), "zh-cn",
                                                  "2026-01-02T00:00:00Z"))
    with open(a, "rb") as fh:
        raw_a = fh.read()
    with open(b, "rb") as fh:
        raw_b = fh.read()
    check("两次生成确实不同（generatedAt 生效）", raw_a != raw_b)
    check("两次生成除 generatedAt 外逐字节相同",
          _strip_generated_at(raw_a) == _strip_generated_at(raw_b))

    # 正例：生成 -> 校验 -> 退出码 0
    idx = os.path.join(packs, "index.json")
    cov = os.path.join(base, "coverage.md")
    eq("生成 index 的 main 退出码",
       packindex.main(["--packs-dir", packs, "--index", idx, "--coverage", cov,
                       "--pool", os.path.join(packs, "pool.jsonl"), "--quiet"]), 0)
    ok, diffs, doc2 = packindex.check_index(idx, packs)
    check("--check 正例：一致", ok, repr(diffs))
    eq("--check 正例无差异", diffs, [])
    eq("--check 正例退出码 0",
       packindex.main(["--check", "--packs-dir", packs, "--index", idx, "--quiet"]), 0)
    eq("磁盘 index 三总数", (doc2["packCount"], doc2["totalRows"], doc2["totalBytes"]),
       (3, 6, want_bytes))

    # coverage.md：口径必须写在数字旁边
    with open(cov, encoding="utf-8") as fh:
        ctext = fh.read()
    check("coverage 写明分母 = pool 行数", "分母 = " in ctext and "的 4 行" in ctext)
    check("coverage 写明分子 = h 精确命中", "精确命中" in ctext)
    check("coverage 覆盖率 = 3 / 4 = 75.00%", "75.00%" in ctext)
    check("coverage 声明不引用人手填的百分比", "不引用任何人手填的百分比" in ctext)
    check("coverage 逐 mod 表按 rows 降序",
          ctext.index("| ZZZ |") < ctext.index("| Mmm |") < ctext.index("| aaa |"))

    # 负例：每个都用全新副本，避免上一条的破坏污染下一条
    def fresh(tag):
        dst = os.path.join(base, "neg_" + tag)
        shutil.copytree(packs, dst)
        i = os.path.join(dst, "index.json")
        packindex.main(["--packs-dir", dst, "--index", i, "--no-coverage", "--quiet"])
        return dst, i

    d, i = fresh("byte")
    with open(os.path.join(d, "zh-cn", "Mmm.jsonl"), "ab") as fh:
        fh.write(b" ")
    ok, diffs, _ = packindex.check_index(i, d)
    check("负例 改一个字节 -> 不一致", not ok)
    check("负例 改一个字节 -> 报出 sha256 变了",
          any("sha256 变了" in x for x in diffs), repr(diffs))
    eq("负例 改一个字节 -> 退出码 1",
       packindex.main(["--check", "--packs-dir", d, "--index", i, "--quiet"]), 1)

    d, i = fresh("rows")
    with open(os.path.join(d, "zh-cn", "ZZZ.jsonl"), "a", encoding="utf-8", newline="") as fh:
        fh.write(_pack_row("Golf", "Golf", "高尔夫", "ZZZ") + "\n")   # 追加，不是覆盖
    ok, diffs, _ = packindex.check_index(i, d)
    check("负例 加一行 -> rows 变了", not ok and any("rows 变了" in x for x in diffs), repr(diffs))
    eq("负例 加一行 -> 退出码 1",
       packindex.main(["--check", "--packs-dir", d, "--index", i, "--quiet"]), 1)

    d, i = fresh("extra")
    _write(os.path.join(d, "zh-cn", "Yyy.jsonl"), _pack_row("Hotel", "Hotel", "旅馆", "Yyy") + "\n")
    ok, diffs, _ = packindex.check_index(i, d)
    check("负例 多出文件 -> 未登记文件",
          not ok and any("未登记文件" in x for x in diffs), repr(diffs))
    eq("负例 多出文件 -> 退出码 1",
       packindex.main(["--check", "--packs-dir", d, "--index", i, "--quiet"]), 1)

    d, i = fresh("missing")
    os.remove(os.path.join(d, "zh-cn", "aaa.jsonl"))
    ok, diffs, _ = packindex.check_index(i, d)
    check("负例 删文件 -> 文件缺失", not ok and any("文件缺失" in x for x in diffs), repr(diffs))
    eq("负例 删文件 -> 退出码 1",
       packindex.main(["--check", "--packs-dir", d, "--index", i, "--quiet"]), 1)


def test_packindex_real():
    print("[8] 索引产物自检（真实 packs/index.json）")
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    index_path = os.path.join(repo, "packs", "index.json")
    if not os.path.isfile(index_path):
        print("  [skip] packs/index.json 还不存在（先跑 python -m i18n_tools.packindex）")
        return
    doc = packindex.load_index(index_path)
    # 真值随手上的包体变化：改了 packs/zh-cn 就必须重跑 python -m i18n_tools.packindex
    # 并同步这三个数字，否则本项失败——这是「清单不许与磁盘漂移」的最后一道提示。
    # 1.3 出包后（capture → 骨架 → 入包，含 overrides 人工通道）的真值。
    # 每次重跑 translate_il / packindex 之后都要同步这三行 —— 失败时把 got 抄回来，别改断言方向。
    eq("真实 packCount == 97", doc["packCount"], 97)
    eq("真实 totalRows == 14954", doc["totalRows"], 14954)
    # 2026-10-26 p 归一（486 行 "p":1 → "p":true，14 个 .il.jsonl）使 totalBytes +1458 = 486×3：
    # 5170859 → 5172317，行数与包数不变。这是唯一一次「字节变了但语义没变」的重算。
    eq("真实 totalBytes == 5172317", doc["totalBytes"], 5172317)
    eq("真实 schema / lang", (doc["schema"], doc["lang"]), (1, "zh-cn"))
    entries = packindex.scan_packs(os.path.join(repo, "packs"), "zh-cn")
    eq("清单条目与现场扫描逐项一致（清单不能与磁盘漂移）", doc["packs"], entries)
    eq("cfg 包 41 / IL 包 56", (sum(1 for e in entries if not e["file"].endswith(".il.jsonl")),
                              sum(1 for e in entries if e["file"].endswith(".il.jsonl"))), (41, 56))
    eq("唯一 mod 数 82（同名 IL 包与 cfg 包合并成同一个 mod 身份）",
       len({e["mod"] for e in entries}), 82)
    eq("真实 --check 退出码 0", packindex.main(["--check", "--quiet"]), 0)


# ---------------------------------------------------------------------------
# 6. 真实产物自检（有真值文件时才查）
# ---------------------------------------------------------------------------
def test_artifacts():
    print("[6] 产物自检")
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    pool = os.path.join(repo, "packs", "pool.jsonl")
    ref = os.path.join(repo, "_scratch", "corpus", "pool.jsonl")
    glos = os.path.join(repo, "packs", "glossary.jsonl")
    if not os.path.isfile(pool):
        print("  [skip] packs/pool.jsonl 还不存在（先跑 python -m i18n_tools.harvest）")
        return
    with open(pool, "rb") as fh:
        mine = hashlib.sha256(fh.read()).hexdigest()
    n_lines = sum(1 for _ in open(pool, encoding="utf-8"))
    check("packs/pool.jsonl 行数 = 12792", n_lines == 12792, "got=%d" % n_lines)
    if os.path.isfile(ref):
        with open(ref, "rb") as fh:
            want = hashlib.sha256(fh.read()).hexdigest()
        eq("packs/pool.jsonl 与 _scratch 真值逐字节一致", mine, want)
    else:
        print("  [skip] _scratch/corpus/pool.jsonl 不在（真值文件不入库），跳过逐字节比对")
    if os.path.isfile(glos):
        with open(glos, encoding="utf-8") as fh:
            rows = [json.loads(line) for line in fh.read().split("\n")]
        check("glossary 行数 >= 2000", len(rows) >= 2000, "got=%d" % len(rows))
        check("glossary 键都以 # 开头", all(r["key"].startswith("#") for r in rows))
        check("glossary 每行 en/zh 都非空", all(r["en"] and r["zh"] for r in rows))
        with open(glos, "rb") as fh:
            gtail = fh.read()[-1:]
        check("glossary 末尾无换行", gtail != b"\n")
    else:
        print("  [skip] packs/glossary.jsonl 还不存在（先跑 python -m i18n_tools.glossary）")


# ---------------------------------------------------------------------------
# 9. IL 字符串采集（ilstrings.py）
# ---------------------------------------------------------------------------
class _FakeResolver:
    """selftest 用的假签名解析器：只认识 GUIContent..ctor 与 Debug.Log 两个 token。"""

    TABLE = {
        0x0A000001: {"type": "UnityEngine.GUIContent", "name": ".ctor",
                     "params": ["System.String"], "ret": "System.Void"},
        0x0A000002: {"type": "UnityEngine.Debug", "name": "Log",
                     "params": ["System.Object"], "ret": "System.Void"},
        0x0A000003: {"type": "TMPro.TMP_InputField", "name": "set_text",
                     "params": ["System.String"], "ret": "System.Void"},
    }

    def resolve(self, token):
        return self.TABLE.get(token)


def _census_entry(group, family):
    return {"group": group, "family": family, "calls": 0, "methods": set(),
            "lits": set(), "dlls": {}, "reflection_text": False}


def test_ilstrings(base):
    print("[9] IL 字符串采集（ilstrings.py）")
    # --- sink 白名单：必须与 PatchRegistry 一致，Debug.Log 绝不算 sink ---
    eq("sink: GUI.Label 在 A 组", ilstrings.classify("UnityEngine.GUI", "Label"),
       ("A", "GUI.Label"))
    eq("sink: GUILayout.Label 走公开前门 -> DoLabel",
       ilstrings.classify("UnityEngine.GUILayout", "Label"), ("A", "GUILayout.DoLabel"))
    eq("sink: GUIContent..ctor 算 sink（IMGUI 文本入口）",
       ilstrings.classify("UnityEngine.GUIContent", ".ctor"), ("A", "GUIContent..ctor"))
    eq("sink: TMPro.TMP_Text.set_text 在 A 组",
       ilstrings.classify("TMPro.TMP_Text", "set_text"), ("A", "TMPro.TMP_Text.set_text"))
    eq("Debug.Log 归 LOG（对照），不是 sink",
       ilstrings.classify("UnityEngine.Debug", "Log")[0], "LOG")
    check("A 组里没有任何 Debug 方法",
          all(not api.startswith("UnityEngine.Debug")
              for grp, api in ilstrings.API_TABLE.values() if grp == "A"))
    eq("B 组: TMPro.TMP_InputField.set_text",
       ilstrings.classify("TMPro.TMP_InputField", "set_text")[0], "B")
    eq("REFL: PropertyInfo.SetValue",
       ilstrings.classify("System.Reflection.PropertyInfo", "SetValue")[0], "REFL")
    check("REFL 字段名白名单含 text / m_text",
          {"text", "m_text"} <= ilstrings.REFL_FIELD_NAMES)

    # --- 真解 IL：手写字节码 ldstr / newobj / ret ---
    insns = ilmeta.decode_il(bytes.fromhex("7201000070" "730100000a" "2a"))
    eq("decode_il: 操作码序列", [i[1] for i in insns], ["ldstr", "newobj", "ret"])
    eq("decode_il: 偏移按操作数长度累加", [i[0] for i in insns], [0, 5, 10])
    eq("decode_il: ldstr 操作数是 #US 偏移", insns[0][2] & 0xFFFFFF, 1)
    try:
        ilmeta.decode_il(bytes.fromhex("7201000070" "28"))
        truncated = False
    except ilmeta.IlError:
        truncated = True
    check("decode_il: 操作数越界抛 IlError（不许静默吞掉）", truncated)

    # --- 数据流：ldstr -> GUIContent..ctor 必须产出 1 条 hit ---
    census = {"GUIContent..ctor": _census_entry("A", "GUIContent..ctor")}
    hits = []
    ilstrings.Analyzer(_FakeResolver(), {1: "Load Vessel"}, "X.dll", "X", "X.Y", "M()",
                       census, hits, {"aborted": 0, "dataflow": 0}).run(insns)
    eq("人造 IL: GUIContent..ctor 收到 1 条字面量", len(hits), 1)
    eq("人造 IL: 文本", hits[0]["text"] if hits else None, "Load Vessel")
    eq("人造 IL: sink", hits[0]["sink"] if hits else None, "GUIContent..ctor")
    eq("人造 IL: type 是被分析方法的声明类型", hits[0]["type"] if hits else None, "X.Y")
    eq("人造 IL: ilOffset 是 ldstr 的偏移", hits[0]["ilOffset"] if hits else None, 0)

    # --- 负例：Debug.Log 不算 sink（同一条 ldstr 流进去也不产 hit）---
    log_insns = ilmeta.decode_il(bytes.fromhex("7201000070" "280200000a" "2a"))
    log_census = {"UnityEngine.Debug.Log": _census_entry("LOG", "UnityEngine.Debug.Log*")}
    log_hits = []
    log_stats = {"aborted": 0, "dataflow": 0}
    ilstrings.Analyzer(_FakeResolver(), {1: "Loading..."}, "X.dll", "X", "X.Y", "M()",
                       log_census, log_hits, log_stats).run(log_insns)
    eq("负例: Debug.Log 的字面量不进候选", len(log_hits), 0)
    eq("负例: 也没有把 Debug.Log 当未解析 token", log_stats["aborted"], 0)

    # --- B 组（疑似盲区）：进普查计数，但不进候选 ---
    b_insns = ilmeta.decode_il(bytes.fromhex("7201000070" "280300000a" "2a"))
    b_census = {"TMPro.TMP_InputField.set_text": _census_entry(
        "B", "TMPro.TMP_InputField.set_text")}
    b_hits = []
    ilstrings.Analyzer(_FakeResolver(), {1: "Search..."}, "X.dll", "X", "X.Y", "M()",
                       b_census, b_hits, {"aborted": 0, "dataflow": 0}).run(b_insns)
    eq("B 组: 不进候选", len(b_hits), 0)
    eq("B 组: 但字面量进普查计数",
       len(b_census["TMPro.TMP_InputField.set_text"]["lits"]), 1)

    # --- 签名解析回归：CLASS/VALUETYPE 与 CMOD_* 后面都是压缩的 TypeDefOrRefEncoded ---
    res = ilstrings.Resolver.__new__(ilstrings.Resolver)
    res._type_names = {}
    res.typedef_name = lambda rid: "T%d" % rid
    res.class_name = lambda cls: "R%d" % cls[1]
    eq("签名: VALUETYPE + 压缩 coded 索引",
       ilstrings.Resolver._parse_type(res, bytes.fromhex("1105"), 0)[0], "R1")
    eq("签名: CMOD_REQD 后是压缩索引（不是 4 字节 token）",
       ilstrings.Resolver._parse_type(res, bytes.fromhex("1f8145" "10" "1128"), 0)[0], "T10&")

    # --- 候选行：去重 / 排序 / h 口径 ---
    raw = [
        {"asm": "B.dll", "type": "T", "method": "M()", "ilOffset": 0, "sink": "GUI.Label",
         "text": "Beta"},
        {"asm": "A.dll", "type": "T", "method": "M()", "ilOffset": 0, "sink": "GUI.Label",
         "text": "  Alpha  "},
        {"asm": "A.dll", "type": "T", "method": "M()", "ilOffset": 9, "sink": "GUI.Button",
         "text": "Alpha"},
    ]
    rows = ilstrings.build_candidates(raw)
    eq("候选: 同一 asm 同一文本只留一行", len(rows), 2)
    eq("候选: 按 asm 升序", [r["asm"] for r in rows], ["A.dll", "B.dll"])
    eq("候选: n 是归一化后的文本", rows[0]["n"], "Alpha")
    eq("候选: h == fnv.hash_text(n)（与 pool 同口径）", rows[0]["h"], fnv.hash_text("Alpha"))
    check("候选: h 是 16 位小写 hex",
          all(len(r["h"]) == 16 and r["h"] == r["h"].lower() for r in rows))
    eq("候选: 字段齐全且顺序固定", list(rows[0]),
       ["asm", "type", "method", "ilOffset", "sink", "text", "h", "n"])

    # --- 落盘：幂等 + 无 BOM + LF ---
    path = os.path.join(base, "il-candidates.jsonl")
    ilstrings.write_jsonl(path, rows)
    with open(path, "rb") as fh:
        first = fh.read()
    ilstrings.write_jsonl(path, rows)
    with open(path, "rb") as fh:
        second = fh.read()
    check("write_jsonl: 跑两遍逐字节相同", first == second)
    check("write_jsonl: 无 BOM、只有 LF、末尾有换行",
          not first.startswith(b"\xef\xbb\xbf") and b"\r" not in first
          and first.endswith(b"\n"))

    # --- 噪声分类 / 漏斗 ---
    eq("noise: #autoLOC_ 键引用", ilstrings.noise_class("#autoLOC_500460"), "键引用")
    eq("noise: URL", ilstrings.noise_class("https://example.com/a"), "URL")
    eq("noise: 句子 -> 其他（含待译句子）", ilstrings.noise_class("Load Vessel"),
       "其他（含待译句子）")
    funnel = ilstrings.funnel_stats(["", "ab", "A real sentence here", "123"])
    eq("漏斗: #US 去重计数", funnel["us_distinct"], 4)
    eq("漏斗: 过滤后只剩 2 条（空 / 纯数字被丢）", funnel["filtered"], 2)
    eq("漏斗: >=4 字符 1 条（ab 只有 2 字符）", funnel["ge4"], 1)
    eq("漏斗: 像 UI 文本 1 条", funnel["ui"], 1)


def test_ilstrings_real():
    print("[10] IL 候选产物自检（真实 packs/il-candidates.jsonl）")
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    path = os.path.join(repo, "packs", "il-candidates.jsonl")
    if not os.path.isfile(path):
        print("  [skip] packs/il-candidates.jsonl 还不存在（先跑 python -m i18n_tools.ilstrings）")
        return
    with open(path, "rb") as fh:
        raw = fh.read()
    rows = [json.loads(x) for x in raw.decode("utf-8").split("\n") if x]
    check("真实候选行数 > 2000", len(rows) > 2000, "got=%d" % len(rows))
    bad_h = [r for r in rows if r["h"] != fnv.hash_text(textnorm.normalize(r["text"]))]
    eq("每行 h == fnv.hash_text(normalize(text))（与 pool 同口径）", len(bad_h), 0)
    bad_n = [r for r in rows if r["n"] != textnorm.normalize(r["text"])]
    eq("每行 n == normalize(text)", len(bad_n), 0)
    keys = [(r["asm"], r["n"]) for r in rows]
    eq("按 (asm, n) 无重复", len(set(keys)), len(keys))
    eq("按 (asm, n) 升序稳定排序", keys, sorted(keys))
    sink_ok = {api for grp, api in ilstrings.API_TABLE.values() if grp == "A"}
    bad_sink = [r for r in rows if r["sink"] not in sink_ok]
    eq("sink 全在白名单内", len(bad_sink), 0)
    check("没有任何一行的 sink 是日志 API", not any("Debug" in r["sink"] for r in rows))
    check("无 BOM、只有 LF",
          not raw.startswith(b"\xef\xbb\xbf") and b"\r" not in raw)


def test_capture_ingest(base):
    print("[11] 运行时捕获摄入（capture_ingest.py）")
    for name, ok, detail in capture_ingest.selftest_cases(base):
        check(name, ok, detail)


def test_capture_real():
    print("[12] 捕获候选产物自检（真实 packs/capture-candidates.jsonl）")
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    path = os.path.join(repo, "packs", "capture-candidates.jsonl")
    if not os.path.isfile(path):
        print("  [skip] packs/capture-candidates.jsonl 还不存在"
              "（先跑 python -m i18n_tools.capture_ingest）")
        return
    with open(path, "rb") as fh:
        raw = fh.read()
    rows = [json.loads(x) for x in raw.decode("utf-8").split("\n") if x]
    check("真实捕获候选行数 > 500", len(rows) > 500, "got=%d" % len(rows))
    eq("只有 candidate / pattern 两档（template 已塌缩）",
       sorted({r["bucket"] for r in rows}), ["candidate", "pattern"])
    eq("每行 h == fnv.hash_text(normalize(n))（与 pool 同口径）",
       sum(1 for r in rows if r["h"] != fnv.hash_text(textnorm.normalize(r["n"]))), 0)
    eq("h 无重复", len({r["h"] for r in rows}), len(rows))
    order = [capture_ingest.BUCKET_ORDER[r["bucket"]] for r in rows]
    eq("排序 = (bucket 序, h)", order, sorted(order))
    eq("字段顺序固定", list(rows[0]), list(capture_ingest.OUT_FIELDS))
    check("mods 是非空列表", all(isinstance(r["mods"], list) and r["mods"] for r in rows))
    # pattern 行是骨架（数字已换成 #），不是运行时拿来匹配的原文，所以只查 candidate。
    eq("没有本该 never 的串混进产物（candidate 行）",
       sum(1 for r in rows
           if r["bucket"] != "pattern"
           and capture_ingest.classify_capture(r["n"])[0] == "never"), 0)
    eq("pattern 骨架里没有纯符号（不含 ASCII 字母）的残留",
       sum(1 for r in rows if r["bucket"] == "pattern"
           and not re.search(r"[A-Za-z]", r["n"])), 0)
    check("无 BOM、只有 LF、末尾有换行",
          not raw.startswith(b"\xef\xbb\xbf") and b"\r" not in raw and raw.endswith(b"\n"))


def test_extra_tools():
    """把后加的 python 工具的 selftest 并进门禁（check_overrides / harvest_cfgcache / merge_overrides / golden_gate / candidates / render_coverage / api / journal_gate）。

    为什么放在这里：CI 只跑 `python -m i18n_tools.selftest`。新工具各自的 --selftest 若没人调用，
    逻辑坏了也永远绿着——v1.4 的译文门禁（check_overrides）与 ConfigCache 通道（harvest_cfgcache）
    正是这样进仓库的，合并器（merge_overrides）也走同一条路；golden 门禁（golden_gate，docs/08 D-T5）
    更严重——它是「用户报过的串有没有被再丢掉」的唯一证据，自己坏掉就等于门禁形同不存在。
    """
    import contextlib
    import io

    runners = (("check_overrides", check_overrides.selftest),
               ("harvest_cfgcache", harvest_cfgcache.selftest),
               ("merge_overrides", merge_overrides.selftest),
               ("golden_gate", golden_gate.selftest),
               ("candidates", candidates.selftest),
               ("render_coverage", render_coverage.selftest),
               ("api", api.selftest),
               ("journal_gate", journal_gate.selftest))
    for name, runner in runners:
        buffer = io.StringIO()
        try:
            with contextlib.redirect_stdout(buffer):
                result = runner()
        except Exception as exc:                     # 自检里任何异常都算失败
            check(name + ".selftest", False, "raised {!r}".format(exc))
            continue
        if isinstance(result, tuple):
            failures, total = result
        else:
            failures, total = ([] if result == 0 else ["见 stdout"]), 1
        check(name + ".selftest", not failures,
              "{} 项检查 {} 项失败 {}".format(total, len(failures), list(failures)[:3]))


def main(argv=None) -> int:
    ensure_stdout()
    print("[selftest] i18n_tools 自检（python %s）" %
          ".".join(str(v) for v in sys.version_info[:3]))
    test_fnv()
    test_textnorm()
    test_cfgparse()
    base = tempfile.mkdtemp(prefix="ui18n_selftest_")
    try:
        test_pipeline(os.path.join(base, "pipeline"))
        test_glossary(os.path.join(base, "glossary"))
        test_packindex(os.path.join(base, "packindex"))
        test_ilstrings(os.path.join(base, "ilstrings"))
        test_capture_ingest(os.path.join(base, "capture_ingest"))
    finally:
        shutil.rmtree(base, ignore_errors=True)
    test_extra_tools()
    test_artifacts()
    test_packindex_real()
    test_ilstrings_real()
    test_capture_real()
    print("")
    if _FAILED:
        print("[selftest] 失败 %d / %d 项：" % (len(_FAILED), _CHECKED))
        for name in _FAILED:
            print("  - " + name)
        return 1
    print("[selftest] 全部通过（%d 项）" % _CHECKED)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())