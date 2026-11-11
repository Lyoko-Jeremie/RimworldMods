# Frostpunk Heat Generator Tower 在 AO 下的工作机制分析

> 目标 mod：`F:\SteamLibrary\steamapps\workshop\content\294100\3545379519`
> 分析对象：`kyon_HeatGeneratorTower`、`kyon_HeatGeneratorTowerLarge`
> 参照系：Atmosphere Overhaul（`294100/3830470974`，1.7，MVID `4e9f1eae1c1d40e48ce779113df84ba7`）与 RimWorld 1.6 `Assembly-CSharp`（MVID `46372F5D-ADBF-4AF8-939E-608076251180`）
> 结论日期：2026-11-11

---

## 一、结论摘要

1. 这个 mod **不含任何 DLL**，只有两个 `ThingDef` 与三个 XML patch。它的全部行为来自原版 `CompHeatPusherPowered` 与 `CompProperties_Refuelable` 的组合。
2. 在 AO 地图上，**AO 已经自动接管**它：`CompHeatPusher.CompTickRare` 被 Prefix 拦截，改由 `CompHeatSourceAtmosphere.CompTick` 写 AO 温度场。**不需要为本建筑写兼容补丁。**
3. 但两者语义不同：
   - 原版：热量**摊到整个房间**（`Room.Temperature += energy / CellCount`），房间均匀升温。
   - AO：热量**集中在设备所在的一格**（`TemperaturePerTick = heatPerSecond * heatFactor / 60`），再由平流/扩散传播。
   - 同样 750 heat/s，原版 25 格房间约 `2.08 ℃/tick/格`，AO 单格 `37.5 ℃/tick`——**局部瞬时升温高约 18 倍**，但有 AO 的 `ShouldPushHeatNow` 60 ℃ 上限兜底。
4. `heatPushMaxTemperature` **仍然生效**（AO 反射调用 `ShouldPushHeatNow`），不是被忽略。小型机到 60 ℃ 停机，大型机未设上限（默认 99999），由 AO 自己的 200–300 ℃ 衰减接管。
5. 真正的兼容缺口有三个：
   - `ProxyHeat.CompProperties_TemperatureSource` 在 AO 下变成**死值读数**（26 格恒 30 ℃ / 60 格恒 50 ℃），与场脱节。相关决策见 `AO兼容层重构设计.md` 第 12 章（不兼容 VTE - Proxy Heat Patch）。
   - 燃烧**只耗氧、不产 CO₂/水汽、无烟道**。
   - 没有挂 `BuildingAtmosphereExtension`，`blockWind=true` 在 AO 风场里没有对应语义。
6. `DubsPatch` 把 `heatPerSecond` 从 750/2500 砍到 25/60，是因为作者假定装了 DCH/DBH 后热量走锅炉管网。这条依赖在 AO 下依然成立：DCH 的 `CompRadiator` 被 AO 的 `DbhHeat.Control` 接管，锅炉热量最终仍进 AO 场。

---

## 二、Mod 构成

### 2.1 文件结构

```
3545379519/
├── About/About.xml
├── LoadFolders.xml
├── 1.6/Defs/HeatGeneratorTower.xml        # 268 行，全部建筑定义
├── 1.6/Patches/                           # 无
├── CoalPatch/Patches/coalpatch.xml        # 条件加载
├── DubsPatch/Patches/dubspatch.xml        # 条件加载
├── 1.6/Textures/...
└── 1.6/Languages/...
```

### 2.2 元数据

| 项 | 值 |
|---|---|
| `packageId` | `HarryRobinson.FrostpunkHeatGeneratorTower` |
| 作者 | Harry_Robinson |
| 版本 | 0.0.3 |
| supportedVersions | 1.5、1.6 |
| `loadAfter` | Ratkin、Rimefeller、DubsCentralHeating、DubsBadHygiene、VanillaTemperatureExpanded、TerraformingAsteroids、simplecoal |
| DLL | **无** |

### 2.3 条件加载目录

| 目录 | 触发条件 | 内容 |
|---|---|---|
| `CoalPatch` | `zal.simplecoal` / `Owlchemist.SimpleChains.Steel` / `dizzyioeuy.SimpleChains.Steel` | 给燃料过滤器追加 `WDCoal` |
| `DubsPatch` | `Dubwise.DubsCentralHeating` / `Dubwise.DubsBadHygiene` | 把 `heatPerSecond` 改成 25 / 60 |

---

## 三、建筑 Def 全数据

### 3.1 `kyon_HeatGeneratorTower`（小型）

