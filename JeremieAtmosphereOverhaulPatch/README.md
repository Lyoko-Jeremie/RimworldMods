# Atmosphere Overhaul Patch（大气层重构兼容补丁）

给 [Atmosphere Overhaul](https://steamcommunity.com/sharedfiles/filedetails/?id=3830470974)（下称 AO）与几个温控 Mod 之间补一层适配。只支持 RimWorld **1.7**。

设计目标：

1. 只加载 1.7；
2. 每个目标 Mod 一个独立兼容 DLL，互不依赖；
3. 目标 Mod 全部不存在、或只存在其中几个，都要能正常工作；
4. AO 以后自带某个 Mod 的兼容时，能一键让路或整目录回退；
5. 结构、i18n、构建方式与「超维科技」主 Mod 保持一致。

## 架构（与 AO 对 Dubs Bad Hygiene 的做法一致）

```
JeremieAtmosphereOverhaulPatch/
├─ About/About.xml                 # 只有 1.7；loadAfter 四个目标
├─ LoadFolders.xml                 # <v1.7> 根目录 + 四条 IfModActive 条件目录
├─ Assemblies/                     # 宿主 DLL（注册表 / 设置 / 公共 API）
├─ Patches/                        # 纯 XML 补丁（目标 Def 不存在时整段跳过）
├─ Languages/{ChineseSimplified,English}/Keyed/
├─ 1.7/Compat/<X>/Assemblies/      # 每个目标一个独立 DLL，条件加载
└─ Source/
   ├─ JeremieAtmosphereOverhaulPatch/          # 宿主：Mod 入口、设置、Api、Util、Compat 基类
   ├─ Compat.GoodClimate/
   ├─ Compat.VanillaTemperatureExpanded/
   ├─ Compat.CentralizedClimateControl/
   └─ Compat.OmniCrafter/
```

**宿主不引用任何兼容 DLL，兼容 DLL 也不静态引用目标 Mod。** 启动时
`CompatRegistry` 扫描已加载程序集，找到 `ICompatModule` 实现后按 `packageId` 配对。
目标 Mod 不在时条件目录不会被加载，对应 DLL 自然不存在，界面显示「未安装」，一切照常。
所有对 AO 与目标 Mod 的调用都走 `ReflectionCache`，签名变化时降级为 `Skipped` 并写日志。

## 当前覆盖

| 目标 | packageId | 条件目录 | 状态 |
| --- | --- | --- | --- |
| Good Climate | `GOS.GoodClimate` | `1.7/Compat/GoodClimate` | **已实现**：真空关阀 → AO 透气面限速归零，真空解除后恢复原值 |
| Vanilla Temperature Expanded | `VanillaExpanded.Temperature` | `1.7/Compat/VanillaTemperatureExpanded` | **已实现**：接管 4 个温控类，保留管道效率与资源消耗；附 Def 扩展 |
| Centralized Climate Control (Continued) | `Mlie.CentralizedClimateControl` | `1.7/Compat/CentralizedClimateControl` | **已实现**：风口真空判定改读 AO 气压；可选风口局部化 |
| 超维科技 OuterrealmTech | `Jeremie.Fully.Automatic.OmniCrafter` | `1.7/Compat/OmniCrafter` | **已实现**：生物圈 / 穹顶强制稳定 AO 物理场 |

> CCC 的 `About.xml` 只声明到 1.6，但 `ModContentPack.InitLoadFolders` 会回退到
> 「不超过当前版本的最大 `<vX.Y>`」，所以 1.7 下实际加载的是 `1.6/` 目录，建筑可正常建造。

## 五条设计约束如何落实

1. **只支持 1.7**：`supportedVersions` 与 `LoadFolders` 都只列 1.7；源码不写 1.6 分支。
2. **多目标**：每个目标一个独立项目 + 独立 DLL + 独立条件目录，互不依赖。
3. **任意子集可用**：目标不存在 → 条件目录不加载 → `Instance == null` → `NotInstalled`；
   目标存在但成员缺失 → `TryApply` 返回 false → `Skipped`，`Detail` 写明缺什么；
   缺 AO API → `Required` 闸门 → `Skipped`（缺哪个能力也写在 `Detail`）。
4. **快速禁用/回退**（三级）：
   - 设置界面每个模块一个开关，「立即重装补丁」会 `UnpatchAll` 后按当前开关重装；
   - 注释掉 `/LoadFolders.xml` 中对应 `<li>`：该兼容 DLL 不再被加载（下次启动生效）；
   - 直接删除 `1.7/Compat/<X>/` 目录：等价于从未存在过。
   另外，检测到 AO 已内置某个 Mod 的兼容时模块会自动 `Skipped`，不需要玩家操作。
5. **i18n + source + about**：中英 `Keyed` 各一份；每个项目都有独立 csproj。

## 构建

依赖 .NET Framework 4.8.1 targeting pack + RimWorld 1.7 根目录。

```bat
:: 1) 先构建宿主（兼容项目要引用它的 DLL）
dotnet build Source\JeremieAtmosphereOverhaulPatch\JeremieAtmosphereOverhaulPatch.csproj -c Release

:: 2) 再构建四个兼容项目（输出到 1.7\Compat\<X>\Assemblies\）
for /d %d in (Source\Compat.*) do dotnet build %d\*.csproj -c Release
```

输出位置由 csproj 固定：

- 宿主 → `Assemblies/JeremieAtmosphereOverhaulPatch.dll`；
- 兼容 → `1.7/Compat/<X>/Assemblies/`。

游戏不在默认位置时：

```bat
dotnet build -c Release -p:RimWorldDir="D:\SteamLibrary\steamapps\common\RimWorld\"
```

`0Harmony.dll` 与宿主 DLL 只做编译期引用（`Private=False`），运行时由 Harmony Mod 与 `Assemblies/` 提供。

> 改过宿主公共 API 后必须先重编宿主，再编兼容项目，否则兼容项目会引用旧 DLL。

## 新增一个目标 Mod 的步骤

1. `LoadFolders.xml` 加一行：`<li IfModActive="Some.PackageId">1.7/Compat/SomeName</li>`；
2. 新建 `Source/Compat.SomeName/SomeNameCompat.cs`：`public sealed class SomeNameCompat : CompatModuleBase`，
   实现 `PackageId` / `LabelKey` / `HintKey` / `ReadSetting` / `WriteSetting` / `TryApply`，
   需要 AO 能力时覆写 `Required`；
3. 复制任一 `Compat.*.csproj`，改 `ProjectGuid`、`AssemblyName`、`OutputPath`；
4. `AOPatchSettings` 加 `bool` 字段并在 `ExposeData` 里 `Scribe_Values.Look`；
5. `CompatRegistry.BuildInfos()` 加一条 `Info(...)`，绑定该设置字段；
6. 中英 `Keyed` 加 `AOPatch_Module_SomeName` / `...Desc` / `...Hint`；
7. 编译、进游戏，看 `Player.log` 里的 `[AO Patch]` 汇总行。

## 四个模块做了什么

### Good Climate 真空联动

Good Climate 的「强力换热」在检测到 `Room.Vacuum > 0` 时会 `DoFlick()` 关闭原版
`CompFlickable`，并弹出「气压骤降」信件。但 AO 的透气面只认自己的
`CompAirVent.speedLimit`，对 Flick 信号只 `MarkDirty`，所以阀关了、AO 仍在漏气。

本模块在 `GOS.GoodClimate.GC_MapComponent.MapComponentTick` 之后补一刀：
当 GC 判定真空并保持强力模式时，把同一建筑上的 `AtmosphereOverhaul.Buildings.CompAirVent`
的 `SpeedLimit` 设为 `0`；真空解除后恢复保存的原限速。

### Vanilla Temperature Expanded

AO 的 `FacilityCompInjection` 会给所有继承 `Building_TempControl` 的 Def 注入
`CompTempControlAtmosphere`，`TempControlSubclassPatches` 又会给重写了 `TickRare` 的子类打 Prefix，
所以 VTE 的四个温控类在 AO 下会被**整体接管**。问题有三处：

- `Building_AcUnit` 是双向控温（按目标温度与出风格温度比较决定冷热），
  而 AO 只按 `energyPerSecond` 正负判断，可能永远只加热；
- AO 固定「South = 冷侧、North = 热侧」，VTE 的出风方向由
  `TemperatureOutputPositionModExtension.offsetNorth` 决定，方向可能错位；
- VTE 的 `CompResourceTrader.ResourceOn` 与 `AcPipeNet.Efficiency`（`PowerOutput * (2 - Efficiency)`）
  在 AO 接管后完全丢失。

本模块：

1. 在 AO 的 `CompTempControlAtmosphere.Run` 之前拦截：目标 Def 的 `thingClass` 属于 VTE 四类就返回 false，
   不让 AO 双写；
2. 给四个类的 `TickRare` 打 `Priority.First` 的 Prefix，复刻 VTE 逻辑并改写为
   `AtmosphereApi.AddTemperatureDelta` 逐格输出，同时保留资源开关、管道效率与高/低档功耗；
3. `Patches/VteFacilities.xml` 给风管/风阀加 `airLeakFactor=0`，给温控设备补耐压/抗风参数。

### Centralized Climate Control

CCC 的 `Building_AirVent.TickRare` 把温差写进 `room.Temperature`（AO 的写钩子会翻译成逐格注入），
但真空判定读的是原版 `Room.Vacuum`，与 AO 自管气压场脱节：AO 里房间已经加压，CCC 仍按真空衰减。

本模块 patch `Building_AirVent.TickRare` 的 Prefix：完整复刻 CCC 的换热公式，
把真空度改为 `Clamp01(1 - pascals / 101325)` 由 AO 气压推算；温度仍写 `room.Temperature`
交给 AO 写钩子；用 `AddPressureDelta` 补回真空泄掉的气压。
设置里打开「CCC 风口局部化」时，额外在出风口前两格写温度与风脉冲。

### 超维科技

生物圈和穹顶原本只覆盖**读数**：`GetTemperatureForCell` / `TryGetTemperatureForCell` /
`VacuumUtility.GetVacuum` 的 Postfix，加上 `Room.OpenRoofCount` 伪装屋顶。AO 地图上
温度场、气压、氧气、湿度完全不变，植物、腐烂、火都按真实场走。

本模块不 patch 目标 Mod 的方法，而是注册 `OmniEnvironmentBridge : MapComponent`，
每 30 tick 扫描殖民者建筑，把：

- `CompBiosphere`（`controlTemperature` + `Area.ActiveCells`）；
- `CompOmniForceFieldDome`（`DomeRect` + `IsDomeCell`）；

转成 `IForceFieldRegion` 交给宿主的 `ForceFieldStabilizer`，用 `AddDeltas` 把目标温度与常压写回 AO 物理场。
超过 256 格的区域走「采样 + 矩形批量写」，多轮收敛。

## 全局选项

- **强制稳定区**：开关 / 间隔（默认 6 tick）/ 每轮格数（默认 256）/ 区内清零风力；
- **CCC 风口局部化**（实验，默认关）；
- **强制覆盖 AO 内置兼容**：默认关。AO 自带的兼容优先，只有确认其实现不如预期时才打开。

## 已知边界

- 所有 AO 与目标 Mod 调用都走反射；对方改类名/字段名时降级为 `Skipped`，不会崩游戏，但功能会静默消失。
- AO 地图上原版 `GenTemperature.EqualizeTemperaturesThroughBuilding` 被 AO 禁用，
  所以「普通模式」通风在 AO 下几乎无效果——这是 AO 的设计，不是本 Mod 的 bug。
- 房间超过 3000 格时 AO 的写钩子会静默忽略，任何依赖 `Room.Temperature` 写入的方案都会失效；
  超大穹顶请依赖强制稳定区（走 `AddDeltas`，不经过写钩子）。
- 强制稳定区每轮只处理 N 格，超大区域需要多轮收敛，会有几秒过渡。
- `Room.Vacuum` 是原版概念，与 AO 压力场的换算依赖 `CurrentSolverPressureToPascals()`；
  AO 若改动该函数，CCC 模块的真空判定会退化为原版逻辑。
- 缺氧/氧气、湿度目前只做了「抽湿不超 75% RH」与「区内清零风力」，
  没有强制补氧；需要补氧的场景请用 AO 的空气压缩机 / PSA。

## 许可

仅用于个人 Mod 兼容，未包含任何目标 Mod 的代码或资源。
