# AO 兼容层重构设计（推倒重来）

> 前提：完全废弃现有四个 `Compat.*` 模块的实现。本文只给设计，不含代码。
> 约束：**尽可能不使用 transpiler**。
> 日期：2026-11-11

---

## 0. 一句话结论

现有 patch 的思路是"**给每个 mod 打补丁**"，这是错的。正确的思路是"**给 AO 补三个它没有的能力层**"，让所有 mod 自动受益：

1. **真空/气压桥** —— 让原版 `Room.Vacuum` 反映 AO 的气压场（Postfix）。
2. **环境稳定器** —— 一个通用 comp，挂在任何"封闭环境"建筑上，写温度/压力/氧/湿度/风。
3. **房间交换器** —— 一个通用 comp，在两个房间之间做**成对守恒**的能量与湿度搬运。

三个能力层都用 XML + Postfix/Prefix 实现，**零 transpiler**。

---

## 1. 关键事实：AO 已经自动接管了所有原版系温控建筑

`AtmosphereOverhaul.Buildings.FacilityCompInjection`（`[StaticConstructorOnStartup]`）：

```csharp
foreach (ThingDef thingDef in DefDatabase<ThingDef>.AllDefsListForReading) {
    if (thingDef.thingClass != null
        && !thingDef.HasComp(typeof(CompTempControlAtmosphere))
        && (typeof(Building_Heater).IsAssignableFrom(thingDef.thingClass)
         || typeof(Building_Cooler).IsAssignableFrom(thingDef.thingClass)
         || (typeof(Building_TempControl).IsAssignableFrom(thingDef.thingClass)
             && thingDef.GetCompProperties<CompProperties_TempControl>() != null)))
    {
        thingDef.comps.Add(new CompProperties_TempControlAtmosphere());
    }
}
```

配合 `TempControlSubclassPatches`（扫 `Building_TempControl` 所有非抽象子类，给 `TickRare` 打 Prefix+Finalizer）：

```csharp
Prefix:  __state = !FacilityPatchUtility.RunTempControl(__instance);   // 有 AO comp 就接管
Finalizer: if (__state) RoomTemperatureWriteHook.Exit();
```

**结论**：任何第三方 mod 的 `Building_Heater` / `Building_Cooler` / `Building_TempControl` 子类，只要挂了 `CompProperties_TempControl`，AO 加载后就会自动被 `CompTempControlAtmosphere` 接管。

已确认的实例：

| Mod | 类型 | 基类链 | AO 是否已接管 |
|---|---|---|---|
| VTE | `Building_TwoCellHeater` | `Building_HeaterWithOffset` → `Building_TempControl` | ✅ 自动 |
| VTE | `Building_TwoCellCooler` | 同上 | ✅ 自动 |
| VTE | `Building_AcUnit` | `Building_TempControl` | ✅ 自动 |
| VTE | `Building_AcControlUnit` | 管网控制器，非温控建筑 | ❌ 不接管（也不需要） |
| 原版 | `Heater` / `Cooler` | — | ✅ 显式 Prefix |

**所以 VTE 模块根本不需要存在。** 现有 `Compat.VanillaTemperatureExpanded`（"接管 4 个温控类"）是重复劳动，而且它直接写 `Room.Temperature` 反而破坏了 AO。

同理，任何"继承原版温控建筑"的 mod 都不需要 patch。

---

## 2. 真正需要解决的只有三类

| 类别 | 特征 | 例子 |
|---|---|---|
| **A. 读真空/气压** | 用 `Room.Vacuum`、`VacuumUtility.GetVacuum` 判定环境 | CCC、GC 通风口 |
| **B. 自造环境** | 自己维持温度/压力/氧/湿度，但不写物理场 | Omni 穹顶、Omni 生物圈 |
| **C. 房间间交换** | 直接写 `Room.Temperature` 做两房间热交换 | GC 通风口、DCH 管网 |

对应三个能力层，一一解决。

---

