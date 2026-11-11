# AO 自带兼容补丁的设计分析（1.7/Compat）

> 对象：`F:\SteamLibrary\steamapps\workshop\content\294100\3830470974\1.7\Compat\`
> 源码：`F:\RimworldCodeIL\AtmosphereOverhaul.DBH\`、`...DCH\`、`...VGE\`（dotPeek 反编译）
> 程序集：`AtmosphereOverhaul.DBH.dll`、`.DCH.dll`（MVID `AFE6CD48-37CC-470A-B188-5E942C27BA68`）、`.VGE.dll`（MVID `6FA76766-A72D-44DA-A768-EE68CC44586D`）
> 分析日期：2026-11-11

---

## 0. 结论摘要

1. AO 的兼容补丁**不修改目标 mod 的建筑与逻辑**，只劫持它们调用的原版热力 API。
2. 三个包：DBH（水/饮用/蒸汽，热量部分与 DCH 共用代码）、DCH（暖气/空调/冷库/吊扇）、VGE（氧气推杆/耀斑环境温度）。
3. 加热：**单格**热源，`TemperaturePerTick = u × rate`，**不产生风**。
4. 制冷：**两格**平分热源，**产生 15 m/s 出风**，**抽湿到 40% RH**，**生成冷凝水**，并经室外机以 1.25 倍排热。
5. "风"不是冲量，而是让该格速度**指数趋近**目标值，在 solver 之后执行。
6. `ToleranceK`/`BandK`/`OutletWind`/`HotSideFactor` 四个 `const` 是文档性的，代码里全部用字面量。

---

## 1. 三个兼容包的定位

| 包 | 目标 mod | 文件数 | 主要内容 | Hook 方式 |
|---|---|---|---|---|
| `AtmosphereOverhaul.DBH.dll` | Dubs Bad Hygiene | 23 个 .cs | 水系统、饮用、管道、蒸汽、吊扇、热量（`DbhHeat`） | `PatchAll` + 显式 `Patch` |
| `AtmosphereOverhaul.DCH.dll` | Dubs Central Heating | 7 个 .cs | 暖气片、房间温控机、冷库机、空调、吊扇 | 同 DBH，`PatchAll` + `DbhHeat.Patch` |
| `AtmosphereOverhaul.VGE.dll` | Vanilla Gravship Expanded | 3 个 .cs | 氧气推杆桥接、太阳耀斑环境温度 | 单点 `Patch` + 静态委托 |

DCH 包只有两个入口文件：

- `DchCompat.cs`（39 行）：Harmony id `h7lu.AtmosphereOverhaul.DCH`，`PatchAll(assembly)` + `DbhHeat.Patch`。
- `1DchCompat.cs`（20 行）：`DchMaintainComponent : MapComponent`，每 240 tick 调 `DbhHeat.Maintain(map)`。

`DbhCompat.HasAtmosphere(Map)` 是所有兼容逻辑的总开关：

```csharp
public static bool HasAtmosphere(Map map)
{
    AtmosphereMapComponent component = map?.GetComponent<AtmosphereMapComponent>();
    return component != null && component.HasEngine;
}
```

没有 AO 引擎的地图，全部回退到原版行为。

---

## 2. DCH 的核心手法：transpiler 劫持原版 API

`DbhHeat.Patch`（`DbhHeat.cs:55-64`）对四个 `CompTick` 挂 Hook：

```csharp
Route(harmony, AccessTools.Method(typeof (CompRadiator),    "CompTick"));  // 暖气片
Route(harmony, AccessTools.Method(typeof (CompRoomUnit),    "CompTick"));  // 房间温控机
Route(harmony, AccessTools.Method(typeof (CompFreezerUnit), "CompTick"));  // 冷库机
MethodInfo original = AccessTools.Method(typeof (CompAirconBaseUnit), "CompTick");
harmony.Patch(original,
    prefix:     new HarmonyMethod(typeof (DbhHeat), "Enter"),
    transpiler: new HarmonyMethod(typeof (DbhHeat), "HotSideTranspiler"),
    finalizer:  new HarmonyMethod(typeof (DbhHeat), "Leave"));
