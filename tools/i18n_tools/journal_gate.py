r"""journal_gate.py -- packs 里的写操作必须留下 journal 事件（docs/10 §5 的 CI 硬门禁）。

为什么要有这道门（docs/10 §1 的事故表 + §5）：api.py 把「写译文」收敛成一个入口之后，
剩下的漏洞只有一个 —— 绕开它，直接用编辑器 / agent 改 packs/*.jsonl。那种改动不会留下
审计事件，于是「这条译文是谁写的、依据是什么、过了哪些门禁」全部丢失；更糟的是两个人
各读整文件再写回时，后写的会把前一个吃掉，而事后没有任何痕迹能定位。

判据只有两条，都很硬，不靠自觉：

  1. 受管文件的**当前 sha256 字节**必须出现在某条 journal 事件的 file_sha256_after 里。
     手改一个字节就换 sha256，而 sha256 对不上任何事件 —— 拦得住。
  2. journal 只能**追加**：旧内容必须仍然是从开头算起的前缀。删事件、改事件、把文件截短
     都拦得住。

受管范围（WATCHED）就是 docs/10 §5 列的四个位置：packs/zh-cn/**、packs/overrides.jsonl、
packs/glossary.jsonl、packs/mods.json，**再加上 packs/submission/**（M3 投稿目录：meta.json、
index.json、每个 mod 的 .jsonl 都算）** —— 不改这条，投稿目录就是唯一能手改不留痕的地方。
除此之外的文件不管（tools/、src/、docs/ 随便改）。

M3 的事件形状与别的写操作不同（docs/10 §6.7）：**一笔投稿只写一条事件**，事件顶层没有 file 字段，
文件清单在 files[] 里、候选表记在 candidates 里。所以认账时这三个位置都要查
（event.file / event.files[].path / event.candidates.path），否则投稿刚收完就自己红。
注：packs/candidates.jsonl 本身**不受管**（25 MB 的表由流水线整表重算，套上必然天天红），
但它若哪天进了受管范围，事件里的 candidates 分支已经能直接当证据用。

拒绝时**不只报错**，还会把「这件事该用哪条 api 命令做」打出来 —— 门禁的意义是让人换条路
走通，而不是把人堵死。

用法（在 tools 目录下执行）：

    cd tools
    python -m i18n_tools.journal_gate --worktree                 # 本地：相对 HEAD 的工作区改动
    python -m i18n_tools.journal_gate --staged                   # 提交前：index 里的改动
    python -m i18n_tools.journal_gate --base origin/master       # CI：base..HEAD 的改动
    python -m i18n_tools.journal_gate --worktree --json          # 机器读
    python -m i18n_tools.journal_gate --selftest                 # 自检（造临时 git 仓库）

三个模式互斥，不给就用 --base HEAD~1（CI 的默认）。

退出码（程序接口，语义冻结）：
    0 通过（没有任何受管改动，或所有受管改动都有 journal 事件背书）
    1 拒绝（有受管改动缺事件 / journal 被改写 / 受管文件被删）
    2 用法错误或 git 不可用（不是仓库、rev 不存在、git 不在 PATH）
"""

import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

EXIT_OK = 0
EXIT_RED = 1
EXIT_USAGE = 2

#: 受管目录（相对仓库根，末尾带斜杠表示「整个子树」），只收 .jsonl
WATCHED_DIRS = ("packs/zh-cn/",)
#: 整棵子树都受管（不分后缀）：投稿目录里的 meta.json / index.json 也必须留痕
WATCHED_ANY = ("packs/submission/",)
#: 受管单文件
WATCHED_FILES = ("packs/overrides.jsonl", "packs/glossary.jsonl", "packs/mods.json")
#: journal 目录（只追加）
JOURNAL_DIR = "packs/journal/"

