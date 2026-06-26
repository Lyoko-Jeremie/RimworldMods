# Caravan Speed Patch 与极端 MoveSpeed 导致世界路径异常的问题报告

## 问题现象

当 Pawn 的 `MoveSpeed` 被装备、基因、特性或其他 mod 提升到极高数值时，远行队在世界地图上的路径规划会异常。

已观察到的表现：

- 世界地图路径在完全平坦地形上也会绕圈、螺旋或选择明显非最短路径。
- 规划界面显示预计到达时间为 `0`。
- 问题在极高移动速度下稳定复现。例如单个 Pawn 的 `MoveSpeed` 达到约 `5000000` cells/s 时，远行队移动速度显示可达到 `60000` 格/天。

## 涉及的第三方 Mod

Mod 名称：`Caravan Speed Patch`

源码/反编译路径：

```text
F:\RimworldCodeIL\CaravanSpeedPatch
```

其核心补丁位于：

```text
F:\RimworldCodeIL\CaravanSpeedPatch\1Patch.cs
```

补丁目标：

```csharp
[HarmonyPatch(typeof(CaravanTicksPerMoveUtility), "GetTicksPerMove",
    new System.Type[] { typeof(Caravan), typeof(StringBuilder) })]
public static class CaravanTicksPerMoveUtility_GetTicksPerMove_Patch
```

## 代码级原因

`Caravan Speed Patch` 会在原版远行队速度计算之后，根据 Pawn 当前 `MoveSpeed` 进一步缩放 `ticksPerMove`。

关键逻辑如下：

```csharp
private static float GetCaravanSpeedFactorByPawnMoveSpeed(Pawn pawn)
{
    float statValueAbstract = pawn.def.GetStatValueAbstract(StatDefOf.MoveSpeed);
    float statValue = pawn.GetStatValue(StatDefOf.MoveSpeed);
    return statValue <= statValueAbstract ? 1f : statValueAbstract / statValue;
}
```

然后：

```csharp
float num4 = num3 * CaravanSpeedPatchSettings.caravanSpeedMultiplier;
__result = Mathf.RoundToInt((float)__result * num4);
```

当 `MoveSpeed` 极高时，`statValueAbstract / statValue` 会变成极小值。

例如：

```text
Pawn 种族基础 MoveSpeed: 1000
Pawn 当前 MoveSpeed:     5000000
factor = 1000 / 5000000 = 0.0002
```

如果原版或其他 mod 已经把远行队速度限制到：

```text
__result = 50 ticks/世界格
```

经过 `Caravan Speed Patch` 后会变成：

```text
50 * 0.0002 = 0.01
Mathf.RoundToInt(0.01) = 0
```

最终结果是：

```text
Caravan.TicksPerMove == 0
```

## 为什么 `TicksPerMove == 0` 会导致绕圈路径

RimWorld 原版世界路径算法 `WorldPathing.FindPath` 使用整数边权。

关键公式如下：

```csharp
int cost = (int)(
    caravanTicksPerMove *
    movementDifficulty *
    roadMovementDifficultyMultiplier
) + knownCost;
```

当 `caravanTicksPerMove == 0` 时，无论地形难度、道路倍率是多少：

```text
0 * movementDifficulty * roadMultiplier = 0
```

因此所有可通行世界格的移动成本都变成 `0`。

这会破坏 A* 路径搜索的基本前提：

- 所有边权相同且为 0。
- 已知成本不再随着路径长度增加。
- 优先队列中大量节点拥有相同优先级。
- 搜索结果会高度依赖邻居枚举顺序，而不是几何最短路径或实际最快路径。

最终表现就是：

- 平坦地形上也可能绕圈。
- 路径可能呈螺旋状。
- 到达时间可能显示为 `0`。

## 为什么这是高风险问题

原版实际移动成本 `Caravan_PathFollower.CostToMove` 内部有最小值保护：

```csharp
Mathf.Clamp(cost, 1, 30000)
```