```

### 2.1 三件套

| 补丁 | 作用 |
|---|---|
| `Enter(ThingComp __instance)` | `device = __instance.parent`（`[ThreadStatic]`，避免递归污染） |
| `Transpiler` | 把对 `GenTemperature.ControlTemperatureTempChange` 的调用换成 `DbhHeat.Control`；把对 `Room.Temperature` setter 的调用换成 `DbhHeat.SetRoomTemperature` |
| `HotSideTranspiler` | 仅空调：把 `GenTemperature.PushHeat` 换成 `DbhHeat.HotSide` |
| `Leave(Exception)` | `device = null` |

`Transpiler` 里有自检：若某方法的 `control` 调用数或 `write` 调用数为 0，打警告

```csharp
Log.Warning($"[AtmosphereOverhaul] DBH heat routing incomplete in {type}.{name} (thermostat calls {control}, room writes {write}).");
```

这说明作者知道"目标 mod 改版后 hook 可能失配"，用日志兜底。

### 2.2 被抑制的副作用

原版 `ControlTemperatureTempChange` 除了算热量，还会把整个房间的温度写成目标值。AO 版的重写版 `SetRoomTemperature` 直接吃掉这个副作用：

```csharp
public static void SetRoomTemperature(Room room, float value)
{
    if (room == null || (device != null && DbhCompat.HasAtmosphere(room.Map))) return;
    room.Temperature = value;   // 只有非 AO 地图才执行
}
```

这正是 AO 在 `RoomTemperatureWriteHook` 里做同一件事的 DCH 版本：AO 地图上房间温度只能由场决定。

---

## 3. 加热装置

### 3.1 判定与升温逻辑

`Control`（`DbhHeat.cs:167-219`）的加热分支（`energyLimit > 0`）：

```csharp
c.aim = targetTemperature + 2f;                    // 死区 +2 K
c.u   = Clamp01((c.aim - celsius) / 1f);           // 1 K 线性带宽
float num = Mathf.Abs(energyLimit) / c.interval;   // 每 tick 额度
c.rate = 1f * c.u * num;                           // ℃/tick
```

- `celsius` 取 `AtmosphereInterface.TryGetTemperature(map, openCell)`，即**该格的空气温度**，不是房间平均。
- `c.interval` 由 `ControllerFor` 动态测量：`Clamp(now - lastTick, 1, 2500)`。
- `return c.u * energyLimit` 回传给 DCH 当"实际热交换量"，用于耗电。

### 3.2 写入形式

```csharp
AtmosphereSourceRates rates = new AtmosphereSourceRates {
    TemperaturePerTick = c.rate,          // 单格时全额
    MaintainTargetWind = on,              // 加热分支 on = false
    TargetWind = vector2_1,               // Zero
    TargetWindBlendPerTick = 0.1f
};
c.slots.Set(device, 0, intVec3_1, rates);
```

`openCell` 的选取（`OpenCell`，`:297-310`）：

1. 若传入 `cell` 本身可通行 → 用它；
2. 否则设备朝向前格（`Rotation.FacingCell`），若出界或不可通行则取反向；
3. 再不行取四邻第一个可通行格；
4. 都不行返回原 `cell`。

**只有一格**。不是房间、不是半径、不是视锥。

### 3.3 热量如何扩散

`SourceSlots.Set` → `AtmosphereInterface.RegisterContinuousSource` / `UpdateContinuousSource`。之后由 `ApplyContinuousSourcesUnsafe` 每仿真 tick 取用：

```csharp
pendingTemperatureDeltas[cellIndex] += ratesPerTick.TemperaturePerTick * (float)deltaTicks;
```

再进入 `AdvanceProvisional` 的平流与扩散。因为本地模型是二维单层、没有浮力项，热气不会自发上升，只能靠：

- 半拉格朗日平流（被速度场带走）；
- 温度扩散（`TemperatureDiffusion`）；
- 与相邻格的压力耦合产生水平流动。

---

## 4. 制冷装置

制冷分支（`energyLimit < 0`）比加热多四件事。

### 4.1 两格平分

```csharp
IntVec3 dir      = (openCell - device.Position) != IntVec3.Zero ? (openCell - device.Position) : AwayFromWall(device, map);
IntVec3 axis     = Mathf.Abs(dir.x) >= Mathf.Abs(dir.z)
                 ? new IntVec3(dir.x > 0 ? 1 : -1, 0, 0)
                 : new IntVec3(0, 0, dir.z > 0 ? 1 : -1);
