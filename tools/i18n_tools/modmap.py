#!/usr/bin/env python3
"""把 packs/mods.json 的 folders / assemblies 从「真实 GameData」推导出来（M1 数据层缺口）。

为什么需要它：packs/mods.json 是生产端的身份表（modId / displayName / ckan / assemblies /
folders / aliases）。它不随发布包走（docs/06），但 audit / mod resolve / submission 都要靠它
回答「这句话是哪个 mod 的」。现状是 82 条记录的 assemblies / folders / aliases 全是空数组——
不是忘了填，而是这些字段只能从装机后的磁盘读出来（哪个文件夹属于哪个 mod、里面有哪些 DLL），
仓库里没有这份事实。本工具就是把「磁盘事实」变成「身份表」的那一步。

三条纪律：
  1. 不猜：只有磁盘上真的存在的文件夹 / DLL 才进 plan。没匹配上任何 modId 的文件夹单独列进
     unmatched，由人来决定它是不是新 mod —— 绝不按名字相似度硬塞给某个已有 modId。
  2. 不越权：写盘一律走 python -m i18n_tools.api mod put（packs 的唯一写入口，带 journal 与
     门禁）。本工具自己一个字节都不写 packs/mods.json。
  3. 可空跑：没有 GameData 的机器上 --stats / --check 一律 SKIP（exit 0），不是失败 ——
     仓库本来就不该带装机路径。

映射规则与 policy.py 共用同一个函数（folder_mod_id）：精确 → 去 000_ 前缀 → 大小写不敏感。
绝不引入第二套别名表：两处各写一份，迟早会漂。

跑法（维护者机器上）：
    cd tools
    python -m i18n_tools.modmap --gamedata "D:/Kerbal Space Program/GameData" --stats
    python -m i18n_tools.modmap --gamedata "…/GameData" --check
    python -m i18n_tools.modmap --gamedata "…/GameData" --apply
    python -m i18n_tools.modmap --selftest
"""

import argparse
import contextlib
import io
import json
import os
import sys

from . import api
from .policy import folder_mod_id

SCHEMA_SUPPORTED = 1
DEFAULT_MAX_DEPTH = 3
ACTOR = "pipeline:modmap"
DLL_EXT = ".dll"


# ---------------------------------------------------------------------------
# 读身份表
# ---------------------------------------------------------------------------

def repo_root():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(os.path.dirname(here))


def default_mods_path(root=None):
    return os.path.join(root or repo_root(), "packs", "mods.json")


def load_mods(path):
    """读 packs/mods.json，返回 (doc, mods)。结构不对抛 ValueError（不静默降级：这是生产端数据）。"""
    with io.open(path, "r", encoding="utf-8-sig") as handle:
        doc = json.load(handle)
    if not isinstance(doc, dict):
        raise ValueError("%s 顶层必须是对象" % path)
    schema = doc.get("schema")
    if schema != SCHEMA_SUPPORTED:
        raise ValueError("%s 的 schema=%r，本工具只认 %d" % (path, schema, SCHEMA_SUPPORTED))
    mods = doc.get("mods")
    if not isinstance(mods, list):
        raise ValueError("%s 的 mods 必须是数组" % path)
    return doc, mods


def mod_ids(mods):
    ids = []
    for item in mods:
        if isinstance(item, dict):
            mod_id = str(item.get("modId") or "").strip()
            if mod_id:
                ids.append(mod_id)
    return ids


def entry_of(mods, mod_id):
    for item in mods:
        if isinstance(item, dict) and str(item.get("modId") or "") == mod_id:
            return item
    return None


# ---------------------------------------------------------------------------
# 扫 GameData（只读证据）
# ---------------------------------------------------------------------------

def scan_folders(gamedata, max_depth=DEFAULT_MAX_DEPTH):
    """返回 [{folder, dlls}]：GameData 一级子目录为文件夹身份，里面的 *.dll 为程序集身份。

    只认一级子目录：GameData 下的 <Mod>/Parts/x.dll 属于 Mod，而 <Mod>/SubMod/ 不是另一个 mod
    （KSP 的 mod 边界是 GameData 第一层，这也是 CKAN 与 PackLocator 的口径）。
    max_depth 限制向下找 DLL 的层数，避免在超大树里走得没完。
    """
    found = []
    if not gamedata or not os.path.isdir(gamedata):
        return found
    for name in sorted(os.listdir(gamedata)):
        folder = os.path.join(gamedata, name)
        if not os.path.isdir(folder):
            continue
        dlls = []
        base_depth = folder.rstrip("\\/").count(os.sep)
        for root, dirs, files in os.walk(folder):
            if root.count(os.sep) - base_depth >= max_depth:
                dirs[:] = []
                continue
            dirs.sort()
            for file_name in sorted(files):
                if file_name.lower().endswith(DLL_EXT):
                    stem = file_name[:-len(DLL_EXT)]
                    if stem and stem not in dlls:
                        dlls.append(stem)
        found.append({"folder": name, "dlls": dlls})
    return found


