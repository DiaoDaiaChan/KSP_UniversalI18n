"""netkan（CKAN 元数据）校验器。

为什么需要它：CKAN 客户端拿到一份 .netkan 后不做任何「你确定吗」的追问 —— 字段缺了、
版本和 zip 对不上、install 段指错目录，玩家看到的是「装完没反应」或「索引报错」。而这些
错误全都能在离线用 JSON 形状判出来，不该留到玩家机器上才发现。

它守什么（每一条都对应一次真实的翻车方式）：

1. spec_version 必须存在 —— CKAN-meta 的 PR 模板要求，缺了 ci 直接打回；
2. version 必须存在、且等于 -Version 与 zip 内 GameData/UI18n/UI18n.version
   —— 三者不一致时 CKAN 里会出现「同一版本号两个不同 zip」，玩家永远装到旧的；
3. install 必须恰好是一条 GameData -> GameData —— zip 内顶层就是 GameData，
   指错一层等于把 GameData/GameData/UI18n 塞进游戏；
4. depends 只许 Harmony2 —— ModuleManager 一旦进来就是强制玩家多装一个用不到的 mod
   （UI18n 不改 cfg、不写 MM patch，见 docs/10 与 README 第 9 节）；
5. 任何 @占位符@ 没被替换掉、download 不是 https、sha256 不是 64 位 hex 都要拒。

用法::

    python -m i18n_tools.netkan dist/UI18n.netkan --version 1.7.0 --zip dist/UI18n-1.7.0.zip
    python -m i18n_tools.netkan --template src/UI18n.Runtime/ckan/UI18n.netkan.template
    python -m i18n_tools.netkan --selftest
"""

import argparse
import io
import json
import os
import re
import sys
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_TEMPLATE = os.path.join(REPO, "src", "UI18n.Runtime", "ckan", "UI18n.netkan.template")

# 模板里允许出现的占位符（build-release.ps1 负责替换）。多一个少一个都会在 --template 里报。
KNOWN_PLACEHOLDERS = ("@VERSION@", "@KSP_VERSION@", "@SHA256@", "@DOWNLOAD_URL@",
                      "@LICENSE@", "@AUTHOR@", "@HOMEPAGE@", "@REPOSITORY@")

# 模板渲染时用的假值：只为了能跑字段校验，不写盘
DUMMY_VALUES = {
    "@VERSION@": "9.9.9",
    "@KSP_VERSION@": "1.12",
    "@SHA256@": "0" * 64,
    "@DOWNLOAD_URL@": "https://example.invalid/UI18n-9.9.9.zip",
    "@LICENSE@": "MIT",
    "@AUTHOR@": "someone",
    "@HOMEPAGE@": "https://example.invalid/ui18n",
    "@REPOSITORY@": "https://example.invalid/ui18n.git",
}

REQUIRED_FIELDS = ("spec_version", "identifier", "name", "abstract", "author", "license",
                   "version", "ksp_version", "download")

# 发布相关的默认值（CI 与 publish-ckan.ps1 都从这里取，避免两处各写一份）
REPO_SLUG = "DiaoDaiaChan/KSP_UniversalI18n"
DEFAULT_AUTHOR = "DiaoDaiaChan"
DEFAULT_LICENSE = "MIT"
DEFAULT_KSP_VERSION = "1.12"

ALLOWED_DEPENDS = ("Harmony2",)
REQUIRED_DEPENDS = ("Harmony2",)
FORBIDDEN_DEPENDS = ("ModuleManager",)

_VERSION_RE = re.compile(r"^\d+(?:\.\d+)*$")
_IDENTIFIER_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]*$")
_PLACEHOLDER_RE = re.compile(r"@[A-Z0-9_]+@")
_SHA256_RE = re.compile(r"^[0-9a-f]{64}$", re.I)
_SHA1_RE = re.compile(r"^[0-9a-f]{40}$", re.I)
_KSP_VERSION_RE = re.compile(r"^\d+(?:\.\d+){0,2}$")
_RELEASE_STATUS = ("stable", "testing", "development")