IntVec3 second   = openCell + axis;
bool twoCells    = GenGrid.InBounds(second, map) && !GenGrid.Impassable(second, map);

rates.TemperaturePerTick = twoCells ? c.rate * 0.5f : c.rate;
c.slots.Set(device, 0, openCell, rates);
if (twoCells) c.slots.Set(device, 1, second, rates);
```

即冷气沿出风主轴铺两格，各承担一半。主轴取 `|dx|` 与 `|dz|` 较大者，所以只沿四个正方向之一，不斜向。

### 4.2 风：速度钳制式混合

```csharp
bool on = !heat && comp != null && c.u > 0;      // comp = CompAirconIndoorUnit
if (on) {
    Vector2 v = new Vector2(dir.x, dir.z);
    targetWind = v.normalized * 15f;             // OutletWind
}
rates.MaintainTargetWind     = on;
rates.TargetWind             = targetWind;
rates.TargetWindBlendPerTick = 0.1f;
```

执行点在 solver 之后。非 temporal patch 格走 `ApplyTargetWindSourcesAfterSolverUnsafe`：

```csharp
float k = 1f - Mathf.Pow(1f - rates.TargetWindBlendPerTick, lastApplicationDeltaTicks);
Vector2 target = Vector2.ClampMagnitude(rates.TargetWind, Mathf.Max(0f, settings.MaxWindSpeed));
array[cellIndex]  += (target.x - array[cellIndex])  * k;
array2[cellIndex] += (target.y - array2[cellIndex]) * k;
current.LastApplicationDeltaTicks = 0;           // 防重复
```

temporal patch 格走 `ApplyPatchTargetWindAfterSolverUnsafe` → `temporalPatchManager.ApplyOwnedTargetWind(...)`，公式相同。

还有一个"队列版"路径 `FlushContinuousIntervalToQueues`，把 `(target - current) × k` 作为速度增量入队，供异步仿真任务消费：

```csharp
if (ratesPerTick.MaintainTargetWind && engine != null) {
    LocalAtmosphereSample sample = engine.SampleSnapshot(source.CellIndex);
    temporalPatchManager.TryBlendSample(source.CellIndex, ref sample);
    Vector2 val = Vector2.ClampMagnitude(ratesPerTick.TargetWind, settings.MaxWindSpeed);
    float num3 = 1f - Mathf.Pow(1f - Mathf.Clamp01(ratesPerTick.TargetWindBlendPerTick), num2);
    EnqueueVelocityExternalDelta(source.CellIndex, (val.x - sample.VelocityX) * num3, (val.y - sample.VelocityZ) * num3);
}
```

三种路径的计算完全一致，区别只是落在哪个缓冲。

**关键性质**：`MaintainTargetWind` 每 tick 都会把速度重新拉向目标，所以它对抗：

- 压力投影产生的回流；
- 相邻格的粘性扩散；
- `DampVelocity` / `ApplyOpenBoundarySponge` 的衰减。

净效果是一股"持续吹"的定速风，而不是一次性的冲量。

### 4.3 除湿与冷凝水

只有 `CompAirconIndoorUnit` 存在时才执行（`Condense`，`:221-259`）：

```csharp
float share = Mathf.Clamp01(0.5f * (float)c.interval / 250f);
float vapor = Mathf.Max(0f, TryGetAbsoluteVapor - 0.4f * VaporCapacity(celsius, settings)) * share;
float mist  = Mathf.Max(0f, TryGetMist) * share;
AddMoistureDelta(-vapor);
AddMistDelta(-mist);
poolKg += (vapor + mist) / WaterMath.HpaCellPerKg(celsius);
c.condensationRate = (vapor * latent) / c.interval;
```

- 目标 **40% RH**（`DryGoal = 0.4f`），不是 0。
- 雾滴全部抽走。
- `share = 0.5 × interval / 250`：每 250 tick（约 10 秒）抽掉当前可抽量的一半；`interval` 越小抽得越少。这是一个**按时间比例**的抽湿速率，不是瞬时抽干。
- `poolKg` 每满 0.25 kg 生成一个 `AO_Water` 物品，一次最多 20 个（`MaxUnitsPerRun`），落到 `openCell`。
- 冷凝潜热返回给 `controller.condensationRate`，转手进室外机。

**冷库机（`CompFreezerUnit`）没有 `CompAirconIndoorUnit`，所以不除湿、不出风**——只走两格降温。这是一个容易忽略的差异。

### 4.4 室外机排热

`HotSide`（`:84-109`）：

```csharp
Controller c = ControllerFor(device);
PlumbingNet net = comp.PipeComp?.pipeNet;
float sum = 0f;
if (net != null)
    foreach (Controller other in controllers.Values)
        if (other.active && other.net == net && other.rate < 0f)
            sum += -other.rate + other.condensationRate;   // 制冷量 + 冷凝潜热