## 3. 能力层 1：真空/气压桥

### 目的

让原版语义的 `Vacuum` 反映 AO 的真实气压。这样所有读 `Room.Vacuum` 的 mod（CCC、GC、原版 VacBarrier 等）**不改一行代码就正确**。

### 实现

```csharp
[HarmonyPatch(typeof(Room), nameof(Room.Vacuum), MethodType.Getter)]
static class Room_Vacuum_Bridge
{
    static void Postfix(Room __instance, ref bool __result)
    {
        if (!__result) return;                       // 原版判真空，尊重
        if (!AoField.TryGetMapEngine(__instance.Map, out var engine)) return;   // 非 AO 地图
        if (!AoField.TryGetRoomMeanPressure(__instance, out float pa)) return;
        __result = pa < AoConstants.VacuumThresholdPa;   // 默认 10000 Pa
    }
}
```

- **只读增强**，不修改 AO 状态。
- 阈值可配置（默认 10 kPa，约 0.1 atm）。
- `VacuumUtility.GetVacuum` 同理加一个 Postfix（它内部读 `Room.Vacuum`，可能不需要）。

### 覆盖 mod

- **CCC**：所有 `roomOrAdjacent.Vacuum` 与 `VacuumUtility.GetVacuum` 判定自动正确。
- **GC 通风口**：`GC_IE_ventAllowIntensiveExchange` 里的 `room.Vacuum > 0` 判定自动正确（真空房间里 GC 通风口不再强化交换）。
- **原版**：`Building_VacBarrier`、真空伤害、`GenTemperature` 的真空分支。

### 风险

- AO 内部是否读 `Room.Vacuum`？已确认 `VacuumEnvironment` 用 `map.Biome.inVacuum`，不用 `Room.Vacuum`；`CompAirVent` 不读。所以污染面很小。
- 需要防护：只在 `map.GetComponent<AtmosphereMapComponent>()?.Engine != null` 时生效。

---

## 4. 能力层 2：环境稳定器 `CompFieldStabilizer`

### 目的

给任何"自造环境"的建筑一个统一的、可配置的物理写入器。不改目标 mod 的任何代码，只给它加一个 comp。

### 配置（`CompProperties_FieldStabilizer`）

```csharp
public enum StabilizerScope { Rect, Area, Room }

public sealed class CompProperties_FieldStabilizer : CompProperties
{
    // 目标值
    public float targetTemperatureCelsius = 21f;
    public float targetPressureRatio      = 1f;      // 1 = 环境压
    public float targetOxygenPercent      = 21f;
    public float targetRelativeHumidity   = 0.5f;
    public float targetWindSpeed          = 0f;

    // 开关
    public bool controlTemperature = true;
    public bool controlPressure    = true;
    public bool controlOxygen      = true;
    public bool controlHumidity    = true;
    public bool controlWind        = false;

    // 限幅（每次写入的最大量）
    public float maxTemperatureDeltaK = 20f;
    public float maxPressureDeltaPa   = 2000f;
    public float maxOxygenDelta       = 5f;
    public float maxHumidityDelta     = 0.2f;

    // 作用域
    public StabilizerScope scope = StabilizerScope.Rect;
    public int rectWidth = 11, rectHeight = 11;
    public string areaName;           // scope = Area 时必填
    public bool roofedOnly = false;   // 只处理有顶格

    // 节奏
    public int tickInterval  = 250;
    public int cellsPerStep  = 500;
    public bool requirePower = true;
    public float idleWatts   = 5f;
    public float runningWatts = 500f;
}
```

### 行为（`CompFieldStabilizer`）

每次 `CompTick` 到 `tickInterval`：

