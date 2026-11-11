# AO 逐建筑特性与公式设计

> 范围：`294100/3830470974`（AO）、`294100/3836392033`（ED）、`294100/3669891833`（GC）、`FullyAutomaticOmniCrafter`（Omni）
> 目的：逐个建筑说明"要添加哪些特性"，并给出可实现的公式。只做设计，不改代码。
> 日期：2026-11-11

---

## 0. 通用物理框架

所有建筑都在这套量上工作，后面的公式只写差异部分。

### 0.1 格子的状态（AO 场）

| 符号 | 含义 | 单位 | AO 字段 |
|---|---|---|---|
| `T` | 空气温度 | ℃ | `Temperature` |
| `p` | 绝对压力 | Pa | `AmbientSurfacePressurePa + StoredPressurePa` |
| `O₂` | 氧体积分数 | % | `Oxygen`（byte） |
| `q` | 绝对水汽 | hPa·cell | `Moisture` |
| `M` | 雾密度 | — | `Mist` |
| `v` | 风速矢量 | m/s | `VelocityX/Z` |

### 0.2 派生量

```
格体积        V_cell = 5 m³                    （AO 的 columnHeight 默认 5，格 1 m²）
空气密度      ρ = p / (287.05 · (T + 273.15))  [kg/m³]
格空气质量    m_cell = ρ · V_cell              [kg]
定压比热      c_p = 1005 J/(kg·K)
水汽潜热      L = 2.5e6 J/kg
水汽容量      q_sat(T) = q_base · exp(17.6·T / (243 + T))      （Magnus-Tetens）
相对湿度      RH = q / q_sat(T)
1 kg 水 ≈ HpaCellPerKg(T) = 461.5 · (T + 273.15) / 500  [hPa·cell]
```

### 0.3 搬运的通用公式（所有"输运类"建筑共用）

给定源格集合 `S`、目标格集合 `D`、体积流 `Q`（m³/s）、效率 `η`、步长 `Δt`（s）：

```
V_x = η · Q · Δt                                   [m³]     本步搬运的空气体积
f   = V_x / V_cell                                 [—]      每格被替换的比例

// 质量（决定热）
m_x = ρ_src · V_x                                  [kg]

// 五路搬运，全部成对
O₂ :  ΔO₂ = f · (O₂_src − O₂_dst)                  [百分点]
热 :  ΔQ  = m_x · c_p · (T_src − T_dst)            [J]
      ΔT_src = −ΔQ / (m_src · c_p),  ΔT_dst = +ΔQ / (m_dst · c_p)
湿 :  Δq  = f · (q_src − q_dst)                    [hPa·cell]
压 :  Δp  = f · (p_src − p_dst)                    [Pa]
动量: Δv  = η_blend · (v_target − v_dst)           [m/s]
```

写入时**源扣目标加**，符号相反，这是能量守恒的唯一方式。

---

## 1. Atmosphere Overhaul 自身建筑

### 1.1 `AO_IndustrialBlower` 工业鼓风机（2×1）

**现状**：`CompSingleFieldFan`，风场 2×2，600 W / 200 W，额定 120 m³/s，出口 4 m²。
风速 `v = (Q_rated/A_out) · (P/P_max)^(1/3)` = 30 × (P/600)^(1/3) m/s。

**在 AO 体系中的定位**：定向射流源，只给动量，不给质量。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | **吸气模式** | 反向：从风场前方抽气，从背后格排出，形成负压 |
| 2 | **质量搬运** | 当前只改速度，空气/氧/热/湿不随流走；应改为"搬 V_x" |
| 3 | **压差反馈** | `Q_eff = Q_rated · clamp(1 − Δp/Δp_stall, 0, 1)`，背压过大时风量下降 |
| 4 | **温升** | 电机发热：`ΔT_motor = P_loss/(m·c_p)`，`P_loss ≈ 0.1·P` |
| 5 | **风压载荷** | 风速 > 15 m/s 时对前方建筑施加 `WindStructuralLoad` |

**公式**：

```
v_target = (Q_rated / A_out) · (P / P_max)^(1/3) · s_dir
Q        = A_out · v_target · clamp(1 − Δp/Δp_stall, 0, 1)
Δp_stall = 0.5 · ρ · v_target²                       [Pa]  风压
V_x      = Q · Δt
// 若 enableAirExchange：
//   在 outlet 格：AddDraft(V_x, from=sampledInlet)   // 五路搬运
//   在 inlet 格：反向搬运（负压）
// 否则（纯动量模式，兼容现状）：
//   AddWindImpulse(dir · (v_target − v)·blend)
```