#: 拒绝时给出的补救命令（按文件位置猜最贴切的那条，猜不准就给最通用的）
HINTS = (
    ("packs/submission/",
     "python -m i18n_tools.api submit accept --bundle <投稿.json> --actor human:<你>"
     "（投稿目录是收投稿时写的；要重收先删掉该 id 的目录，别手改）"),
    ("packs/overrides.jsonl",
     'python -m i18n_tools.api override put --en "<英文>" --zh "<中文>" --actor human:<你>'),
    ("packs/glossary.jsonl",
     'python -m i18n_tools.api glossary put --en "<英文>" --zh "<中文>" --actor human:<你>'),
    ("packs/mods.json",
     "python -m i18n_tools.api mod put --mod <modId> --display <名字> --actor human:<你>"),
    ("packs/zh-cn/",
     'python -m i18n_tools.api entry put --mod <modId> --lang zh-cn --text "<英文>" '
     '--t "<中文>" --actor human:<你>'),
)


def ensure_stdout():
    """Windows GBK 控制台打中文会 UnicodeEncodeError；这里就地改成 utf-8。

    StringIO 之类的假 stdout 没有 reconfigure，所以必须逐个探测而不是直接调。
    """
    for stream in (sys.stdout, sys.stderr):
        hook = getattr(stream, "reconfigure", None)
        if hook is None:
            continue
        try:
            hook(encoding="utf-8", errors="replace")
        except (ValueError, OSError):
            pass


class GateUsage(Exception):
    """用法错误或环境不可用（git 不在、不是仓库、rev 不存在）→ 退出码 2。"""


# ---------------------------------------------------------------------------
# git 小工具（只用 subprocess，不引第三方库）
# ---------------------------------------------------------------------------

