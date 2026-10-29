# tests/runtime-compile —— Runtime 的影子语义编译

这个目录解决一个具体问题：**`src/UI18n.Runtime`（29 个文件、约 8,200 行）在没有 KSP 的机器上编不了。**

真工程 `src/UI18n.Runtime/UI18n.Runtime.csproj` 是 `net472`，并且 `<Reference>` 直接指向真实 KSP 安装里的
`KSP_x64_Data/Managed/*.dll`（`Assembly-CSharp.dll`、`UnityEngine*.dll` 等，有版权、不能进仓库）。
于是过去只有两条路：

* 在装了 KSP 的机器上 `csc`/MSBuild 编 —— 大部分机器上没有；
* 用 `csc -t:library` 做「纯语法」校验 —— 只查括号/分号/关键字，**查不到成员名写错（CS0117）、
  方法不存在（CS1061）、参数类型不匹配（CS1503）**。

第二种给了很危险的错觉：8,000 行 Runtime 代码长期「语法没问题」，而实际上任何一个 `GUI.Foo(...)`
打错一个字母都不会被发现，直到真机编译或运行时报错。本轮（M5-d）补上第三种：

把 Unity / KSP / Harmony 里**我们真正用到的那部分 API 形状**写成最小桩（`stubs/`），
再用 `RuntimeCompileCheck.csproj` 把 `src/UI18n.Runtime` 的**真实源码**和这些桩一起编 ——
于是成员名、参数类型、返回类型、调用关系、`typeof()` 引用、`#if` 分支可达性，全部由编译器检查。

## 证明什么，不证明什么

| 它证明 | 它不证明 |
| --- | --- |
| Runtime 源码内部自洽（成员名、参数类型、返回值用法、调用关系） | 桩里的签名与真实 Unity/KSP/Harmony 一致 |
| 我们 `typeof(T)` / `AccessTools.Method` 里写的类型名在桩里存在 | 真实运行时该类型/方法真的存在（`TypeByName` 的字符串是运行时才解析的） |
| 新增的每个 .cs 都真的参与了编译（不是被 glob 漏掉） | 反射目标（`GUI.DoWindow` 之类靠字符串描述的目标）真的可调 |
| 桩不会混进发布 DLL（第 7 项断言） | 真机行为（字体回退、`#if NET472` 的 TLS 路径等） |

**桩与真实 API 不符时，以真机编译为准。** 这个目录的定位是「抓我们自己的笔误」，
不是「替代真机编译」。加桩的纪律因此是：**只加 Runtime 真的调用的成员**，不抄整份 Unity API ——
抄得越多，越容易在桩里写出真实 API 并不存在的东西，反而制造假绿。

## 两个 TFM 的关系

| 机器 | 编什么 | 谁是权威 |
| --- | --- | --- |
| Windows + 装了 .NET Framework 4.7.2 参考程序集 | `net472` 与 `net10.0` 都编 | `net472`（与真实 Runtime 同 TFM） |
| CI（ubuntu-latest，没有 .NET Framework 参考程序集） | 只编 `net10.0` | 只能证明 net10.0 下自洽 |

非 Windows 上 `src/UI18n.Runtime/Update/UpdateHttp.cs` 的 `#if NET472` 块（TLS 1.2 兜底）不参与编译，
其余代码全量覆盖。TFM 选择顺序：`--tfm` 参数 > 环境变量 `UI18N_SHADOW_TFM` >
（Windows 且参考程序集目录存在 → `net472`，否则 `net10.0`）。

## 跑法

    # 机器检查（7 项：覆盖集合 / 0 错误 / 反向对照 / 桩不许进发布工程）
    python tests/runtime-compile/test-shadow-compile.py
    python tests/runtime-compile/test-shadow-compile.py --quiet --tfm net10.0

    # 只想编译一次
    dotnet build tests/runtime-compile/RuntimeCompileCheck.csproj -f net472 -v q

    # 想确认 Compile 项到底包含了哪些文件
    dotnet msbuild tests/runtime-compile/RuntimeCompileCheck.csproj -p:TargetFramework=net472 -getItem:Compile -nologo

退出码：`0` 全过 / `1` 有失败 / `2` SKIP（本机没有 dotnet）。

接线：`tools/run-tests.ps1` 的第 4c 步、`.github/workflows/i18n.yml` 的 `code` job
（CI 里把 SKIP 视作异常，因为那一步之前刚用 dotnet 编过单测）。

## 文件

| 文件 | 作用 |
| --- | --- |
| `RuntimeCompileCheck.csproj` | 影子工程：`stubs/**/*.cs` + `../../src/UI18n.Runtime/**/*.cs`（排除 obj/bin）+ 引用 UI18n.Core |
| `stubs/UnityEngine.cs` | UnityEngine 侧：Object/Component/Behaviour/GameObject、Rect/Vector2/Color/Color32、Font、Texture2D、AssetBundle、Resources、Application、Debug/Mathf/Time/Screen、GUIStyle/GUIContent/GUISkin/GUI/GUILayout/Event |
| `stubs/UnityUiAndTmp.cs` | `UnityEngine.UI.Text`、`TMPro.TMP_Text` / `TMP_FontAsset` / `TextMeshProUGUI` |
| `stubs/KspApi.cs` | 全局命名空间：`ConfigNode`/`HighLogic`/`FlightGlobals`/`Vessel`/`ScreenMessages`/`KSPAddon`/`Versioning`/`KSPUtil`；`KSP.UI.Screens.ApplicationLauncher` |
| `stubs/HarmonyApi.cs` | `HarmonyLib`：Harmony / HarmonyMethod / HarmonyPatch 系列特性 / AccessTools / CodeInstruction |
| `test-shadow-compile.py` | 上面那 7 项检查（含「故意写错必须编红」的反向对照） |

## 已知存疑的桩（下次有真机时优先核对）

* `ConfigNode.nodes` 写成 `List<ConfigNode>`、`ConfigNode.values` 写成 `string[]`：
  Runtime 实际**不读这两个字段**（`grep -n '\.nodes\|\.values' src/UI18n.Runtime` 无命中），
  它们只是为了让桩看起来完整；真机核对前不要用它们写新代码。
* `ScreenMessages.PostMessage` 的六参数重载是照 `src/UI18n.Runtime/Patches/PatchRegistry.cs`
  里实际传的参数拟的（ScreenMessage / string / ScreenMessagesText / float / ScreenMessageStyle / Color）。
* `KSPAddon` 特性与 `ApplicationLauncher.AddModApplication` 的形参顺序按 KSP 1.12 的常见签名写，
  没有在真机上核对过。