### 1.2 `AO_HouseholdFan` 家用风扇（1×1）

**现状**：100 W / 50 W，10 m³/s，出口 1 m²，可调功率、可手转。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 与鼓风机同构的质量/热/湿搬运 | 体积小，`V_x` 小 |
| 2 | **摇头模式** | `dir = rotate(dir, ±30°)` 周期性摆动 |
| 3 | 手转模式 | `powerSource=Manual`，由小人推，消耗体力 |

**公式**：同 0.3，`Q = 10 · (P/100)^(1/3)`。

### 1.3 `AO_WindUpFan` 发条风扇（1×1）

**现状**：5 W，5 m³/s，900 J 储能，满弦 180 s，自动上弦。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | **风量随储能衰减** | `Q = Q_rated · sqrt(E/E_max)` |
| 2 | 上弦消耗体力 | 每次上弦消耗 `0.02 · 食物` |
| 3 | 上弦期间停风 | `E < E_min` 时 `Q=0` |

**公式**：

```
E_next = max(0, E − P·Δt)
Q      = Q_rated · sqrt(E / E_max)
```

### 1.4 `AO_ArchotechWindProducer` 圣物风机（5×5）

**现状**：无限制电源，100 kW，2250 m³/s，出口 25 m²，风场 5×7，目标 90 m/s。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | **风墙**：整个 5×7 区域达到 90 m/s | 当前 `TargetWind` 已按格写，但缺结构载荷 |
| 2 | 持续载荷 | 区域内建筑按 `WindStructuralLoad` 受损 |
| 3 | 吹飞小人与物品 | `WindTransport` 已有 |

**公式**：`v = 90 · (P/100000)^(1/3)`，`P` 默认 0（需手动调），最大 100 kW。

### 1.5 `AO_Turbojet` 涡喷（3×3）

**现状**：`CompPairedJet`，入口 1×1（offset 0,-2），出口 3×13（offset 0,7），燃料 10/s，水 1.5 kg/燃料，maxSpeed 150，halfSpeed 60，maxTemp 500，maxPressureRatio 2，halfPressureRatio 1.4，inletPressureRatio 0.5，oxygenPercent 13，columnHeight 5。

**在 AO 体系中的定位**：强制对流 + 燃烧产物源。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | **入口负压** | 当前只写出风口；应写入口抽吸 |
| 2 | **质量守恒** | 入口体积 ≈ 出口体积 × (T_out/T_in)（热膨胀） |
| 3 | **燃烧耗氧** | 出口氧 13%，入口应同步扣除 |
| 4 | **水汽产物** | 1.5 kg 水/燃料，已建模但未接到 `AddMoistureDelta` |
| 5 | 噪声/热辐射 | 附近小人心情/灼伤 |

**公式**：

```
P_fuel = min(1, fuelRate / 10)
v_out  = Curve(P_fuel, 60, 150)                        // 半分点 60、满 150
T_out  = T_amb + (500 − T_amb) · P_fuel
p_out  = p_amb · Lerp(1, 2, P_fuel)                    // 满功率 2 倍压
O₂_out = 21 − 8 · P_fuel                               // 21 → 13
ṁ_H2O  = 1.5 · fuelRate                                // kg/s

// 出口（3×13 格，按面积均分）
V_out  = A_out · v_out · Δt
// 入口（1×1）
V_in   = V_out · (T_out_K / T_amb_K)                   // 理想气体，质量守恒
v_in   = V_in / (A_in · Δt)
p_in   = p_amb · (0.5 + 0.5·P_fuel)                    // inletPressureRatio
```

### 1.6 `AO_DopplerWeatherRadar` 气象雷达（5×5）

**现状**：150 W，需露天，解锁世界地图 55 个 overlay。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 探测距离受天气衰减 | `R_eff = R · exp(−k·rain)` |
| 2 | 多雷达叠加 | 两台雷达覆盖率叠加，预报精度提高 |
| 3 | 断电保留最后一帧 | 已由 `GlobalForecastReader` 缓存 |

**公式**：`R_eff = R_0 · exp(−0.02 · rainMmPerHour)`。

### 1.7 `AO_Humidifier` 加湿器

