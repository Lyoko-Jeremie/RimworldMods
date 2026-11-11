# AO GPU 计算层（WGSL）分析

> 对象：`AtmosphereOverhaul.Perf81.dll` 的 3 个嵌入 WGSL 资源
> 提取位置：`F:\temp\ao_wgsl\`（PowerShell 反射 `GetManifestResourceNames()` 导出）
> 运行时：`1.6/Native/WebGpu/win-x64/wgpu_native.dll`（wgpu-core，git commit `87576b7`）
> 分析日期：2026-11-11

---

## 0. 结论摘要

1. 世界模式的**全部物理计算**在三个 WGSL 里：`Waves.wgsl`（半隐式压力/重力波 BiCGStab）、`DynamicsVI.wgsl`（TRiSK 向量不变动量 + 连续方程 + 位温输运）、`Surface.wgsl`（地表 skin/slab 能量 + 云光学）。
2. 这三个文件是**独立重写的 GPU 实现**，不是 C# 的机械翻译；C# 里能找到对应函数，但两边的公式组织方式不同。
3. **`Waves.wgsl` 把时间步硬编码为 300 秒**（`PressureColumn` 的 `300.0 *`、`Transport` 的 `/ 300.0`），而 `AutodriveSession` 的步长会因 CFL 降到 150 秒。这是 C# 侧完全看不到的行为差异。
4. GPU 只覆盖动力学/波解/地表；湿物理、辐射、对流、海冰、气旋仍在 C#（`MoistPhysics.cs`、`DryCore` 的 CPU 部分）。

---

## 1. 资产与加载

| 资源名 | 大小 | 行数 | 入口 |
|---|---|---|---|
| `WebGpu.Waves.wgsl` | 8,309 B | 164 | 7 个 compute |
| `WebGpu.DynamicsVI.wgsl` | 13,145 B | 256 | 5 个 compute |
| `WebGpu.Surface.wgsl` | 6,575 B | 124 | 1 个 compute |

加载链路：

```csharp
// WebGpuBackend.Shader(name)
Assembly.GetExecutingAssembly().GetManifestResourceStream("WebGpu." + name)
// 缺失抛 FileNotFoundException("Embedded shader missing: " + name)
```

`WebGpuBackend` 构造时建 7 条 wave 管线 + 1 条 surface 管线；`InitializeDynamics` 只接受 `"DynamicsVI.hlsl"`（非该名抛 `NotSupportedException`），再建 5 条 dynamics 管线。

---

## 2. `Waves.wgsl` —— 半隐式压力/重力波求解器

### 2.1 Params

```wgsl
struct Params {
    n: u32, e: u32, c: u32, m: u32,          // 列数、边数、cell 数、工作区宽
    source: u32, destination: u32, mode: u32, force: u32,
    a: f32, top: f32, gas: f32, kappa: f32,  // a = 时间步/系数
    tolSq: f32, finalTolSq: f32, pad0: f32, pad1: f32,
};
```

绑定：`meshData`(i32)、`geo`(f32)、`frozen`(f32，只读状态)、`work`(f32，工作区)、`scalar`(f32，控制/统计)、`partialSums`(f32，归约)、`P`(uniform)。

### 2.2 工作区切片（`work`，每片 `m` 个 float）

| 常量 | 值 | 含义 |
|---|---|---|
| `SX` | 0 | 解向量 x |
| `SR` | 1 | 残差 r |
| `SSHADOW` | 2 | 影子残差 r̂ |
| `SP` | 3 | 搜索方向 p |
| `SV` | 4 | A·p |
| `SS` | 5 | s |
| `ST` | 6 | t |
| `SPH` | 7 | φ（气压扰动） |
| `SSH` | 8 | ŝ |
| `SRHS` | 9 | 右端项 |
| `SBQ` | 10 | b（由 B·q 得到） |
| `SBU` | 11 | B·u |
| `SDU` | 12 | 速度增量 Δu |
| `SWW` | 13 | 边上的动量倾向 |
| `STW` | 14 | θ 倾向 |
| `SPHI` | 15 | Exner/位势 |
| `SAD` | 16 | 绝热系数 α |
| `SHD` | 17 | 水平质量散度 |
| `SQD` | 18 | 位温通量散度 |

`isActive() = force != 0 || scalar[0] == 0`——`scalar[0]` 是求解器状态机：

| `scalar[0]` | 含义 |
|---|---|
| 0 | 运行中 |
| 1 | 已收敛 |
| 2 | 提前满足（由 `FinishEarly` 转 1） |
| -1 | 出现 NaN/Inf |
| -2 | 除零（`|v| < TINY` 或 `|scalar[6]| < TINY`） |
| -3 | 迭代结束但未达 `finalTolSq` |

`scalar[1]` = 迭代计数，`scalar[2]` = ‖b‖²，`scalar[3]` = tol²·max(‖b‖²,1e-24)，`scalar[9]` = 相对残差。

### 2.3 `PressureColumn`（第 35-48 行）

每列 `c` 自顶向下（`l = 7 → 0`）计算：

```wgsl
let td = F(1u,i) * (300.0 * Q(P.source, P.n + i) - Db(l) * F(0u,i) * dp) / (Db(l) * depth)
       + F(2u,i) * P.kappa * Bc(l) * dp / F(3u,i);
