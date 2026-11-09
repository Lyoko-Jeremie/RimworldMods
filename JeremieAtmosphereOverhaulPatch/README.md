# Atmosphere Overhaul Patch（大气层重构兼容补丁）

给 [Atmosphere Overhaul](https://steamcommunity.com/sharedfiles/filedetails/?id=3830470974) 做第三方温控 Mod 兼容层的框架。

- 仅支持 **RimWorld 1.7**（`About/About.xml` 只有 1.7，`LoadFolders.xml` 只有 `<v1.7>`）。
- 软依赖：目标 Mod 全都不存在、只存在一个、或全部存在，都能正常启动。
- 可回退：每个目标一个独立条件目录 + 独立 DLL + 独立开关；AO 日后自带兼容时，注释 `/LoadFolders.xml` 一行即可。

## 架构（与 AO 对 Dubs Bad Hygiene 的做法一致）

```
JeremieAtmosphereOverhaulPatch/
├─ About/About.xml                      # 元数据、依赖、loadAfter
├─ LoadFolders.xml                      # v1.7 + 4 条 IfModActive 条件目录
├─ Assemblies/
│  └─ JeremieAtmosphereOverhaulPatch.dll    # 宿主：注册表 + 设置 + 反射 API
├─ 1.7/Compat/
│  ├─ GoodClimate/Assemblies/Jeremie.AtmosphereOverhaulPatch.GoodClimate.dll
│  ├─ VanillaTemperatureExpanded/Assemblies/…
│  ├─ CentralizedClimateControl/Assemblies/…
│  └─ OmniCrafter/Assemblies/…
├─ Patches/README.md                    # XML 补丁约定（默认不用 XML）
├─ Languages/{ChineseSimplified (简体中文),English}/Keyed/JeremieAOPatch.xml
├─ README.md
└─ Source/
   ├─ JeremieAtmosphereOverhaulPatch/   # 宿主
   │  ├─ AOPatchMod.cs                  # Mod 入口 + 启动引导
   │  ├─ AOPatchSettings.cs             # 持久化开关
   │  ├─ Api/ReflectionCache.cs         # 跨程序集反射缓存
   │  ├─ Api/AtmosphereApi.cs           # AO AtmosphereInterface 的反射封装
   │  ├─ Compat/ICompatModule.cs        # 兼容模块契约（跨程序集）
   │  ├─ Compat/CompatModuleBase.cs     # 生命周期 + 独立 Harmony id
   │  ├─ Compat/CompatModuleInfo.cs     # 设置界面用的模块视图
   │  ├─ Compat/CompatRegistry.cs       # 发现实现、配对、重装、回退
   │  └─ Util/PatchLog.cs
   ├─ Compat.GoodClimate/               # 每个目标一个独立项目
   ├─ Compat.VanillaTemperatureExpanded/
   ├─ Compat.CentralizedClimateControl/
   └─ Compat.OmniCrafter/
```

**宿主不引用任何兼容 DLL，兼容 DLL 也不引用宿主以外的目标 Mod。** 启动时
`CompatRegistry` 扫描已加载程序集，找到 `ICompatModule` 实现后按 `packageId` 配对。
所以目标 Mod 不在时，条件目录不会被加载，对应的兼容 DLL 自然也不存在，
界面显示「未安装」，一切照常。

## 当前覆盖

| 目标 | packageId | 条件目录 | 状态 |
| --- | --- | --- | --- |
| Good Climate | `GOS.GoodClimate` | `1.7/Compat/GoodClimate` | **已实现**：真空关阀 → AO 透气面限速归零 |
| Vanilla Temperature Expanded | `VanillaExpanded.Temperature` | `1.7/Compat/VanillaTemperatureExpanded` | 诊断（预留持续源接入） |
| Centralized Climate Control (Continued) | `Mlie.CentralizedClimateControl` | `1.7/Compat/CentralizedClimateControl` | 检测（上游未声明 1.7，通常不加载） |
| 超维科技 OuterrealmTech | `Jeremie.Fully.Automatic.OmniCrafter` | `1.7/Compat/OmniCrafter` | 诊断（预留生物圈持续源） |

## 五条设计约束如何落实

1. **只支持 1.7**：`supportedVersions` 与 `LoadFolders` 都只列 1.7；源码不写 1.6 分支。
2. **多目标**：每个目标一个独立项目 + 独立 DLL + 独立条件目录，互不依赖。
3. **任意子集可用**：目标不存在 → 条件目录不加载 → 模块 `Instance == null` → `NotInstalled`；
   目标存在但成员缺失 → `TryApply` 返回 false → `Skipped`，并在 `Detail` 说明缺什么。
4. **快速禁用/回退**（三级）：
   - 设置界面每个模块一个开关，「立即重装补丁」会 `UnpatchAll` 后按当前开关重装；
   - 注释掉 `/LoadFolders.xml` 中对应 `<li>`：该兼容 DLL 不再被加载（下次启动生效）；
   - 直接删除 `1.7/Compat/<X>/` 目录：等价于从未存在过。
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

## 新增一个目标 Mod 的步骤

1. `LoadFolders.xml` 加一行：`<li IfModActive="Some.PackageId">1.7/Compat/SomeName</li>`；
2. 新建 `Source/Compat.SomeName/SomeNameCompat.cs`：`public sealed class SomeNameCompat : CompatModuleBase`，
   实现 `PackageId` / `LabelKey` / `HintKey` / `ReadSetting` / `WriteSetting` / `TryApply`；
3. 复制任一 `Compat.*.csproj`，改 `ProjectGuid`、`AssemblyName`、`OutputPath`；
4. `AOPatchSettings` 加 `bool` 字段并在 `ExposeData` 里 `Scribe_Values.Look`；
5. `CompatRegistry.BuildInfos()` 加一条 `Info(...)`，绑定该设置字段；
6. 中英 `Keyed` 加 `AOPatch_Module_SomeName` 与 `...Desc`；
7. 编译、进游戏，看 `Player.log` 里的 `[AO Patch]` 汇总行。

## 已实现模块做了什么

### Good Climate 真空联动

Good Climate 的「强力换热」在检测到 `Room.Vacuum > 0` 时会 `DoFlick()` 关闭原版
`CompFlickable`，并弹出「气压骤降」信件。但 AO 的透气面只认自己的
`CompAirVent.speedLimit`，对 Flick 信号只 `MarkDirty`，所以阀关了、AO 仍在漏气。

本模块在 `GOS.GoodClimate.GC_MapComponent.MapComponentTick` 之后补一刀：
当 GC 判定真空并保持强力模式时，把同一建筑上的 `AtmosphereOverhaul.Buildings.CompAirVent`
的 `SpeedLimit` 设为 `0`，让 AO 的透气面真正关闭。

## 已知边界

- AO 的 `AtmosphereInterface` 全部方法都用反射调用；AO 改签名时降级为空操作并写日志，不会崩游戏。
- AO 地图上原版 `GenTemperature.EqualizeTemperaturesThroughBuilding` 被 AO 禁用，
  所以「普通模式」通风在 AO 下几乎无效果——这是 AO 的设计，不是本 Mod 的 bug。
- `Room.Vacuum` 是原版概念，与 AO 自管气压场不一定同步；本模块只把它当作 GC 的触发条件。
- 房间超过 3000 格时 AO 的写钩子会静默忽略，任何依赖 `Room.Temperature` 写入的方案都会失效。

## 许可

仅用于个人 Mod 兼容，未包含任何目标 Mod 的代码或资源。