def _git(root, args, check=False):
    """跑一条 git 命令，返回 (退出码, stdout 字节, stderr 文本)。"""
    try:
        done = subprocess.run(
            ["git"] + [str(item) for item in args],
            cwd=str(root),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except FileNotFoundError:
        raise GateUsage("找不到 git 命令：请先装 git（或把 git 放进 PATH）")
    except OSError as exc:
        raise GateUsage("调用 git 失败：%s" % exc)
    out = done.stdout or b""
    err = (done.stderr or b"").decode("utf-8", "replace")
    if check and done.returncode != 0:
        raise GateUsage("git %s 失败：%s" % (" ".join(str(item) for item in args), err.strip()))
    return done.returncode, out, err


def repo_root_of(start=None):
    """仓库根：git rev-parse --show-toplevel；不在仓库里就抛 GateUsage。"""
    start = Path(start or os.getcwd())
    code, out, err = _git(start, ["rev-parse", "--show-toplevel"])
    if code != 0:
        raise GateUsage("当前目录不在 git 仓库里（%s）：%s" % (start, err.strip()))
    return Path(out.decode("utf-8", "replace").strip())


def _rev_exists(root, rev):
    code, _out, _err = _git(root, ["rev-parse", "--verify", "--quiet", rev + "^{commit}"])
    return code == 0


def _split_paths(raw):
    """git 输出的一行一路径；注意 git 可能把非 ASCII 或含空格路径加引号，这里统一去引号。"""
    paths = []
    for line in raw.decode("utf-8", "replace").splitlines():
        name = line.strip()
        if not name:
            continue
        if name.startswith('"') and name.endswith('"'):
            name = name[1:-1]
            name = name.encode("utf-8", "surrogateescape").decode("unicode_escape")
            name = name.encode("latin-1", "replace").decode("utf-8", "replace")
        paths.append(name.replace(os.sep, "/"))
    return paths


def changed_paths(root, mode="worktree", base=None):
    """列出这次要审的改动（相对仓库根的 posix 路径，可能含被删文件）。"""
    paths = set()
    if mode == "staged":
        _code, out, _err = _git(root, ["diff", "--cached", "--name-only", "--diff-filter=ACDMRT"])
        paths.update(_split_paths(out))
    elif mode == "worktree":
        _code, out, _err = _git(root, ["diff", "--name-only", "--diff-filter=ACDMRT", "HEAD"])
        paths.update(_split_paths(out))
        _code, out, _err = _git(root, ["ls-files", "--others", "--exclude-standard"])
        paths.update(_split_paths(out))
    elif mode == "base":
        rev = base or "HEAD~1"
        if not _rev_exists(root, rev):
            raise GateUsage("--base %s 不存在（仓库太新或写错 rev）" % rev)
        _code, out, _err = _git(root, ["diff", "--name-only", "--diff-filter=ACDMRT", rev, "HEAD"])
        paths.update(_split_paths(out))
        _code, out, _err = _git(root, ["ls-files", "--others", "--exclude-standard"])
        paths.update(_split_paths(out))
    else:
        raise GateUsage("未知模式 %r（只有 worktree / staged / base）" % (mode,))
    return sorted(paths)


def is_watched(rel):
    """受管判定：packs/zh-cn/ 的 .jsonl + packs/submission/ 整棵子树 + 三个单文件。"""
    rel = str(rel).replace(os.sep, "/")
    if rel in WATCHED_FILES:
        return True
    if any(rel.startswith(prefix) for prefix in WATCHED_ANY):
        return True
    return any(rel.startswith(prefix) and rel.endswith(".jsonl") for prefix in WATCHED_DIRS)


def sha256_hex(data):
    import hashlib
    return hashlib.sha256(data).hexdigest()


def file_bytes(root, rel):
    path = Path(root) / rel
    try:
        return path.read_bytes()
    except OSError:
        return None


def rev_bytes(root, rel, rev):
    """取某个 rev 里的文件内容；不存在返回 b""（新增文件就是这样）。"""
    if not rev:
        return b""
    code, out, _err = _git(root, ["show", "%s:%s" % (rev, rel)])
    return out if code == 0 else b""


def journal_events(root):
    """读全部 journal 事件（按文件名 + 行序）；坏行直接报出来，不静默跳过。"""
    directory = Path(root) / JOURNAL_DIR
    events, bad = [], []
    if not directory.is_dir():
        return events, bad
    for path in sorted(directory.glob("*.jsonl")):
        for number, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            if not line.strip():
                continue
            try:
                event = json.loads(line)
            except ValueError:
                bad.append("%s:%d" % (path.name, number))
                continue
            if not isinstance(event, dict):
                bad.append("%s:%d" % (path.name, number))
                continue
            event["_file"] = path.name
            event["_line"] = number
            events.append(event)
    return events, bad


def hint_for(rel):
    for prefix, text in HINTS:
        if rel == prefix or rel.startswith(prefix):
            return text
    return "python -m i18n_tools.api --help"


def _new_bytes(root, rel, mode):
    """被审的「新内容」：worktree/base 看磁盘，staged 看 index。"""
    if mode == "staged":
        code, out, _err = _git(root, ["show", ":" + rel])
        return out if code == 0 else None
    return file_bytes(root, rel)


def _old_bytes(root, rel, mode, base, head):
    rev = base if mode == "base" else head
    if not rev:
        return b""
    return rev_bytes(root, rel, rev)


def check(root, mode="worktree", base=None, head=None):
    """跑一遍门禁，返回结构化结果（不打印、不退出，便于自检与别处复用）。

    返回 dict：mode / base / changed（受管改动）/ ignored（不管的改动）/ violations /
    journal（事件数 + 坏行）/ ok（bool）。
    """
    root = Path(root)
    if head is None:
        code, out, _err = _git(root, ["rev-parse", "HEAD"])
        head = out.decode("utf-8", "replace").strip() if code == 0 else ""
    paths = changed_paths(root, mode=mode, base=base)
    watched = [path for path in paths if is_watched(path)]
    # journal 文件不进 ignored：它们由下面的「只追加」规则专门审，混在一起会让人以为没人管
    ignored = [path for path in paths
               if not is_watched(path) and not path.startswith(JOURNAL_DIR)]

    events, bad = journal_events(root)
    by_file = {}
    known_after = {}

    def index_event(event, name, sha):
        name = str(name or "").replace(os.sep, "/")
        if not name:
            return
        by_file.setdefault(name, []).append(event)
        if sha:
            known_after.setdefault(name, set()).add(str(sha))

    for event in events:
        index_event(event, event.get("file"), event.get("file_sha256_after"))
        # M3（docs/10 §6.7）：一笔投稿一条事件，文件清单在 files[] 里
        for item in event.get("files") or []:
            if isinstance(item, dict):
                index_event(event, item.get("path") or item.get("file"),
                            item.get("file_sha256_after"))
        cands = event.get("candidates")
        if isinstance(cands, dict):
            index_event(event, cands.get("path"), cands.get("file_sha256_after"))

    violations = []
    for rel in watched:
        data = _new_bytes(root, rel, mode)
        if data is None:
            violations.append({
                "kind": "deleted", "file": rel,
                "detail": "受管文件被删掉了；改语言包结构请走 api（或写进 docs 走评审）",
                "hint": hint_for(rel)})
            continue
        current = sha256_hex(data)
        history = by_file.get(rel.replace(os.sep, "/")) or []
        known = known_after.get(rel.replace(os.sep, "/")) or set()
        if not history:
            violations.append({
                "kind": "no-journal", "file": rel, "sha256": current,
                "detail": "这个文件改过了，但 journal 里没有它的任何事件 → 像是手改的",
                "hint": hint_for(rel)})
        elif current not in known:
            violations.append({
                "kind": "sha-mismatch", "file": rel, "sha256": current,
                "detail": "当前内容的 sha256 不在任何 journal 事件的 file_sha256_after 里"
                          "（文件内容与事件记录不一致）",
                "hint": hint_for(rel)})

    # journal 只追加：受影响文件（不只是受管的）逐个比「旧内容是不是新内容的开头前缀」
    journal_paths = [path for path in paths if path.startswith(JOURNAL_DIR)]
    for rel in journal_paths:
        new = _new_bytes(root, rel, mode)
        if new is None:
            violations.append({
                "kind": "journal-deleted", "file": rel,
                "detail": "journal 文件被删了 —— 审计记录只能追加，不能删",
                "hint": "恢复该文件（git checkout -- %s）" % rel})
            continue
        old = _old_bytes(root, rel, mode, base, head)
        old_lines = [line for line in old.decode("utf-8", "replace").splitlines() if line.strip()]
        new_lines = [line for line in new.decode("utf-8", "replace").splitlines() if line.strip()]
        if len(new_lines) < len(old_lines):
            violations.append({
                "kind": "journal-shrunk", "file": rel,
                "detail": "行数从 %d 减到 %d（只允许追加）" % (len(old_lines), len(new_lines)),
                "hint": "git checkout -- %s" % rel})
        elif new_lines[:len(old_lines)] != old_lines:
            violations.append({
                "kind": "journal-rewritten", "file": rel,
                "detail": "旧行不再是从开头算起的前缀（事件被改过或重排过）",
                "hint": "git checkout -- %s" % rel})

    return {
        "mode": mode, "base": base, "head": head,
        "changed": watched, "ignored": ignored,
        "violations": violations,
        "journal": {"events": len(events), "files": len(by_file), "bad": bad},
        "ok": not violations,
    }


def render_report(result):
    """给人看的报告：先结论，再逐条违规与补救命令，最后一行汇总。"""
    lines = []
    if result["ok"]:
        lines.append("[journal] 通过：受管改动 %d 个，都能在 journal 里找到对应事件（模式 %s）"
                     % (len(result["changed"]), result["mode"]))
        for rel in result["changed"]:
            lines.append("  ok  %s" % rel)
    else:
        lines.append("[journal] 拒绝：%d 个受管文件没有可信的 journal 记录（模式 %s）"
                     % (len(result["violations"]), result["mode"]))
        for item in result["violations"]:
            lines.append("  RED %s  [%s]" % (item["file"], item["kind"]))
            lines.append("      %s" % item["detail"])
            lines.append("      该改用：%s" % item["hint"])
    if result["ignored"]:
        lines.append("[journal] 不管的改动 %d 个（不在受管范围）：%s"
                     % (len(result["ignored"]), ", ".join(result["ignored"][:5])
                        + ("…" if len(result["ignored"]) > 5 else "")))
    journal = result["journal"]
    lines.append("[journal] journal 事件 %d 条（涉及 %d 个文件），坏行 %d 个"
                 % (journal["events"], journal["files"], len(journal["bad"])))
    if journal["bad"]:
        lines.append("[journal] 坏行：%s" % ", ".join(journal["bad"][:5]))
    return lines


def build_parser():
    parser = argparse.ArgumentParser(
        prog="i18n_tools.journal_gate",
        description="packs 里的写操作必须留下 journal 事件（docs/10 §5 的 CI 硬门禁）")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--worktree", dest="mode", action="store_const", const="worktree",
                      help="审工作区相对 HEAD 的改动（本地与 run-tests.ps1 用这个）")
    mode.add_argument("--staged", dest="mode", action="store_const", const="staged",
                      help="审 index 里的改动（提交前用）")
    mode.add_argument("--base", dest="mode", action="store_const", const="base",
                      help="审 base..HEAD 的改动（CI 用，默认 base=HEAD~1）")
    parser.add_argument("--base-rev", "--base-ref", dest="base_rev", default="HEAD~1",
                        help="--base 模式的比较基准 rev（默认 HEAD~1）")
    parser.add_argument("--root", default=None, help="仓库根（默认从当前目录向上找）")
    parser.add_argument("--json", action="store_true", help="输出 JSON（机器读）")
    parser.add_argument("--quiet", action="store_true", help="只在失败时输出")
    parser.add_argument("--selftest", action="store_true", help="跑自检（造临时 git 仓库）")
    parser.set_defaults(mode=None)
    return parser


