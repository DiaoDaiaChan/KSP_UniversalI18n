"""把我们已经译好的条目导出成原 mod 能直接用的 Localization 目录（docs/11 §7 M5-c）。

用途：给原 mod 提 zh-cn PR。产物是「原 mod 自己的仓库该长什么样」，不是我们的语言包：

    build/feedback/<mod>/GameData/<Mod>/Localization/zh-cn/<mod>.cfg
    build/feedback/<mod>/PR-BODY.md

和 mmgen 的分工：mmgen 是「把中文塞进**玩家机器**的 GameData/UI18n/Packs/<lang>/Native/」，条目来源是池的 loc 行；
本模块是「把中文交还**作者**」，条目来源是 packs/zh-cn/*.jsonl，格式同样是 KSP 原生 Localization 节点
（所以可以复用 mmgen.render_cfg / read_back 做往返校验，两处格式不会漂）。

只导出 `s` 是 `#KEY` 形态的行：只有这类才能进 Localization/*.cfg；IL 字面量与 cfg 字段的译文没有 key，
作者那边也无处可放（那部分要么进我们的字典，要么由作者自己抽 key）。

用法::

    python -m i18n_tools.feedback --stats
    python -m i18n_tools.feedback --mod AJE --out ../build/feedback
    python -m i18n_tools.feedback --all
"""

import io
import json
import os
import sys

from . import mmgen

REPO = mmgen.REPO
PACKS_ROOT = mmgen.PACKS_ROOT
PACKS_DIR = os.path.join(PACKS_ROOT, "zh-cn")
DEFAULT_OUT = os.path.join(REPO, "build", "feedback")
DEFAULT_LANG = "zh-cn"
SCHEMA = "ui18n-feedback/1"
_PIPELINE = {}
_POOL_TEXT = {}
MIN_ENTRIES = 10


def warn(message):
    sys.stderr.write("[feedback] " + message + "\n")


def pack_path(mod, packs_dir=PACKS_DIR):
    return os.path.join(packs_dir, mod + ".jsonl")


def pipeline(lang=DEFAULT_LANG, packs_dir=PACKS_DIR, pool_path=mmgen.POOL_PATH):
    """池 + 中文记忆 -> (patches, unresolved, skip, ambiguous)，按 (packs_dir, pool_path) 缓存。"""
    key = (packs_dir, pool_path, lang)
    if key not in _PIPELINE:
        pool = mmgen.load_pool(pool_path)
        translations = mmgen.load_translations(packs_dir)
        _PIPELINE[key] = mmgen.build_rows(pool, translations)
    return _PIPELINE[key]


def pool_texts(pool_path=mmgen.POOL_PATH):
    """{key: 英文原文}，只用于 PR 正文里的对照表。"""
    if pool_path not in _POOL_TEXT:
        table = {}
        for row in mmgen.load_pool(pool_path):
            if not mmgen.is_location_row(row):
                continue
            key = row.get("key")
            if key and key not in table:
                table[key] = row.get("text") or ""
        _POOL_TEXT[pool_path] = table
    return _POOL_TEXT[pool_path]



def list_mods(packs_dir=PACKS_DIR):
    if not os.path.isdir(packs_dir):
        return []
    names = []
    for name in sorted(os.listdir(packs_dir)):
        if not name.endswith(".jsonl"):
            continue
        mod = name[: -len(".jsonl")]
        if mod:
            names.append(mod)
    return names


def collect(mod, packs_dir=PACKS_DIR, lang=DEFAULT_LANG, pool_path=mmgen.POOL_PATH):
    """返回 (entries, skip)。entries = [{key, zh, en}]，按 key 排序（与 mmgen 同序，保证幂等）。"""
    patches, _unresolved, info, _ambiguous = pipeline(lang, packs_dir, pool_path)
    rows = list(patches.get(mod) or ())
    texts = pool_texts(pool_path)
    entries = [{"key": key, "zh": zh, "en": texts.get(key, "")} for key, zh in rows]
    skip_info = info.get("skip") or {}
    skip = {"same_as_source": int(skip_info.get("same_as_source") or 0)}
    return entries, skip

def mod_folder(mod):
    """原 mod 的 GameData 子目录名。我们的包名就是 modId，这里直接用（作者可自行改路径）。"""
    return mmgen.mod_file_name(mod)