float share = (net == null || net.OutdoorUnitCapacitySum <= 0f)
            ? 1f
            : Mathf.Clamp01(comp.Capacity / net.OutdoorUnitCapacitySum);

c.rate = 1.25f * sum * share;                              // HotSideFactor
c.outlet = float.NaN;
c.slots.Set(device, 0, OpenCell(cell, map, device), new AtmosphereSourceRates { TemperaturePerTick = c.rate });
```

- `1.25` 近似 COP 倒数：每搬走 1 份热，向室外多排 0.25 份功。
- 同管网内所有制冷机组汇总，按室外机容量占比分摊——多台室内机共享一台室外机会按比例分热。
- 排到室外机 `OpenCell` 一格。
- `outlet = NaN` 使 `Inspect` 返回 null，所以室外机不显示出风口信息。

---

## 5. DBH 天花板风扇

`Building_CeilingFan_Tick_DbhPatch.Prefix` 直接 `return false` 跳过原版 Tick，自行实现：

```csharp
if (!__instance.Spawned || !DbhCompat.HasAtmosphere(__instance.Map)) return true;

__instance.FanRotation += __instance.FanSpeed;
if (__instance.FanRotation > 360f) __instance.FanRotation = 0f;
__instance.FanSpeed = DubUtils.FInterpTo(__instance.FanSpeed, TargetSpeed(__instance), 0.016f, 0.08f);

if (!Gen.IsHashIntervalTick(__instance, 250)) return false;   // 每 250 tick 才更新源

bool on = __instance.ShouldPushHeatNow;
float t   = Mathf.Clamp01(Mathf.InverseLerp(10f, 40f, __instance.AmbientTemperature));
TargetSpeed(__instance) = on ? (2f + 3f * t) : 0f;             // 2..5
if (__instance.powerComp != null)
    __instance.powerComp.PowerOutput = on ? -__instance.powerComp.Props.PowerConsumption : -15f;

float speed = on ? Mathf.Lerp(0.5f, 1.5f, t) : 0f;
float radius = Mathf.Max(def.size.x, def.size.z) * 0.5f;
float outer  = radius + 2f;

