"""per-mod 仲裁 policy 的推导与落地（docs/00 §8.3，docs/11 §7 M5-a）。

policy 只有四种取值（判定顺序即优先级）：

    NATIVE          mod 自己有 Localization/<当前语言>   => 完全不动，跳过该 asm
    NATIVE_PARTIAL  有 Localization/ 但没有当前语言        => 只补字典里没有的串
    PARTIAL         引用 Localizer 但没有 Localization/    => L2 全量翻
    LEGACY          都没有                                => L2 全量翻

本模块只负责「算出来 + 写进数据文件」，不碰 Runtime：

- 数据落地在 packs/policies.json（与 packs/mods.json 分离，因为它是**派生物**，可随时重算；
  mods.json 是人审过的身份表，不该混进会变的分类结果）；
- Runtime 侧读取是后续步骤（M5-a 的后半段），代码里仍然不点名任何 mod。

证据有两路，优先级从高到低：

1. `--gamedata <KSP 根>/GameData`：直接看磁盘上每个 mod 目录里有没有 Localization/<lang>；
2. 没有 GameData 时退而求其次，用 packs/pool.jsonl 里 src=loc 的行（那些 key 就是从 mod 自己的
   Localization/*.cfg 里 harvest 出来的）推断「这个 mod 确实自带本地化节点」，但推不出它有没有 zh-cn，
   因此这一路只能给出 PARTIAL / LEGACY，并在 evidence 里标 source=pool（未实测语言目录）。

用法::

    python -m i18n_tools.policy --stats
    python -m i18n_tools.policy                       # 写 packs/policies.json
    python -m i18n_tools.policy --gamedata "<KSP 根>/GameData"   # 用真实目录判定 NATIVE
    python -m i18n_tools.policy --check               # 磁盘上的产物是否最新
"""

import io
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PACKS_ROOT = os.path.join(REPO, "packs")
MODS_PATH = os.path.join(PACKS_ROOT, "mods.json")
POOL_PATH = os.path.join(PACKS_ROOT, "pool.jsonl")
DEFAULT_LANG = "zh-cn"
DEFAULT_OUT = os.path.join(PACKS_ROOT, "policies.json")
SCHEMA = "ui18n-policies/1"
LOCALIZATION_DIR = "Localization"
POLICIES = ("NATIVE", "NATIVE_PARTIAL", "PARTIAL", "LEGACY")


def warn(message):
    sys.stderr.write("[policy] " + message + "\n")


def classify(languages, has_localizer=False, loc_keys=0):
    """按 docs/00 §8.3 的表判 policy。

    languages: 这个 mod 的 Localization/ 下存在的语言目录名（小写，例如 ["en-us"]）。
    has_localizer: 是否引用 KSP.Localization.Localizer（静态扫描的结论）。
    loc_keys: 池里属于这个 mod 的本地点化 key 数（>0 说明它确实带 Localization 节点）。
    """
    names = set()
    for item in languages or ():
        if item:
            names.add(str(item).strip().lower().replace("_", "-"))
    if DEFAULT_LANG in names:
        return "NATIVE"
    if names:
        return "NATIVE_PARTIAL"
    if has_localizer or loc_keys > 0:
        return "PARTIAL"
    return "LEGACY"


def normalize_lang(lang):
    return str(lang or DEFAULT_LANG).strip().lower().replace("_", "-")


def iter_jsonl(path):
    with io.open(path, "r", encoding="utf-8-sig") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if isinstance(row, dict):
                yield row


def load_mods(path=MODS_PATH):
    """返回 mods.json 里的 modId 列表（保序）。"""
    try:
        with io.open(path, "r", encoding="utf-8-sig") as handle:
            data = json.load(handle)
    except (IOError, OSError, ValueError) as error:
        raise ValueError("读不了 mods.json: " + str(error))
    mods = data.get("mods") if isinstance(data, dict) else None
    ids = []
    for item in mods or ():
        if isinstance(item, dict):
            mod_id = str(item.get("modId") or "").strip()
            if mod_id:
                ids.append(mod_id)
    return ids