def build_mod(mod, packs_dir=PACKS_DIR, lang=DEFAULT_LANG, pool_path=mmgen.POOL_PATH):
    entries, skip = collect(mod, packs_dir, lang, pool_path)
    folder = mod_folder(mod)
    cfg = ""
    if entries:
        cfg = mmgen.render_cfg(mod, [(item["key"], item["zh"]) for item in entries], lang,
                               source_note="packs/pool.jsonl + packs/zh-cn/*.jsonl (feedback export)")
        back = mmgen.read_back(cfg, lang)
        want = dict((item["key"], item["zh"]) for item in entries)
        if back != want:
            raise ValueError("往返校验失败: " + mod)
    return {
        "schema": SCHEMA,
        "mod": mod,
        "lang": lang,
        "folder": folder,
        "relPath": "GameData/" + folder + "/Localization/" + lang + "/" + folder + ".cfg",
        "entries": entries,
        "skip": skip,
        "cfg": cfg,
    }

def build_all(packs_dir=PACKS_DIR, lang=DEFAULT_LANG, min_entries=MIN_ENTRIES, pool_path=mmgen.POOL_PATH):
    """返回 (bundles, skipped_mods)。条目太少的 mod 不导出（作者收到 3 行 PR 只会觉得扰民）。"""
    patches = pipeline(lang, packs_dir, pool_path)[0]
    bundles = []
    skipped = []
    for mod in sorted(patches):
        bundle = build_mod(mod, packs_dir, lang, pool_path)
        if len(bundle["entries"]) < min_entries:
            skipped.append((mod, len(bundle["entries"])))
            continue
        bundles.append(bundle)
    return bundles, skipped

def render_pr_body(bundle):
    entries = bundle["entries"]
    lines = []
    lines.append("## 关于")
    lines.append("")
    lines.append("这是 " + bundle["mod"] + " 的简体中文（zh-cn）本地化，由 [UI18n](https://github.com/DiaoDaiaChan/KSP_UniversalI18n)")
    lines.append("的社区翻译整理而来，共 **" + str(len(entries)) + "** 条。可以直接合进上游：文件是标准 KSP")
    lines.append("`Localization/<lang>/*.cfg` 节点，不需要任何额外依赖，也不需要 ModuleManager。")
    lines.append("")
    lines.append("## 改了什么")
    lines.append("")
    lines.append("- 新增 `" + bundle["relPath"] + "`（" + str(len(entries)) + " 条，UTF-8）")
    lines.append("- 只包含 key 与译文，未改动任何原文件、未改动游戏逻辑")
    lines.append("")
    lines.append("## 合并前建议确认")
    lines.append("")
    lines.append("1. 文件路径与你的项目结构一致（我们猜的是 `GameData/" + bundle["folder"] + "/Localization/zh-cn/`）；")
    lines.append("2. key 与上游一致（我们按上游 Localization 里的 key 原样保留，未改写）；")
    lines.append("3. 术语用词可以按你的偏好调整 —— 我们只保证与英文原文一一对应。")
    lines.append("")
    lines.append("## 条目示例")
    lines.append("")
    lines.append("| key | 英文 | 中文 |")
    lines.append("| --- | --- | --- |")
    for item in entries[:5]:
        en = item["en"].replace("|", "\\|").replace("\n", " ")
        zh = item["zh"].replace("|", "\\|").replace("\n", " ")
        lines.append("| `" + item["key"] + "` | " + en + " | " + zh + " |")
    lines.append("")
    lines.append("> 这些译文来自社区，若与你的设定冲突请直接改或关闭 PR；不需要说明理由。")
    lines.append("")
    return "\n".join(lines)


