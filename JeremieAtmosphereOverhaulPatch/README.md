# Atmosphere Overhaul Patch（大气层重构兼容补丁）

给 [Atmosphere Overhaul](https://steamcommunity.com/sharedfiles/filedetails/?id=3830470974) 做第三方温控 Mod 兼容层的**框架**。

- 仅支持 **RimWorld 1.7**（`About/About.xml` 只有 1.7，`LoadFolders.xml` 只有 `<v1.7>`）。
- 软依赖：目标 Mod 全都不存在、只存在一个、或全部存在，都能正常启动。
- 可回退：每个目标一个独立开关；AO 日后自带兼容时，关掉对应开关并重启即可。

## 当前覆盖

| 目标 | packageId | 状态 |
| --- | --- | --- |
| Good Climate | `GOS.GoodClimate` | **已实现**：真空关阀 → AO 透气面限速归零 |
| Vanilla Temperature Expanded | `VanillaExpanded.Temperature` | 诊断（预留行为补丁） |
| Centralized Climate Control (Continued) | `Mlie.CentralizedClimateControl` | 检测（上游未声明 1.7，通常不加载） |
| 超维科技 OuterrealmTech | `Jeremie.Fully.Automatic.OmniCrafter` | 诊断（预留持续源接入） |

## 目录结构

```
JeremieAtmosphereOverhaulPatch/
├─ About/About.xml                 # 元数据、依赖、loadAfter
├─ LoadFolders.xml                 # 仅 v1.7
├─ Patches/README.md               # XML 补丁约定（默认不用 XML）
├─ Languages/
│  ├─ ChineseSimplified (简体中文)/Keyed/JeremieAOPatch.xml
│  └─ English/Keyed/JeremieAOPatch.xml
└─ Source/JeremieAtmosphereOverhaulPatch/
   ├─ JeremieAtmosphereOverhaulPatch.csproj
   ├─ AssemblyInfo.cs (Properties/)
   ├─ AOPatchMod.cs                # Mod 入口 + 启动引导
   ├─ AOPatchSettings.cs           # 持久化开关
   ├─ Api/
   │  ├─ ReflectionCache.cs        # 跨程序集反射缓存
   │  └─ AtmosphereApi.cs          # AO AtmosphereInterface 的反射封装
   ├─ Compat/
   │  ├─ CompatState.cs
   │  ├─ CompatModule.cs           # 模块基类：开关/存在性/安装/异常隔离
   │  ├─ CompatRegistry.cs         # 模块注册、一键重装
   │  ├─ GoodClimateCompat.cs
   │  ├─ VanillaTemperatureExpandedCompat.cs
   │  ├─ CentralizedClimateControlCompat.cs
   │  └─ OmniCrafterCompat.cs
   └─ Util/PatchLog.cs
```

## 五条设计约束如何落实

1. **只支持 1.7**：`supportedVersions` 与 `LoadFolders` 都只列 1.7；源码不写 1.6 分支。
2. **多目标**：`CompatRegistry` 里每个目标一个 `CompatModule` 子类，互不依赖。
3. **任意子集可用**：`CompatModule.Apply` 先查 `ModLister.GetActiveModWithIdentifier(packageId, true)`；未安装直接 `NotInstalled`，不做任何反射。所有跨 Mod 访问走 `ReflectionCache`，**不静态引用任何目标 DLL**。
4. **快速禁用/回退**：
   - 设置界面每个模块一个开关（持久化到 `ModSettings`）；
   - 「立即重装补丁」按钮可 `UnpatchAll` 后按当前开关重装；
   - 总开关一键关闭全部；
   - 删除/停用本 Mod 也不会破坏存档（只装 Harmony 补丁，不写 Def、不写存档字段）。
5. **i18n + source + about**：见目录结构；键值中英各一份。

## 构建

依赖：.NET Framework 4.8.1 targeting pack（或可用的 MSBuild/Roslyn）+ RimWorld 1.7 根目录。

```bat
cd Source\JeremieAtmosphereOverhaulPatch
dotnet build -c Release
```

输出固定到 Mod 根目录的 `Assemblies/JeremieAtmosphereOverhaulPatch.dll`
（csproj 里 `ModAssembliesDir` 默认 `..\..\Assemblies\`）。

如果游戏不在默认位置，可覆盖：

```bat
dotnet build -c Release -p:RimWorldDir="D:\SteamLibrary\steamapps\common\RimWorld\"
```

`0Harmony.dll` 只需编译期引用（`Private=False`），运行时由 Harmony Mod 提供。

## 新增一个目标 Mod 的步骤

1. 在 `Compat/` 新建 `XxxCompat : CompatModule`，实现 `PackageId` / `LabelKey` / `HintKey` / `ReadSetting` / `WriteSetting` / `TryApply`；
2. `TryApply` 里用 `ReflectionCache.Type(...)` + `ReflectionCache.Method(...)` 取目标成员，找不到就 `Detail = "..."` 并 `return false`（Skipped）；
3. 在 `AOPatchSettings` 加一个 `bool` 字段并在 `ExposeData` 里 `Scribe_Values.Look`；
4. 在 `CompatRegistry.Initialize` 里 `Modules.Add(new XxxCompat())`；
5. 在 `Languages/*/Keyed/JeremieAOPatch.xml` 加 `AOPatch_Module_Xxx` 与 `...Desc`；
6. 编译、进游戏，看 Player.log 里的 `[AO Patch]` 汇总行。

## 已实现模块做了什么

### Good Climate 真空联动

Good Climate 的「强力换热」在检测到 `Room.Vacuum > 0` 时会 `DoFlick()` 关闭原版
`CompFlickable`，并弹出「气压骤降」信件。但 AO 的透气面只认自己的
`CompAirVent.speedLimit`，对 Flick 信号只 `MarkDirty`，所以阀关了、AO 仍在漏气。

本模块在 `GOS.GoodClimate.GC_MapComponent.MapComponentTick` 之后补一刀：
当 GC 判定真空并保持强力模式时，把同一建筑上的 `AtmosphereOverhaul.Buildings.CompAirVent`
的 `SpeedLimit` 设为 `0`，让 AO 的透气面真正关闭。

## 已知边界

- AO 的 `AtmosphereInterface` 全部方法都用反射调用；AO 改签名时会降级为空操作并在日志里提示，不会崩游戏。
- AO 地图上原版 `GenTemperature.EqualizeTemperaturesThroughBuilding` 被 AO 禁用，
  因此「普通模式」通风在 AO 下几乎无效果——这是 AO 的设计，不是本 Mod 的 bug。
- `Room.Vacuum` 是原版概念，与 AO 自管气压场不一定同步；本模块只把它当作 GC 的触发条件，不反过来信它。
- 房间超过 3000 格时 AO 的写钩子会静默忽略，任何依赖 `Room.Temperature` 写入的方案都会失效。

## 许可

仅用于个人 Mod 兼容，未包含任何目标 Mod 的代码或资源。
