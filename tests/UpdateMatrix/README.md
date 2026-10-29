# 更新矩阵（UpdateMatrix）

离线端到端验收 `src/UI18n.Runtime/Update/*` + `src/UI18n.Core/Update/*`：**直接编译真实生产源码**（它们刻意写成 Unity-free），不起游戏、不联网。

## 这个工程在测什么

14 个用例，每个用例都在独立沙箱里跑一遍 `UpdateWorkflow.Run`，然后比对「装前 / 装后」整棵 `Packs` 目录的 sha256 快照：

| 用例 | 场景 | 关键期望 |
|---|---|---|
| 01 | 更新功能关闭（空 base url） | 一次网络请求都不发，`disabled` |
| 02 | `checkForUpdates=false` | 同上 |
| 03 | 正常更新 | `update: installed 3 file(s), version 1.2.0`，只覆盖包文件、`Keep.jsonl` 原样保留 |
| 04 | 远端 == 本地 | `up-to-date`，树不变 |
| 05 | zip 的 sha256 不符 | `failed (verify)`，树不变 |
| 06 | zip 尺寸不符 | `failed (verify)`，树不变 |
| 07 | 远端 404 | `failed (http)`，树不变 |
| 08 | 服务端卡死（`/slow/` sleep 6s） | 超时后 `failed (timeout)`，树不变 |
| 09 | 清单里塞 `../` 路径 | 整份清单被拒（zip-slip），树不变 |
| 10 | `autoDownload=false` | 只报 `update-available`，不下载 |
| 11 | 未到检查间隔 | 不发请求（`not-due`） |
| 12 | 请求不阻塞主线程 | `RunThread` 立即返回 |
| 13 | **装到一半失败**（用同名目录挡住第 3 个包文件） | `update: install failed (commit-failed …) -> N file(s) were restored, packs are exactly as before`，`TreeUnchanged`，无 `.ui18n-new`/`.ui18n-bak` 残留 |
| 14 | **远端版本比本地旧**（本地 1.3.0 / 远端 1.1.0） | `update: up to date (local 1.3.0, remote 1.1.0)`，不下载，树不变 |

用例 13 是 `PackInstallTransaction`（两段式安装事务）的执行证据：所有内容先落 `.ui18n-new`，逐条替换时旧文件留 `.ui18n-bak`，回读校验通过才删备份；任何一步失败按逆序回滚。

## 怎么跑

```bash
# 1. 生成 fixture 发布物（读仓库里真实的两份包，造 6 个场景目录）
python tests/UpdateMatrix/fixtures/makefixtures.py

# 2. 起本地发布源（127.0.0.1:8099；路径含 /slow/ 时故意 sleep 6 秒）
python tests/UpdateMatrix/fixtures/serve.py

# 3. 另一个终端跑矩阵
dotnet run --project tests/UpdateMatrix -- matrix _scratch/updwork http://127.0.0.1:8099 tests/UpdateMatrix/fixtures/root
```

期望最后一行 `checks=64 failures=0`，退出码 0。矩阵只打印摘要，逐用例完整日志在 `_scratch/updwork/` 下（每个用例一个子目录）。

只想验一份现成的 zip + manifest：

```bash
dotnet run --project tests/UpdateMatrix -- zipcheck <zip> <manifest.json>
```

## 约定

- `fixtures/root/` 是生成物（已被 `.gitignore` 忽略），删掉重跑 `makefixtures.py` 即可重建。
- `makefixtures.py` 不依赖机器路径：仓库根按脚本位置上推（`tests/UpdateMatrix/fixtures` → 上 3 层）。
- 本工程**不在** `UI18n.sln` 里，`dotnet build` 整库时不会连带构建；需要时单独 `dotnet build tests/UpdateMatrix`。
- 加用例的姿势：在 `Matrix.cs` 的 `Run` 里加一行 `RunXxxCase(workRoot, baseUrl);`，用 `PrepareSandbox` 造沙箱、`Execute`/`ExecuteIn` 跑生产代码、`Expect` 断言，最后靠 `TreeUnchanged` 兜住「不该动的地方一个字节都没动」。
