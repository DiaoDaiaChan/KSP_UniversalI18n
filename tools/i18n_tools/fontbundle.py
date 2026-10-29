"""随包字体清单（`fonts.json`）的生成与校验（M5-b）。

为什么需要这个工具：`FontMode = Bundled` 载入的是**二进制** AssetBundle，一旦这个文件被换掉
（发布包被中间人改、硬盘上的文件被别的工具覆盖、手动替换试字体），Unity 侧只会给出难懂的
载入失败，或者更糟——载入成功但字形不对。所以「字库文件 + 它的 sha256 + 许可证」必须是
一份**声明式**数据，由本工具生成，由 Runtime 的 `UI18n.Core.Config.FontBundle` 校验。

两侧的原因码必须一致（这里是唯一的对照表）：

    no-manifest / bad-manifest / unsafe-name / missing / too-large
    size-mismatch / sha256-mismatch / no-license

用法::

    # 生成（字库与许可证一起落盘，再写清单）
    python -m i18n_tools.fontbundle --pack NotoSansSC-Subset.bundle \\
        --license OFL.txt --spdx OFL-1.1 --name "Noto Sans SC" \\
        --source https://github.com/notofonts/noto-cjk --subset "CJK 常用 + 拉丁 + 标点"

    # 校验（CI 门禁用这个；没有清单时视为「还没发布随包字体」，退出码 0 并打一行提示）
    python -m i18n_tools.fontbundle --check

    # 自检
    python -m i18n_tools.fontbundle --selftest

体积预算 6 MB（`SIZE_BUDGET`）：发布包每 MB 都是玩家要下载的，随包字体只该是子集，
不该整份塞进去。超预算一律拒（`too-large`），不做「先放进去以后再说」。
"""

from __future__ import print_function

import argparse
import hashlib
import io
import json
import os
import shutil
import sys
import tempfile


SCHEMA = "ui18n-font-bundle/1"
MANIFEST_NAME = "fonts.json"
SIZE_BUDGET = 6 * 1024 * 1024

# 原因码：与 src/UI18n.Core/Config/FontBundle.cs 的 Reason* 常量逐条对应。
REASON_NONE = ""
REASON_NO_MANIFEST = "no-manifest"
REASON_BAD_MANIFEST = "bad-manifest"
REASON_UNSAFE_NAME = "unsafe-name"
REASON_MISSING = "missing"
REASON_TOO_LARGE = "too-large"
REASON_SIZE_MISMATCH = "size-mismatch"
REASON_SHA256_MISMATCH = "sha256-mismatch"
REASON_NO_LICENSE = "no-license"

DEFAULT_DIR = os.path.join("src", "UI18n.Runtime", "GameData", "UI18n", "Fonts")


def repo_root():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(os.path.dirname(here))


def default_dir():
    return os.path.join(repo_root(), DEFAULT_DIR)


def is_safe_name(name):
    """与 Core 的 FontBundle.IsSafeFileName 同口径：纯文件名，不许路径/盘符/空白名。"""
    if not name or len(name) > 200:
        return False
    if name in (".", ".."):
        return False
    if any(ch in name for ch in ("/", "\\", ":")):
        return False
    if any(ch in name for ch in ("<", ">", "|", "?", "*")):
        return False
    return all(ord(ch) >= 32 for ch in name)


def sha256_of(path):
    digest = hashlib.sha256()
    with io.open(path, "rb") as handle:
        while True:
            chunk = handle.read(1 << 20)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest().upper()


def build_manifest(directory, font_name, license_spdx, license_file, source="", subset="", file_name=None):
    """在 directory 里已有字库文件的前提下生成清单 dict（不写盘）。"""
    if not file_name:
        # 不给文件名时自动认目录里唯一的 *.bundle（Unicode 字体资产的常规扩展名）。
        candidates = sorted(n for n in os.listdir(directory) if n.lower().endswith(".bundle"))
        if len(candidates) != 1:
            raise ValueError(REASON_MISSING + ": 目录里 .bundle 文件数量为 %d，请用 --file-name 指定" % len(candidates))
        file_name = candidates[0]

    path = os.path.join(directory, file_name)
    if not os.path.isfile(path):
        raise ValueError(REASON_MISSING + ": 找不到字库文件 " + str(file_name))

    size = os.path.getsize(path)
    return {
        "schema": SCHEMA,
        "file": os.path.basename(path),
        "bytes": size,
        "sha256": sha256_of(path),
        "fontName": font_name,
        "license": license_spdx,
        "licenseFile": license_file,
        "source": source,
        "subset": subset,
    }