def pool_evidence(pool_path=POOL_PATH):
    """池里每个 mod 的本地点化 key 数：{modId: {"locKeys": n, "keys": set()}}。"""
    table = {}
    for row in iter_jsonl(pool_path):
        if str(row.get("src") or "") != "loc":
            continue
        mod = str(row.get("mod") or "").strip()
        if not mod:
            continue
        item = table.get(mod)
        if item is None:
            item = {"locKeys": 0, "keys": set()}
            table[mod] = item
        key = str(row.get("key") or "").strip()
        if key and key not in item["keys"]:
            item["keys"].add(key)
            item["locKeys"] = len(item["keys"])
    return table


def folder_mod_id(folder_name, mod_ids):
    """GameData 子目录名 → modId。只做两个无害的归一：去 `000_` 前缀、大小写不敏感比对。"""
    name = str(folder_name or "").strip()
    if not name:
        return ""
    lookup = {}
    for mod_id in mod_ids:
        lookup.setdefault(mod_id.lower(), mod_id)
    if name.lower() in lookup:
        return lookup[name.lower()]
    stripped = name.split("_", 1)[1] if "_" in name and name.split("_", 1)[0].isdigit() else name
    if stripped.lower() in lookup:
        return lookup[stripped.lower()]
    return ""


def scan_gamedata(gamedata, mod_ids, max_depth=4):
    """扫 GameData 下每个 Localization 目录，返回 {modId: {"folders": [...], "languages": [...]}}。"""
    table = {}
    if not gamedata or not os.path.isdir(gamedata):
        return table
    base_depth = gamedata.rstrip("\\/").count(os.sep)
    for root, dirs, _files in os.walk(gamedata):
        if root.count(os.sep) - base_depth >= max_depth:
            dirs[:] = []
            continue
        keep = []
        for name in dirs:
            if name.lower() == LOCALIZATION_DIR.lower():
                mod_id = folder_mod_id(os.path.basename(root), mod_ids)
                if mod_id:
                    item = table.setdefault(mod_id, {"folders": [], "languages": []})
                    if root not in item["folders"]:
                        item["folders"].append(root)
                    path = os.path.join(root, name)
                    try:
                        langs = sorted(os.listdir(path))
                    except OSError:
                        langs = []
                    for lang in langs:
                        if os.path.isdir(os.path.join(path, lang)) and lang not in item["languages"]:
                            item["languages"].append(lang)
            else:
                keep.append(name)
        dirs[:] = keep
    for item in table.values():
        item["folders"].sort()
        item["languages"].sort()
    return table


def build(mod_ids, evidence=None, scan=None, lang=DEFAULT_LANG):
    """合成 policies.json 的内容。evidence 来自池，scan 来自 --gamedata（可缺省）。"""
    evidence = evidence or {}
    scan = scan or {}
    target = normalize_lang(lang)
    mods = {}
    for mod_id in mod_ids:
        pool = evidence.get(mod_id) or {}
        disk = scan.get(mod_id) or {}
        languages = list(disk.get("languages") or ())
        loc_keys = int(pool.get("locKeys") or 0)
        policy = classify(languages, has_localizer=False, loc_keys=loc_keys)
        entry = {
            "policy": policy,
            "locKeys": loc_keys,
            "languages": languages,
            "source": "gamedata" if disk else ("pool" if loc_keys else "none"),
        }
        if disk.get("folders"):
            entry["folders"] = [os.path.basename(item) for item in disk["folders"]]
        mods[mod_id] = entry
    counts = {}
    for name in POLICIES:
        counts[name] = 0
    for entry in mods.values():
        counts[entry["policy"]] = counts.get(entry["policy"], 0) + 1
    return {
        "schema": SCHEMA,
        "lang": target,
        "derivedFrom": "gameData" if scan else "pool",
        "counts": counts,
        "mods": mods,
    }


def dump_text(data):
    return json.dumps(data, ensure_ascii=False, indent=2, sort_keys=False) + "\n"


def write_file(path, data):
    parent = os.path.dirname(os.path.abspath(path))
    if parent and not os.path.isdir(parent):
        os.makedirs(parent)
    with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(dump_text(data))


def compare_with_disk(path, data):
    """返回 (missing, stale)；missing/stale 都是 bool。"""
    if not os.path.isfile(path):
        return True, False
    with io.open(path, "r", encoding="utf-8-sig") as handle:
        have = handle.read()
    want = dump_text(data)
    return False, have != want