**现状**：`CompHumidifier`，目标 RH 50%，喷 `Mist`，耗水 `maxKgPerHour`。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | **喷水汽而不是雾** | 雾会沉降；应 `AddMoistureDelta` 为主 |
| 2 | 温度修正 | 冷空气容纳少，`Δq` 上限随 `q_sat(T)` 变 |
| 3 | 结露保护 | RH > 95% 时停机 |

**公式**：

```
q_target = RH_goal · q_sat(T)
Δq = min(q_target − q, ṁ_water · HpaCellPerKg(T) · Δt, 0.5·q_sat(T))
```

### 1.8 `AO_Dehumidifier` 除湿器

**现状**：`CompDehumidifier`，目标 RH 40%，抽 vapor 到 `goal · q_sat`，放潜热，产水。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 冷凝放热的正确符号 | 当前把 `Δq·L` 加回同格，应为 `+|Δq|·L` |
| 2 | 低温结霜 | `T < 2 ℃` 时效率减半 |
| 3 | 产水速率与潜热联动 | `ṁ_water = Δq / HpaCellPerKg(T)` |

**公式**：

```
Δq = −min(q − RH_goal·q_sat(T), swept · q)
ΔT = +|Δq| · L / (m_cell · c_p)          // 冷凝放热
ṁ_water = |Δq| / HpaCellPerKg(T)
效率 = T > 2 ? 1 : 0.5
```

### 1.9 `AO_HeatedFloorMat` 地暖

**现状**：`CompHeatedFloorMat`，写 `GroundTemperaturePerTick`。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 地-气分两步传热 | 先热地板，再由地板热空气 |
| 2 | 只对有顶格生效 | 露天地暖无效 |
| 3 | 地温上限 | 防止把地温推到 300 ℃ |

**公式**：

```
T_ground += (T_target − T_ground) · k_floor · Δt
ΔT_air    = (T_ground − T_air) · k_air · Δt
```

### 1.10 气体链

#### `AO_AirTank` / `AO_OxygenTank`
**特性**：储气量、泄漏（`airLeakFactor`）、温度随压缩变化。
**公式**：`p_tank = n·R·T/V_tank`；泄漏 `ṁ_leak = k·(p_tank − p_amb)·A_leak`。

#### `AO_AirCompressor`
**特性**：抽环境空气压缩入罐，耗电，产热。
**公式**：
```
ṁ = Q_comp · ρ_amb
p_tank += ṁ·287.05·T_tank / V_tank · Δt
T_out = T_amb · (p_tank/p_amb)^(0.2857)
Q_heat = ṁ · c_p · (T_out − T_amb)          // 排到环境
```

#### `AO_PsaSeparationUnit`
**特性**：分离氧氮，产 95% 氧。
**公式**：
```
ṁ_O2 = 0.95 · Q_in · ρ_amb
ṁ_N2 = 0.05 · Q_in · ρ_amb                  // 排到室外
P_elec = ṁ_O2 · 0.35 kWh/kg                  // 近似 PSA 能耗
```

#### `AO_GasReleaseSpot`
**特性**：把罐内气体按体积源释放，降压降温。
**公式**：
```
V_release = min(V_tank_req, Q_max · Δt)
AddVolumeSource(cell, V_release, gasO2, T_gas − T_amb)
p_tank −= p_tank · V_release / V_tank
T_gas  −= (γ−1)·T_gas·V_release/V_tank      // 绝热膨胀降温
```

### 1.11 `AO_Irrigator` 灌溉器

**现状**：给土壤加水（mm）。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 从水罐取水 | `mm × 面积 = kg` |
| 2 | 蒸发联动 | 灌溉后 `BareSoilEvaporationMmPerDay` |
| 3 | 与 AO 降水衔接 | 目前 AO 降水不回灌土壤，灌溉器是唯一主动补水 |

**公式**：`Δmm = ṁ_water / (area·ρ_water) · 1000`。

---

## 2. 原版建筑（AO 接管后）

### 2.1 `Vent` 通风口 —— **主动通风孔（用户示例）**