foreach (IntVec3 cell in GenAdj.OccupiedRect(__instance).ExpandedBy(3)) {
    if (!InBounds(cell) || Impassable(cell)) continue;
    float dx = cell.x + 0.5f - center.x, dz = cell.z + 0.5f - center.z;
    float r  = Mathf.Sqrt(dx*dx + dz*dz);
    if (r < 0.3f || r > outer) continue;
    float falloff = r < radius
                  ? r / Mathf.Max(0.5f, radius)
                  : Mathf.Lerp(1f, 0.4f, Mathf.Clamp01((r - radius - 1f) / 1f));
    Vector2 tangent = new Vector2(-dz/r, dx/r) + 0.3f * new Vector2(dx/r, dz/r);   // 切向 + 0.3 径向
    rates.MaintainTargetWind     = speed > 0f;
    rates.TargetWind             = tangent.normalized * (speed * falloff);
    rates.TargetWindBlendPerTick = 0.05f;
    slots.Set(__instance, i++, cell, rates);
}
slots.ZeroFrom(__instance, i);
```

- 风扇**不加热、不降温、不产压**，只把圆盘内的空气绕轴旋转。
- 方向 = 切向为主 + 30% 径向（螺旋），内圈线性衰减，外圈从 1 降到 0.4。
- `TargetSpeed` 2..5 由环境温度决定（10 ℃ 以下最低、40 ℃ 最高）。
- `blend = 0.05`，比空调的 0.1 更"柔"，因为它是持续旋转而非定点喷射。
- 功率：开启时按 `PowerConsumption`，关闭时 `-15f`（待机耗电）。

---

## 6. 支撑结构：SourceSlots 与 ContinuousSource

`SourceSlots`（`AtmosphereOverhaul.Buildings.SourceSlots`）是连续源的槽位包装：

```csharp
public void Set(Thing owner, int slot, IntVec3 cell, AtmosphereSourceRates rates)
{
    while (handles.Count <= slot) handles.Add(AtmosphereSourceHandle.Invalid);
    AtmosphereSourceHandle handle = handles[slot];
    if (!handle.IsValid) AtmosphereInterface.TryGetContinuousSourceHandle(map, owner, prefix + slot, out handle);
    if (!handle.IsValid || !AtmosphereInterface.UpdateContinuousSource(map, owner, handle, cell, rates))
        handle = AtmosphereInterface.RegisterContinuousSource(map, owner, prefix + slot, cell, rates);
    handles[slot] = handle;
}

public void ZeroFrom(Thing owner, int from)
{
    for (int i = from; i < handles.Count; i++)
        if (handles[i].IsValid) AtmosphereInterface.UpdateContinuousSource(owner.Map, owner, handles[i], default);
}
```

- 槽位 key = `prefix + slot`（`"AO.DBH.TempControl.0"`、`"AO.DBH.CeilingFan.3"`）。
- `ZeroFrom` 把 rates 置零而不是注销，避免反复注册开销。
- `RemoveAll` 在建筑销毁时调 `UnregisterContinuousSources`。

`AtmosphereSourceRates`（10 个字段，`AtmosphereOverhaul.RimWorld`）：

| 字段 | 类型 | 用途 |
|---|---|---|
| `TemperaturePerTick` | float | ℃/tick |
| `MoisturePerTick` | float | 绝对水汽/tick |
| `MistPerTick` | float | 雾密度/tick |
| `WindPerTick` | Vector2 | 速度冲量/tick |
| `PressurePerTick` | float | solver 单位/tick |
| `OxygenPercentPerTick` | float | 氧百分点/tick |
| `GroundTemperaturePerTick` | float | 地温/tick |
| `MaintainTargetWind` | bool | 是否持续拉向目标风 |
| `TargetWind` | Vector2 | 目标风速（m/s） |
| `TargetWindBlendPerTick` | float | 每 tick 趋近比例 |

**只有 8 个数值字段被 `ApplyContinuousSourcesUnsafe` 直接消费**；`MaintainTargetWind`/`TargetWind` 走 solver 之后的独立路径（见 4.2）。

---

## 7. VGE 包

`VgeCompatStartup` 只做两件事：

```csharp
new Harmony("h7lu.AtmosphereOverhaul.VGE").Patch(
    AccessTools.Method(typeof (CompResourceTrader_OxygenPusher), "CompTickRare"),
    new HarmonyMethod(typeof (OxygenPusherBridge), "Prefix"));

VacuumEnvironment.AmbientOverride = VgeCompatStartup.SolarFlareAmbient;
```

`SolarFlareAmbient`：太阳耀斑期间返回 120 ℃，否则 `null`（走默认 -75 ℃）。

`OxygenPusherBridge.Run` 是"把 VGE 的氧气推杆改造成 AO 的体积源"：

```csharp
const float TargetPa   = 95000f;
const float StandardPa = 101325f;
const float M3PerUnit  = 5f;
const float CellColumnMetres = 5f;
const int   MaxSamples = 100;