def propose(gamedata, mods, max_depth=DEFAULT_MAX_DEPTH):
    """把磁盘证据摊成 {modId: {folders, assemblies}}，同时给出 unmatched（磁盘上有、身份表里没有）。"""
    ids = mod_ids(mods)
    matches = {}
    unmatched = []
    for item in scan_folders(gamedata, max_depth):
        mod_id = folder_mod_id(item["folder"], ids)
        if not mod_id:
            unmatched.append(item["folder"])
            continue
        slot = matches.setdefault(mod_id, {"folders": [], "assemblies": []})
        if item["folder"] not in slot["folders"]:
            slot["folders"].append(item["folder"])
        for dll in item["dlls"]:
            if dll not in slot["assemblies"]:
                slot["assemblies"].append(dll)
    for slot in matches.values():
        slot["folders"].sort()
        slot["assemblies"].sort()
    unmatched.sort()
    return {"matches": matches, "unmatched": unmatched}


def plan(proposal, mods):
    """只保留「证据里有、身份表里还没有」的项。空 plan = 身份表已经是最新的。"""
    changes = {}
    for mod_id in sorted(proposal["matches"]):
        slot = proposal["matches"][mod_id]
        entry = entry_of(mods, mod_id) or {}
        have_folders = [str(item) for item in (entry.get("folders") or [])]
        have_dlls = [str(item) for item in (entry.get("assemblies") or [])]
        missing_folders = [item for item in slot["folders"] if item not in have_folders]
        missing_dlls = [item for item in slot["assemblies"] if item not in have_dlls]
        if missing_folders or missing_dlls:
            changes[mod_id] = {"folders": missing_folders, "assemblies": missing_dlls}
    return changes


# ---------------------------------------------------------------------------
# 写：一律转成 api 的命令
# ---------------------------------------------------------------------------

def commands(changes, root, actor=ACTOR, dry_run=False):
    """把 plan 摊成 api 的 argv 列表（不执行）。顺序固定，便于 --dry-run 里肉眼核对。"""
    out = []
    for mod_id in sorted(changes):
        slot = changes[mod_id]
        argv = ["--root", str(root), "--actor", actor, "mod", "put", "--mod", mod_id]
        for folder in slot["folders"]:
            argv.extend(["--folder", folder])
        for dll in slot["assemblies"]:
            argv.extend(["--assembly", dll])
        if dry_run:
            argv.append("--dry-run")
        out.append(argv)
    return out


def apply(changes, root, actor=ACTOR, dry_run=False):
    """逐条走 api.main。返回 (ok, failures)；失败项带 api 的退出码，绝不吞掉。"""
    ok = 0
    failures = []
    for argv in commands(changes, root, actor, dry_run):
        mod_id = argv[argv.index("--mod") + 1]
        buffer = io.StringIO()
        try:
            with contextlib.redirect_stdout(buffer), contextlib.redirect_stderr(buffer):
                code = api.main(argv)
        except Exception as error:                    # 逐条隔离：一条坏不连累其他
            failures.append((mod_id, -1, "%s: %s" % (type(error).__name__, error)))
            continue
        if code == 0:
            ok += 1
        else:
            tail = buffer.getvalue().strip().splitlines()
            failures.append((mod_id, code, tail[-1] if tail else ""))
    return ok, failures


# ---------------------------------------------------------------------------
# 报告
# ---------------------------------------------------------------------------

def summary(proposal, changes):
    return {
        "matched": len(proposal["matches"]),
        "pendingMods": len(changes),
        "pendingFolders": sum(len(item["folders"]) for item in changes.values()),
        "pendingAssemblies": sum(len(item["assemblies"]) for item in changes.values()),
        "unmatched": len(proposal["unmatched"]),
    }


