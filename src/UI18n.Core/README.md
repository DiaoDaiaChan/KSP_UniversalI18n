# UI18n.Core

KSP 通用 i18n Mod 的核心库：**纯 BCL，零第三方依赖，零 UnityEngine / KSP / Harmony 引用**，
同时编译 `net472`（游戏内运行）与 `net10.0`（测试 / 工具 / 设计验证）。

## 命名空间

| 命名空间 | 类型 | 职责 |
| --- | --- | --- |
| `UI18n.Core.Hashing` | `Fnv1a` | FNV-1a 64，输入 UTF-8 字节（D11；`string.GetHashCode` 跨进程不稳定，不能做持久化键） |
| `UI18n.Core.Text` | `TextNormalizer` | 归一化五步（NFKC → CR/LF 统一 → 空白折叠 → trim；不折大小写、不统一引号，04 §4.1）；CJK 判定 |
| `UI18n.Core.Keys` | `TextKey` | 三层键字符串：ctx / site / global（D5，00 §4.1） |
| `UI18n.Core.Dict` | `DictEntry` `DictPack` `KeyLevel` | JSONL 字典、分桶查找、ctx → site → global 优先级、命中统计 |
| `UI18n.Core.Policy` | `LanguageGate` | 语言门禁（非中文一个 patch 都不装，D24/D28） |
| `UI18n.Core.Config` | `UI18nConfig` | 用户配置 POCO |

## 契约要点

- **键 = 16 位小写 hex**：`TextKey.Ctx(asm, type, member, ordinal, hash)` 等；哈希与 python 侧
  `tools/i18n_tools/fnv.py` 必须逐字节一致，否则字典全 miss。
- **归一化**：`TextNormalizer.Normalize` 的输出就是字典的 `n` 字段；输入已归一时返回原引用（零分配）。
- **查找**：`DictPack.TryGet(hash, source, out translation)`；同哈希多条目按 `KeyLevel` 升序消歧，
  源文用 Ordinal 比对（先 `s` 再 `n`）。译文 == 源文的条目为 keep：算 miss，单独计 `Keeps`。
- **失败只记不抛**：坏 JSONL 行计入 `ParseErrors`，`Normalize` 遇到非法 UTF-16 降级原文，
  所有公开 API 都不抛（唯一例外是 `Fnv1a.Hash(byte[], int, int)` 的越界参数，属编程错误）。
- **线程模型**：`DictPack` 非线程安全；装配期载入，命中路径只读。

## 构建

```
dotnet build UI18n.sln -c Release
```

`net472` 目标依赖本机 .NET Framework 4.7.2 参考程序集，csproj 里已配好
`FrameworkPathOverride`（本机无 net472 targeting pack）。csproj 里还有一个守卫 target
`UI18nCore_RejectGameDependencies`：一旦有人给 Core 加 UnityEngine / Assembly-CSharp / Harmony
引用或任何 PackageReference，编译直接失败。