**现状**：AO 加 `CompProperties_AirVent`（0–20 m/s 限流面，写 `FaceOpen`），只做被动压差流。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | **主动模式** | 内置风机，A→B 强制送风 |
| 2 | **五路搬运** | 气体/氧/热/湿/压，成对 |
| 3 | **热回收** | 按 AB 温差决定是否额外换热 |
| 4 | **湿回收** | 按 AB 湿度差决定是否额外换湿 |
| 5 | 双向可逆 | 按压差或玩家设定反向 |
| 6 | 限流保护 | 真空侧不能抽（`p < 10 kPa` 停机） |

**主动模式公式**：

```
// 输入
Q_max = A_vent · v_max              (v_max = 20 m/s, A_vent = 1 m²)
η     = 0.7                          // 风机效率
dir   = sign(p_A − p_B) 或玩家设定
V_x   = η · Q_max · Δt
f     = V_x / V_cell

// 1) 气体（体积）—— 等量
// 2) 氧气（体积混合）
ΔO₂ = f · (O₂_A − O₂_B) / 2

// 3) 热（焓搬运）
m_x  = ρ_A · V_x
ΔQ   = m_x · c_p · (T_A − T_B)
ΔT_A = −ΔQ / (m_A · c_p)
ΔT_B = +ΔQ / (m_B · c_p)

// 4) 显热回收（可选，ε_h = 0.6）
ΔT_rec = ε_h · (T_A − T_B) · (1 − f)
ΔT_A += −ΔT_rec/2 ;  ΔT_B += +ΔT_rec/2

// 5) 湿（水汽密度差）
Δq = f · (q_A − q_B)
Δq_rec = ε_m · (q_A − q_B) · (1 − f)        // ε_m = 0.5
Δq_A = −Δq − Δq_rec/2 ;  Δq_B = +Δq + Δq_rec/2

// 6) 压力（体积再分配）
Δp = f · (p_A − p_B)

// 写入（A 扣、B 加）
A: AddTemperature(−ΔT_A), AddOxygenPercent(−ΔO₂), AddMoisture(−Δq_A), AddPressure(−Δp)
B: AddTemperature(+ΔT_B), AddOxygenPercent(+ΔO₂), AddMoisture(+Δq_B), AddPressure(+Δp)
```

**被动模式**（保留现有）：`FaceOpen = f`，由压力投影自然产生流动。

**关键约束**：`p < 10 kPa` 一侧不允许作为源（真空侧没有质量可抽）。

### 2.2 `Heater` 加热器

**现状**：`CompTempControlAtmosphere.RunHeater`，单格，`ΔT = clamp((target+2−T)/1,0,1) · E/60 · heatFactor`，>200 ℃ 降效。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | **热羽流** | 加热产生上升气流（二维下表现为向四周辐散） |
| 2 | **湿度自然下降** | 升温使 RH 降，不需要额外写 q |
| 3 | 过热保护 | > 300 ℃ 停机（已有） |
| 4 | 燃料型加热器耗氧 | 已有 `CompHeatSourceAtmosphere` 路径 |

**公式**：

```
ΔT = clamp((T_target + 2 − T)/1, 0, 1) · (E/60) · heatFactor · falloff(T)
falloff(T) = T ≤ 200 ? 1 : clamp((300−T)/100, 0, 1)
羽流：AddWindImpulse(dir_radial, 0.02 · ΔT)     // 可选
```

### 2.3 `Cooler` 制冷器

**现状**：`RunCooler`，冷/热两侧各一格 + 延伸格，COP 模型，15 m/s 出风，除湿产水。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | **热侧必须排到室外** | 当前热侧写相邻格，若在室内会自循环 |
| 2 | 结霜 | `T_cold < 2 ℃` 时效率减半，需除霜 |
| 3 | 冷凝水已有 | `waterPoolKg` |
| 4 | 出风方向可调 | 当前固定南 |

**公式**：

```
COP = 1/(1 + max(0, max(T_hot − T_cold, T_hot − 40))/65) · clamp((T_cold+273.15)/150, 0, 1)
Q_cold = COP · E_absorbed
Q_hot  = Q_cold · 1.25                     // 含压缩机功
ΔT_cold = −Q_cold / (m_cold · c_p)
ΔT_hot  = +Q_hot  / (m_hot  · c_p)
v_out   = 15 · clamp((T_cold − (target−2))/1, 0, 1)
```

### 2.4 `EvaporativeCooler` 蒸发冷却器

**现状**：按 `(q_sat(T) − q)/deficitReferenceHpa` 决定强度，加水汽 + 降温，耗燃料，`minCelsius=17`。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 湿度上限 | RH > 90% 时失效（已隐含） |
| 2 | 水耗与降温联动 | `ṁ_water = ΔT·m·c_p/L` |
| 3 | 出风 | 应产生风 |

