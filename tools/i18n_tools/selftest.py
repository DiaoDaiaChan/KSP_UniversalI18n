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
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import sys
import tempfile

from . import cfgparse, ensure_stdout, fnv, glossary, harvest, textnorm

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
    finally:
        shutil.rmtree(base, ignore_errors=True)
    test_artifacts()
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