| 字段 | 值 | 备注 |
|---|---|---|
| `size` | 5×5 | 25 格 |
| `MaxHitPoints` | 1000 | |
| `Mass` | 3000 | |
| `Flammability` | 0 | |
| `Beauty` | -5 | |
| `WorkToBuild` | 100000 | |
| `costList` | Steel 1500、ComponentIndustrial 50 | |
| `blockWind` | true | |
| `passability` | `PassThroughOnly` | 可穿过但不可停留 |
| `pathCost` | 1000 | |
| `tickerType` | `Normal` | 每 tick 都 tick（不是 Rare） |
| `designationCategory` | `Temperature` | |
| `buildingTags` | `RitualFocus` | 可作为仪式焦点 |
| `placeWorkers` | `PlaceWorker_PreventInteractionSpotOverlap`、`PlaceWorker_Heater`、`PlaceWorker_GlowRadius` | |

**组件**：

| 组件 | 关键参数 |
|---|---|
| `CompProperties_Flickable` | — |
| `CompGatherSpot` | 闲逛集合点 |
| `CompProperties_Refuelable` | `fuelConsumptionRate=150`/天、`fuelCapacity=500`、`fuelFilter=[WoodLog, Chemfuel]`、`initialFuelPercent=0`、`showFuelGizmo`、`showAllowAutoRefuelToggle`、`canEjectFuel`、`targetFuelLevelConfigurable` |
| `CompProperties_Glower` | `glowRadius=20`、`glowColor=(252,187,113,0)` |
| `CompProperties_HeatPusher` | `compClass=CompHeatPusherPowered`、**`heatPerSecond=750`**、**`heatPushMaxTemperature=60`** |
| `CompProperties_Explosive` | `explosiveRadius=10`、`explosiveExpandPerFuel=0.02`、`Flame`、`destroyThingOnExplosionSize=2`、`startWickHitPointsPercent=0.15`、`Filth_Fuel` 0.5、`wickTicks=150~300` |
| `CompProperties_Breakdownable` | 会故障 |
| `OberoniaAureaGene.Snowstorm.CompProperties_SnowstormCampfire` | `hediffHuman=Hypothermia`、`hediffInsectoid=HypothermicSlowdown`、`affectRadius=26`、`hypothermiaDecreasePreHour=1`；MayRequire `OARK.RatkinFaction.ScenarioExpand.Snowstorm` |
| `Rimefeller.CompProperties_Pipe` | MayRequire `Dubwise.Rimefeller` |
| `DubsCentralHeating.CompProperties_Pipe` | `mode=Heating` |
| `DubsCentralHeating.CompProperties_CompBoiler` | `BaseCapacity=25000`、`ThermostatControl=false` |
| `DubsBadHygiene.CompProperties_Pipe` | `mode=Sewage` |
| `DubsBadHygiene.CompProperties_CompBoiler` | `BaseCapacity=25000`、`ThermostatControl=false` |
| `ProxyHeat.CompProperties_TemperatureSource` | `radius=26`、`tempOutcome=80`、`maxTemperature=30`、`smeltSnowRadius=26`、`smeltSnowAtTemperature=0`、`smeltSnowPower=0.001`；MayRequire `VanillaExpanded.Temperature` |
| `ProxyHeat.CompProperties_TemperatureSource`（第二份） | 同上；MayRequire `DimonSever000.TerraformingAsteroids` |

### 3.2 `kyon_HeatGeneratorTowerLarge`（大型）

| 字段 | 值 | 备注 |
|---|---|---|
| `size` | 6×6 | 36 格 |
| `MaxHitPoints` | 2500 | |
| `Mass` | 5000 | |
| `WorkToBuild` | 300000 | |
| `costList` | Steel 2500、ComponentIndustrial 75 | |
| 其余建筑属性 | 同小型 | |

**组件差异**：

| 组件 | 关键参数 |
|---|---|
| `CompProperties_Refuelable` | `fuelConsumptionRate=300`/天、`fuelCapacity=1000` |
| `CompProperties_Glower` | `glowRadius=40` |
| `CompProperties_HeatPusher` | `compClass=CompHeatPusherPowered`、**`heatPerSecond=2500`**、**未设 `heatPushMaxTemperature`** → 默认 99999 |
| `CompProperties_Explosive` | `explosiveRadius=20`、`explosiveExpandPerFuel=0.03`、`startWickHitPointsPercent=0.25` |
| `SnowstormCampfire` | `affectRadius=60`、`hypothermiaDecreasePreHour=2` |
| DCH/DBH `CompBoiler` | `BaseCapacity=60000` |
| `ProxyHeat.TemperatureSource` | `radius=60`、`tempOutcome=200`、`maxTemperature=50`、`smeltSnowRadius=60` |

### 3.3 三个 patch

**`CoalPatch/Patches/coalpatch.xml`**