def render_report(proposal, changes):
    stats = summary(proposal, changes)
    lines = []
    lines.append("# mods.json 映射（由 GameData 推导）")
    lines.append("")
    lines.append("| 项 | 数 |")
    lines.append("|---|---|")
    lines.append("| GameData 里匹配上的 mod | %d |" % stats["matched"])
    lines.append("| 身份表待补的 mod | %d |" % stats["pendingMods"])
    lines.append("| 待补文件夹 | %d |" % stats["pendingFolders"])
    lines.append("| 待补程序集 | %d |" % stats["pendingAssemblies"])
    lines.append("| 身份表里没有的 GameData 文件夹 | %d |" % stats["unmatched"])
    lines.append("")
    if changes:
        lines.append("## 待补（只列证据里有的）")
        lines.append("")
        lines.append("| modId | folders | assemblies |")
        lines.append("|---|---|---|")
        for mod_id in sorted(changes):
            slot = changes[mod_id]
            lines.append("| %s | %s | %s |" % (
                mod_id,
                ", ".join(slot["folders"]) or "—",
                ", ".join(slot["assemblies"]) or "—",
            ))
        lines.append("")
    if proposal["unmatched"]:
        lines.append("## 身份表里没有的文件夹（不自动登记，先由人判断）")
        lines.append("")
        for folder in proposal["unmatched"]:
            lines.append("- %s" % folder)
        lines.append("")
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------------------
# 自检
# ---------------------------------------------------------------------------