1. **刷新格集**（每 250 tick 或拓扑变化时）：`Rect` → `GenAdj.OccupiedRect(...)`；`Area` → `Area.ActiveCells`；`Room` → `Room.Cells`。
2. **游标轮转**：取 `cellsPerStep` 格，从 `cursor` 开始，环形。
3. **逐格**：
   ```csharp
   if (AoField.TryRead(cell, out var s)) {
       if (ctrlT)  AoField.AddTemperature(cell, Clamp(target - s.T,  -maxT,  +maxT));
       if (ctrlP)  AoField.AddPressure(cell,    Clamp(targetPa - s.Pa, -maxP, +maxP));   // 用体积源，见下
       if (ctrlO2) AoField.AddOxygen(cell,      Clamp(targetO2 - s.O2, -maxO, +maxO));
       if (ctrlRH) AoField.AddMoisture(cell,    Clamp(targetVapor - s.Vapor, -maxH, +maxH));
       if (ctrlW)  AoField.AddWind(cell,        targetWindDir * (targetWindSpeed - s.WindSpeed) * blend);
   }
   ```
4. **压力特殊处理**：不可压模式下 `ApplyThermodynamicPressureBaseline` 每步重算 `Pressure.Read`，直接 `AddPressureDelta` 会被抹掉。所以：
   - 目标 = 环境压且当前是真空 → 用 `AddVolumeSource(cellVolumes, 21f, targetCelsius - currentT)` 补气；
   - 目标 = 环境压且当前是超压 → 用 `AddPressureDelta` 负值（可压缩模式下有效）；
   - 两者都不满足 → 跳过并记日志。

### 挂载（纯 XML，不改 Omni 代码）

```xml
<Patch>
  <Operation Class="PatchOperationAdd">
    <xpath>Defs/ThingDef[defName="OmniForceFieldDome"]/comps</xpath>
    <value>
      <li Class="JeremieAOPatch.CompProperties_FieldStabilizer">
        <scope>Rect</scope>
        <rectWidth>11</rectWidth>
        <rectHeight>11</rectHeight>
        <targetTemperatureCelsius>21</targetTemperatureCelsius>
        <targetOxygenPercent>21</targetOxygenPercent>
        <targetRelativeHumidity>0.5</targetRelativeHumidity>
        <tickInterval>250</tickInterval>
        <cellsPerStep>500</cellsPerStep>
      </li>
    </value>
  </Operation>

  <Operation Class="PatchOperationAdd">
    <xpath>Defs/ThingDef[defName="OmniBiosphere" or defName="OmniBiosphere_mini"]/comps</xpath>
    <value>
      <li Class="JeremieAOPatch.CompProperties_FieldStabilizer">
        <scope>Area</scope>
        <areaName>OmniBiosphereArea</areaName>
        <targetTemperatureCelsius>21</targetTemperatureCelsius>
        <targetOxygenPercent>21</targetOxygenPercent>
        <targetRelativeHumidity>0.5</targetRelativeHumidity>
        <cellsPerStep>500</cellsPerStep>
      </li>
    </value>
  </Operation>
</Patch>
```

**注意**：Omni 的 `CompBiosphere.SelectedArea` 是运行时玩家选的 `Area`，不是固定 Def。所以 `scope=Area` 需要额外一个"从同建筑的 Omni comp 读取 Area"的桥：
- 反射读 `CompBiosphere.SelectedArea` → 得到 `Area`；
- 或读 `areaName` + `Find.CurrentMap.areaManager.GetLabeled(name)`。

这是唯一一处需要反射读第三方 comp 的地方，但**只是读属性，不是 patch**。

### 覆盖

- Omni 穹顶（Rect 11×11）
- Omni 生物圈（Area）
- 未来任何"封闭环境"建筑（MOD 只需写一行 XML）

---

## 5. 能力层 3：房间交换器 `CompAoRoomExchange`

> ⚠️ **本节已被取代（2026-11-14）**。用户 m00486 明确否决「一把写掉整个 Room」：AO 没有逐格质量，房间级成对写在 AO 上不成立；m00500 又否掉了改模拟步进。GC 主动通风口的最终路线改为 **AirMover 单对端口**，见 `AO-主动气体转移装置设计分析.md`（v2）。本节仅作历史设计留档，不要据此实现。