```xml
<Operation Class="PatchOperationAdd">
  <order>Append</order>
  <xpath>
    Defs/ThingDef[defName = "kyon_HeatGeneratorTower"]/comps/li[contains(@Class,'CompProperties_Refuelable')]/fuelFilter/thingDefs |
    Defs/ThingDef[defName = "kyon_HeatGeneratorTowerLarge"]/comps/li[contains(@Class,'CompProperties_Refuelable')]/fuelFilter/thingDefs
  </xpath>
  <value><li>WDCoal</li></value>
</Operation>
```

**`DubsPatch/Patches/dubspatch.xml`**

```xml
<Operation Class="PatchOperationReplace">
  <xpath>Defs/ThingDef[defName = "kyon_HeatGeneratorTower"]/comps/li[contains(@Class,'CompProperties_HeatPusher')]/heatPerSecond</xpath>
  <value><heatPerSecond>25</heatPerSecond></value>
</Operation>
<Operation Class="PatchOperationReplace">
  <xpath>Defs/ThingDef[defName = "kyon_HeatGeneratorTowerLarge"]/comps/li[contains(@Class,'CompProperties_HeatPusher')]/heatPerSecond</xpath>
  <value><heatPerSecond>60</heatPerSecond></value>
</Operation>
```

### 3.4 About 描述里的设计意图

- 小型：`750 heat/s`（**35 倍原版加热器**），能把封闭空间加热到 60 ℃，20 格光照，`150 燃料/天`（15 倍篝火），容量 500。
- 大型：`2500 heat/s`，**无温度上限**，40 格光照，`300 燃料/天`，容量 1000。
- VTE/Cybernian：26 格 +80 ℃、上限 30 ℃；大型 60 格 +200 ℃、上限 50 ℃，可融雪。
- 作者自述："与原版一起用时有人报告卡顿，代码里看不出问题，大概是同时代理加热太多格导致的。"
  → 这条描述本身就是 ProxyHeat 的性能证据：`FloodFill + LineOfSight` 全量重算 26/60 格，多个发电机叠加时开销线性增长。

---

## 四、原版工作机制

### 4.1 开关判定：`ShouldPushHeatNow`

`Verse/CompHeatPusher.cs`：

```csharp
public virtual bool ShouldPushHeatNow
{
  get
  {
    if (!this.parent.SpawnedOrAnyParentSpawned) return false;
    CompProperties_HeatPusher props = this.Props;
    float ambientTemperature = this.parent.AmbientTemperature;
    return this.enabled
        && (double) ambientTemperature < (double) props.heatPushMaxTemperature
        && (double) ambientTemperature > (double) props.heatPushMinTemperature;
  }
}
```

`Verse/CompHeatPusherPowered.cs`：

```csharp
public override bool ShouldPushHeatNow
{
  get
  {
    return base.ShouldPushHeatNow
        && FlickUtility.WantsToBeOn((Thing) this.parent)
        && (this.powerComp == null || this.powerComp.PowerOn)
        && (this.refuelableComp == null || this.refuelableComp.HasFuel)
        && (this.breakdownableComp == null || !this.breakdownableComp.BrokenDown);
  }
}
```

`CompProperties_HeatPusher` 的默认值（`Verse/CompProperties_HeatPusher.cs`）：

```csharp
public float heatPerSecond;
public float heatPushMaxTemperature = 99999f;
public float heatPushMinTemperature = -99999f;
```

→ **小型机 60 ℃ 停机的唯一机制就是这里**；大型机不设上限，等于 99999 ℃ 永不停。

### 4.2 推进：`CompTickRare`

```csharp
public override void CompTickRare()
{
  base.CompTickRare();
  if (!this.ShouldPushHeatNow) return;
  GenTemperature.PushHeat(this.parent.PositionHeld, this.parent.MapHeld,
                          this.Props.heatPerSecond * 4.16666651f);
}
```

`4.16666651 = 250 / 60`。含义：`heatPerSecond` 按"每秒"计，`CompTickRare` 每 60 tick 执行一次，而 1 秒 = 60 tick，所以一次要给 60 秒的量，再乘 `250/60` 做单位对齐（RimWorld 内部温度单位与 tick 的历史换算）。

### 4.3 落点：`GenTemperature.PushHeat` → `Room.PushHeat`

`Verse/GenTemperature.cs:271`：