def load_netkan(path):
    """读一份 .netkan（或模板）。失败抛 ValueError，消息里带路径。"""
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            text = handle.read()
    except OSError as exc:
        raise ValueError("读不了 netkan: {0}（{1}）".format(path, exc))
    try:
        return json.loads(text)
    except ValueError as exc:
        raise ValueError("netkan 不是合法 JSON: {0}（{1}）".format(path, exc))


def render_template(text, values=None):
    """把模板占位符换成给定值（默认假值），返回渲染后的文本。"""
    table = dict(DUMMY_VALUES)
    if values:
        table.update(values)
    out = text
    for key, value in table.items():
        out = out.replace(key, value)
    return out


def release_values(version, sha256, download_url=None, license_name=None, author=None,
                   homepage=None, repository=None):
    # 模板占位符 -> 真实发布值（与 build-release.ps1 的替换同一套），CI 也用它渲染。
    slug = REPO_SLUG
    values = dict(DUMMY_VALUES)
    values["@VERSION@"] = version
    values["@KSP_VERSION@"] = DEFAULT_KSP_VERSION
    values["@SHA256@"] = sha256
    values["@DOWNLOAD_URL@"] = download_url or (
        "https://github.com/{0}/releases/download/v{1}/UI18n-{1}.zip".format(slug, version))
    values["@LICENSE@"] = license_name or DEFAULT_LICENSE
    values["@AUTHOR@"] = author or DEFAULT_AUTHOR
    values["@HOMEPAGE@"] = homepage or ("https://github.com/" + slug)
    values["@REPOSITORY@"] = repository or ("https://github.com/" + slug)
    return values