**公式**：

```
drying = clamp((q_sat(T) − q)/deficitRef, 0, 1) · (T > 17 ? 1 : 0)
ΔT = −drying · heatPerSecond/60
Δq = +|ΔT| · m_cell · c_p / L · HpaCellPerKg(T)
```

### 2.5 火/热源（`CompHeatSourceAtmosphere` 接管的所有建筑）

**现状**：耗氧 `-2.7·fireSize·factor/150`，高温失效，`FireOxygen.Factor` 调制。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 烟/雾 | 燃烧产 `Mist` 与自定义气体 |
| 2 | 热羽流 | 火焰上升气流 |
| 3 | 燃料-氧-热三者耦合 | 缺氧 → 火弱 → 热少 |

**公式**：

```
factor  = clamp(FireOxygen.Factor, 0, 3)       // pO2 < 13 kPa → 0
ΔT      = heatPerSecond·3/60 · factor · falloff(T)
ΔO₂     = −2.7 · fireSize · factor / 150
ΔMist   = +0.1 · fireSize · factor             // 烟
```

---

## 3. Good Climate 的建筑

GC 不添加新 ThingDef，只增强原版三件套。

### 3.1 `Vent`（GC 强化版）

**现状**：加 `GC_CompProperties`（`AE=-100`、`rate=0.8`）+ 10 W 电。每 60 tick 执行：

```csharp
float d = room.Temperature - room2.Temperature;
room2.Temperature += d * 0.8f;      // 直接写房间温度，不守恒
```

**在 AO 下的问题**：`RoomTemperatureWriteHook` 把它变成"整房间逐格加 Δ"，room1 不扣 → 能量漂移；>3000 格静默失效。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | **换成成对搬运** | 用 0.3 节公式，A 扣 B 加 |
| 2 | 质量加权 | 两房间体积不同时按 `min(m1,m2)` 限流 |
| 3 | 湿/氧联动 | 通风同时搬湿与氧 |
| 4 | 保留 GC 的电费与开关 | 只换物理，不换 UI |

**修正公式**：

```
// 原来：room2.T += (T1−T2)·0.8
// 现在：
k   = 0.8                                    // GC 原交换率
m1  = room1.CellCount · m_cell
m2  = room2.CellCount · m_cell
ΔQ  = k · (T1 − T2) · min(m1, m2) · c_p      // 限流到小房间能提供的量
ΔT1 = −ΔQ / (m1 · c_p)
ΔT2 = +ΔQ / (m2 · c_p)
// 湿：同构，Δq = k·(q1−q2)·min(V1,V2)/V_i
```

### 3.2 `Heater`（GC 版）

**现状**：`GC_DefInjector` 改 `CompProperties_TempControl.energyPerSecond`（默认 21 J/s，范围 21..105），AO 的 `CompTempControlAtmosphere` 会读到。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 电费与 AO 效率对齐 | GC 按原版 COP 算电费，AO 有 `efficiency` |
| 2 | 档位 UI 保留 | GC 的滑条仍在 |

**修正公式**：

```
E = GC_EE_tryGetValue(device)                  // J/s
P_elec = E / (efficiency · 1.0)                // AO 的 efficiency ∈ [0,1]
// 而不是 GC 的 GC_EE_CalculatePower(E)
```

### 3.3 `Cooler`（GC 版）

同 3.2。GC 的 `energyPerSecond` 为负（-21..-105），AO 的 `RunCooler` 用 `|E|` 与 `COP`。

---

## 4. 超维科技（Omni）

### 4.1 `OmniForceFieldDome` 穹顶（11×11）

**现状**：`maintainTemperature=true`、`targetTemperature=21`、`blockVacuum=true`、`clearGas` 等，但**只改读数**：
- `GenTemperature.TryGetTemperatureForCell` Postfix → 返回 21
- `VacuumUtility.GetVacuum` Postfix → 返回 0
- `MaintainEnvironment()` 只清原版 `gasGrid`、污染、污渍、火

**在 AO 体系中的定位**：应是一个"**封闭环境维持器**"，等价于一个"有界的、恒温恒压恒氧恒湿的空气包"。

**要添加的特性**：