def dump_manifest(manifest):
    return json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"


def write_manifest(directory, manifest):
    if not os.path.isdir(directory):
        os.makedirs(directory)
    path = os.path.join(directory, MANIFEST_NAME)
    with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(dump_manifest(manifest))
    return path


def load_manifest(directory):
    """读清单；返回 (manifest, error)。error 用原因码开头，与 Core 一致。"""
    if not directory or not os.path.isdir(directory):
        return None, REASON_NO_MANIFEST + ": 目录不存在 " + str(directory)
    path = os.path.join(directory, MANIFEST_NAME)
    if not os.path.isfile(path):
        return None, REASON_NO_MANIFEST + ": " + path
    try:
        with io.open(path, "r", encoding="utf-8-sig") as handle:
            data = json.load(handle)
    except Exception as exc:  # noqa: BLE001 - 任何解析失败都归 bad-manifest
        return None, REASON_BAD_MANIFEST + ": " + str(exc)

    # 允许 {"fonts": [...]} 形态，只取第一条（与 Core 一致，方便以后放多套字体）。
    if isinstance(data, dict) and isinstance(data.get("fonts"), list) and data["fonts"]:
        data = data["fonts"][0]
    if not isinstance(data, dict):
        return None, REASON_BAD_MANIFEST + ": 顶层既不是对象也没有非空 fonts 数组"

    manifest = dict(data)
    if manifest.get("schema") != SCHEMA:
        return None, REASON_BAD_MANIFEST + ": schema=" + repr(manifest.get("schema")) + "，期望 " + SCHEMA
    file_name = manifest.get("file") or manifest.get("fileName")
    if not is_safe_name(file_name):
        return None, REASON_UNSAFE_NAME + ": file=" + repr(file_name)
    sha = (manifest.get("sha256") or "").strip().upper()
    if len(sha) != 64 or any(ch not in "0123456789ABCDEF" for ch in sha):
        return None, REASON_BAD_MANIFEST + ": sha256=" + repr(manifest.get("sha256")) + "，期望 64 位十六进制"
    try:
        size = int(manifest.get("bytes"))
    except (TypeError, ValueError):
        return None, REASON_BAD_MANIFEST + ": bytes=" + repr(manifest.get("bytes"))
    if size <= 0:
        return None, REASON_BAD_MANIFEST + ": bytes=" + str(size) + "，期望正数"

    manifest["file"] = file_name
    manifest["sha256"] = sha
    manifest["bytes"] = size
    manifest["license"] = manifest.get("license") or manifest.get("licenseSpdx")
    manifest["licenseFile"] = manifest.get("licenseFile") or manifest.get("license_file")
    return manifest, None


def verify(directory=None, require_license=True):
    """校验清单与文件。返回 dict(ok, reason, detail, manifest, fileBytes, actualSha, licensePath)。"""
    directory = directory or default_dir()
    result = {
        "ok": False,
        "reason": REASON_NONE,
        "detail": "",
        "manifest": None,
        "fileBytes": -1,
        "actualSha": None,
        "licensePath": None,
    }

    manifest, error = load_manifest(directory)
    if manifest is None:
        # 目录里压根没有清单 ⇒ 还没发布随包字体，不是错误。
        result["reason"] = error.split(":")[0]
        result["detail"] = error
        return result

    result["manifest"] = manifest
    path = os.path.join(directory, manifest["file"])
    if not os.path.isfile(path):
        result["reason"] = REASON_MISSING
        result["detail"] = "字库文件不在盘上：" + path
        return result

    size = os.path.getsize(path)
    result["fileBytes"] = size
    if size > SIZE_BUDGET:
        result["reason"] = REASON_TOO_LARGE
        result["detail"] = str(size) + " bytes > 预算 " + str(SIZE_BUDGET)
        return result
    if size != manifest["bytes"]:
        result["reason"] = REASON_SIZE_MISMATCH
        result["detail"] = "磁盘 " + str(size) + " bytes，清单 " + str(manifest["bytes"])
        return result

    actual = sha256_of(path)
    result["actualSha"] = actual
    if actual != manifest["sha256"]:
        result["reason"] = REASON_SHA256_MISMATCH
        result["detail"] = "磁盘 " + actual + "，清单 " + manifest["sha256"]
        return result

    if require_license:
        if not manifest.get("license") or not manifest.get("licenseFile"):
            result["reason"] = REASON_NO_LICENSE
            result["detail"] = "清单未登记 license/licenseFile（OFL 要求随分发附带许可证全文）"
            return result
        if not is_safe_name(manifest["licenseFile"]):
            result["reason"] = REASON_UNSAFE_NAME
            result["detail"] = "licenseFile=" + repr(manifest["licenseFile"])
            return result
        license_path = os.path.join(directory, manifest["licenseFile"])
        result["licensePath"] = license_path
        if not os.path.isfile(license_path):
            result["reason"] = REASON_NO_LICENSE
            result["detail"] = "许可证文件不存在：" + license_path
            return result

    result["ok"] = True
    return result