def render_release(version, sha256, template_path=None, out_path=None, **kwargs):
    # 渲染成可直接提交的 .netkan；给了 out_path 就写盘，返回渲染后的文本。
    template_path = template_path or DEFAULT_TEMPLATE
    with io.open(template_path, "r", encoding="utf-8-sig") as handle:
        text = handle.read()
    rendered = render_template(text, release_values(version, sha256, **kwargs))
    if out_path:
        parent = os.path.dirname(os.path.abspath(out_path))
        if parent and not os.path.isdir(parent):
            os.makedirs(parent)
        with io.open(out_path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(rendered)
    return rendered


def template_problems(text):
    """检查模板本身：占位符必须都在 KNOWN_PLACEHOLDERS 里，且每个都出现。"""
    problems = []
    found = set(_PLACEHOLDER_RE.findall(text))
    unknown = sorted(found - set(KNOWN_PLACEHOLDERS))
    if unknown:
        problems.append("模板里有未知占位符（build-release.ps1 不会替换它）: " + ", ".join(unknown))
    missing = [p for p in KNOWN_PLACEHOLDERS if p not in found]
    if missing:
        problems.append("模板里缺少占位符: " + ", ".join(missing))
    return problems


def _depends_names(data, problems):
    node = data.get("depends")
    names = []
    if node is None:
        return names
    if not isinstance(node, list):
        problems.append("depends 必须是数组（每条 {name: ...}）")
        return names
    for index, item in enumerate(node):
        if not isinstance(item, dict):
            problems.append("depends[{0}] 必须是对象".format(index))
            continue
        name = item.get("name")
        if not isinstance(name, str) or not name.strip():
            problems.append("depends[{0}] 缺 name".format(index))
            continue
        names.append(name.strip())
    return names


def _install_problems(data, problems):
    node = data.get("install")
    if node is None:
        problems.append("缺 install 段（zip 里的 GameData 要装到哪）")
        return
    if not isinstance(node, list) or not node:
        problems.append("install 必须是非空数组")
        return
    pairs = []
    for index, item in enumerate(node):
        if not isinstance(item, dict):
            problems.append("install[{0}] 必须是对象".format(index))
            continue
        source = item.get("file")
        target = item.get("install_to")
        if not isinstance(source, str) or not source.strip():
            problems.append("install[{0}] 缺 file".format(index))
            continue
        if not isinstance(target, str) or not target.strip():
            problems.append("install[{0}] 缺 install_to".format(index))
            continue
        pairs.append((source.strip().replace("\\", "/"), target.strip().replace("\\", "/")))
    if pairs and pairs != [("GameData", "GameData")]:
        problems.append("install 段应是 [GameData -> GameData]，实际 {0}".format(pairs))


def _zip_problems(zip_path, expected_version, problems):
    if not os.path.isfile(zip_path):
        problems.append("找不到 zip: {0}".format(zip_path))
        return
    try:
        with zipfile.ZipFile(zip_path) as bundle:
            names = bundle.namelist()
            if not any(n.replace("\\", "/").startswith("GameData/UI18n/") for n in names):
                problems.append("zip 里没有 GameData/UI18n/ 顶层目录（install 段会装错位置）")
            version_member = "GameData/UI18n/UI18n.version"
            if version_member in names:
                try:
                    payload = json.loads(bundle.read(version_member).decode("utf-8-sig"))
                    actual = "{0}.{1}.{2}".format(payload["VERSION"]["MAJOR"],
                                                 payload["VERSION"]["MINOR"],
                                                 payload["VERSION"]["PATCH"])
                    if expected_version and actual != expected_version:
                        problems.append("zip 内 UI18n.version 是 {0}，与期望 {1} 不一致".format(actual, expected_version))
                except Exception as exc:              # 解析失败也要报，不能静默
                    problems.append("zip 内 UI18n.version 读不出来: {0!r}".format(exc))
            else:
                problems.append("zip 里没有 {0}".format(version_member))
    except zipfile.BadZipFile as exc:
        problems.append("zip 打不开: {0!r}".format(exc))


def validate(data, expected_version=None, expected_sha256=None, zip_path=None,
             allow_depends=ALLOWED_DEPENDS, require_depends=REQUIRED_DEPENDS):
    """校验一份已解析的 netkan，返回问题清单（空 = 通过）。"""
    problems = []
    if not isinstance(data, dict):
        return ["netkan 顶层必须是对象"]

    text = json.dumps(data, ensure_ascii=False)
    leftovers = sorted(set(_PLACEHOLDER_RE.findall(text)))
    if leftovers:
        problems.append("还有没替换的占位符: " + ", ".join(leftovers))

    for field in REQUIRED_FIELDS:
        value = data.get(field)
        if not isinstance(value, str) or not value.strip():
            problems.append("缺必填字段（或为空）: " + field)

    identifier = data.get("identifier")
    if isinstance(identifier, str) and identifier and not _IDENTIFIER_RE.match(identifier):
        problems.append("identifier 只能用字母数字与 -_（CKAN 模块名规则）: " + identifier)

    version = data.get("version")
    if isinstance(version, str) and version and not _VERSION_RE.match(version):
        problems.append("version 形态不对（应形如 1.7.0，不要带 v 前缀或后缀）: " + version)
    if expected_version and isinstance(version, str) and version != expected_version:
        problems.append("version = {0}，与 -Version {1} 不一致（CKAN 会当成两个版本）".format(version, expected_version))

    ksp_version = data.get("ksp_version")
    if isinstance(ksp_version, str) and ksp_version and not _KSP_VERSION_RE.match(ksp_version):
        problems.append("ksp_version 形态不对（应形如 1.12 或 1.12.5）: " + ksp_version)
    for key in ("ksp_version_min", "ksp_version_max"):
        value = data.get(key)
        if isinstance(value, str) and value and not _KSP_VERSION_RE.match(value):
            problems.append("{0} 形态不对: {1}".format(key, value))

    license_name = data.get("license")
    if isinstance(license_name, str) and license_name and _PLACEHOLDER_RE.search(license_name):
        problems.append("license 还是占位符: " + license_name)

    release_status = data.get("release_status")
    if isinstance(release_status, str) and release_status and release_status not in _RELEASE_STATUS:
        problems.append("release_status 只能是 {0}，实际 {1}".format("/".join(_RELEASE_STATUS), release_status))

    download = data.get("download")
    if isinstance(download, str) and download:
        if not download.lower().startswith("https://"):
            problems.append("download 必须是 https URL: " + download)
        elif _PLACEHOLDER_RE.search(download):
            problems.append("download 还是占位符: " + download)

    digest = None
    for key in ("sha256", "sha1"):
        value = data.get(key)
        if isinstance(value, str) and value:
            digest = (key, value)
            break
    if digest is None:
        problems.append("缺 sha256（或 sha1）—— 没有哈希 CKAN 不认这份下载")
    else:
        key, value = digest
        pattern = _SHA256_RE if key == "sha256" else _SHA1_RE
        if not pattern.match(value):
            problems.append("{0} 不是合法的 {1} 位 hex: {2}".format(key, 64 if key == "sha256" else 40, value))
        elif key == "sha256" and expected_sha256 and value.lower() != expected_sha256.lower():
            problems.append("sha256 与期望值 {0} 不一致".format(expected_sha256))

    _install_problems(data, problems)

    names = _depends_names(data, problems)
    for name in names:
        if name in FORBIDDEN_DEPENDS:
            problems.append("depends 里不许出现 {0}（UI18n 不写 MM patch，装它是白装一个 mod）".format(name))
        elif name not in allow_depends:
            problems.append("depends 里有未登记的依赖 {0}（只许 {1}）".format(name, ", ".join(allow_depends)))
    for name in require_depends:
        if name not in names:
            problems.append("depends 里缺 {0}（运行期靠它打补丁）".format(name))

    for key in ("recommends", "suggests", "conflicts", "supports"):
        node = data.get(key)
        if node is None:
            continue
        if not isinstance(node, list):
            problems.append("{0} 必须是数组".format(key))
            continue
        for index, item in enumerate(node):
            if not isinstance(item, dict) or not isinstance(item.get("name"), str):
                problems.append("{0}[{1}] 必须是 {{name: ...}}".format(key, index))

    resources = data.get("resources")
    if resources is not None:
        if not isinstance(resources, dict):
            problems.append("resources 必须是对象")
        else:
            for key, value in resources.items():
                if not isinstance(value, str) or not value.strip():
                    problems.append("resources.{0} 必须是非空字符串".format(key))
                elif key in ("homepage", "repository") and not value.lower().startswith("http"):
                    problems.append("resources.{0} 应以 http(s) 开头".format(key))

    if zip_path:
        _zip_problems(zip_path, expected_version or (version if isinstance(version, str) else None), problems)

    return problems


def validate_file(path, expected_version=None, expected_sha256=None, zip_path=None):
    return validate(load_netkan(path), expected_version=expected_version,
                    expected_sha256=expected_sha256, zip_path=zip_path)


def validate_template(path=DEFAULT_TEMPLATE):
    """模板校验：占位符集合 + 用假值渲染后的字段校验。"""
    with open(path, "r", encoding="utf-8-sig") as handle:
        text = handle.read()
    problems = template_problems(text)
    rendered = render_template(text)
    leftovers = sorted(set(_PLACEHOLDER_RE.findall(rendered)))
    if leftovers:
        problems.append("渲染后仍有占位符残留: " + ", ".join(leftovers))
    try:
        data = json.loads(rendered)
    except ValueError as exc:
        problems.append("渲染后的模板不是合法 JSON: {0!r}".format(exc))
        return problems
    problems.extend(validate(data, expected_version=DUMMY_VALUES["@VERSION@"],
                             expected_sha256=DUMMY_VALUES["@SHA256@"]))
    return problems


def _raises(exc_type, func, *args, **kwargs):
    try:
        func(*args, **kwargs)
    except exc_type:
        return True
    except Exception:
        return False
    return False


def selftest():
    checks = []

    def check(name, ok, detail=""):
        checks.append((name, bool(ok), detail))

    def eq(name, got, want):
        checks.append((name, got == want, "got={0!r} want={1!r}".format(got, want)))

    good = {
        "spec_version": "v1.34",
        "identifier": "UI18n",
        "name": "UI18n",
        "abstract": "Runtime i18n layer for KSP mods",
        "author": "@someone",
        "license": "MIT",
        "release_status": "testing",
        "version": "1.7.0",
        "ksp_version": "1.12",
        "depends": [{"name": "Harmony2"}],
        "install": [{"file": "GameData", "install_to": "GameData"}],
        "resources": {"homepage": "https://example.invalid", "repository": "https://example.invalid.git"},
        "download": "https://example.invalid/UI18n-1.7.0.zip",
        "sha256": "a" * 64,
    }
    eq("正常 netkan 没有问题", validate(good, expected_version="1.7.0", expected_sha256="a" * 64), [])

    def broken(**changes):
        data = dict(good)
        for key, value in changes.items():
            if value is None:
                data.pop(key, None)
            else:
                data[key] = value
        return validate(data)

    def has(problems, needle):
        return any(needle in p for p in problems)

    check("缺 spec_version 报错", has(broken(spec_version=None), "spec_version"))
    check("缺 version 报错", has(broken(version=None), "version"))
    check("版本不一致报错", has(validate(dict(good, version="1.6.0"), expected_version="1.7.0"), "不一致"))
    check("version 带 v 前缀报错", has(broken(version="v1.7.0"), "version 形态"))
    check("ksp_version 形态报错", has(broken(ksp_version="1.12.x"), "ksp_version"))
    check("download 不是 https 报错", has(broken(download="http://example.invalid/x.zip"), "https"))
    check("sha256 长度不对报错", has(broken(sha256="abc"), "64 位 hex"))
    check("sha256 与期望不一致报错", has(validate(dict(good), expected_sha256="b" * 64), "不一致"))
    check("install 段错报错", has(broken(install=[{"file": "GameData/UI18n", "install_to": "GameData"}]), "install 段"))
    check("depends 混进 ModuleManager 报错", has(broken(depends=[{"name": "Harmony2"}, {"name": "ModuleManager"}]), "ModuleManager"))
    check("depends 缺 Harmony2 报错", has(broken(depends=[]), "缺 Harmony2"))
    check("depends 有未登记项报错", has(broken(depends=[{"name": "Harmony2"}, {"name": "ClickThroughBlocker"}]), "未登记的依赖"))
    check("depends 不是数组报错", has(broken(depends={"name": "Harmony2"}), "depends 必须是数组"))
    check("残留占位符报错", has(broken(abstract="@SOMETHING@"), "占位符"))
    check("release_status 取值报错", has(broken(release_status="beta"), "release_status"))
    check("resources 值形态报错", has(broken(resources={"homepage": 42}), "resources.homepage"))
    check("顶层不是对象报错", validate([1, 2]) != [])
    check("identifier 非法报错", has(broken(identifier="UI 18n"), "identifier"))

    if os.path.isfile(DEFAULT_TEMPLATE):
        eq("仓库模板通过校验", validate_template(DEFAULT_TEMPLATE), [])
    else:
        check("仓库模板存在", False, DEFAULT_TEMPLATE)

    check("模板未知占位符报错", template_problems('{"a": "@NOPE@"}') != [])
    check("模板缺占位符报错", template_problems("{}") != [])

    import tempfile
    import shutil
    tmpdir = tempfile.mkdtemp(prefix="ui18n-netkan-")
    try:
        zip_ok = os.path.join(tmpdir, "ok.zip")
        with zipfile.ZipFile(zip_ok, "w") as bundle:
            bundle.writestr("GameData/UI18n/UI18n.cfg", "Version = 1.7.0")
            bundle.writestr("GameData/UI18n/UI18n.version",
                            json.dumps({"VERSION": {"MAJOR": 1, "MINOR": 7, "PATCH": 0}}))
            bundle.writestr("GameData/UI18n/Packs/zh-cn/Foo.jsonl", "{}")
        eq("zip 与 netkan 一致时通过", validate(good, expected_version="1.7.0", zip_path=zip_ok), [])
        check("zip 内版本不一致报错",
              has(validate(good, expected_version="1.8.0", zip_path=zip_ok), "UI18n.version 是 1.7.0"))

        zip_bad = os.path.join(tmpdir, "bad.zip")
        with zipfile.ZipFile(zip_bad, "w") as bundle:
            bundle.writestr("UI18n/UI18n.cfg", "Version = 1.7.0")
        check("zip 顶层目录不对报错", has(validate(good, zip_path=zip_bad), "GameData/UI18n/"))

        check("load_netkan 读不了文件时抛 ValueError",
              _raises(ValueError, load_netkan, os.path.join(tmpdir, "nope.json")))

        render_path = os.path.join(tmpdir, "out", "UI18n.netkan")
        rendered = render_release("1.7.0", "b" * 64, out_path=render_path)
        eq("render_release 不留占位符", _PLACEHOLDER_RE.findall(rendered), [])
        written = load_netkan(render_path)
        eq("render_release 写盘的版本正确", written.get("version"), "1.7.0")
        eq("render_release 默认 download 指向本版本资产", written.get("download"),
           "https://github.com/" + REPO_SLUG + "/releases/download/v1.7.0/UI18n-1.7.0.zip")
        eq("render_release 的 depends 只有 Harmony2",
           [item.get("name") for item in written.get("depends", [])], ["Harmony2"])
        eq("render_release 结果通过字段校验",
           validate(written, expected_version="1.7.0", expected_sha256="b" * 64), [])
        render_release("2.0.0", "d" * 64, out_path=os.path.join(tmpdir, "x.netkan"),
                       download_url="https://example.invalid/UI18n-2.0.0.zip")
        eq("render_release 显式 download_url 生效",
           load_netkan(os.path.join(tmpdir, "x.netkan")).get("download"),
           "https://example.invalid/UI18n-2.0.0.zip")
        eq("--render 端到端 exit 0",
           main(["--render", os.path.join(tmpdir, "cli.netkan"), "--version", "1.7.0", "--sha256", "c" * 64]), 0)
        check("--render 缺 sha256 时直接报错退出",
              _raises(SystemExit, main, ["--render", os.path.join(tmpdir, "cli2.netkan"), "--version", "1.7.0"]))
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)

    bad_json = os.path.join(tmpdir, "not-json")
    failed = [row for row in checks if not row[1]]
    for name, ok, detail in checks:
        print(("  ok   " if ok else "  FAIL ") + name + ("" if ok else "  " + str(detail)))
    print("[netkan-selftest] {0} 项，失败 {1}".format(len(checks), len(failed)))
    return (failed, len(checks))