### 目的

替代"直接写 `Room.Temperature`"的做法，做**成对守恒**的能量搬运。

### 为什么必须成对

AO 没有"从 A 搬到 B"的 API。若只写 `room2.Temperature += dT`，钩子会把 dT 加到 room2 的每一格，room1 不变 → 能量凭空产生/消失。正确做法：

```
ΔT_target = k × (T_a − T_b) × min(m_a, m_b) / m_a
WriteTemp(a, −ΔT_target × m_a / m_b_capacity)   // 扣
WriteTemp(b, +ΔT_target)                        // 加
```

其中 `m = ρ × V`，`V = 格数 × 格面积 × 气柱高`，`cp ≈ 1005 J/(kg·K)`。

### 配置

```csharp
public sealed class CompProperties_AoRoomExchange : CompProperties
{
    public float exchangeRatePerTick = 0.0f;   // 0 = 用 0.8 / 60 tick（GC 原值）
    public bool exchangeTemperature = true;
    public bool exchangeHumidity    = true;
    public bool exchangeOxygen      = false;
    public bool requireBothIndoors  = true;
    public bool requirePower        = true;
    public float idleWatts = 10f;
}
```

### 行为

每 60 tick：

1. 取建筑朝向的两侧格（`pos + FacingCell` 与 `pos − FacingCell`）。
2. 取两个 `Room`；`requireBothIndoors` 时跳过 `PsychologicallyOutdoors`。
3. 取两房间的格列表与平均温/湿度。
4. 按质量加权算搬运量，**成对写**：
   ```csharp
   float q = k * (Ta - Tb) * Math.Min(massA, massB) * Cp;
   AO_AddTemperature(aCells, -q / (massA * Cp));
   AO_AddTemperature(bCells, +q / (massB * Cp));
   ```
5. 湿度同理，用 `AddMoistureDelta` 成对。

### 挂载

```xml
<Operation Class="PatchOperationAdd">
  <xpath>Defs/ThingDef[defName="Vent"]/comps</xpath>
  <value>
    <li Class="JeremieAOPatch.CompProperties_AoRoomExchange">
      <exchangeTemperature>true</exchangeTemperature>
      <exchangeHumidity>true</exchangeHumidity>
      <requireBothIndoors>true</requireBothIndoors>
    </li>
  </value>
</Operation>
```

### 与 GC 的配合

GC 的 `GC_MapComponent.GC_IE_ventIntensiveExchange` 必须被停掉，否则两套逻辑打架：

```csharp
[HarmonyPatch(typeof(GC_MapComponent), "GC_IE_ventIntensiveExchange")]
static class GC_VentExchange_Suppressor
{
    static bool Prefix(Map map) => !AoField.HasEngine(map);   // AO 地图上跳过 GC 原逻辑
}
```

- 这是 **Prefix，不是 transpiler**。
- GC 的 UI 开关、电费、`GC_ThingComp` 全部保留。
- 交换由 `CompAoRoomExchange` 接管，守恒。

---

## 6. 各 mod 的最终接入方式

| Mod / 建筑 | 问题类别 | 接入 | 是否需要 transpiler |
|---|---|---|---|
| VTE 两格加热/冷却、AcUnit | 已是 `Building_TempControl` 子类 | **无需任何操作**（AO 自动接管） | 否 |
| VTE 吊扇 `CompCeilingFan` | 自造风 | 保留原样；如需 AO 风，加 `FieldStabilizer(controlWind)` | 否 |
| CCC 所有机组 | 读 `Room.Vacuum` | 能力层 1（Postfix） | 否 |
| CCC 管网 | 自管气流与温度 | 保留；只修真空判定 | 否 |
| GC 通风口 | 直接写 `Room.Temperature` | 能力层 3 + 抑制 Prefix | 否 |
| GC 加热/制冷 | 改 `energyPerSecond` | AO 已采纳，无需操作 | 否 |
| GC 电费语义 | 与 AO 效率不符 | 可选的 Postfix 修正（低优先级） | 否 |
| Omni 穹顶 | 只读值伪装 | 能力层 2（Rect） | 否 |
| Omni 生物圈 | 只读值伪装 | 能力层 2（Area） | 否 |
| ED | 无建筑 | 无需 | 否 |
| 原版 `Vent` | 开关语义 | AO 已加 `CompAirVent`；再加能力层 3 | 否 |