但是 `WorldPathing.FindPath` 的路径规划边权没有同样的最小值保护。

因此只要某个 mod 允许 `CaravanTicksPerMoveUtility.GetTicksPerMove(...)` 返回 `0`，就可能导致世界路径规划退化。

## 建议修复方案

### 方案 1：对最终 `__result` 增加最小值保护

这是最直接、兼容性较好的修复方式。

建议在 `Caravan Speed Patch` 的 postfix 末尾增加：

```csharp
__result = Mathf.Max(__result, 1);
```

更稳妥的版本：

```csharp
if (caravan?.Shuttle == null)
{
    __result = Mathf.Max(__result, 1);
}
```

这样至少可以保证世界路径边权不会因为 `ticksPerMove == 0` 完全退化。

### 方案 2：缩放前后都避免极小因子

可以限制 `GetCaravanSpeedFactorByPawnMoveSpeed` 的返回值下限。

例如：

```csharp
private const float MinSpeedFactor = 0.01f;

private static float GetCaravanSpeedFactorByPawnMoveSpeed(Pawn pawn)
{
    float baseMoveSpeed = pawn.def.GetStatValueAbstract(StatDefOf.MoveSpeed);
    float currentMoveSpeed = pawn.GetStatValue(StatDefOf.MoveSpeed);

    if (currentMoveSpeed <= baseMoveSpeed)
    {
        return 1f;
    }

    return Mathf.Max(baseMoveSpeed / currentMoveSpeed, MinSpeedFactor);
}
```

这可以避免超高 `MoveSpeed` 把远行队 ticks 压缩到接近 0。

### 方案 3：使用 `CeilToInt` 而不是 `RoundToInt`

当前逻辑：

```csharp
__result = Mathf.RoundToInt((float)__result * num4);
```

对于小于 `0.5` 的正数会返回 `0`。

可以改为：

```csharp
__result = Mathf.CeilToInt((float)__result * num4);
```

再加最终保护：

```csharp
__result = Mathf.Max(__result, 1);
```

这样只要计算结果是正数，就不会被四舍五入成 `0`。

### 方案 4：设置合理的远行队最高速度上限

原版远行队系统并不是为数万格/天设计的。即使避免 `0`，极端高速度仍可能导致 UI、到达时间估算、补给计算或路径显示出现边界问题。

可以考虑给远行队速度设置最大值，例如：

```csharp
private const int MinTicksPerMove = 10; // 6000 tiles/day
__result = Mathf.Max(__result, MinTicksPerMove);
```

具体数值可以作为 mod 设置项开放。

## 当前本地 Mod 的临时兼容处理

在 `OuterrealmTechRobot` 中，为了避免人造人女仆触发该问题，已添加兼容补丁：

```csharp
[HarmonyPatch(typeof(CaravanTicksPerMoveUtility), nameof(CaravanTicksPerMoveUtility.GetTicksPerMove),
    new System.Type[] { typeof(Caravan), typeof(StringBuilder) })]
[HarmonyAfter("rimworld.ktk_CaravanSpeedPatch")]
public static class Patch_CaravanTicksPerMoveUtility_GetTicksPerMove_Caravan
```

该补丁会在 `Caravan Speed Patch` 之后执行，并把包含人造人女仆的远行队最低移动成本钳制到安全值：

```text
MinWorldTicksPerMove = 50
```

这只是针对本 mod 特定 Pawn 的兼容处理，并不能从根源上解决 `Caravan Speed Patch` 对所有极端 `MoveSpeed` Pawn 可能返回 `0` 的问题。

## 建议结论

建议 `Caravan Speed Patch` 在所有 `GetTicksPerMove` postfix 中保证：

```csharp
__result >= 1
```

并优先考虑：

```csharp
__result = Mathf.Max(Mathf.CeilToInt((float)__result * num4), 1);
```

这样可以避免世界路径算法出现 0 成本边，从根源上解决绕圈、螺旋路径和到达时间为 0 的问题。