def describe(result):
    if result["ok"]:
        manifest = result["manifest"] or {}
        return ("font bundle ok: %s, %d bytes, sha256=%s, license=%s"
                % (manifest.get("file"), result["fileBytes"], (result["actualSha"] or "?")[:12],
                   manifest.get("license")))
    return "font bundle rejected (%s): %s" % (result["reason"], result["detail"])


def pack(font_path, license_path, directory=None, font_name="", license_spdx="OFL-1.1",
         source="", subset="", file_name=None, copy=True):
    """把一个字库文件（+许可证）摆进目录并写清单。返回写出的清单路径。"""
    directory = directory or default_dir()
    if not os.path.isfile(font_path):
        raise ValueError("找不到字库文件：" + font_path)
    if not license_path or not os.path.isfile(license_path):
        raise ValueError("随包字体必须带许可证文件（OFL 要求随分发附带全文）：" + str(license_path))

    if not os.path.isdir(directory):
        os.makedirs(directory)

    target_name = file_name or os.path.basename(font_path)
    if not is_safe_name(target_name):
        raise ValueError(REASON_UNSAFE_NAME + ": " + repr(target_name))
    if not is_safe_name(os.path.basename(license_path)):
        raise ValueError(REASON_UNSAFE_NAME + ": " + repr(os.path.basename(license_path)))

    target = os.path.join(directory, target_name)
    if copy:
        if os.path.abspath(font_path) != os.path.abspath(target):
            shutil.copyfile(font_path, target)
        license_target = os.path.join(directory, os.path.basename(license_path))
        if os.path.abspath(license_path) != os.path.abspath(license_target):
            shutil.copyfile(license_path, license_target)
    elif not os.path.isfile(target):
        raise ValueError(REASON_MISSING + ": 未 copy 时字库必须已在目录里 " + target)

    manifest = build_manifest(directory, font_name, license_spdx, os.path.basename(license_path),
                              source=source, subset=subset, file_name=target_name)
    return write_manifest(directory, manifest)


def summary(directory=None):
    """一行摘要（给 --stats / CI 日志用）。"""
    result = verify(directory, require_license=False)
    if not result["ok"]:
        return "font bundle: " + result["reason"]
    manifest = result["manifest"]
    return "font bundle: 1 bundled font (%s, %d bytes, license %s)" % (
        manifest.get("file"), result["fileBytes"], manifest.get("license"))