**全部零 transpiler。**

---

## 7. 不变量与硬约束

新代码必须遵守这 8 条，否则会踩已知的坑：

| # | 约束 | 原因 |
|---|---|---|
| 1 | 任何 A→B 必须成对写正负 | AO 无搬运 API，单写会凭空产能 |
| 2 | 读压力用 `TryGetStaticPressurePascals`（反射 internal） | 公开 `TryGetPressurePascals` 在可压缩模式下失真 83 倍 |
| 3 | 写压力优先用 `AddVolumeSource`，其次 `AddPressureDelta` | 不可压模式下 `ApplyThermodynamicPressureBaseline` 每步重算 `Pressure.Read` |
| 4 | 持续设备用 `RegisterContinuousSource`，一次性用 `AddDeltas` | `AddDeltas` 同步叠加会过冲 |
| 5 | 每步预算 ≤ 500 格，游标轮转 | 单格 7 次反射 ≈ 2 μs，2000 格/步 ≈ 28 ms |
| 6 | 句柄必须在 `PostDeSpawn` 注销 | 否则泄漏 |
| 7 | 不写 `Room.Temperature` | 会触发整房间平移钩子，能量不守恒 |
| 8 | 世界帧的 `FrontStrength`/`UpperPressure` 等 5 字段不可用 | 恒为 0 |

---

## 8. 目录结构

```
Source/JeremieAOPatch/
├── JeremieAOPatch.csproj
├── AopMod.cs                       // Mod 入口、设置
├── AopSettings.cs
├── Core/
│   ├── AoBridge.cs                 // AO 类型/方法的反射缓存（一次解析，缓存委托）
│   ├── AoField.cs                  // 读/写薄封装：温度/压力/氧/湿/风
│   ├── AoUnits.cs                  // Pa↔solver、水汽、能量、质量
│   ├── AoEnergy.cs                 // 成对搬运（TakeEnergy/GiveEnergy）
│   └── AoLog.cs
├── Comps/
│   ├── CompProperties_FieldStabilizer.cs
│   ├── CompFieldStabilizer.cs
│   ├── CompProperties_AoRoomExchange.cs
│   └── CompAoRoomExchange.cs
├── Bridges/
│   ├── RoomVacuumBridge.cs         // Postfix
│   ├── VacuumUtilityBridge.cs      // Postfix（可选）
│   └── GcVentSuppressor.cs         // Prefix（可选，仅当 GC 在场）
└── Defs/
    └── Patches/
        ├── Stabilizer_Dome.xml
        ├── Stabilizer_Biosphere.xml
        └── RoomExchange_Vent.xml
```

对比现有结构（`Api/` + `Compat/` + 四个独立 DLL + `SourceLease` + `CompatRegistry`），新结构**只有两个程序集**：宿主 + 一个 XML 包，不再需要按 mod 拆 DLL。

---

## 9. 实施顺序

### 阶段 0：验证（不改代码）
1. 进游戏建 AO 地图，放一台 VTE 两格加热器，确认 `CompTempControlAtmosphere` 已被自动注入（用 dev mode 的 comp 列表或日志）。
2. 放一台原版 Cooler，确认冷/热两侧的温度场真的在变（而不是房间平均）。
3. 确认 `Room.Vacuum` 在 AO 真空地图上的原版值（大概率恒 false）。

### 阶段 1：能力层 1（1 天）
4. `AoBridge` + `RoomVacuumBridge`。
5. 验证 CCC 在真空地图上停转。