work[SPHI * P.m + i] = lower + P.gas * (td * F(4u,i) + F(2u,i) * F(6u,i) * dp);
lower = lower + P.gas * (td * F(5u,i) + F(2u,i) * F(7u,i) * dp);
work[SAD * P.m + i] = P.gas * td / F(3u,i) - F(8u,i) * Bc(l) * dp / F(3u,i);
```

- `depth = Ps(c) - top`，`dp = Q(source, c)`（地面气压增量）。
- `F(0,i)` = θ，`F(1,i)` = 温度，`F(2,i)` = 层厚权重，`F(3,i)` = 层压，`F(4..7,i)` = 静力重构系数，`F(8,i)` = 密度/层质量。
- **`300.0` 是字面量**：本应使用 `P.a`（C# 传进来的 `dt`）。

### 2.4 `PressureEdge`（第 51-59 行）

```wgsl
let gradient = (Ps(cb) - Ps(ca)) / d;
work[SWW*P.m + i] = -(Q(SPHI, ib) - Q(SPHI, ia)) / d - 0.5 * Bc(l) *
    ((Q(SAD, ia) + Q(SAD, ib)) * gradient + (F(8u,ia) + F(8u,ib)) * (Q(P.source, cb) - Q(P.source, ca)) / d);
```

即气压梯度力的边分量：`-∂Φ/∂d - 0.5·B_l·(ᾱ·∂p_s/∂d + ρ̄·∂Δp/∂d)`。

### 2.5 `Transport`（第 62-87 行）

第一遍（逐层，逐 cell）：把该 cell 的所有边通量累加

```wgsl
let flux = SignAt(s) * geo[P.e + edge] * Q(P.source, l*P.e + edge) * Db(l) * (0.5*(Ps(ca)+Ps(cb)) - P.top);
mf += flux; tf += flux * 0.5 * (F(0u, l*P.n + ca) + F(0u, l*P.n + cb));
work[SHD*P.m + l*P.n + c] = mf / area;   // 水平质量散度
work[SQD*P.m + l*P.n + c] = tf / area;   // 位温通量散度
```

第二遍（垂直）：`STW[c] = -Σ_l SHD_l`（= ∂p_s/∂t），然后自下而上积分垂直质量通量：

```wgsl
let lowerFlux = upper + Db(l) * total - Q(SHD, l*P.n+c);
work[STW*P.m + P.n + l*P.n + c] = (-Q(SQD, l*P.n+c) - lowerFlux*tl + upper*tu) / 300.0;
upper = lowerFlux;
```

**`/ 300.0` 同样是字面量**。这是 `∂(θΔp)/∂t = -∇·(uθΔp) - ∂(ωθ)/∂η` 的离散，除以 300 把它变成"每 300 秒"的增量。

### 2.6 `VectorOp`（第 90-106 行，6 个 mode）

| mode | 作用 | 关键行 |
|---|---|---|
| 7 | 速度增量 | `work[SDU] = Q(SBU,i) + P.a * Q(SWW,i)` |
| 0 | 初始化：`b = BQ + a·TW`，写入 `RHS/R/SHADOW`，清零 `X/P/V` | `Q(SBQ,i) + P.a*Q(STW,i)` |
| 1 | 更新 p：`p = r + ω·(P - c·V)` | `Q(SR,i) + scalar[8]*(Q(SP,i) - scalar[6]*Q(SV,i))` |
| 2 | `s = r - α·V` | `Q(SR,i) - scalar[5]*Q(SV,i)` |
| 3 | `ŝ = s`；若 `scalar[0]==2` 则 `X += scalar[5]·PH` | |
| 4 | `X += α·PH + c·Ŝ`；`r = s - c·T` | |
| 5 | `dest = source - a²·TW`（BiCGStab 的 x 更新） | `Q(P.source,i) - P.a*P.a*Q(STW,i)` |

`scalar[5]=α`、`scalar[6]=c`、`scalar[8]=ω`。

### 2.7 `ReducePartial` / `ReduceFinal`（第 112-161 行）

256 线程两两归约，算两个内积 `u`/`v`：

| mode | u | v |
|---|---|---|
| 0 | ‖RHS‖² | — |
| 1 | ‖R‖² | ⟨SHADOW, R⟩ |
| 2 | ⟨SHADOW, V⟩ | — |
| 3 | ‖S‖² | — |
| 4 | ‖T‖² | ⟨T, S⟩ |
| 5 | ‖RHS - V‖² | — |

`ReduceFinal` 据此更新 BiCGStab 的 α/β/ρ/ω，并在 mode 5 计算相对残差 `scalar[9] = u / max(scalar[2], 1e-24)`。

### 2.8 300 秒问题的影响

- `AutodriveSession.AdvanceHour`：`int dt = min(allowedStep, round(targetHour - SimulationSeconds))`，`allowedStep` 默认 300，`ValidateAutodriveState > 0.6` 或捕获 `InvalidOperationException` 时降到 **150**。
- `StepImplicit(dt)` → `WaveSolve.Solve(..., a=dt, ...)` → `gpuWaveDevice.Solve(..., a, ...)`。
- 但 shader 的 `PressureColumn`/`Transport` 用 300，不用 `P.a`。
- **后果**：dt=150 时压力与位温增量仍按 300 秒计算，约 2 倍过冲。可能被 `ValidateAutodriveState` 的 CFL 检查捕获并触发重试降步，也可能静默偏移。
- C# 的 `StepArs232` / `EvaluateRightHandSide` / `WaveSolve` 的 CPU 分支用参数 `dt`，因此 CPU 路径正确。

---

## 3. `DynamicsVI.wgsl` —— TRiSK 向量不变动量

### 3.1 绑定与缓冲布局

| binding | 名称 | 内容 |
|---|---|---|
| 0 | `meshData` | 拓扑：`[0..e)` = EdgeCellA，`[e..2e)` = EdgeCellB，`[2e..2e+n+1)` = 每格边起始偏移，`[2e+n+1 .. 4e+n+1)` = 每格边索引，`[4e+n+1 ..)` = 符号 |
| 1 | `geo` | 几何：`[0..e)` = Dist（原边长），`[e..2e)` = Len（对偶边长），`[2e..2e+n)` = Area，`[2e+n..2e+n+8)` = ΔB，`[2e+n+8..+16)` = B_c |
| 2 | `donor` | 上风格：`[0..e)` = cell A 侧的 donor，`[e..2e)` = cell B 侧 |
| 3 | `dg` | 顶点/边算子（`VectorInvariantV2.PackGpu` 的 `11*E+9*N` 打包） |
| 4 | `inputData` | 状态：`[0..n)` = p_s，`[n..9n)` = θ(8 层)，`[9n..9n+8e)` = u_e(8 层)，其后是 Exner、φ 等 |
| 5 | `scratch` | 中间量 |
| 6 | `outputData` | 输出倾向 |

`scratch` 布局：

- `SK(l,c) = l*n+c`：格心动能
- `SD(l,c) = 8n + l*n+c`：格心散度
- `SD2(l,c) = 16n + l*n+c`：∇²散度（超粘性）
- `24n + l*n+c`：水平质量散度
- `32n + k*n+c`：界面垂直质量通量
- `FluxIndex(kind,l,e) = 41n + (kind*8+l)*e + e`：kind 0 = 质量通量，kind 1 = 位温通量
- `SQHat(l,e) = 41n + 16e + l*e + e`：插值后的位涡
- `SG(l,e) = 41n + 24e`：边梯度
- `SC(l,e) = 41n + 32e`：边旋度
- `SVx(l,v) = 41n + 40e + l*NV + v`：顶点位涡/旋度

`outputData` 布局：

- `[0..n)`：地面气压倾向
- `[n..9n)`：位温倾向
- `[9n..9n+8e)`：风倾向
- `[9n+8e ..)`：垂直质量通量（8 层）
- `[18n+8e ..)`：水平质量散度（8 层）
- `[17n+8e ..)`：Exner

### 3.2 `CellVelocity`（第 135-153 行）

逐格累加：

```wgsl
ke += (0.25 * Len(e) * Dist(e)) * u * u;    // 动能
div += (SignAt(s) * Len(e)) * u;            // 散度
scratch[SK] = ke / area;  scratch[SD] = div / area;
```

再对顶点（`v = 0..NV-1`，`NV = 2n-4`）算位涡：

```wgsl
h = Σ_k KiteFraction(v,k) * (DB(lv) * Depth(VCell(v,k)));
scratch[SVx(lv,v)] = (Vorticity(lv,v) + VCoriolis(v)) / max(1.0, h);
```

`Vorticity = Σ_k VSign(v,k) * Dist(e) * Wind(l,e) * VInverseArea(v)`——即 `ζ = ∮u·dl / A`。

### 3.3 `EdgeFlux`（第 156-185 行）

```wgsl
let u = Wind(l,e); let depth = 0.5*(Depth(a)+Depth(b));
let f = u * DB(l) * depth;                       // 质量通量
scratch[FluxIndex(0,l,e)] = f;
scratch[FluxIndex(1,l,e)] = f * ReconstructTheta(l, e, u >= 0.0);   // θ 通量（MUSCL/minmod）
```

**气压梯度力**（写入 `outputData[9n+i]`）：

```wgsl
let aa = P.gas * Exner_a / Theta_a;   // ᾱ_A = κ·Π/θ
let ab = P.gas * Exner_b / Theta_b;
outputData[9n+i] = -phi - 0.5*(aa+ab)*BC(l)*grad;
```

**CLUST25 位涡插值**：

```wgsl
let ut = TriskSum(l, e, false, 0.0);            // TRiSK 沿边速度
let centred = CentredRight(e)*SVx(right) + (1-CentredRight(e))*SVx(left);
var dir = 1u; var up = left; if (ut >= 0.0) { dir = 0u; up = right; }
var lust = ClustWeight(e,dir,0)*SVx(up);
for (k = 0..2) { lust += ClustWeight(e,dir,k+1)*SVx(对边顶点); }
let beta = abs(ut) / sqrt(u*u + ut*ut + 1e-20);  // CLUST 混合系数
scratch[SQHat(l,e)] = beta*lust + (1-beta)*centred;
```

最后写边梯度 `SG = (SD[b]-SD[a])/Dist` 与边旋度 `SC = (Vorticity(left)-Vorticity(right))/Len`。

### 3.4 `MassColumns`（第 188-229 行）

1. 逐层水平质量散度：`div = Σ sign*Len*flux / area`，写 `scratch[24n+...]` 与 `outputData[18n+8e+...]`；
2. 地面气压倾向：`outputData[c] = -Σ_l div`；
3. 界面垂直质量通量自下而上积分：`f -= DB(l)*(-total) + div_l`；
4. 超粘性梯度散度 `SD2 = Σ sign*Len*SG / area`；
5. **顶点动能混合**（可选）：`SK = (1-blend)*SK + blend*Σ CcwFraction*VertexKinetic`；
6. 顶点旋度：`SVx = Σ_k VSign*Dist*SC * VInverseArea`。

### 3.5 `CellTendencies`（第 232-243 行）

```wgsl
var power = 0.0;
for (边) { power += -Len(e) * Dist(e) * flux0(l,e) * Hyper(l,e); }
let exner = Exner(l,c) / Theta(l,c);
outputData[n+i] = -Divergence(1u,l,c) - (VTheta(l+1,c) - VTheta(l,c))
                + (0.5*power/Area(c)) * InverseCp() / exner;