def selftest():
    import shutil
    import tempfile

    checks = []
    failures = []

    def check(name, ok, detail=""):
        checks.append(name)
        if not ok:
            failures.append((name, detail))

    work = tempfile.mkdtemp(prefix="ui18n-modmap-")
    try:
        root = os.path.join(work, "repo")
        os.makedirs(os.path.join(root, "packs"))
        gamedata = os.path.join(work, "GameData")

        def touch(path, text=""):
            parent = os.path.dirname(path)
            if parent and not os.path.isdir(parent):
                os.makedirs(parent)
            with io.open(path, "w", encoding="utf-8") as handle:
                handle.write(text)

        touch(os.path.join(gamedata, "000_AJE", "AJE.dll"))
        touch(os.path.join(gamedata, "000_AJE", "Parts", "AJE_Parts.dll"))
        touch(os.path.join(gamedata, "TexturesUnlimited", "TUnlimited.dll"))
        touch(os.path.join(gamedata, "TexturesUnlimited", "README.txt"))
        touch(os.path.join(gamedata, "Squad", "Squad.dll"))
        touch(os.path.join(gamedata, "Unlisted", "Foo.dll"))
        touch(os.path.join(gamedata, "Loose", "notanmod.txt"))

        mods_doc = {
            "schema": 1,
            "mods": [
                {"modId": "AJE", "displayName": "AJE", "ckan": None, "assemblies": [], "folders": [], "aliases": []},
                {"modId": "Squad", "displayName": "Squad", "ckan": None, "assemblies": [], "folders": [], "aliases": []},
                {"modId": "TexturesUnlimited", "displayName": "TexturesUnlimited", "ckan": None,
                 "assemblies": [], "folders": ["TexturesUnlimited"], "aliases": []},
            ],
        }
        mods_path = os.path.join(root, "packs", "mods.json")
        with io.open(mods_path, "w", encoding="utf-8") as handle:
            json.dump(mods_doc, handle, ensure_ascii=False, indent=2)

        doc, mods = load_mods(mods_path)
        check("load_mods 读出 3 条", len(mods) == 3, str(len(mods)))
        check("mod_ids 只取非空 modId", mod_ids(mods) == ["AJE", "Squad", "TexturesUnlimited"], str(mod_ids(mods)))

        folders = scan_folders(gamedata)
        names = [item["folder"] for item in folders]
        check("scan_folders 只认一级子目录", names == ["000_AJE", "Loose", "Squad", "TexturesUnlimited", "Unlisted"], str(names))
        aje = [item for item in folders if item["folder"] == "000_AJE"][0]
        check("scan_folders 向下收 DLL（子目录里的也算）", aje["dlls"] == ["AJE", "AJE_Parts"], str(aje["dlls"]))
        check("scan_folders 不收非 DLL", [item for item in folders if item["folder"] == "TexturesUnlimited"][0]["dlls"] == ["TUnlimited"])

        deep = scan_folders(gamedata, max_depth=1)
        deep_aje = [item for item in deep if item["folder"] == "000_AJE"][0]
        check("max_depth=1 时不进子目录", deep_aje["dlls"] == ["AJE"], str(deep_aje["dlls"]))
        check("空路径不抛", scan_folders("") == [] and scan_folders(os.path.join(work, "nope")) == [])

        proposal = propose(gamedata, mods)
        check("000_ 前缀映射回 AJE", "AJE" in proposal["matches"])
        check("精确名映射 TexturesUnlimited", proposal["matches"]["TexturesUnlimited"]["folders"] == ["TexturesUnlimited"])
        check("Squad 映射上", "Squad" in proposal["matches"])
        check("未匹配的文件夹进 unmatched", proposal["unmatched"] == ["Loose", "Unlisted"], str(proposal["unmatched"]))

        changes = plan(proposal, mods)
        check("plan 跳过已记录的 folder",
              changes["TexturesUnlimited"]["folders"] == [], str(changes["TexturesUnlimited"]))
        check("plan 只缺 assemblies",
              changes["TexturesUnlimited"]["assemblies"] == ["TUnlimited"], str(changes["TexturesUnlimited"]))
        check("plan 缺 folder + assemblies",
              changes["AJE"] == {"folders": ["000_AJE"], "assemblies": ["AJE", "AJE_Parts"]}, str(changes["AJE"]))
        check("plan 覆盖 Squad", "Squad" in changes)
        check("plan 按 modId 升序", list(changes) == sorted(changes), str(list(changes)))

        argv = commands(changes, root)
        check("commands 都是 api 调用",
              all(item[:5] == ["--root", str(root), "--actor", ACTOR, "mod"] for item in argv), str(argv[:1]))
        check("commands 带 --folder / --assembly",
              any("--folder" in item and "--assembly" in item for item in argv))
        check("dry_run 命令带 --dry-run",
              all("--dry-run" in item for item in commands(changes, root, dry_run=True)))

        with io.open(mods_path, "rb") as handle:
            before = handle.read()
        ok_count, failed = apply(changes, root, dry_run=True)
        with io.open(mods_path, "rb") as handle:
            after = handle.read()
        check("dry-run 走通全部命令", ok_count == len(changes) and not failed, "%s %s" % (ok_count, failed))
        check("dry-run 不动盘上的 mods.json", before == after)

        ok_count, failed = apply(changes, root)
        check("apply 全部成功", ok_count == len(changes) and not failed, "%s %s" % (ok_count, failed))
        doc2, mods2 = load_mods(mods_path)
        check("apply 后 schema 不变", doc2.get("schema") == 1)
        check("apply 后 AJE 有 folder", entry_of(mods2, "AJE")["folders"] == ["000_AJE"], str(entry_of(mods2, "AJE")))
        check("apply 后 AJE 有 assemblies", entry_of(mods2, "AJE")["assemblies"] == ["AJE", "AJE_Parts"])
        check("apply 后 mods 仍按 modId 升序",
              [item["modId"] for item in mods2] == sorted(item["modId"] for item in mods2))
        check("apply 幂等：再算一遍 plan 为空",
              plan(propose(gamedata, mods2), mods2) == {}, str(plan(propose(gamedata, mods2), mods2)))

        report = render_report(proposal, changes)
        check("报告带统计表", "| 身份表待补的 mod |" in report)
        check("报告列 unmatched", "Unlisted" in report)
        check("报告逐行列出 plan 里的每个 mod",
              all(("| %s |" % mod_id) in report for mod_id in changes), report)

        stats = summary(proposal, changes)
        check("summary 计数一致",
              stats["pendingMods"] == len(changes) and stats["unmatched"] == 2, str(stats))

        bad_path = os.path.join(root, "packs", "bad.json")
        with io.open(bad_path, "w", encoding="utf-8") as handle:
            handle.write('{"schema": 2, "mods": []}')
        rejected = False
        try:
            load_mods(bad_path)
        except ValueError:
            rejected = True
        check("schema 不认识时抛 ValueError", rejected)

        broken_path = os.path.join(root, "packs", "broken.json")
        with io.open(broken_path, "w", encoding="utf-8") as handle:
            handle.write('{"schema": 1, "mods": {}}')
        rejected = False
        try:
            load_mods(broken_path)
        except ValueError:
            rejected = True
        check("mods 不是数组时抛 ValueError", rejected)

        check("映射规则与 policy.folder_mod_id 一致",
              folder_mod_id("000_aje", ["AJE"]) == "AJE"
              and folder_mod_id("texturesunlimited", ["TexturesUnlimited"]) == "TexturesUnlimited")

        empty = propose(os.path.join(work, "nope"), mods)
        check("没有 GameData 时 propose 返回空不抛", empty["matches"] == {} and empty["unmatched"] == [])

        check("render_report 空 plan 也不炸", "| 身份表待补的 mod | 0 |" in render_report(proposal, {}))
    finally:
        shutil.rmtree(work, ignore_errors=True)

    for name, detail in failures:
        sys.stderr.write("[modmap-selftest] 失败：%s  %s\n" % (name, detail))
    if failures:
        print("[modmap-selftest] %d 项失败 / 共 %d 项" % (len(failures), len(checks)))
        return 1
    print("[modmap-selftest] 全部通过（%d 项）" % len(checks))
    return 0