### 阶段 2：能力层 2（3 天）
6. `CompFieldStabilizer` + `AoEnergy`。
7. 挂到 Omni 穹顶，验证穹顶内 250 tick 内温度/氧趋近目标，且能量有来源（电耗）。

### 阶段 3：能力层 3（2 天）
8. `CompAoRoomExchange`。
9. 挂到原版 Vent，验证两房间温度趋同且总能量不变。
10. 加 GC 抑制 Prefix。

### 阶段 4：清理
11. 删除旧 `Compat.*` 四个项目与 `1.7/Compat/` 目录。
12. 删除 `SourceLease` / `CompatRegistry` / `CompatModuleBase`（不再需要按目标 mod 隔离）。

---

## 10. 风险与待验证

| 风险 | 影响 | 验证方式 |
|---|---|---|
| AO 内部也读 `Room.Vacuum` | Postfix 会污染 AO 自己的判断 | 全库 grep `Room.Vacuum`；已知 `VacuumEnvironment` 不用 |
| `CompBiosphere.SelectedArea` 反射失败 | 生物圈稳定器拿不到 Area | 反射读属性名，失败降级为 Rect |
| Omni 的读值 Postfix 与我们的写场冲突 | 读数被覆盖为 21，但场里可能是 50 | 我们的 comp 写场后，两者应一致；冲突时以场为准 |
| `AddVolumeSource` 持续升压 | 压力无限增长 | 只在 `pa < targetPa` 时补，且按差值比例 |
| GC 的 `GC_MapComponent` 类型名变化 | Prefix 失效 | 用 `AccessTools.TypeByName` + 空判 |
| 500 格/步不够大穹顶（如 25×25） | 稳定慢 | 提高到 1000，观察 `projectionStopwatchTicks` |

---

## 11. 与旧方案的本质差异

| 维度 | 旧实现 | 新设计 |
|---|---|---|
| 组织方式 | 每个 mod 一个 DLL + 一个 `ICompatModule` | 三个能力层，与 mod 无关 |
| 补丁方式 | transpiler 替换调用 | Postfix 读值 + Prefix 抑制 + XML 加 comp |
| 温度写入 | 直接写 `Room.Temperature`（VTE 模块） | 只写格，房间只用于选格集 |
| 能量守恒 | 未处理 | 成对搬运，显式守恒 |
| 对 VTE | 接管 4 个类 | **不需要**（AO 已自动） |
| 对 GC | 改 `CompAirVent.speedLimit`（无效） | 抑制 GC 交换 + 自己的守恒交换 |
| 对 Omni | `AddDeltas` 每步叠加 | 通用稳定器 comp，XML 挂载 |
| 代码量 | 4 个兼容 DLL + 26 个 .cs | 2 个程序集 + 约 12 个 .cs |

---

## 12. 明确不兼容清单

> 决策日期：2026-11-11。本节只记录决策与理由，**不落地代码**；About.xml 的措辞在第 12.5 节，属实施阶段动作。

### 12.1 决策：不兼容 VTE - Proxy Heat Patch

| 项 | 值 |
|---|---|
| 名称 | VTE - Proxy Heat Patch |
| packageId | `ewolfy.vtePatch` |
| workshop | `294100/3439148119` |
| 作者 | E Wolfy |
| 依赖 | Harmony、`VanillaExpanded.Temperature`（VTE） |
| 处理方式 | **不写兼容代码**；在 About.xml 的 `description` 中声明不兼容 |

### 12.2 它做了什么

它是 ProxyHeat 的一个 Transpiler 补丁，把 `ProxyHeat.ProxyHeatManager.GetTemperatureOutcomeFor` 里这条早退拆掉：

```csharp
if (GridsUtility.GetEdifice(cell, this.map) != null)
  return result;                      // 格子上有 edifice（家具/建筑）就不代理
```

目的：让 proxy heat 覆盖"可通行家具建筑"所在格。原版 ProxyHeat 只代理空格子，家具格（小人实际坐/工作/睡觉的格）不升温。