| # | 特性 | 目标值 | 写入原语 |
|---|---|---|---|
| 1 | 温度闭环 | 21 ℃ | `AddTemperatureDelta` |
| 2 | 压力闭环 | 环境压（101325 Pa） | `AddVolumeSource` / `AddPressureDelta` |
| 3 | 氧闭环 | 21% | `AddOxygenPercentDelta` |
| 4 | 湿度闭环 | 50% RH | `AddMoistureDelta` |
| 5 | 风屏蔽 | 0 m/s | `AddWindImpulse(−v)` |
| 6 | 屋顶/房间 | 已有 | `roofGrid` |
| 7 | 能量代价 | 按 Σm·c_p·ΔT 折算电费 | `CompPowerTrader` |

**公式**（每 250 tick，逐格，游标 ≤500 格）：

```
// 1) 温度
ΔT = clamp(21 − T, −20, +20) · 0.6
// 2) 压力：目标 = 环境压
Δp = clamp(p_amb − p, −2000, +2000)          [Pa]
if (Δp > 0 && p < p_amb)  AddVolumeSource(V_cell · Δp/p_amb, 21, ΔT)
else                       AddPressureDelta(Δp/SolverPascalsPerUnit)
// 3) 氧
ΔO₂ = clamp(21 − O₂, −5, +5) · 0.5
// 4) 湿
q_t = 0.5 · q_sat(T)
Δq  = clamp(q_t − q, −0.2·q_sat, +0.2·q_sat) · 0.5
// 5) 风
AddWindImpulse(−v · 0.5)
// 6) 电费
P = Σ (m_cell · c_p · |ΔT| + |Δq|·L) / (Δt · COP)
```

**与 Omni 现有 Postfix 的关系**：Postfix 继续作为"读数兜底"；本 comp 写场后两者应一致。若不一致（如外部热源持续加热），以场为准，Postfix 会掩盖问题——建议在 AO 地图上关闭 Omni 的 `maintainTemperature` Postfix，只用本 comp。

### 4.2 `OmniBiosphere` / `OmniBiosphere_mini` 生物圈

**现状**：`controlTemperature`、`targetTemperature`、`ensureNoVacuum`、`SelectedArea`（玩家画的 `Area`）。`ApplyEffects` 每格只 `GenTemperature.PushHeat(cell, map, 0.01f)`。

**定位**：**区域级封闭环境**，作用域是任意形状的 `Area`，不是矩形。

**要添加的特性**：与穹顶同构（温度/压力/氧/湿/风五路），差异：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 作用域 = `Area.ActiveCells` | 面积可能上千格，必须分片 |
| 2 | 植物生长保留 | Omni 的 `ApplyPlantGrowth` 不动 |
| 3 | 区域边界密封 | 需要 `airLeakFactor=0`，否则风/压从边界漏 |
| 4 | 与穹顶可叠加 | 重叠区域以更强的一方为准 |

**公式**：同 4.1，但：
```
cells = SelectedArea.ActiveCells
每 250 tick 处理 cellsPerStep（默认 500）格，游标轮转
```

### 4.3 `OmniPersonalTemperatureController` 个人温控器

**现状**：给穿戴者调温（未细读）。

**定位**：**个体级体感偏移**，不应改场。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 只改体感，不改场 | `FeelsLikeTemperature` 偏移 |
| 2 | 耗电 | 按 `|T_target − T_air|` 算 |
| 3 | 环境极端时失效 | `p < 10 kPa` 或 `O₂ < 10%` 时无效 |

**公式**：

```
T_feels = T_air + (T_target − T_air) · k        // k = 0.8
P = |T_target − T_air| · 2 W/K
```

### 4.4 `OmniPhantomWall` / `OmniPhantomWall2` 幻影墙

**现状**：可能是"视觉墙"，不参与 AO 的 `ObstacleMask`。

**定位**：**气流屏障**。

**要添加的特性**：

| # | 特性 | 说明 |
|---|---|---|
| 1 | 加 `BuildingAtmosphereExtension` | `airLeakFactor=0`、`pressureToleranceFactor=1e6` |
| 2 | 可选：只挡风不挡气 | `windToleranceFactor` 高、`airLeakFactor` 低 |

**XML**：

```xml
<li Class="AtmosphereOverhaul.Simulation.BuildingAtmosphereExtension">
  <airLeakFactor>0</airLeakFactor>
  <pressureToleranceFactor>1000000</pressureToleranceFactor>
  <windToleranceFactor>3</windToleranceFactor>
</li>
```

