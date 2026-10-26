# ConfigCache 文本缺口

> 自动生成，勿手改：`python -m i18n_tools.harvest_cfgcache --gamedata <GameData>`

| 项 | 值 |
| --- | --- |
| 输入 | `D:/Kerbal Space Program_newmod/GameData/ModuleManager.ConfigCache` |
| 大小 | 27,734,643 B |
| 修改时间 | 2026-10-26 13:25:19 |
| 键值行（四个字段） | 14,526 |
| 唯一文本 | 6,000 |
| 已被字典覆盖 | 3,395 |
| **本次要翻（缺）** | **2,605** |
| 缺失总字符 | 355,139 |

## 1. 赋值分布（去重前）

| 字段 | 赋值数 |
| --- | --- |
| `title` | 7,182 |
| `description` | 3,871 |
| `displayName` | 1,667 |
| `manufacturer` | 1,832 |

## 2. 缺失按字段

| 字段 | 条数 |
| --- | --- |
| `description` | 1,324 |
| `title` | 1,171 |
| `displayName` | 69 |
| `manufacturer` | 41 |

## 3. 缺失按长度

| 长度 | 条数 |
| --- | --- |
| ≤40 | 1,240 |
| 41-100 | 304 |
| 101-300 | 660 |
| >300 | 401 |

## 4. 跳过明细

| 原因 | 次数 |
| --- | --- |
| `already-cjk` | 3,997 |
| `keyref` | 7 |

## 5. `keyref` 跳过（纯本地化 key 引用）

本次命中 **7** 次，全部跳过、不进候选清单。

判据：值归一化后整串匹配 `^#[A-Za-z0-9_]+$`（例如 `#LOC_CRP_X_Title`、
`#autoLOC_500100`、`#Sol_Site_esrange_desc`）。这类原文在运行时会被 KSP `Localizer`
解析成**别的**字符串，按 key 原文建字典永远命中不了，还会白占翻译量。
整串匹配意味着「#」开头但含空格/中文的真正文不受影响（它们照常按正常规则判）。

被跳过的样例（最多 5 条）：

- `#LOC_CRP_LithiumHydride_Title`
- `#LOC_CRP_LithiumDeuteride_Title`
- `#LOC_CRP_Nitratine_Title`
- `#LOC_CRP_Magnesium_DisplayName`
- `#LOC_CRP_Potassium_DisplayName`

## 6. 为什么这些字符串以前进不了池

两条独立的原因，缺一不可：

1. `harvest.py` 只收**原始 cfg** 的文本字段，且明确规定补丁写法（`@title` / `@description`）不算命中
   （依据见 `tools/i18n_tools/harvest.py:258-265`）。可是运行时字符串是 MM 打完补丁之后的合成结果，
   于是 RO / RP-1 这类整合包里，池子里的原文与玩家看到的文本对不上 hash。
2. 采集通道（`capture_ingest.py:121 MAX_LEN = 300`、C# `CaptureOptions.MaxTextLength = 400`）把长文本当噪声丢掉，
   而零件 description 长度的 p90 是 204、p99 是 577 字符 —— 最需要翻的那批正好落在上限外面。

ModuleManager.ConfigCache 是 MM 打完所有补丁后写下的完整 ConfigNode 树，
里面这几个文本字段**就是游戏真正读到的字符串**，所以它才是本 mod 该用的正文来源。

## 7. 复现

```
cd tools
python -m i18n_tools.harvest_cfgcache --gamedata "D:/Kerbal Space Program_newmod/GameData"
```