```csharp
public static bool PushHeat(IntVec3 c, Map map, float energy)
{
  if (map == null) { Log.Error("Added heat to null map."); return false; }
  Room room1 = c.GetRoom(map);
  if (room1 != null) return room1.PushHeat(energy);
  GenTemperature.neighRooms.Clear();
  for (int index = 0; index < 8; ++index)
  {
    IntVec3 intVec3 = c + GenAdj.AdjacentCells[index];
    if (intVec3.InBounds(map))
    {
      Room room2 = intVec3.GetRoom(map);
      if (room2 != null) GenTemperature.neighRooms.Add(room2);
    }
  }
  float energy1 = energy / (float) GenTemperature.neighRooms.Count;
  for (int index = 0; index < GenTemperature.neighRooms.Count; ++index)
    GenTemperature.neighRooms[index].PushHeat(energy1);
  int num = GenTemperature.neighRooms.Count > 0 ? 1 : 0;
  GenTemperature.neighRooms.Clear();
  return num != 0;
}
```

`Verse/Room.cs:480`：

```csharp
public bool PushHeat(float energy)
{
  if (this.UsesOutdoorTemperature) return false;
  this.Temperature += energy / (float) this.CellCount;
  return true;
}
```

**语义**：`energy` 是"温度当量 × 格数"，不是焦耳。`Room.PushHeat` 直接把它平摊到每一格。

**三个后果**：
1. 房间瞬间整体升温，没有传播过程。
2. 户外房间（`UsesOutdoorTemperature`）**完全丢弃热量**。
3. 不在任何房间 → 找 8 邻房间均分；都没有 → 丢弃。

### 4.4 数值换算（小型，25 格房间）

```
每次 CompTickRare：energy = 750 × 4.1667 = 3125
每格每次：3125 / 25 = 125 ℃
每秒（60 tick）：125 ℃
```

→ 一个 5×5 房间从 20 ℃ 到 60 ℃ 约需 `(60-20)/125 = 0.32` 次 tick = **约 19 tick**。这就是"35 倍加热器"的实际手感。

---

## 五、AO 接管机制

### 5.1 接管点

`AtmosphereOverhaul/Buildings/3HeatSources.cs:14-22`：

```csharp
[HarmonyPatch(typeof(CompHeatPusher), "CompTickRare")]
internal static class CompHeatPusher_Patch
{
  static bool Prefix(CompHeatPusher __instance)
  {
    return __instance == null || !__instance.TryGetComp<CompHeatSourceAtmosphere>().Replaces;
  }
}
```

`Replaces` 的定义（`Buildings/1HeatSources.cs`）：

```csharp
public bool Replaces => parent.Spawned && FacilityMath.Atmosphere(parent) != null;
```

→ 只要地图有 AO 引擎，**所有** `CompHeatPusher`（含子类）都被接管。

同时 `Buildings/2HeatSources.cs:21-34` 是 `[StaticConstructorOnStartup]` 注入器：遍历 `DefDatabase<ThingDef>.AllDefsListForReading`，给所有带 `CompProperties_HeatPusher` 且 `compClass` 属于原版程序集的建筑追加 `CompHeatSourceAtmosphere`。

### 5.2 `CompHeatSourceAtmosphere.CompTick`（`Buildings/1HeatSources.cs:53-80`，逐字）

```csharp
public virtual void CompTick()
{
  base.CompTick();
  if (!parent.Spawned || !Gen.IsHashIntervalTick(parent, 60) || FacilityMath.Atmosphere(parent) == null)
    return;

  CompHeatPusher comp = parent.GetComp<CompHeatPusher>();
  if (comp == null) return;

  Map map = parent.Map;
  IntVec3 position = parent.Position;

  bool flag1 = CompHeatSourceAtmosphere.ShouldPush(comp);          // 反射调 ShouldPushHeatNow
  bool flag2 = Burns && comp.Props.heatPerSecond > 0f;             // 是否消耗氧
  AO_FacilityExtension ext = AO_FacilityExtension.For(parent.def);

  float heatPerSecond = comp.Props.heatPerSecond;
  float num1 = 1f;
  if (flag2) num1 = Mathf.Clamp(FireOxygen.Factor(map, position, out float _), 0f, 3f);
  float num2 = ext.fireSize > 0f ? ext.fireSize : heatPerSecond / 64f;
  float num3 = ext.fireSize > 0f
      ? 160f * ext.fireSize * 3f / 150f
      : heatPerSecond * (ext.pusherHeatFactor > 0f ? ext.pusherHeatFactor : 3f) / 60f;
  float num4 = FacilityMath.Celsius(map, position, parent);
  float num5 = heatPerSecond < 0f
      ? Mathf.Clamp01((num4 + 273.15f) / 150f)
      : (num4 <= 200f ? 1f : Mathf.Clamp01((300f - num4) / 100f));
  bool flag3 = flag1 && num1 > 0f;

  lastOn = flag3;
  lastFactor = num1;

  slots.Set(parent, 0, position, new AtmosphereSourceRates
  {
    TemperaturePerTick = flag3 ? num3 * num1 * num5 : 0f,
    OxygenPercentPerTick = flag3 && flag2 ? -2.7f * num2 * num1 / 150f : 0f
  });
}
```