def selftest():
    """自检：清单往返、六类失败原因码、体积预算、许可证要求。"""
    checks = []
    failures = []

    def check(name, ok, detail=""):
        checks.append(name)
        if not ok:
            failures.append(name + (": " + str(detail) if detail else ""))

    def expect_reason(name, result, reason):
        check(name, result["reason"] == reason, "got %r (%s)" % (result["reason"], result["detail"]))

    root = tempfile.mkdtemp(prefix="ui18n-fontbundle-")
    try:
        good = os.path.join(root, "good")
        os.makedirs(good)
        payload = bytes(bytearray((i * 31 + 7) & 0xFF for i in range(4096)))
        with io.open(os.path.join(good, "ui18n-cjk.bundle"), "wb") as handle:
            handle.write(payload)
        with io.open(os.path.join(good, "OFL.txt"), "w", encoding="utf-8") as handle:
            handle.write("SIL OPEN FONT LICENSE Version 1.1\n")

        manifest = build_manifest(good, "Noto Sans SC", "OFL-1.1", "OFL.txt",
                                  source="https://example.invalid/noto", subset="CJK")
        check("sha256 是 64 位大写十六进制", len(manifest["sha256"]) == 64 and manifest["sha256"] == manifest["sha256"].upper())
        check("bytes 与文件一致", manifest["bytes"] == len(payload))

        written = write_manifest(good, manifest)
        check("清单写盘路径", written == os.path.join(good, MANIFEST_NAME))
        with io.open(written, "r", encoding="utf-8") as handle:
            text = handle.read()
        check("清单以换行结尾", text.endswith("\n"))
        check("中文不转义（ensure_ascii=False）", "CJK" in text)

        result = verify(good, require_license=True)
        check("完整清单通过校验", result["ok"], describe(result))
        check("通过时给出文件名", result["manifest"]["file"] == "ui18n-cjk.bundle")
        check("通过时给出许可证路径", result["licensePath"] == os.path.join(good, "OFL.txt"))
        check("describe() 说 ok", describe(result).startswith("font bundle ok:"))

        expect_reason("缺清单 → no-manifest", verify(os.path.join(root, "absent"), require_license=True), REASON_NO_MANIFEST)
        expect_reason("目录不存在 → no-manifest", verify(os.path.join(root, "nope"), require_license=False), REASON_NO_MANIFEST)

        broken = os.path.join(root, "broken")
        os.makedirs(broken)
        with io.open(os.path.join(broken, MANIFEST_NAME), "w", encoding="utf-8") as handle:
            handle.write("{ not json")
        expect_reason("坏 JSON → bad-manifest", verify(broken, require_license=False), REASON_BAD_MANIFEST)

        wrong_schema = os.path.join(root, "wrongschema")
        os.makedirs(wrong_schema)
        bad = dict(manifest)
        bad["schema"] = "ui18n-font-bundle/2"
        write_manifest(wrong_schema, bad)
        expect_reason("错误 schema → bad-manifest", verify(wrong_schema, require_license=False), REASON_BAD_MANIFEST)

        unsafe = os.path.join(root, "unsafe")
        os.makedirs(unsafe)
        bad = dict(manifest)
        bad["file"] = "../evil.bundle"
        write_manifest(unsafe, bad)
        expect_reason("路径穿越文件名 → unsafe-name", verify(unsafe, require_license=False), REASON_UNSAFE_NAME)

        bad = dict(manifest)
        bad["file"] = "absent.bundle"
        write_manifest(unsafe, bad)
        expect_reason("清单指的文件不存在 → missing", verify(unsafe, require_license=False), REASON_MISSING)

        mismatch = os.path.join(root, "mismatch")
        os.makedirs(mismatch)
        with io.open(os.path.join(mismatch, "ui18n-cjk.bundle"), "wb") as handle:
            handle.write(payload)
        with io.open(os.path.join(mismatch, "OFL.txt"), "w", encoding="utf-8") as handle:
            handle.write("OFL\n")
        bad = dict(manifest)
        bad["bytes"] = len(payload) - 1
        write_manifest(mismatch, bad)
        expect_reason("字节数不符 → size-mismatch", verify(mismatch, require_license=False), REASON_SIZE_MISMATCH)

        bad = dict(manifest)
        bad["sha256"] = "0" * 64
        write_manifest(mismatch, bad)
        expect_reason("sha256 不符 → sha256-mismatch", verify(mismatch, require_license=False), REASON_SHA256_MISMATCH)

        nolicense = os.path.join(root, "nolicense")
        os.makedirs(nolicense)
        with io.open(os.path.join(nolicense, "ui18n-cjk.bundle"), "wb") as handle:
            handle.write(payload)
        bad = dict(manifest)
        bad["license"] = ""
        bad["licenseFile"] = ""
        write_manifest(nolicense, bad)
        check("不要求许可证时能过", verify(nolicense, require_license=False)["ok"])
        expect_reason("要求许可证但未登记 → no-license", verify(nolicense, require_license=True), REASON_NO_LICENSE)

        missing_license = os.path.join(root, "missinglicense")
        os.makedirs(missing_license)
        with io.open(os.path.join(missing_license, "ui18n-cjk.bundle"), "wb") as handle:
            handle.write(payload)
        write_manifest(missing_license, manifest)
        expect_reason("许可证文件不存在 → no-license", verify(missing_license, require_license=True), REASON_NO_LICENSE)

        check("体积预算 6 MB", SIZE_BUDGET == 6 * 1024 * 1024)
        check("is_safe_name 拒路径", not is_safe_name("../a.bundle") and not is_safe_name("a/b") and not is_safe_name("C:a"))
        check("is_safe_name 拒空与点", not is_safe_name("") and not is_safe_name(".") and not is_safe_name(".."))
        check("is_safe_name 收普通名", is_safe_name("ui18n-cjk.bundle"))

        packed = os.path.join(root, "packed")
        source_font = os.path.join(root, "source.bundle")
        with io.open(source_font, "wb") as handle:
            handle.write(payload)
        license_src = os.path.join(root, "OFL-1.1.txt")
        with io.open(license_src, "w", encoding="utf-8") as handle:
            handle.write("OFL\n")
        pack(source_font, license_src, directory=packed, font_name="Noto Sans SC",
             license_spdx="OFL-1.1", source="https://example.invalid", subset="CJK")
        packed_result = verify(packed, require_license=True)
        check("pack() 产物能通过校验", packed_result["ok"], describe(packed_result))
        check("pack() 同时摆好许可证", os.path.isfile(os.path.join(packed, "OFL-1.1.txt")))
        check("pack() 后摘要说 1 bundled font", summary(packed).startswith("font bundle: 1 bundled font"))

        raises = False
        try:
            pack(source_font, None, directory=os.path.join(root, "nolic"), font_name="X")
        except ValueError:
            raises = True
        check("pack() 无许可证时抛 ValueError", raises)

        raises = False
        try:
            pack(source_font, license_src, directory=os.path.join(root, "badname"), file_name="../x.bundle")
        except ValueError:
            raises = True
        check("pack() 不安全文件名抛 ValueError", raises)
    finally:
        shutil.rmtree(root, ignore_errors=True)

    for failure in failures:
        print("[FAIL] " + failure)
    print("[fontbundle-selftest] %s（%d 项）" % ("全部通过" if not failures else "有失败", len(checks)))
    return 0 if not failures else 1