```

即 `∂(θΔp)/∂t = -∇·(uθΔp) - ∂(ωθ)/∂η + 耗散加热`。最后一项是**超粘性耗散转热**，C# 的 `AddVectorInvariantHeating` 对应，但 GPU 直接内联。

### 3.6 `WindFinish`（第 246-256 行）

```wgsl
var value = outputData[9n+i] + TriskSum(l,e,true,QHat) - (SK[b]-SK[a])/d + Hyper(l,e);
let upper = EdgeVertical(l,e); let lower = EdgeVertical(l+1,e);
value += (-VWind(l+1,e,lower) + VWind(l,e,upper) + Wind(l,e)*(lower-upper))
       / max(1.0, DB(l)*0.5*(Depth(a)+Depth(b)));
outputData[9n+i] = value;
```

即完整动量倾向：`气压梯度 + 位涡通量 - ∇K + 超粘性 + 垂直动量平流`。

### 3.7 与 C# 的关系

- `CellVelocity` ↔ `ComputeHorizontalMomentumComponent` + 顶点位涡
- `EdgeFlux` ↔ `ComputeCoriolisAcceleration`（不，科氏在 TRiSK 里通过 `VCoriolis` 进位涡）+ `AddHorizontalMomentumTransport`
- `MassColumns` ↔ `DiagnoseSurfaceAndVerticalMassFlux`
- `CellTendencies` ↔ `ComputePotentialTemperatureContentTendency` + `AddVectorInvariantHeating`
- `WindFinish` ↔ `AddVerticalMomentumTransport` 的收尾
- `TriskSum` 与 `VectorInvariantV2.cs` 的 `TriskWeights`/`ClustWeights` 一一对应，CPU 侧有一份等价实现用于验证/回退。

---

## 4. `Surface.wgsl` —— 地表能量与云光学

### 4.1 Params 与绑定

```wgsl
struct SurfaceParams { n: u32, refresh: u32, longwaveMode: u32, solarScale: f32,
                       seconds: f32, dayProgress: f32, yearProgress: f32, opticalScale: f32 };