关键行逐条：

| 行 | 含义 |
|---|---|
| `ShouldPush(comp)` | 反射委托（`AccessTools.PropertyGetter(typeof(CompHeatPusher), "ShouldPushHeatNow")`），**完整保留 60 ℃ / 燃料 / 通电 / 未损坏判定** |
| `Burns` | `AO_FacilityExtension.burnsOxygen >= 0` 用它；否则 `GetComp<CompRefuelable>() != null && !NoBurn.Contains(defName)` |
| `num3` | `heatPerSecond × pusherHeatFactor(默认 3) / 60` = **每 tick 的温度当量** |
| `num5` | 负热源（制冷）用 `(T+273)/150`；正热源 `T<=200 → 1`，`200~300` 线性到 0 |
| `TemperaturePerTick` | `flag3 ? num3*num1*num5 : 0` |
| `OxygenPercentPerTick` | 燃烧时 `-2.7 × (heatPerSecond/64) × num1 / 150` |

### 5.3 应用：`ApplyContinuousSourcesUnsafe`

`RimWorld/AtmosphereMapComponent.cs:2534`：

```csharp
int num4 = ticksGame - continuousSource.LastAppliedTick;
if (num4 > 0)
{
  continuousSource.LastAppliedTick = ticksGame;
  continuousSource.LastApplicationDeltaTicks = num4;
  if (ratesPerTick.TemperaturePerTick != 0f && index < pendingTemperatureDeltas.Length)
    pendingTemperatureDeltas[index] += ratesPerTick.TemperaturePerTick * (float) num4;
  ...
}
```

随后在 `RunGlobalSubcyclingUnsafe` 中按子步比例写入：

```csharp
float frac = (float) num / (float) elapsedTicks;
engine.ApplyTemperatureDeltas(pendingTemperatureDeltas, frac);
```

`LocalAtmosphereEngine.ApplyTemperatureDeltas`：

```csharp
for (int i = 0; i < deltas.Length; i++)
  if (deltas[i] != 0f && ObstacleMask[i] > 0f)
  {
    if (compressibleActive && RoofMask[i] > 0.5f) NoteThermalExpansion(i, deltas[i] * scale);
    AddTemperatureDelta(i, deltas[i] * scale);
  }
```

→ **只写设备所在的一格**，并且有顶 + 可压缩时顺手转成体积膨胀源。

### 5.4 数值换算（小型）

```
num3 = 750 × 3 / 60 = 37.5 ℃/tick
每 60 tick 更新一次 rates
ApplyContinuousSourcesUnsafe 每 SimulationIntervalTicks(=6) 执行一次，dt = 6
每 6 tick：pendingTemperatureDeltas += 37.5 × 6 = 225 ℃
折算：37.5 ℃/tick
```

与原版对比：

| | 原版 | AO |
|---|---|---|
| 作用范围 | 整个房间（25 格） | 单格 |
| 每格升温 | `3125/25 = 125 ℃` 每 60 tick | `37.5 ℃` 每 tick = `2250 ℃` 每 60 tick |
| 传播 | 瞬间均摊 | 平流 + 扩散 |
| 户外 | 丢弃 | 正常写场 |
| 上限 | `heatPushMaxTemperature=60` | 同一判定 + `num5` 200–300 ℃ 衰减 |

→ **AO 的局部瞬时升温约 18 倍**，但被 `ShouldPushHeatNow` 的 60 ℃ 判定立刻掐断（设备格一到 60 ℃ 就停），所以稳态由"扩散带走多少"决定，不会失控。

### 5.5 热源组件 vs 温控组件的区别

这是容易混淆的一点：

| | `CompTempControlAtmosphere` | `CompHeatSourceAtmosphere` |
|---|---|---|
| 接管对象 | `Building_TempControl` 子类（加热器/空调） | `CompHeatPusher` 子类（篝火/熔炉/发电机） |
| 目标 | 朝 `TargetTemperature` 闭环 | 无目标，恒功率输出 |
| 风 | 制冷时出风 15 m/s | 无风 |
| 除湿 | 制冷时除湿、产水 | 无 |
| 功率 | 受 `PowerTrader` 节流 | 不受功率影响（燃料驱动） |

发电机属于后者：**恒功率热源 + 耗氧**，不闭环、不出风、不除湿。

---

## 六、原版 vs AO 行为对照