def write_bundle(bundle, out_root=DEFAULT_OUT):
    base = os.path.join(out_root, bundle["mod"])
    target = os.path.join(base, bundle["relPath"].replace("/", os.sep))
    parent = os.path.dirname(target)
    if not os.path.isdir(parent):
        os.makedirs(parent)
    with io.open(target, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(bundle["cfg"])
    body = os.path.join(base, "PR-BODY.md")
    with io.open(body, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(render_pr_body(bundle))
    return target, body


def summary(bundles, skipped):
    total = sum(len(bundle["entries"]) for bundle in bundles)
    return total, len(bundles), len(skipped)


def selftest():
    import json
    import shutil
    import tempfile

    from . import fnv as _fnv
    from . import textnorm as _textnorm

    checks = []
    failed = []

    def check(name, ok):
        checks.append(name)
        if not ok:
            failed.append(name)

    check("列 mod 只认 .jsonl", list_mods("/no/such/dir") == [])
    check("mod 目录名走 mmgen 同一函数", mod_folder("Squad") == mmgen.mod_file_name("Squad"))

    root = tempfile.mkdtemp()
    try:
        packs = os.path.join(root, "packs")
        os.makedirs(packs)
        pool = os.path.join(root, "pool.jsonl")
        pool_rows = [
            {"src": "loc", "mod": "Demo", "key": "#LOC_a", "text": "Alpha"},
            {"src": "loc", "mod": "Demo", "key": "#LOC_b", "text": "Beta"},
            {"src": "loc", "mod": "Demo", "key": "#LOC_c", "text": "Gamma"},
            {"src": "loc", "mod": "Demo", "key": "#LOC_d", "text": "Delta"},
            {"src": "cfg", "mod": "Demo", "key": "", "text": "not a key row"},
        ]
        with io.open(pool, "w", encoding="utf-8", newline="\n") as handle:
            for row in pool_rows:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")
        pack_rows = [
            {"h": "1", "n": "#LOC_a", "s": "#LOC_a", "t": "甲"},
            {"h": "2", "n": "Beta", "s": "Beta", "t": "乙"},
            {"h": "3", "n": "Gamma", "s": "Gamma", "t": "Gamma"},
            {"h": "4", "n": "#LOC_d", "s": "#LOC_d", "t": "丁\n换行"},
        ]
        for row in pack_rows:
            row["h"] = _fnv.hash_text(_textnorm.normalize(row["s"]))
        with io.open(os.path.join(packs, "Demo.jsonl"), "w", encoding="utf-8", newline="\n") as handle:
            for row in pack_rows:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")

        entries, _skip = collect("Demo", packs, "zh-cn", pool)
        keys = [item["key"] for item in entries]
        check("按 key 配对与按原文哈希配对都能命中", keys == ["#LOC_a", "#LOC_b"])
        check("同文同译不算可导出", "#LOC_c" not in keys)
        check("含换行的译文不算可导出", "#LOC_d" not in keys)
        check("条目带英文原文", entries[0]["en"] == "Alpha")

        bundle = build_mod("Demo", packs, "zh-cn", pool)
        check("路径是上游形态", bundle["relPath"] == "GameData/Demo/Localization/zh-cn/Demo.cfg")
        check("cfg 往返一致", mmgen.read_back(bundle["cfg"], "zh-cn") == {"#LOC_a": "甲", "#LOC_b": "乙"})
        check("PR 正文含条数", "**2** 条" in render_pr_body(bundle))
        check("PR 正文含路径", bundle["relPath"] in render_pr_body(bundle))
        check("PR 正文含示例表", "| `#LOC_a` |" in render_pr_body(bundle))

        target, body = write_bundle(bundle, os.path.join(root, "out"))
        check("写出 cfg", os.path.isfile(target))
        check("写出 PR-BODY", os.path.isfile(body))

        bundles, skipped = build_all(packs, "zh-cn", 1, pool)
        check("build_all 导出达标 mod", len(bundles) == 1 and skipped == [])
        bundles2, skipped2 = build_all(packs, "zh-cn", 10, pool)
        check("条目不足 min_entries 时跳过", bundles2 == [] and skipped2[0][0] == "Demo")
        check("summary 统计条数", summary(bundles, skipped)[:2] == (2, 1))
    finally:
        shutil.rmtree(root, ignore_errors=True)

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
        sys.stdout.write("[feedback] 自检 " + str(total) + " 项，失败 " + str(len(failed)) + "\n")
        return 0 if not failed else 1

    lang = take("--lang", DEFAULT_LANG)
    out = take("--out", DEFAULT_OUT)
    packs_dir = take("--packs-dir", PACKS_DIR)
    min_entries = int(take("--min-entries", str(MIN_ENTRIES)))
    mod = take("--mod", None)
    want_stats = "--stats" in argv
    quiet = "--quiet" in argv
    for flag in ("--stats", "--all", "--quiet"):
        while flag in argv:
            argv.remove(flag)
    if argv:
        warn("未知参数: " + " ".join(argv))
        return 2

    if mod:
        bundle = build_mod(mod, packs_dir, lang)
        if not bundle["entries"]:
            warn("这个 mod 没有可导出的 key 行: " + mod)
            return 1
        bundles, skipped = [bundle], []
    else:
        bundles, skipped = build_all(packs_dir, lang, min_entries)

    total, mods, skipped_count = summary(bundles, skipped)
    if want_stats:
        sys.stdout.write("[feedback] 可导出 " + str(mods) + " 个 mod / " + str(total) + " 条；" +
                         "条目不足 " + str(min_entries) + " 条的 mod: " + str(skipped_count) + " 个\n")
        return 0

    for bundle in bundles:
        target, _body = write_bundle(bundle, out)
        if not quiet:
            sys.stdout.write("[feedback] " + bundle["mod"] + ": " + str(len(bundle["entries"])) +
                             " 条 -> " + target + "\n")
    sys.stdout.write("[feedback] 共 " + str(mods) + " 个 mod / " + str(total) + " 条（输出根目录 " + out + "）\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