def main(argv):
    parser = argparse.ArgumentParser(description="随包字体清单（fonts.json）生成与校验")
    parser.add_argument("--dir", default=None, help="字体目录（默认 " + DEFAULT_DIR + "）")
    parser.add_argument("--pack", default=None, metavar="FONT", help="要收纳的字库文件路径")
    parser.add_argument("--license", default=None, metavar="FILE", help="许可证全文文件（OFL.txt）")
    parser.add_argument("--spdx", default="OFL-1.1", help="许可证 SPDX 标识（默认 OFL-1.1）")
    parser.add_argument("--name", default="", help="字体族名（只进清单，给日志看）")
    parser.add_argument("--source", default="", help="上游来源 URL / 说明")
    parser.add_argument("--subset", default="", help="子集化说明（保留哪些码位）")
    parser.add_argument("--file-name", default=None, help="落盘后的文件名（默认沿用源文件名）")
    parser.add_argument("--check", action="store_true", help="校验现有清单与文件")
    parser.add_argument("--stats", action="store_true", help="打一行摘要")
    parser.add_argument("--selftest", action="store_true", help="跑自检")
    parser.add_argument("--quiet", action="store_true", help="只打错误")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    directory = args.dir or default_dir()

    if args.pack:
        try:
            written = pack(args.pack, args.license, directory=directory, font_name=args.name,
                           license_spdx=args.spdx, source=args.source, subset=args.subset,
                           file_name=args.file_name)
        except ValueError as exc:
            print("[fontbundle] " + str(exc), file=sys.stderr)
            return 4
        if not args.quiet:
            print("[fontbundle] wrote " + written)
        return 0

    if args.check or args.stats:
        result = verify(directory)
        if result["ok"]:
            if not args.quiet:
                print("[fontbundle] " + describe(result))
            return 0
        if result["reason"] == REASON_NO_MANIFEST:
            # 还没发布随包字体是合法状态（默认 OsFont 档），不算失败。
            if not args.quiet:
                print("[fontbundle] no bundled font shipped (" + result["detail"] + ")")
            return 0
        print("[fontbundle] " + describe(result), file=sys.stderr)
        return 4

    parser.print_help()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