| 场景 | 原版 | AO |
|---|---|---|
| 封闭房间 | 整房间均匀升温到 60 ℃ | 设备格升温，热量扩散到房间，可能形成梯度 |
| 户外 | 热量丢弃（`UsesOutdoorTemperature`） | 正常加热空气 |
| 通风 | 房间边界不连通则热不出去 | 门/缝/透气面（`FaceOpen`）决定热量流出 |
| 强风 | 无影响 | 平流带走热量，下风向变暖 |
| 密封房间 | 无压力效应 | 升温 → `NoteThermalExpansion` → 体积膨胀 → 升压 |
| 氧气 | 无消耗 | 燃烧耗氧 `-2.7 × (heatPerSecond/64) / 150` %/tick |
| 湿度 | 无 | 无（燃烧不产水汽，这是缺口） |
| 燃料耗尽 | 停 | 停（`ShouldPushHeatNow` 检查 `HasFuel`） |
| 故障 | 停 | 停（`BrokenDown`） |

---

## 七、四层第三方兼容

| 兼容 | 小型 | 大型 | 在 AO 下的状态 |
|---|---|---|---|
| `ProxyHeat.TemperatureSource` | r=26、+80 ℃、cap 30 | r=60、+200 ℃、cap 50 | **死值**：`Thing.AmbientTemperature` Postfix 无条件加 `tempOutcome`，被 `maxTemperature` 夹住 → 26 格恒 30 ℃、60 格恒 50 ℃ |
| `DCH.CompBoiler` + `Pipe(Heating)` | 25000 | 60000 | AO 不接管 boiler；但 DCH `CompRadiator` 被 AO 的 `DbhHeat.Control` 接管 → 热量最终进 AO 场 |
| `DBH.CompBoiler` + `Pipe(Sewage)` | 25000 | 60000 | 同上（DBH 的散热末端走 AO） |
| `Rimefeller.Pipe` | — | — | 与 AO 无关 |
| `Ratkin SnowstormCampfire` | r=26、-1/时 | r=60、-2/时 | 直接改 `Hediff_Hypothermia`，与 AO 温度场并行，不冲突但会"双重保暖" |
| `CoalPatch` | 加 `WDCoal` | 同 | 无影响 |
| `DubsPatch` | `heatPerSecond=25` | `=60` | 直排热降到 2–3%，热量改走锅炉 → radiator → AO 场 |

### 7.1 `DubsPatch` 的设计意图

作者在描述里写得很清楚：装了 DCH/DBH 后，"Generator 周围的热量显著减少"，因为锅炉接管了供热。

数值上：750 → 25 是 **3.3%**；2500 → 60 是 **2.4%**。等于说"发电机本体只作为锅炉的热源，不再直接烤房间"。

### 7.2 DCH 链路在 AO 下是否成立

```
CompBoiler（燃料→热水）
  → DCH 管网
  → CompRadiator / CompRoomUnit / CompFreezerUnit
  → AO DbhHeat.Patch 的三件套（Prefix Enter + Transpiler + Finalizer Leave）
  → DbhHeat.Control(cell, map, energyLimit, targetTemperature)
  → slots.Set(... TemperaturePerTick = ±u*|energyLimit|/interval)
  → AO 温度场
```

结论：**成立**。AO 的 DCH compat 只 patch 了四个 CompTick，`CompBoiler` 不在其中，所以锅炉本身仍按 DCH 原逻辑烧水；热量在 radiator 处被 AO 接走。

---

## 八、AO 下的实际表现

### 8.1 正常路径（不装 DCH/DBH）

1. 加燃料 → `HasFuel` 为真 → `ShouldPushHeatNow` 为真。
2. 每 60 tick 写一次 `TemperaturePerTick = 37.5`（小型）。
3. 设备格温度上升；一旦 `parent.AmbientTemperature >= 60`，`ShouldPushHeatNow` 返回 false，停止。
4. 热量被 `AdvectScalar`（风）和 `DiffuseScalar`（扩散）带出，设备格降温，重新点火。
5. 持续耗氧：`OxygenPercentPerTick = -2.7 × (750/64) × 1 / 150 = -0.211 %/tick`；按 `ApplyContinuousSourcesUnsafe` 的 `* num4`（`num4=6`）积分，每次 `-1.27%`，每 60 tick 施加 10 次 = **`-12.66%` 每 60 tick（每秒）**，且只写左下角一格。

> **注意**：这个量级远超"一个房间摊薄"的直觉——AO 只写一格，不摊薄。设备格氧气理论上约 8 秒耗尽（21% / 12.66% per s）。但 `ShouldPushHeatNow` 到 60 ℃ 会停机（此时 `TemperaturePerTick=0` 且 `OxygenPercentPerTick=0`），所以实际是**脉冲式**：点火 → 升温 → 60 ℃ 停 → 扩散降温 → 复燃。占空比由扩散速率决定，稳态耗氧速率需实测。