def main(argv=None):
    ensure_stdout()
    parser = build_parser()
    args = parser.parse_args(list(argv) if argv is not None else None)

    if args.selftest:
        import io
        out, err = io.StringIO(), io.StringIO()
        saved = sys.stdout, sys.stderr
        sys.stdout, sys.stderr = out, err
        try:
            failures, total = selftest()
        finally:
            sys.stdout, sys.stderr = saved
        sys.stdout.write("".join(render_selftest(failures, total)))
        return EXIT_OK if not failures else EXIT_RED

    mode = args.mode or "base"
    try:
        root = Path(args.root) if args.root else repo_root_of()
        if not root.is_dir():
            raise GateUsage("--root %s 不是目录" % root)
        result = check(root, mode=mode, base=args.base_rev if mode == "base" else None)
    except GateUsage as exc:
        sys.stderr.write("[journal] 用法错误：%s\n" % exc)
        return EXIT_USAGE

    if args.json:
        sys.stdout.write(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    elif not (args.quiet and result["ok"]):
        sys.stdout.write("\n".join(render_report(result)) + "\n")
    if not result["ok"]:
        sys.stdout.write("[journal] 提示：先把改动回退（git checkout -- <file>），"
                         "再用上面给出的 api 命令重做一遍。\n")
        return EXIT_RED
    return EXIT_OK


# ---------------------------------------------------------------------------
# 自检：造一个临时 git 仓库，把「该拦的」和「不该拦的」都走一遍
# ---------------------------------------------------------------------------

def render_selftest(failures, total):
    lines = []
    for name, detail in failures:
        lines.append("[journal selftest] 失败：%s  %s" % (name, detail))
    if failures:
        lines.append("[journal selftest] %d 项失败 / 共 %d 项" % (len(failures), total))
    else:
        lines.append("[journal selftest] 全部通过（%d 项）" % total)
    return lines


def _write(root, rel, text):
    path = Path(root) / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(text.encode("utf-8"))


def _commit(root, message="c"):
    _git(root, ["add", "-A"])
    code, _out, err = _git(root, ["-c", "user.name=gate", "-c", "user.email=gate@example.com",
                                  "commit", "-m", message, "--no-gpg-sign"])
    return code == 0, err


def _journal_line(rel, sha, action="insert"):
    return json.dumps({
        "ts": "2026-01-01T00:00:00Z", "actor": "pipeline:selftest", "op": "entry.put",
        "lang": "zh-cn", "mod": "TUFX", "h": "0123456789abcdef", "action": action,
        "file": rel, "revision_before": "aaaaaaaaaaaa@1", "revision_after": "bbbbbbbbbbbb@2",
        "file_sha256_after": sha, "bytes_after": 100, "gates": [], "note": "",
    }, ensure_ascii=False)


def _submit_event(files, cand_sha, sid="abc123def4567890"):
    """造一条 M3 形状的事件：顶层没有 file，文件清单在 files[]、候选表在 candidates 里。"""
    return json.dumps({
        "ts": "2026-01-01T00:00:00Z", "actor": "pipeline:selftest", "op": "submit.accept",
        "lang": "zh-cn", "mod": None, "h": None, "action": "insert", "file": None,
        "revision_before": None, "revision_after": None, "file_sha256_after": None,
        "bytes_after": None, "gates": [], "note": "",
        "submission_id": sid, "bundle_sha256": "0" * 64, "rows": 1, "mods": ["TUFX"],
        "files": files,
        "candidates": {"changed": True, "path": "packs/candidates.jsonl",
                       "file_sha256_after": cand_sha, "inserted": 1, "updated": 0},
    }, ensure_ascii=False)


def selftest():
    """返回 (失败列表, 总项数)。全部在临时 git 仓库里跑。"""
    failures = []
    total = 0

    def check_it(name, condition, detail=""):
        nonlocal total
        total += 1
        if not condition:
            failures.append((name, str(detail)))

    with tempfile.TemporaryDirectory(prefix="ui18n-gate-") as temp:
        root = Path(temp)
        _git(root, ["init"])  # 老 git 可能不支持 -b
        _write(root, "packs/zh-cn/TUFX.jsonl",
               '{"h": "0123456789abcdef", "n": "Blue Paint", "s": "Blue Paint",'
               ' "t": "\u84dd\u8272\u6d82\u88c5", "mod": "TUFX"}\n')
        _write(root, "packs/glossary.jsonl", '{"key": "Kerbal", "en": "Kerbal", "zh": "\u574e\u5df4\u62c9\u4eba", "short": false}\n')
        _write(root, "packs/journal/2026-01-01.jsonl", _journal_line(
            "packs/zh-cn/TUFX.jsonl", sha256_hex((root / "packs/zh-cn/TUFX.jsonl").read_bytes())) + "\n")
        _write(root, "tools/scratch.py", "# 不管的文件\n")
        ok, err = _commit(root, "first")
        check_it("\u4e34\u65f6\u4ed3\u5e93\u80fd\u63d0\u4ea4", ok, err)

        # 1) 干净工作区 → 通过
        result = check(root, mode="worktree")
        check_it("\u5e72\u51c0\u5de5\u4f5c\u533a\u901a\u8fc7", result["ok"] and not result["changed"],
                 result["violations"])
        check_it("\u6e05\u5355\u4e3a\u7a7a\u65f6\u4e5f\u7b97\u901a\u8fc7", render_report(result)[0].startswith("[journal] \u901a\u8fc7"))

        # 2) 手改受管文件（不写 journal）→ 拒绝
        _write(root, "packs/zh-cn/TUFX.jsonl",
               '{"h": "0123456789abcdef", "n": "Blue Paint", "s": "Blue Paint",'
               ' "t": "\u84dd\u8272\u6d82\u6599", "mod": "TUFX"}\n')
        result = check(root, mode="worktree")
        check_it("\u624b\u6539\u53d7\u7ba1\u6587\u4ef6\u88ab\u62e6", not result["ok"], result["violations"])
        check_it("\u62e6\u622a\u7c7b\u578b\u662f sha-mismatch",
                 [item["kind"] for item in result["violations"]] == ["sha-mismatch"],
                 result["violations"])
        check_it("\u62d2\u7edd\u65f6\u7ed9\u51fa\u8be5\u7528\u7684 api \u547d\u4ee4",
                 "api entry put" in result["violations"][0]["hint"],
                 result["violations"][0]["hint"])

        # 3) 走 api 语义（改文件 + 追加事件）→ 通过
        new_data = (root / "packs/zh-cn/TUFX.jsonl").read_bytes()
        journal_path = root / "packs/journal/2026-01-01.jsonl"
        with journal_path.open("a", encoding="utf-8", newline="") as handle:
            handle.write(_journal_line("packs/zh-cn/TUFX.jsonl", sha256_hex(new_data)) + "\n")
        result = check(root, mode="worktree")
        check_it("\u6539\u6587\u4ef6 + \u8ffd\u52a0\u4e8b\u4ef6\u540e\u901a\u8fc7", result["ok"], result["violations"])
        check_it("journal \u4e8b\u4ef6\u6570\u7edf\u8ba1", result["journal"]["events"] == 2,
                 result["journal"])
        check_it("journal \u6587\u4ef6\u4e0d\u8fdb ignored\uff08\u7531\u53ea\u8ffd\u52a0\u89c4\u5219\u4e13\u95e8\u5ba1\uff09",
                 check(root, mode="worktree")["ignored"] == [],
                 check(root, mode="worktree")["ignored"])
        ok, err = _commit(root, "api write")
        check_it("\u628a API \u90a3\u6b21\u5199\u5165\u63d0\u4ea4\u6210\u57fa\u51c6", ok, err)

        # 4) journal 被截短 → 拒绝
        lines = journal_path.read_text(encoding="utf-8").splitlines()
        check_it("提交后 journal 有 2 条事件", len(lines) == 2, len(lines))
        journal_path.write_bytes((lines[0] + "\n").encode("utf-8"))
        result = check(root, mode="worktree")
        kinds = [item["kind"] for item in result["violations"]]
        check_it("journal \u88ab\u622a\u77ed\u88ab\u62e6", "journal-shrunk" in kinds, kinds)
        check_it("截短 journal 单独一类违规（不动包文件）", kinds == ["journal-shrunk"], kinds)

        # 5) journal 行数不变、只改内容 → 拒绝（rewritten）
        first = json.loads(lines[0])
        first["actor"] = "human:cheater"
        altered = [json.dumps(first, ensure_ascii=False)] + lines[1:]
        journal_path.write_bytes(("\n".join(altered) + "\n").encode("utf-8"))
        result = check(root, mode="worktree")
        check_it("journal \u884c\u5185\u5bb9\u88ab\u6539\u62e6\u4f4f",
                 "journal-rewritten" in [item["kind"] for item in result["violations"]],
                 result["violations"])

        # 6) 受管文件被删 → 拒绝
        _git(root, ["checkout", "--", "packs/journal/2026-01-01.jsonl"])
        os.remove(root / "packs/zh-cn/TUFX.jsonl")
        result = check(root, mode="worktree")
        check_it("\u53d7\u7ba1\u6587\u4ef6\u88ab\u5220\u88ab\u62e6",
                 "deleted" in [item["kind"] for item in result["violations"]],
                 result["violations"])
        _git(root, ["checkout", "--", "packs/zh-cn/TUFX.jsonl"])
        check_it("\u56de\u6eda\u540e\u53c8\u901a\u8fc7", check(root, mode="worktree")["ok"])

        # 7) 只改不受管的文件 → 通过且 ignored 里能看到它
        _write(root, "tools/scratch.py", "# 改了但不管\n")
        result = check(root, mode="worktree")
        check_it("\u53ea\u6539 tools/ \u4e0d\u62e6", result["ok"], result["violations"])
        check_it("\u4e0d\u53d7\u7ba1\u6539\u52a8\u5217\u5728 ignored", "tools/scratch.py" in result["ignored"],
                 result["ignored"])
        _git(root, ["checkout", "--", "tools/scratch.py"])

        # 8) staged 模式：暂存一个手改 → 拒绝
        pack_path = root / "packs/zh-cn/TUFX.jsonl"
        pack_path.write_bytes(pack_path.read_bytes() + b"\n")  # 手改一个字节就够
        _git(root, ["add", "packs/zh-cn/TUFX.jsonl"])
        result = check(root, mode="staged")
        check_it("staged \u6a21\u5f0f\u62e6\u624b\u6539", not result["ok"], result["violations"])
        check_it("staged \u6a21\u5f0f\u8bfb\u7684\u662f index \u800c\u975e\u78c1\u76d8",
                 result["violations"] and result["violations"][0]["kind"] == "sha-mismatch",
                 result["violations"])
        _git(root, ["reset", "--hard", "HEAD"])

        # 9) base 模式：HEAD~1 不存在时报用法错误；有历史时能审
        raised = None
        try:
            check(root, mode="base", base="HEAD~99")
        except GateUsage as exc:
            raised = exc
        check_it("--base \u6307\u4e0d\u5b58\u5728\u7684 rev \u62a5\u7528\u6cd5\u9519\u8bef",
                 raised is not None, raised)

        # 10) 两个提交之后 base 模式正常工作
        _write(root, "tools/more.py", "# x\n")
        _commit(root, "second")
        result = check(root, mode="base", base="HEAD~1")
        check_it("base \u6a21\u5f0f\u80fd\u5ba1\u63d0\u4ea4", result["ok"], result["violations"])
        check_it("base \u6a21\u5f0f\u8bfb\u5230\u4e86\u6b63\u786e\u7684 head", bool(result["head"]), result["head"])

        # 11) 命令行用法错误 → 退出码 2
        try:
            main(["--worktree", "--staged", "--root", str(root)])
            code = 0
        except SystemExit as exc:
            code = exc.code
        check_it("\u4e24\u4e2a\u6a21\u5f0f\u540c\u65f6\u7ed9 \u2192 exit 2", code == 2, code)
        code = main(["--worktree", "--root", str(root), "--quiet", "--json"])
        check_it("\u547d\u4ee4\u884c\u6b63\u5e38\u8dd1\u901a exit 0", code == EXIT_OK, code)
        code = main(["--root", str(root / "nope")])
        check_it("\u6307\u4e0d\u5b58\u5728\u7684 --root \u2192 exit 2", code == EXIT_USAGE, code)

        # 12) M3 投稿目录：一笔事件带 files[] 也要能认账；手改 meta.json 要红
        sid = "abc123def4567890"
        sub_dir = "packs/submission/%s" % sid
        _write(root, sub_dir + "/meta.json", '{"id": "%s"}\n' % sid)
        _write(root, sub_dir + "/TUFX.jsonl",
               '{"h": "0123456789abcdef", "s": "Blue Paint", "mod": "TUFX"}\n')
        _write(root, "packs/submission/index.json", '{"schema": 1, "submissions": {}}\n')
        _write(root, "packs/candidates.jsonl", '{"h": "0123456789abcdef", "s": "Blue Paint"}\n')

        def _sha(rel):
            return sha256_hex((root / rel).read_bytes())

        event_files = [{"path": sub_dir + "/meta.json", "rows": 0,
                        "file_sha256_after": _sha(sub_dir + "/meta.json")},
                       {"path": sub_dir + "/TUFX.jsonl", "rows": 1,
                        "file_sha256_after": _sha(sub_dir + "/TUFX.jsonl")},
                       {"path": "packs/submission/index.json", "rows": 0,
                        "file_sha256_after": _sha("packs/submission/index.json")}]
        with journal_path.open("a", encoding="utf-8", newline="") as handle:
            handle.write(_submit_event(event_files, _sha("packs/candidates.jsonl"), sid) + "\n")
        result = check(root, mode="worktree")
        check_it("\u6295\u7a3f\u76ee\u5f55 + \u5019\u9009\u8868\u6709\u4e8b\u4ef6\u80cc\u4e66 \u2192 \u901a\u8fc7",
                 result["ok"], result["violations"])
        check_it("\u6295\u7a3f\u76ee\u5f55\u6574\u68f5\u5b50\u6811\u90fd\u53d7\u7ba1\uff08\u542b meta.json / index.json\uff09",
                 set(result["changed"]) >= {sub_dir + "/meta.json", sub_dir + "/TUFX.jsonl",
                                            "packs/submission/index.json"},
                 result["changed"])
        check_it("\u5019\u9009\u8868\u4e0d\u8fdb\u53d7\u7ba1\u6e05\u5355\uff08\u6574\u8868\u91cd\u7b97\u4e0d\u8be5\u88ab\u95e8\u7981\u5361\uff0c\u4e8b\u4ef6\u91cc\u7684 candidates \u5206\u652f\u53ea\u767b\u8bb0\uff09",
                 "packs/candidates.jsonl" not in result["changed"], result["changed"])

        _write(root, sub_dir + "/meta.json", '{"id": "%s", "note": "\u624b\u6539"}\n' % sid)
        result = check(root, mode="worktree")
        check_it("\u624b\u6539\u6295\u7a3f meta.json \u88ab\u62e6",
                 not result["ok"]
                 and [item["file"] for item in result["violations"]] == [sub_dir + "/meta.json"],
                 result["violations"])
        check_it("\u6295\u7a3f\u88ab\u62e6\u65f6\u7684\u8865\u6551\u547d\u4ee4\u6307\u5411 api submit accept",
                 bool(result["violations"])
                 and "api submit accept" in result["violations"][0]["hint"],
                 result["violations"][:1])

    return failures, total


if __name__ == "__main__":
    raise SystemExit(main())