def coverage(data):
    counts = data.get("counts") or {}
    total = sum(int(counts.get(name) or 0) for name in POLICIES)
    return total, counts


def render_report(data):
    total, counts = coverage(data)
    lines = ["# per-mod 仲裁 policy 报告", "",
             "- 语言: " + str(data.get("lang")),
             "- 证据来源: " + str(data.get("derivedFrom")),
             "- mod 总数: " + str(total), ""]
    lines.append("| policy | mod 数 | 行为 |")
    lines.append("| --- | --- | --- |")
    behavior = {
        "NATIVE": "完全不动（mod 自带当前语言）",
        "NATIVE_PARTIAL": "只补字典里没有的串",
        "PARTIAL": "L2 全量翻",
        "LEGACY": "L2 全量翻",
    }
    for name in POLICIES:
        lines.append("| " + name + " | " + str(counts.get(name) or 0) + " | " + behavior[name] + " |")
    lines.append("")
    lines.append("## 有本地化证据的 mod（PARTIAL 及以上）")
    lines.append("")
    lines.append("| mod | policy | 池内 key 数 | 语言目录 | 证据 |")
    lines.append("| --- | --- | --- | --- | --- |")
    rows = []
    for mod_id in sorted(data.get("mods") or {}):
        entry = data["mods"][mod_id]
        if entry.get("policy") == "LEGACY":
            continue
        rows.append("| " + mod_id + " | " + entry["policy"] + " | " + str(entry.get("locKeys") or 0) +
                    " | " + (", ".join(entry.get("languages") or ()) or "-") + " | " + str(entry.get("source")) + " |")
    lines.extend(rows or ["| （无） | - | - | - | - |"])
    lines.append("")
    return "\n".join(lines)