// 1. 采样房间压力（每 max(1, cellCount/100) 个格子取一个）
float meanPa = sum / count;

// 2. 还差多少体积
float need = (TargetPa - meanPa) / StandardPa * cellCount * 5f;
if (need <= 0.01f) { comp.LowPowerMode = true; return; }

// 3. 资源换算
float perTick  = compProps.consumptionPerTick * 250f;
float amount   = Mathf.Min(perTick, available);
float volume   = amount * 5f;
if (volume > need) { amount *= need / volume; volume = need; }

// 4. 注入：温度夹在 15..25 ℃，氧 21%
float density = Mathf.Max(1f, meanPa / 1000f);
float cellVolumes = volume * 101.325f / density / 5f;
AtmosphereInterface.AddVolumeSourceAtTemperature(map, cell1, cellVolumes, 21f, Mathf.Clamp(celsius, 15f, 25f));
pipeNet.ExtraConsumptionThisTick += amount;
```

设计要点：

- 目标 **95 kPa**（不是 101.325），留了余量。
- 用 `AddVolumeSourceAtTemperature` 而不是 `AddPressureDelta`——因为要**同时**补质量、补氧、补温度，这正是体积源的语义。
- 温度取当前格并夹在 15..25 ℃，避免"注入冷气"。
- 只在 `map.Biome.inVacuum` 时接管，否则 `return true` 走原版。

**一个潜在问题**：它用 `AtmosphereInterface.TryGetPressurePascals` 读压力。该 API 在可压缩模式下用 `100000 + solver×1000` 换算，而 `solver = StoredPressurePa / SolverPascalsPerUnit`（默认 12），所以读数 ≈ `100000 + 83.3 × Δp`。真空地图上 `AmbientSurfacePressurePa = 0`，房间要真实达到 95 kPa，读数会是约 791 万 Pa，`need` 为负，泵会判定"压力已足"而长期 `LowPowerMode`。这条差异是否在实际游戏里发作，取决于真空地图上可压缩模式是否开启，需要实测确认。

---

## 8. 设计后果与问题清单

### 8.1 会形成温度振荡

`aim = target ± 2 K`，`u = clamp01((aim - ambient)/1)`。室温到 `target+1` 才开始降功率，到 `target+2` 归零。叠加 AO 的户外松弛（`OutdoorTemperatureRelaxation` 持续把无顶格拉向室外温），设备会在全功率与停机之间来回。

### 8.2 常量是文档，不是开关

```csharp
public const float ToleranceK     = 2f;
public const float BandK          = 1f;
public const float OutletWind     = 15f;
public const float HotSideFactor  = 1.25f;
private const float DryGoal       = 0.4f;
private const float SweptShare    = 0.5f;
private const int   MaxUnitsPerRun = 20;
```

但 `Control`/`HotSide` 里全部写的是字面量 `2f`、`1f`、`15f`、`1.25f`。`BandK` 的 `find_usages` 返回 0。改这些 `const` 不会影响行为。

### 8.3 加热无风、制冷有风，物理上不对称

真实的热源也会产生浮力羽流与卷吸。但 AO 本地是二维单层、没有浮力项，作者没有为加热器伪造风，只为"机械送风"的空调与风扇加了风源。这是一个诚实的取舍，但也意味着"热空气上升"在 DCH 设备层面同样不成立。

### 8.4 15 m/s 在 AO 里是强风量级

`WindStructuralLoad` 的 `DefaultWindDamageBaseSpeed = 2.24`、`SpeedPerHitPoint = 0.056`，15 m/s 足以触发结构损伤判定；`WindTransport` 也会尝试吹动物品。空调出风可能把房间里的东西吹走。

### 8.5 冷库不出风也不除湿

`Condense` 与风都要求 `CompAirconIndoorUnit`，冷库机没有这个 comp。所以冷库是"纯降温"，不做除湿——与真实冷库（会结霜、需要除霜）不同。

### 8.6 除湿是"按时间比例抽"，不是"设定湿度"

`share = 0.5 × interval / 250`，所以 250 tick 的设备一次抽掉可抽量的一半；若设备 tick 更频繁，单次抽得更少，但总量接近。它没有"维持 40% RH"的闭环，只有"每次削掉一半超额"的开环。

### 8.7 VGE 的压力读数依赖有争议的 API

见第 7 节末尾。如果可压缩模式在真空地图开启，氧气推杆的判定会失真。

---

## 9. 与本 patch 的关系

这套"transpiler 劫持原版 API"的模式，正是本 patch 四个兼容模块缺的：

| patch 模块 | 现状 | 应改 |
|---|---|---|
| `Compat.CentralizedClimateControl` | 只读真空判定 | 找到 CCC 调用 `GenTemperature.*` 的位置，替换为写 AO 场 |
| `Compat.VanillaTemperatureExpanded` | 直接写 `Room.Temperature` | 走被抑制的那条路，应改为 transpiler 替换 `ControlTemperatureTempChange` |
| `Compat.GoodClimate` | 改 `CompAirVent.speedLimit` | 相对安全，但只是阀门，不涉及热力 |
| `Compat.OmniCrafter` | `AddDeltas` 每步叠加 | 应改为 `RegisterContinuousSource` + `TemperaturePerTick`，由 AO 按 tick 差量应用 |

其中第 4 条最关键：`AddDeltas` 是"立即塞进缓冲"，同一步多次调用会叠加；而 `RegisterContinuousSource` 是"登记速率"，AO 会按 `now - lastAppliedTick` 自动积分，且天然跨异步仿真任务。`DbhHeat` 用的正是后者。

---

## 10. 关键源码行号索引

| 内容 | 位置 |
|---|---|
| 常量与字段 | `DbhHeat.cs:24-53` |
| `Patch`（四个 Hook） | `DbhHeat.cs:55-64` |
| `HotSideTranspiler` | `DbhHeat.cs:66-82` |
| `HotSide`（室外机） | `DbhHeat.cs:84-109` |
| `ControllerFor`（interval 测量） | `DbhHeat.cs:111-125` |
| `Route` / `Enter` / `Leave` | `DbhHeat.cs:127-140` |
| `Transpiler` | `DbhHeat.cs:142-165` |
| `Control`（加热/制冷主逻辑） | `DbhHeat.cs:167-219` |
| `Condense` | `DbhHeat.cs:221-259` |
| `CondenseCell` | `DbhHeat.cs:261-288` |
| `SetRoomTemperature`（抑制写房间） | `DbhHeat.cs:290-295` |
| `OpenCell` / `AwayFromWall` | `DbhHeat.cs:297-317` |
| `Maintain` / `Remove` / `Inspect` | `DbhHeat.cs:319-356` |
| `Controller` 内部类 | `DbhHeat.cs:358-372` |
| 风扇 Prefix | `Building_CeilingFan_Tick_DbhPatch.cs` |
| DCH 启动 | `DchCompat.cs:15-38` |
| DCH 维护组件 | `1DchCompat.cs:12-19` |
| VGE 启动 | `VgeCompat.cs:17-32` |
| VGE 氧气推杆 | `1VgeCompat.cs:17-125` |
| `SourceSlots` | `AtmosphereOverhaul.Buildings.SourceSlots`（主 DLL） |
| `AtmosphereSourceRates` | `AtmosphereOverhaul.RimWorld.AtmosphereSourceRates`（主 DLL） |
| `ApplyContinuousSourcesUnsafe` | `AtmosphereMapComponent`（主 DLL） |
| `ApplyTargetWindSourcesAfterSolverUnsafe` | `AtmosphereMapComponent`（主 DLL） |
| `ApplyPatchTargetWindAfterSolverUnsafe` | `AtmosphereMapComponent`（主 DLL） |
| `FlushContinuousIntervalToQueues` | `AtmosphereMapComponent`（主 DLL） |