---

## 5. Explosive Diarrhea

**无建筑**。只有 `ED_Biogas`（`AtmosphereGasDef`）、两个 Ability、Filth/Hediff/Thought。

**若要新增"沼气池"建筑**，照 `ED_Biogas` 的样板：

| 特性 | 参数 |
|---|---|
| 产气 | `MoisturePerTick` 消耗有机物 → `AddGas(ED_Biogas, ...)` |
| 温度 | 厌氧发酵放热 `ΔT = 0.02 · 产气速率` |
| 爆炸 | 密度 > 阈值 + 火花 → `TryQueueExplosionAtmosphere` |
| 气体定义 | 复用 `ED_Biogas`（`maxDensity=255`、`naturalVanishRatePerTick=0.004`） |

公式：

```
Δgas = k_ferment · organicKg · Δt
ΔT   = 0.02 · Δgas
if (density > 200 && spark) Explode(radius 3, 0.5 TNT)
```

---

## 6. 汇总表

| Mod | 建筑 | 当前能力 | 需添加的核心特性 | 写入原语 |
|---|---|---|---|---|
| AO | 工业鼓风机 | 只给动量 | 吸气模式、质量搬运、背压反馈、电机温升 | Wind + Delta |
| AO | 家用风扇 | 只给动量 | 质量搬运、摇头、手转 | Wind + Delta |
| AO | 发条风扇 | 只给动量 | 风量随储能衰减、上弦耗体力 | Wind |
| AO | 圣物风机 | 只给动量 | 风墙结构载荷 | Wind + Load |
| AO | 涡喷 | 出口射流 | 入口负压、质量守恒、耗氧、产水 | Volume + Delta |
| AO | 气象雷达 | 解锁 overlay | 天气衰减、多雷达叠加 | 无 |
| AO | 加湿器 | 喷雾 | 改喷水汽、温度修正 | Moisture |
| AO | 除湿器 | 抽汽放热 | 结霜、潜热符号修正 | Moisture + Temp |
| AO | 地暖 | 写地温 | 两步传热、有顶限定 | Ground + Temp |
| AO | 气罐/压缩机/PSA/释放点 | 储/压/分/放 | 泄漏、绝热温变、能耗 | Volume + Pressure |
| AO | 灌溉器 | 加水 | 与水罐/蒸发联动 | 土壤水 |
| 原版 | 通风口 | 被动限流 | **主动双向五路搬运 + 热湿回收** | Volume + Delta |
| 原版 | 加热器 | 单格热 | 热羽流、过热保护 | Temp + Wind |
| 原版 | 制冷器 | 两格 + COP + 除湿 | 热侧外排、结霜、出风可调 | Temp + Wind |
| 原版 | 蒸发冷却 | 加水降温 | 湿度上限、水耗联动、出风 | Temp + Moisture |
| 原版 | 火/热源 | 耗氧 + 高温失效 | 烟、羽流、氧-热耦合 | Temp + O₂ + Mist |
| GC | 通风口 | 直接写房间温度 | **换成成对守恒搬运** | Temp + Moisture |
| GC | 加热器/制冷器 | 改 energyPerSecond | 电费与 AO 效率对齐 | 无（已采纳） |
| Omni | 穹顶 | 只改读数 | **温度/压力/氧/湿/风五路闭环** | 全套 |
| Omni | 生物圈 | 只 PushHeat 0.01 | 同上，Area 作用域 + 分片 | 全套 |
| Omni | 个人温控器 | 未细读 | 只改体感、耗电、极端失效 | 无（体感） |
| Omni | 幻影墙 | 无 | `airLeakFactor=0` 的 ModExtension | XML |
| ED | 无 | — | 可选：沼气池 | Gas |

---

## 7. 三条最关键的实现约束

1. **一切搬运必须成对**。AO 没有"搬运"API，只写一侧会让能量/质量凭空出现。
2. **压力读用 `TryGetStaticPressurePascals`**（internal，需反射）。公开的 `TryGetPressurePascals` 在可压缩模式下放大 83 倍。
3. **不可压模式下不要用 `AddPressureDelta` 维持压力**。`ApplyThermodynamicPressureBaseline` 每步重算 `Pressure.Read`，会把增量抹掉；用 `AddVolumeSource` 补质量。