### 8.2 五个问题

**问题 1：`ProxyHeat` 读数死值**

`HarmonyPatches.cs:361` 的 `Thing.AmbientTemperature` Postfix 无条件执行：

```csharp
__result = mgr.GetTemperatureOutcomeFor(pos, __result);
```

`GetTemperatureOutcomeFor` 把 `result + tempOutcome` 夹到 `maxTemperature`。AO 下 `result` 是真实场温，`tempOutcome=80`，`maxTemperature=30` → 只要场温 < 30，读数恒为 30。

后果：
- 小人不会觉得"发电机旁边 80 ℃"（被夹到 30，反而不热）。
- 植物在 26 格内按 30 ℃ 生长。
- 融雪按 30 ℃ 判定。
- 与 AO 场脱节，且 `maxTemperature` 越低越"冷"，语义完全反了。

**问题 2：燃烧只耗氧，不产 CO₂/水汽**

`CompHeatSourceAtmosphere` 只写 `OxygenPercentPerTick`。燃烧产物（CO₂、H₂O）在 AO 里没有对应气体 Def（`ED_Biogas` 是另一个 mod 的样例）。密封房间里烧发电机，氧气下降但没有"窒息性气体"上涨。

**问题 3：没有烟气/烟道**

`CompProperties_HeatPusher` 没有排气口概念。AO 有 `AddMistDelta`（雾/烟）但没有被调用。原版 `Filth_Fuel` 只在爆炸时生成。

**问题 4：`blockWind=true` 在 AO 风场里无对应**

原版 `blockWind` 只影响 `WindManager` 的视觉与 `SnowGrid`；AO 的风场由 `ObstacleMask`/`FaceOpen` 决定，不看 `blockWind`。要让它挡风，需要挂 `BuildingAtmosphereExtension`（`windToleranceFactor`）。

**问题 5：燃料→热量的量纲不真实**

`heatPerSecond=750` 是游戏内部"温度当量/秒"，不是瓦特。150 单位燃料/天 → 750 当量/秒，反推每单位燃料 5000 当量·秒。这跟木头热值没有可比性。AO 若要做"真实燃料"，需要重新标定。

---

## 九、接入 AO 体系的缺口与特性建议

### 9.1 必须做（P0）

| # | 特性 | 实现要点 |
|---|---|---|
| 1 | 移除 `ProxyHeat.CompProperties_TemperatureSource` | XML `PatchOperationRemove`，或在 patch mod 里 Prefix 掉 `GetTemperatureOutcomeFor` |
| 2 | 燃烧产湿 | `CompHeatSourceAtmosphere` 已支持 `MoisturePerTick`，但没写。可加 `AO_FacilityExtension` 字段或独立 comp：`MoisturePerTick = heatPerSecond/64 * 0.012`（kg/s → 绝对水汽） |
| 3 | 排烟 | 用 `AddMistDelta` 在设备上方生成 `Mist`，或挂 `CompSmokeEmitter`（AO 没有，需新写） |
| 4 | 多格覆盖 | 用 `slots.Set(parent, i, cell, rates)`（slot 索引自增）对 footprint 每格注册，或至少改用中心格；否则 5×5/6×6 的热量全堆在左下角 |

### 9.2 应该做（P1）

| # | 特性 | 实现要点 |
|---|---|---|
| 4 | 挡风 | 给两个 Def 挂 `BuildingAtmosphereExtension`：`windToleranceFactor=3`、`pressureToleranceFactor=...` |
| 5 | 密封房间升压 | AO 的 `NoteThermalExpansion` 已自动处理有顶格；但要确认 `RoofMask` 覆盖发电机所在格 |
| 6 | 燃料热值标定 | 把 `heatPerSecond` 换算成真实瓦特：`750 当量/秒 ≈ 750 × 4.186 J/s ≈ 3.1 kW`（RimWorld 1 温度当量 ≈ 1 卡） |

### 9.3 可选（P2）

| # | 特性 | 实现要点 |
|---|---|---|
| 7 | 余热回收 | 在烟道口加 `AddTemperatureDelta` 或热交换器 |
| 8 | 风致散热 | 设备格温度高 + 风速大 → 额外散热（`Q = h·A·(T−T_air)`） |
| 9 | 雪暴联动 | `SnowstormCampfire` 与 AO 温度场叠加时去重，避免"双重保暖" |

### 9.4 不建议做

- **不要**给发电机加 `CompTempControlAtmosphere`（它不是温控建筑，会破坏 `TargetTemperature` 语义）。
- **不要**用 `AddTemperatureDelta` 直接写大 delta（会触发 `PatchSafeDivergence` 限幅，且与 `SourceSlots` 的 tick 积分重复）。