### 12.3 为什么不能兼容

1. **它是 IL 层面的 hack，且栈不平衡**。Transpiler 把 `brfalse.s` 改成 `nop`，并删除紧随其后的 `ldarg.2; ret`，但 `call GridsUtility.GetEdifice` 压入的 `Thing` 引用从此无人消费。方法 `ret` 时栈上残留一个 `Thing`。
   - Unity/Mono 不做完整 IL 验证 → 能跑，残留值被丢弃。
   - CoreCLR / 任何开启 IL 验证的运行时 → `InvalidProgramException`。
   - 这不是"可能失效"，是"换运行时必崩"。
2. **方向相反**。AO 的目标是让所有温度读数回到物理场（`TryGetAuthoritativeTemperature`）；这个 patch 让 ProxyHeat 的"读值加成"覆盖更多格子，包括家具格和加热器自身所在格。
3. **关闭开关也关不掉它**。`GetTemperatureOutcomeFor` 只检查 `enableProxyHeatEffectIndoors`，**不检查** `ProxyHeatMod.settings.enableProxyHeat`。即使玩家在 VTE 设置里关掉 proxy heat，这个 patch 仍会让室内家具格继续叠加 `tempOutcome`。
4. **无失败日志**。Transpiler 靠"`brfalse.s` 目标为 `newobj`"的模式匹配；VTE 更新只要多一条指令，匹配落空，patch 静默失效，玩家与作者都不会知道。
5. **它让自引用成立**。移除 `GetEdifice` 早退后，加热器自身所在的 edifice 格也会进入代理列表，`GetTemperatureOutcomeFor` 把 `tempOutcome` 加到自己头上（再被 `Average()` 稀释）。在 AO 下这是纯读数污染。

### 12.4 与本 patch 原有 VTE 模块的关系

原有的 `Source/Compat.VanillaTemperatureExpanded/` 本来就该删除——AO 的 `FacilityCompInjection` 已自动接管所有 `Building_TempControl` 子类（含 VTE 的 `Building_TwoCellHeater`/`Building_TwoCellCooler`/`Building_AcUnit`/`Building_HeaterWithOffset`），本 patch 再接管一次是重复劳动。

ProxyHeat 的处置（此前讨论中的方案 A/B/C，本文档首次落盘）：

- **方案 C（把 `tempOutcome` 转成能量源）作废**——既然声明不兼容，就不再为它做能量翻译。
- **只保留方案 A**：禁用读值代理 + 移植 `GenSight.LineOfSight` 的视线遮挡思想（AO 缺"点源辐射"）。
- 方案 B（只保留融雪/避让等副作用）也不做——副作用依赖同一套代理数据，剥离成本高于重写。

### 12.5 About.xml 措辞（实施阶段再落地）

不建议写成"与 XXX 冲突"（RimWorld 没有这个字段，只能写进 description）。建议：

```xml
<description>
…（原有描述）…

【不兼容 / Incompatible】
- VTE - Proxy Heat Patch (ewolfy.vtePatch)：该 mod 用 IL 栈不平衡的
  Transpiler 移除 ProxyHeat 的 edifice 早退，与 Atmosphere Overhaul 的
  场读数模型方向相反，且无法通过 VTE 设置关闭。请勿同时启用。
</description>
```

如果将来 DSH 侧要强制拦截，可以在启动时检测 `ModLister.GetActiveModWithIdentifier("ewolfy.vtePatch")` 并 `Log.Error` + 提示，但**不做强制禁用**（强制禁用别人的 mod 属于越界）。

### 12.6 不兼容清单的维护规则

- 每条必须写清：**它做了什么、为什么不能兼容、玩家看到的症状**。
- 只声明，不静默 patch 掉别人的行为（Prefix 抑制 GC 的 `ventIntensiveExchange` 是例外——那是本 patch 主动接管同一功能，不是禁用别人）。
- 若某天 AO 或对方 mod 修复了根本原因，从清单里删除并留一行变更记录。