# ---------------------------------------------------------------------------
# 命令行
# ---------------------------------------------------------------------------

def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="python -m i18n_tools.modmap",
        description="从真实 GameData 推导 packs/mods.json 的 folders / assemblies（写盘走 api）。",
    )
    parser.add_argument("--gamedata", default=None, help="KSP 的 GameData 目录（没有就 SKIP）")
    parser.add_argument("--root", default=None, help="仓库根（默认本仓库）")
    parser.add_argument("--mods", default=None, help="身份表路径（默认 <root>/packs/mods.json）")
    parser.add_argument("--max-depth", type=int, default=DEFAULT_MAX_DEPTH, help="向下找 DLL 的层数（默认 3）")
    parser.add_argument("--apply", action="store_true", help="真的写：逐条调用 api mod put")
    parser.add_argument("--dry-run", dest="dry_run", action="store_true", help="与 --apply 合用：走 api 的 dry-run")
    parser.add_argument("--report", default=None, help="把 markdown 报告写到这个路径")
    parser.add_argument("--stats", action="store_true", help="额外列出身份表里没有的 GameData 文件夹（最多 40 个）")
    parser.add_argument("--check", action="store_true", help="有缺口 exit 4；没有 GameData 则 SKIP exit 0")
    parser.add_argument("--selftest", action="store_true", help="自检")
    parser.add_argument("--quiet", action="store_true", help="只报错误")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    root = args.root or repo_root()
    mods_path = args.mods or default_mods_path(root)
    try:
        _doc, mods = load_mods(mods_path)
    except (OSError, ValueError) as error:
        sys.stderr.write("[modmap] 读不了身份表：%s\n" % error)
        return 4

    if not args.gamedata or not os.path.isdir(args.gamedata or ""):
        print("[modmap] skip: 没有可用的 GameData（%s）—— 这个工具只在装机后的机器上有意义"
              % (args.gamedata or "未指定"))
        return 0

    proposal = propose(args.gamedata, mods, args.max_depth)
    changes = plan(proposal, mods)
    stats = summary(proposal, changes)

    if not args.quiet:
        print("[modmap] matched=%d pending=%d (folders=%d, assemblies=%d) unmatched=%d" % (
            stats["matched"], stats["pendingMods"], stats["pendingFolders"],
            stats["pendingAssemblies"], stats["unmatched"]))

    if args.report:
        report = render_report(proposal, changes)
        parent = os.path.dirname(os.path.abspath(args.report))
        if parent and not os.path.isdir(parent):
            os.makedirs(parent)
        with io.open(args.report, "w", encoding="utf-8") as handle:
            handle.write(report)
        if not args.quiet:
            print("[modmap] 报告已写：%s" % args.report)

    if args.check:
        if not changes:
            print("[modmap] 身份表的 folders / assemblies 与磁盘一致")
            return 0
        sys.stderr.write("[modmap] 身份表落后于磁盘：%d 个 mod 有缺口（先跑 --apply）\n" % stats["pendingMods"])
        return 4

    if args.apply:
        ok_count, failures = apply(changes, root, ACTOR, args.dry_run)
        if failures:
            for mod_id, code, detail in failures:
                sys.stderr.write("[modmap] 失败：%s (exit %s) %s\n" % (mod_id, code, detail))
            return 5
        print("[modmap] %s完成：%d 个 mod" % ("dry-run " if args.dry_run else "", ok_count))

    if args.stats and proposal["unmatched"]:
        shown = proposal["unmatched"][:40]
        for folder in shown:
            print("[modmap] unmatched: %s" % folder)
        if len(proposal["unmatched"]) > len(shown):
            print("[modmap] unmatched: …（还有 %d 个，用 --report 看全表）" % (len(proposal["unmatched"]) - len(shown)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