---

## 十、文件与行号索引

### 10.1 Frostpunk mod

| 文件 | 内容 |
|---|---|
| `About/About.xml` | packageId、loadAfter |
| `LoadFolders.xml` | 条件目录 |
| `1.6/Defs/HeatGeneratorTower.xml` | 两个 ThingDef（268 行） |
| `CoalPatch/Patches/coalpatch.xml` | 加 `WDCoal` |
| `DubsPatch/Patches/dubspatch.xml` | `heatPerSecond` → 25/60 |

### 10.2 RimWorld

| 文件 | 行 | 内容 |
|---|---|---|
| `Verse/CompHeatPusher.cs` | 14-24 | `ShouldPushHeatNow`（含 `heatPushMaxTemperature`） |
| `Verse/CompHeatPusher.cs` | 37-43 | `CompTickRare` → `PushHeat(heatPerSecond * 4.1667)` |
| `Verse/CompHeatPusherPowered.cs` | 16-27 | 通电/燃料/故障判定 |
| `Verse/CompProperties_HeatPusher.cs` | 9-11 | `heatPushMaxTemperature = 99999f` 默认 |
| `Verse/GenTemperature.cs` | 271-298 | `PushHeat(IntVec3, Map, energy)` |
| `Verse/Room.cs` | 480-485 | `PushHeat` → `Temperature += energy / CellCount` |

### 10.3 AtmosphereOverhaul

| 文件 | 行 | 内容 |
|---|---|---|
| `Buildings/3HeatSources.cs` | 14-22 | `CompHeatPusher.CompTickRare` Prefix |
| `Buildings/2HeatSources.cs` | 21-34 | `[StaticConstructorOnStartup]` 注入 `CompHeatSourceAtmosphere` |
| `Buildings/1HeatSources.cs` | 30-31 | `ShouldPush` 反射委托 |
| `Buildings/1HeatSources.cs` | 33-46 | `Burns` / `Replaces` |
| `Buildings/1HeatSources.cs` | 48-80 | `CompTick` 主逻辑 |
| `RimWorld/AtmosphereMapComponent.cs` | 2534-2600 | `ApplyContinuousSourcesUnsafe` |
| `RimWorld/AtmosphereMapComponent.cs` | 2931-2963 | `PushHeat(cell, energy)`（房间兜底） |
| `Simulation/LocalAtmosphereEngine.cs` | `ApplyTemperatureDeltas` | 写场 + `NoteThermalExpansion` |

---

## 十一、未确认项

1. **耗氧的稳态速率**：算术已确定（`-0.211 %/tick`，每秒 `-12.66%`，只写一格），但实际是脉冲式（60 ℃ 停机），占空比取决于扩散速率。需在游戏内用密封小房间测氧气下降曲线，才能给出"一台发电机养活多少人"的结论。
2. **`heatPushMaxTemperature` 与 AO `num5` 的交互**：小型机 60 ℃ 停机先于 200 ℃ 衰减，所以 `num5` 永远用不上；大型机 99999 上限，实际由 `num5` 在 200–300 ℃ 截断——但设备格能否到 200 ℃ 取决于扩散速度，需实测。
3. **`DubsPatch` 在 AO 下是否应保留**：装了 DCH 时 `heatPerSecond=25`，AO 几乎不产热；如果玩家装了 DCH 但没建 radiator，发电机就成了"只烧燃料不产热"的摆设。这是原作者的假设，AO 下是否要改回 750，需要权衡。
4. **`ProxyHeat` 的 `smeltSnowRadius` 在 AO 下是否还有雪可融**：AO 的 `GroundTemperature` 与雪覆盖是两套系统，需确认 `SnowGrid` 是否被 AO 接管。
5. **已确认：多格建筑只加热左下角一格**。`CompHeatSourceAtmosphere.CompTick` 传给 `slots.Set` 的是 `parent.Position`（`IntVec3`，多格建筑的左下角），不是中心、也不是铺满 footprint。`SourceSlots.Set(owner, slot, IntVec3 cell, rates)` 本身接受任意格，并通过 `UpdateContinuousSource` 转成 `OwnerOffsetX/OwnerOffsetZ`（`RimWorld/AtmosphereMapComponent.cs:2443`），所以**技术上支持**覆盖整个建筑：patch mod 可以按 footprint 注册多个 slot（`slots.Set(parent, i, cell, rates)`，slot 索引自增，key = `"AO.HeatSource." + i`）。
   - 对 1×1 篝火无影响；对 5×5 / 6×6 发电机，热源偏在角落，房间一侧明显更热。这是 AO 自身的行为，不是本 mod 的问题。
