import hashlib
import io
import json
import os
import shutil
import sys
import zipfile

# 手工构造「发布物」fixture（不是真的出包：只为端到端验收 UpdateWorkflow）。
# 布局：<srv>/<scenario>/<channel>/{manifest.json, UI18n-1.2.0.zip}

HERE = os.path.dirname(os.path.abspath(__file__))
# tests/UpdateMatrix/fixtures -> tests/UpdateMatrix -> tests -> <repo>
REPO = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "root")
PACKS = os.path.join(REPO, "packs", "zh-cn")

ZIP_NAME = "UI18n-1.2.0.zip"
VERSION = "1.2.0"

def read(name):
    with open(os.path.join(PACKS, name), "rb") as f:
        return f.read()

# 三个真包（会被装进 Packs/zh-cn/），一个别的语言，两个绝不该被安装的文件
entries = []

def add(name, data):
    entries.append((name, data))

add("GameData/UI18n/Packs/zh-cn/Squad.jsonl", read("Squad.jsonl"))
add("GameData/UI18n/Packs/zh-cn/RealismOverhaul.jsonl", read("RealismOverhaul.jsonl"))
add("GameData/UI18n/Packs/zh-cn/NewMod.jsonl", b'{"h":"00000000000000aa","n":"Brand New","s":"Brand New","t":"\xe5\x85\xa8\xe6\x96\xb0","mod":"NewMod"}\n')
add("GameData/UI18n/Packs/en-us/Squad.jsonl", b'{"h":"00000000000000bb","n":"Brand New","s":"Brand New","t":"Brand New","mod":"Squad"}\n')
add("GameData/UI18n/Plugins/UI18n.Runtime.dll", b"NOT-A-REAL-DLL")
add("GameData/UI18n/Config.cfg", b"UI18n { Enabled = True }\n")

def build_zip(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in entries:
            info = zipfile.ZipInfo(name)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.date_time = (2026, 10, 25, 12, 0, 0)
            info.external_attr = 0o644 << 16
            z.writestr(info, data)
    with open(path, "rb") as f:
        return f.read()

def sha256(data):
    return hashlib.sha256(data).hexdigest().upper()

def manifest(version, zip_bytes, zip_sha, zip_len, evil=False):
    files = []
    for name, data in entries:
        files.append({"path": name, "bytes": len(data), "sha256": sha256(data)})
    if evil:
        files.append({"path": "GameData/UI18n/Packs/zh-cn/../../evil.jsonl", "bytes": 3, "sha256": sha256(b"bad")})
    return {
        "version": version,
        "builtAt": "2026-10-25T12:00:00+08:00",
        "kspVersion": "1.12.5",
        "language": "zh-cn",
        "zip": {"name": ZIP_NAME, "bytes": zip_len, "sha256": zip_sha},
        "files": files,
    }

def write(dirpath, name, data):
    os.makedirs(dirpath, exist_ok=True)
    with open(os.path.join(dirpath, name), "wb") as f:
        f.write(data)

if os.path.isdir(OUT):
    shutil.rmtree(OUT)

# --- 基准 zip ---
tmp_zip = os.path.join(OUT, "_stable_zip", ZIP_NAME)
zip_bytes = build_zip(tmp_zip)
zip_sha = sha256(zip_bytes)
zip_len = len(zip_bytes)

ok_manifest = json.dumps(manifest(VERSION, zip_bytes, zip_sha, zip_len), indent=2).encode("utf-8")
same_manifest = json.dumps(manifest("1.1.0", zip_bytes, zip_sha, zip_len), indent=2).encode("utf-8")

# ok：正常更新
write(os.path.join(OUT, "ok", "stable"), "manifest.json", ok_manifest)
write(os.path.join(OUT, "ok", "stable"), ZIP_NAME, zip_bytes)
# samever：远端版本 == 本地 1.1.0 → 不下载
write(os.path.join(OUT, "samever", "stable"), "manifest.json", same_manifest)
write(os.path.join(OUT, "samever", "stable"), ZIP_NAME, zip_bytes)

# badsha：同长度但内容被改一字节 → 只有 sha256 会不符
tampered = bytearray(zip_bytes)
tampered[len(tampered) // 2] ^= 0xFF
tampered = bytes(tampered)
write(os.path.join(OUT, "badsha", "stable"), "manifest.json", ok_manifest)
write(os.path.join(OUT, "badsha", "stable"), ZIP_NAME, tampered)

# badbytes：清单写 bytes+1 → 尺寸不符（sha 是对的先过 size 再 sha？Verification 先 size）
bad_bytes_manifest = json.loads(ok_manifest.decode("utf-8"))
bad_bytes_manifest["zip"]["bytes"] = zip_len + 1
bad_bytes_manifest["zip"]["sha256"] = sha256(tampered)  # 故意配「被改过那份」的 sha
write(os.path.join(OUT, "badbytes", "stable"), "manifest.json", json.dumps(bad_bytes_manifest, indent=2).encode("utf-8"))
write(os.path.join(OUT, "badbytes", "stable"), ZIP_NAME, tampered)

# slow：正常内容，服务端在 /slow/ 上 sleep 6 秒
write(os.path.join(OUT, "slow", "stable"), "manifest.json", ok_manifest)
write(os.path.join(OUT, "slow", "stable"), ZIP_NAME, zip_bytes)

# zipslip：清单里塞一条带 .. 的路径 → 整份清单必须被拒
evil = manifest(VERSION, zip_bytes, zip_sha, zip_len, evil=True)
write(os.path.join(OUT, "zipslip", "stable"), "manifest.json", json.dumps(evil, indent=2).encode("utf-8"))
write(os.path.join(OUT, "zipslip", "stable"), ZIP_NAME, zip_bytes)

print("fixtures: %s" % os.path.abspath(OUT))
print("zip: %s bytes=%d sha256=%s" % (ZIP_NAME, zip_len, zip_sha))
print("entries=%d" % len(entries))
for d in sorted(os.listdir(OUT)):
    if d.startswith("_"):
        continue
    p = os.path.join(OUT, d, "stable")
    print("  %-8s manifest=%d B" % (d, os.path.getsize(os.path.join(p, "manifest.json"))))