def main(argv=None):
    parser = argparse.ArgumentParser(prog="i18n_tools.netkan", description="CKAN netkan 字段校验器")
    parser.add_argument("netkan", nargs="?", help="要校验的 .netkan 文件")
    parser.add_argument("--template", nargs="?", const=DEFAULT_TEMPLATE,
                        help="校验模板本身（默认 src/UI18n.Runtime/ckan/UI18n.netkan.template）")
    parser.add_argument("--version", default=None, help="期望版本号（与 netkan.version 及 zip 内版本比对）")
    parser.add_argument("--sha256", default=None, help="期望 zip 的 sha256")
    parser.add_argument("--zip", default=None, help="发布 zip，用来核对 install 段与内部版本")
    parser.add_argument("--json", action="store_true", help="以 JSON 输出问题清单")
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--render", default=None,
                        help="渲染模板到指定路径（需要 --version 与 --sha256），然后校验渲染结果")
    parser.add_argument("--download-url", default=None, help="--render 用，默认按仓库 slug 拼 Release 资产地址")
    args = parser.parse_args(argv)

    if args.selftest:
        failures, _total = selftest()
        return 1 if failures else 0

    problems = []
    if args.render:
        if not args.version or not args.sha256:
            parser.error("--render 需要同时给 --version 与 --sha256")
        try:
            rendered = render_release(args.version, args.sha256, out_path=args.render,
                                      download_url=args.download_url)
            print("[netkan] 已渲染: {0}".format(args.render))
            data = json.loads(rendered)
        except (ValueError, OSError) as exc:
            rendered = ""
            problems = ["渲染失败: {0}".format(exc)]
            data = None
        if data is not None:
            problems = validate(data, expected_version=args.version,
                                 expected_sha256=args.sha256)
    elif args.template:
        if not os.path.isfile(args.template):
            problems = ["找不到模板: " + args.template]
        else:
            problems = validate_template(args.template)
            if problems:
                problems = ["模板 " + args.template + " 不合格"] + problems
    elif args.netkan:
        try:
            problems = validate_file(args.netkan, expected_version=args.version,
                                     expected_sha256=args.sha256, zip_path=args.zip)
        except ValueError as exc:
            problems = [str(exc)]
    else:
        parser.error("要么给一个 .netkan，要么用 --template / --render（或 --selftest）")

    if args.json:
        print(json.dumps({"ok": not problems, "problems": problems}, ensure_ascii=False, indent=2))
    elif problems:
        print("[netkan] 不合格 {0} 条：".format(len(problems)))
        for item in problems:
            print("  - " + item)
    else:
        print("[netkan] 通过")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