def selftest():
    import shutil
    import tempfile

    checks = []
    failed = []

    def check(name, ok):
        checks.append(name)
        if not ok:
            failed.append(name)

    check("自带 zh-cn → NATIVE", classify(["zh-cn"], loc_keys=0) == "NATIVE")
    check("只有 en-us → NATIVE_PARTIAL", classify(["en-us"], loc_keys=0) == "NATIVE_PARTIAL")
    check("语言目录为空但有 loc key → PARTIAL", classify([], loc_keys=3) == "PARTIAL")
    check("引用 Localizer → PARTIAL", classify([], has_localizer=True) == "PARTIAL")
    check("什么都没有 → LEGACY", classify([], loc_keys=0) == "LEGACY")
    check("语言名归一（zh_CN → NATIVE）", classify(["zh_CN"]) == "NATIVE")

    row_ids = ["AJE", "000_TexturesUnlimited", "Squad"]
    check("目录名精确匹配 modId", folder_mod_id("AJE", row_ids) == "AJE")
    check("去 000_ 前缀匹配", folder_mod_id("000_TexturesUnlimited", row_ids) == "000_TexturesUnlimited")
    check("大小写不敏感", folder_mod_id("aje", row_ids) == "AJE")
    check("匹配不到返回空", folder_mod_id("NoSuchMod", row_ids) == "")

    evidence = {"AJE": {"locKeys": 5}, "Squad": {"locKeys": 0}}
    data = build(["AJE", "Squad", "RealFuels"], evidence=evidence)
    check("build: 有 loc key 的 mod → PARTIAL", data["mods"]["AJE"]["policy"] == "PARTIAL")
    check("build: 无证据的 mod → LEGACY", data["mods"]["RealFuels"]["policy"] == "LEGACY")
    check("build: counts 合计 == mod 数", sum(data["counts"].values()) == 3)
    check("build: derivedFrom=pool", data["derivedFrom"] == "pool")

    scanned = {"AJE": {"languages": ["en-us"], "folders": ["/x/GameData/AJE"]}}
    data2 = build(["AJE"], evidence=evidence, scan=scanned)
    check("build: 有 scan → NATIVE_PARTIAL", data2["mods"]["AJE"]["policy"] == "NATIVE_PARTIAL")
    check("build: derivedFrom=gameData", data2["derivedFrom"] == "gameData")
    check("build: folders 只留目录名", data2["mods"]["AJE"]["folders"] == ["AJE"])

    text = dump_text(data)
    check("dump_text 可被 json 解析", isinstance(json.loads(text), dict))
    check("dump_text 保留中文不转义", "\\u" not in text)
    check("渲染报告含表头", "| policy | mod 数 |" in render_report(data))
    check("渲染报告列出 PARTIAL 行", "AJE" in render_report(data))
    check("scan_gamedata 对不存在目录返回空", scan_gamedata("/no/such/dir", row_ids) == {})
    root = tempfile.mkdtemp()
    try:
        os.makedirs(os.path.join(root, "AJE", "Localization", "en-us"))
        os.makedirs(os.path.join(root, "000_TexturesUnlimited", "Localization", "zh-cn"))
        scanned = scan_gamedata(root, row_ids)
        check("scan_gamedata 找到 en-us", scanned.get("AJE", {}).get("languages") == ["en-us"])
        check("scan_gamedata 认出 000_ 前缀目录", scanned.get("000_TexturesUnlimited", {}).get("languages") == ["zh-cn"])
        built = build(row_ids, scan=scanned)
        check("扫描结果驱动 NATIVE", built["mods"]["000_TexturesUnlimited"]["policy"] == "NATIVE")
        check("扫描结果驱动 NATIVE_PARTIAL", built["mods"]["AJE"]["policy"] == "NATIVE_PARTIAL")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    check("scan() 判定顺序：语言目录优先于池证据",
          classify(["zh-cn"], loc_keys=99) == "NATIVE")

    return failed, len(checks)


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)

    def take(flag, default=None):
        if flag in argv:
            index = argv.index(flag)
            if index + 1 < len(argv):
                value = argv[index + 1]
                del argv[index:index + 2]
                return value
            del argv[index]
        return default

    if "--selftest" in argv:
        failed, total = selftest()
        for name in failed:
            warn("失败: " + name)
        sys.stdout.write("[policy] 自检 " + str(total) + " 项，失败 " + str(len(failed)) + "\n")
        return 0 if not failed else 1

    lang = take("--lang", DEFAULT_LANG)
    out = take("--out", DEFAULT_OUT)
    mods_path = take("--mods", MODS_PATH)
    pool_path = take("--pool", POOL_PATH)
    gamedata = take("--gamedata", None)
    report_path = take("--report", None)
    want_stats = "--stats" in argv
    want_check = "--check" in argv
    quiet = "--quiet" in argv
    for flag in ("--stats", "--check", "--quiet"):
        while flag in argv:
            argv.remove(flag)
    if argv:
        warn("未知参数: " + " ".join(argv))
        return 2

    mod_ids = load_mods(mods_path)
    evidence = pool_evidence(pool_path)
    scan = scan_gamedata(gamedata, mod_ids) if gamedata else {}
    data = build(mod_ids, evidence=evidence, scan=scan, lang=lang)
    total, counts = coverage(data)

    if want_check:
        missing, stale = compare_with_disk(out, data)
        if missing:
            sys.stdout.write("[policy] 产物缺失: " + out + "\n")
            return 1
        if stale:
            sys.stdout.write("[policy] 产物过期，请重跑: " + out + "\n")
            return 1
        sys.stdout.write("[policy] mod " + str(total) + " 个；产物最新: " + out + "\n")
        return 0

    if want_stats:
        sys.stdout.write("[policy] 证据来源: " + data["derivedFrom"] + "\n")
        sys.stdout.write("[policy] mod " + str(total) + " 个：" +
                         " / ".join(name + "=" + str(counts.get(name) or 0) for name in POLICIES) + "\n")
        return 0

    write_file(out, data)
    if not quiet:
        sys.stdout.write("[policy] 写入 " + out + "（" + str(total) + " 个 mod，" +
                         " / ".join(name + "=" + str(counts.get(name) or 0) for name in POLICIES) + "）\n")
    if report_path:
        path = report_path if os.path.isabs(report_path) else os.path.join(REPO, report_path)
        parent = os.path.dirname(path)
        if parent and not os.path.isdir(parent):
            os.makedirs(parent)
        with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(render_report(data))
        if not quiet:
            sys.stdout.write("[policy] 报告: " + path + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