```

绑定：`surfaceStatic`（19 列/格静态参数）、`surfaceInput`（11 列/格状态）、`surfaceOutput`（6 列/格）、`surfaceClouds`（17 列/格云）、`surfaceOptics`（2 列/格：云量、透过率）。

### 4.2 `advanceGround`（第 14-31 行）

```wgsl
let exchange = clamp(select(stableK, unstableK, difference > 0.0) * difference, -100.0, 400.0);
let outgoing = emissivity * SIGMA * pow(max(180.0, skin), 4.0);
var nextSkin = skin + (solar*(1-albedo) + downLongwave - outgoing - exchange + externalHeat
    + (restoreTo - skin)*skinCapacity*restoringRate) * seconds / skinCapacity;
if (slabCapacity > 1.0) {
    let restored = slab + (max(restoreTo, slabFloor) - slab)*restoringRate*seconds;
    let mean = (skinCapacity*nextSkin + slabCapacity*restored) / (skinCapacity + slabCapacity);
    let split = (nextSkin - restored) * exp(-conductance*(1/skinCapacity + 1/slabCapacity)*seconds);
    nextSkin = mean + slabCapacity/(skinCapacity+slabCapacity)*split;
    nextSlab = mean - skinCapacity/(skinCapacity+slabCapacity)*split;
}
return Ground(clamp(nextSkin,180,340), clamp(nextSlab,180,340), exchange);
```

- 稳定/不稳定分层用 `select(stableK, unstableK, difference > 0)`——**这是 C# 侧 `UpdateSlowSurface` 里也有的逻辑，但 GPU 把常数写死在调用点**（陆地 15/3、海洋 9/3、海冰 15/3）。
- `clamp(180, 340)` K 温度限幅写在 shader 里。

### 4.3 `cloudOptics`（第 33-50 行）

8 层云，`f = 1 - exp(-mm/0.1)`，`perMm = 50 或 150`（按 `surfaceClouds[(8+l)*n+id] < 253.15` 选：冷云更长），`tau = opticalScale * perMm * mm / f`，`through *= (1 - f + f/(1 + 0.1125*tau))`，`clear *= (1-f)`。

返回 `(云量, 透过率)`；`longwaveMode == 1` 时云量取 `lowMax`（低云最大值），否则 `1-clear`。

### 4.4 `SurfaceStep`（第 53-124 行）

1. 读静态参数：陆地分数、陆地/海洋/海冰的 albedo、热容、目标温度、恢复率、传导率、海冰分数、经度、纬度、lead 混合率；
2. 太阳高度角：
   ```wgsl
   let declination = (23.5*PI/180)*sin(2*PI*yearProgress);
   let cosHourAngle = cos(2*PI*dayProgress + longitude - PI);
   let sinElevation = clamp(latY*sin(decl) + cosLat*cos(decl)*cosHourAngle, 0, 1);
   let solar = 1361.0 * solarScale * sinElevation * transmission;
   ```
3. 长波：`0.78*SIGMA*T_air⁴*(0.82+0.18*cloud)`，或 `longwaveMode==2` 时用 `surfaceInput[10n+id]`；
4. 三套 `advanceGround`：陆地（emissivity 0.94）、海洋（0.94）、海冰（0.98，slabFloor 271.35）；
5. 海冰与 lead 的混合（`seaIce` 分数，`leadMixingRate`）；
6. 输出：陆地 skin/slab、海洋 lead/under、空气交换、海冰表面温度。

---

## 5. 三层实现对照

| 层 | 载体 | 是否 C# 可见 | 与源码一致性 |
|---|---|---|---|
| 本地求解器核 | `AtmosphereOverhaul.Burst.dll` | 是（同名 `TryBurst*` + C# 回退） | 严格一致 |
| 世界模式动力学 | `DynamicsVI.wgsl` / `Waves.wgsl` / `Surface.wgsl` | 否，独立重写 | 公式对应，但组织与常数不同 |
| 托管杂项 | `Perf81.dll` 的 C# | 是 | — |

---

## 6. 发现的问题

### 6.1 `Waves.wgsl` 硬编码 300 秒（严重）

- 位置：第 42 行 `300.0 * Q(P.source, P.n + i)`、第 84 行 `/ 300.0`。
- 影响：`allowedStep` 从 300 降到 150 后，GPU 仍按 300 秒计算压力与位温增量。
- 为什么 C# 看不到：`P.a` 传了 `dt`，但只被 `VectorOp` 的 mode 0/5/7 使用；`PressureColumn`/`Transport` 完全忽略它。
- 建议：若要做世界模式数值修正，需改 WGSL 并重新编译 Perf81；无法从 C# 侧修补。

### 6.2 `DynamicsVI.wgsl` 的 dt 施加点

`DynamicsVI.wgsl` 全部 5 个入口都**不出现** `P.a` 或 `300`，输出的是**倾向**（per second 量纲）；时间步的乘法在 `WindFinish` 之外的 C#/`VectorOp` 层完成。这一点与 `Waves.wgsl` 不一致，需要交叉验证 `AutodriveSession` 的调用顺序。

### 6.3 温度限幅写死在 shader

`advanceGround` 的 `clamp(nextSkin, 180, 340)`、`clamp(nextSlab, 180, 340)` 是恒定值；C# 的 `UpdateSlowSurface` 若允许配置，GPU 路径会忽略。

### 6.4 `longwaveMode` 三条分支

`select(0.78*SIGMA*T⁴*(0.82+0.18*cloud), surfaceInput[10n+id], longwaveMode == 2)`；`cloudOptics` 里 `longwaveMode == 1` 又走 `lowMax`。三种模式在 C# 侧的对应关系需要核对。

---

## 7. 对 patch 的意义

1. patch 不碰世界模式，WGSL 的 300 秒问题不影响现有四个兼容模块。
2. 若要给世界模式加功能（例如按全球风调本地风），必须新建通道，且不能假设 GPU 与 C# 数值一致。
3. 调试世界模式时，优先看 `scalar[0]` 状态码（-1/-2/-3 分别代表 NaN、除零、未收敛），这比读 C# 日志直接。
4. 若要修改世界模式物理，改动点在三个 `.wgsl` + `Perf81` 重新编译，而非 `AtmosphereOverhaul.Burst.dll`。
