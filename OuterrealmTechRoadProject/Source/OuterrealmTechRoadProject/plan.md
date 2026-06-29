# OuterrealmTechRoadProject 超维链路实现方案（待审阅）

本文根据新的设计目标重写：本 mod 只添加一种新的世界道路：**超维链路**。它扩展 `Roads of the Rim`（以下简称 RotR）和 RimWorld 原版世界道路系统，使玩家可以通过建筑在世界地图上直接绘制道路，确认后瞬间完成建造。

## 1. 设计目标

### 1.1 新增内容

只新增一种道路：

- 中文名：超维链路
- 英文名：Outerrealm Link
- 建议 `RoadDef.defName`：`OuterrealmTech_OuterrealmLink`

道路特性：

- 速度快于 RotR 的 `GlitterRoad`（闪耀高速公路）。
- 可以铺设在任何世界地形上，包括：
  - 普通陆地
  - 冰面、海冰、原本不允许铺路的生态圈
  - Ocean、Lake 等水域
  - `Hilliness.Impassable` 的不可通行山脉
  - 其他 `BiomeDef.allowRoads == false` 或 `BiomeDef.impassable == true` 的 tile
- 当前版本建造不消耗任何资源。
- 当前版本不使用车队逐格施工。
- 当前版本通过建筑在世界地图选点，确认后瞬间完成整条路线。

### 1.2 局部地图生成目标

当世界道路生成到局部地图时：

- 如果世界 tile 对应局部地图几乎全是深水/海洋深水，则道路位置直接生成重型桥梁。
- 如果世界 tile 是不可通过山地，则在道路位置清除出一条没有岩顶的空旷直线。
- 其他地形上生成超维链路路面。

## 2. 原版与 RotR 机制结论

RimWorld 原版世界道路最终保存在：

```csharp
SurfaceTile.potentialRoads
```

道路不是独立对象，而是相邻两个世界 tile 之间的双向链接：

```csharp
SurfaceTile.RoadLink
{
    neighbor = otherTile,
    road = roadDef
}
```

RotR 的施工系统最终也是把道路写入 `SurfaceTile.potentialRoads`，因此本项目也应沿用该数据结构。

必须注意三个原版限制：

1. `SurfaceTile.Roads` 在 `PrimaryBiome.allowRoads == false` 时返回 `null`，禁路生态圈即使有 `potentialRoads` 也可能被原版视为无路。
2. `WorldPathGrid.CalculatedMovementDifficultyAt` 对 `PrimaryBiome.impassable` 或 `Hilliness.Impassable` 直接返回 `1000f`，导致 `World.Impassable(tile)` 为 true。
3. `WorldPathing.FindPath` 扩展邻居时先排除 `world.Impassable(neighbor)`，再计算道路倍率。也就是说，仅仅把 `RoadDef.movementCostMultiplier` 设得很低，不能让海洋和不可通行山脉变成可走。

因此超维链路需要同时处理：

- 世界道路数据写入。
- 禁路/不可通行 tile 的道路可见性。
- 世界寻路可通行性。
- 必要时对特殊 tile 做边级通行限制。
- 局部地图道路生成。

## 3. RoadDef 设计

### 3.1 超维链路 RoadDef

建议定义：

```xml
<RoadDef>
  <defName>OuterrealmTech_OuterrealmLink</defName>
  <label>outerrealm link</label>
  <priority>100</priority>
  <movementCostMultiplier>0.10</movementCostMultiplier>
  <tilesPerSegment>100</tilesPerSegment>
  <pathingMode>Bulldoze</pathingMode>
  ...
</RoadDef>
```

说明：

- RotR `GlitterRoad` 的 `movementCostMultiplier` 是 `0.25`。
- 超维链路建议初始设为 `0.10`，明显快于闪耀高速公路。
- `priority` 建议设为 `100`，确保它能覆盖普通道路、RotR 道路和原版高速公路。
- `pathingMode` 使用 `Bulldoze`，便于局部地图生成时清理障碍。

### 3.2 RotR 扩展

为了复用 RotR 的速度修正逻辑，建议给超维链路挂 RotR 的扩展：

```xml
<modExtensions>
  <li Class="RoadsOfTheRim.DefModExtension_RotR_RoadDef">
    <built>true</built>
    <biomeModifier>1</biomeModifier>
    <hillinessModifier>1</hillinessModifier>
    <winterModifier>1</winterModifier>
    <canBuildOnImpassable>true</canBuildOnImpassable>
    <canBuildOnWater>true</canBuildOnWater>
    <costs>
      <Work>1</Work>
    </costs>
  </li>
  <li Class="OuterrealmTechRoadProject.DefModExtension_OuterrealmLinkRoad">
    <allowAnyTerrain>true</allowAnyTerrain>
    <strictEdgePassability>true</strictEdgePassability>
    <impassableTileMovementDifficulty>4</impassableTileMovementDifficulty>
  </li>
</modExtensions>
```

当前不走 RotR 车队施工，所以 `costs` 只是为了兼容 RotR 的数据结构。实际建筑直建逻辑不读取资源成本。

### 3.3 本项目扩展

建议新增：

```csharp
public class DefModExtension_OuterrealmLinkRoad : DefModExtension
{
    public bool allowAnyTerrain = true;
    public bool strictEdgePassability = true;
    public float impassableTileMovementDifficulty = 4f;
}
```

用途：

- 标记这是本项目的超维链路。
- 允许任意地形。
- 控制不可通行 tile 被道路激活后的基础移动难度。
- 控制是否必须沿道路边通行。

## 4. 建筑直建系统

### 4.1 建筑定义

建议添加一个建筑作为唯一入口：

- 中文名：超维链路投射器
- 英文名：Outerrealm Link Projector
- ThingDef：`OuterrealmTech_OuterrealmLinkProjector`
- C# 类：`Building_OuterrealmLinkProjector`

选中建筑后提供 gizmo：

- 规划超维链路
- 取消规划

当前版本不需要：

- 资源库存。
- 电力消耗。
- 分段施工。
- 车队派遣。

这些都作为后续扩展预留。

### 4.2 交互流程

1. 玩家在殖民地地图建造并选中“超维链路投射器”。
2. 点击“规划超维链路”。
3. 自动切换或允许玩家切换到世界地图。
4. 起点默认为建筑所在地图的世界 tile：

```csharp
PlanetTile startTile = building.Tile;
```

5. 使用 `Find.WorldTargeter.BeginTargeting` 进入世界地图选点模式。
6. 玩家逐个点击相邻世界 tile，形成路线。
7. 点击已选节点可以回退到该节点。
8. 右键取消，或点击确认按钮结束规划。
9. 弹出确认窗口，显示：
   - 起点
   - 终点
   - 总 tile 数
   - 总路段数
   - 本次不消耗资源
10. 玩家确认后，立即把整条路线写入世界道路数据。

### 4.3 路线约束

每段必须连接相邻 tile：

```csharp
Find.WorldGrid.IsNeighbor(from, to)
```

不允许一条 `RoadLink` 跨多个 tile。原版道路显示、存档和寻路都假设道路边是相邻 tile。

当前版本允许任意地形，因此选点校验只需要：

- tile 有效。
- tile 在同一 `PlanetLayer`。
- tile 是 `SurfaceTile`。
- tile 与上一节点相邻。

不检查：

- `BiomeDef.allowRoads`
- `BiomeDef.impassable`
- `Hilliness.Impassable`
- 是否水域
- 是否冰面

## 5. 瞬间完成建造

### 5.1 统一写路工具

建议所有写路逻辑集中到工具类：

```csharp
public static class OuterrealmLinkUtility
{
    public static bool TryOverlayOuterrealmLinkSegment(PlanetTile from, PlanetTile to)
    {
        RoadDef roadDef = OuterrealmRoadDefOf.OuterrealmTech_OuterrealmLink;
        return TryOverlayRoadSegment(from, to, roadDef);
    }

    public static bool TryOverlayRoadSegment(PlanetTile from, PlanetTile to, RoadDef roadDef)
    {
        if (!from.Valid || !to.Valid)
        {
            return false;
        }

        if (from.Layer != to.Layer)
        {
            return false;
        }

        if (!Find.WorldGrid.IsNeighbor(from, to))
        {
            return false;
        }

        if (!(Find.WorldGrid[from] is SurfaceTile fromTile))
        {
            return false;
        }

        if (!(Find.WorldGrid[to] is SurfaceTile toTile))
        {
            return false;
        }

        fromTile.potentialRoads ??= new List<SurfaceTile.RoadLink>();
        toTile.potentialRoads ??= new List<SurfaceTile.RoadLink>();

        RemoveLowerPriorityRoad(fromTile, to, roadDef);
        RemoveLowerPriorityRoad(toTile, from, roadDef);

        AddRoadLinkIfMissing(fromTile, to, roadDef);
        AddRoadLinkIfMissing(toTile, from, roadDef);

        MarkWorldRoadsDirtyAndRecalculate(from, to);
        return true;
    }
}
```

### 5.2 覆盖规则

超维链路应覆盖所有低优先级道路。

建议规则：

- 如果同一边已有超维链路，不重复添加。
- 如果同一边已有更低 priority 的道路，移除旧路后添加超维链路。
- 如果理论上已有更高 priority 的道路，则保留旧路。但当前超维链路 priority 设为 `100`，一般不会遇到更高道路。

### 5.3 完成整条路线

确认后执行：

```csharp
for (int i = 0; i < route.Count - 1; i++)
{
    OuterrealmLinkUtility.TryOverlayOuterrealmLinkSegment(route[i], route[i + 1]);
}
```

如果某段失败：

- 记录错误日志。
- 跳过失败段或中断整个建造，需要设计取舍。

建议初版中断并提示玩家，因为路线数据理论上在确认前已经验证过，不应失败。

## 6. 世界寻路

### 6.1 让任意地形可通行

需要 patch `WorldPathGrid.CalculatedMovementDifficultyAt`：

- 如果原结果小于 `1000f`，普通可通行 tile 不需要处理。
- 如果原结果是 `1000f`，检查该 tile 的 `potentialRoads`。
- 只要任意一条道路是超维链路，就把该 tile 的移动难度改为 `impassableTileMovementDifficulty`，建议初始值 `4f`。

伪代码：

```csharp
[HarmonyPatch(typeof(WorldPathGrid), nameof(WorldPathGrid.CalculatedMovementDifficultyAt))]
public static class Patch_WorldPathGrid_CalculatedMovementDifficultyAt
{
    public static void Postfix(ref float __result, PlanetTile tile)
    {
        if (__result < 1000f)
        {
            return;
        }

        if (!OuterrealmLinkUtility.TileHasOuterrealmLink(tile, out var ext))
        {
            return;
        }

        __result = ext.impassableTileMovementDifficulty;
    }
}
```

这样海洋、湖泊、不可通行山脉、禁路冰面等 tile 只要拥有超维链路，就会从世界寻路角度变为可通行。

### 6.2 边级通行限制

强烈建议保留边级限制。

原因：如果只做 tile 级放行，一个海洋 tile 只要有一条超维链路，就可能被原版 A* 从任意方向进入/离开，造成“桥面外漏”。

规则：

- 普通可通行 tile 之间，不限制。
- 如果 from 或 to 是原本不可通行/禁路/水域 tile，则必须存在 from-to 的超维链路 RoadLink。

工具函数：

```csharp
public static bool CanTraverseWorldEdge(PlanetTile from, PlanetTile to)
{
    if (!NeedsOuterrealmLinkEdge(from) && !NeedsOuterrealmLinkEdge(to))
    {
        return true;
    }

    return HasOuterrealmLinkBetween(from, to);
}
```

`NeedsOuterrealmLinkEdge(tile)` 判断：

- `SurfaceTile.WaterCovered`
- `surface.PrimaryBiome.impassable`
- `surface.PrimaryBiome.allowRoads == false`
- `surface.hilliness == Hilliness.Impassable`

实现方式：

- 第一阶段可以暂时不做 transpiler，只实现 tile 级放行，快速验证功能。
- 正式版本建议 patch `WorldPathing.FindPath` 的邻居扩展逻辑，在 `!world.Impassable(neighbor)` 后追加 `CanTraverseWorldEdge(current, neighbor)`。

该 patch 位于高频寻路路径，必须避免分配：

- 不使用 LINQ。
- 不创建临时 List。
- 直接遍历 `SurfaceTile.potentialRoads`。
- 不在热路径做反射。

### 6.3 刷新缓存

每写入一段超维链路后需要刷新世界显示和寻路缓存：

```csharp
Find.World.renderer.SetDirty<WorldDrawLayer_Paths>(from.Layer);

bool needsRecacheFrom;
bool needsRecacheTo;
Find.WorldPathGrid.RecalculatePerceivedMovementDifficultyAt(from, out needsRecacheFrom);
Find.WorldPathGrid.RecalculatePerceivedMovementDifficultyAt(to, out needsRecacheTo);

if (needsRecacheFrom || needsRecacheTo)
{
    Find.WorldReachability.ClearCache();
}
```

如果新增自定义水上道路显示层，也要标记该层 dirty。

## 7. 世界地图显示

### 7.1 普通显示

`RoadDef.worldRenderSteps` 建议使用明显区别于 RotR 道路的层和颜色。

可新增：

```xml
<RoadWorldLayerDef>
  <defName>OuterrealmLinkGlow</defName>
  <order>...</order>
  <color>(0.2, 0.95, 1.0, 1.0)</color>
</RoadWorldLayerDef>
```

超维链路 RoadDef：

```xml
<worldRenderSteps>
  <li>
    <layer>Outline</layer>
    <width>0.80</width>
  </li>
  <li>
    <layer>OuterrealmLinkGlow</layer>
    <width>0.45</width>
  </li>
</worldRenderSteps>
```

### 7.2 水上显示

RotR 已有 `WorldLayer_RoadsOnWater`，会绘制 `surfaceTile.WaterCovered` tile 上的 `potentialRoads`，并将 mesh 抬高约 `0.012f` 避免被水面遮住。

如果实测 RotR 的水上层能绘制超维链路，则直接复用。

如果不能稳定绘制，则新增本项目世界层：

- 继承 `WorldDrawLayer_Paths`。
- 只处理 `SurfaceTile.WaterCovered` 且存在超维链路的 tile。
- 读取超维链路的 `worldRenderSteps`。
- `FinalizePoint` 中沿法线抬高，避免 z-fighting。

## 8. 局部地图生成

### 8.1 总体策略

原版 `GenStep_Roads` 会根据世界道路 `RoadDef.roadGenSteps` 在局部地图上生成道路。超维链路需要额外处理两类特殊局部地图：

- 深水/海洋深水地图：生成重型桥梁。
- 不可通行山地地图：清理出无岩顶空旷直线。

建议新增或 patch 一个专门的 road gen step：

```csharp
public class RoadDefGenStep_OuterrealmLinkPlace : RoadDefGenStep_Place
{
    public override void Place(
        Map map,
        IntVec3 position,
        TerrainDef rockDef,
        IntVec3 origin,
        GenStep_Roads.DistanceElement[,] distance)
    {
        // 根据当前位置地形和世界 tile 类型选择生成方式。
    }
}
```

也可以对 `RoadDefGenStep_Place.Place` 做 Harmony postfix，只处理 `place == OuterrealmLinkTerrain` 或当前 RoadDef 是超维链路的情况。

### 8.2 海洋/深水：重型桥梁

如果位置地形是深水或海洋深水：

- 直接设置为重型桥梁地形。
- 可复用 RotR 的 `ConcreteBridge`，因为它支持重型建筑。
- 或新增 `OuterrealmHeavyBridge`。

建议初版复用 RotR：

```csharp
map.terrainGrid.SetTerrain(position, RoadsOfTheRim.TerrainDefOf.ConcreteBridge);
```

如果不想直接依赖 RotR 地形类，也可以通过 `DefDatabase<TerrainDef>.GetNamed("ConcreteBridge")` 获取。

需要覆盖的地形包括：

- `WaterDeep`
- `WaterOceanDeep`
- `WaterMovingDeep`
- 其他 `TerrainDef.IsWater == true` 的深水地形

如果是浅水，也可以统一生成重型桥梁，保持超维链路连续。

### 8.3 不可通行山地：无岩顶空旷直线

如果当前地图所在世界 tile 是：

```csharp
Find.WorldGrid[map.Tile].hilliness == Hilliness.Impassable
```

则道路生成位置应变为空旷直线：

- 清除当前位置 edifice，包括岩石墙。
- 清除当前位置 roof，尤其是 `RoofDefOf.RoofRockThick`。
- 设置为超维链路路面或普通可行走地面。
- 清除雾，避免道路生成后仍不可见。

伪代码：

```csharp
private static void ClearMountainTunnelCell(Map map, IntVec3 c, TerrainDef linkTerrain)
{
    Thing edifice = c.GetEdifice(map);
    if (edifice != null)
    {
        edifice.Destroy(DestroyMode.Vanish);
    }

    map.roofGrid.SetRoof(c, null);
    map.fogGrid.Unfog(c);
    map.terrainGrid.SetTerrain(c, linkTerrain);
}
```

注意：

- 需求明确是“没有岩顶的空旷直线”，所以不保留厚岩顶。
- 如果道路两侧仍是岩山，空旷直线相当于切出一条露天山口。
- 由于 `GenStep_Roads` 生成的是有宽度的道路带，实际清理范围由 `roadGenSteps` 曲线决定。

### 8.4 普通地形和冰面：超维链路路面

新增地形：

- `OuterrealmLinkTerrain`

建议特性：

- 可行走。
- 美观/清洁度按超维科技风格设置。
- `affordances` 至少支持 Light/Medium/Heavy，避免局部地图道路承载能力差。
- 是否可拆除按玩法决定；初版可以不可建造、不可拆，只作为世界道路生成地形。

在普通地形、冰面、沼泽等非深水位置：

```csharp
map.terrainGrid.SetTerrain(position, OuterrealmTerrainDefOf.OuterrealmLinkTerrain);
```

如果当前位置有阻挡物：

- 对 `pathingMode=Bulldoze`，原版会尝试推平/清除。
- 本项目可在 postfix 中额外清理道路位置的可摧毁物，避免道路被岩石/建筑残留堵住。

## 9. 建筑 UI 与贴图

### 9.1 Gizmo 文本

所有显示文本使用翻译 key：

- `OuterrealmTechRoadProject_CommandPlanOuterrealmLink`
- `OuterrealmTechRoadProject_CommandPlanOuterrealmLinkDesc`
- `OuterrealmTechRoadProject_ConfirmOuterrealmLinkTitle`
- `OuterrealmTechRoadProject_ConfirmOuterrealmLinkText`
- `OuterrealmTechRoadProject_OuterrealmLinkBuilt`
- `OuterrealmTechRoadProject_OuterrealmLinkBuiltText`

中文放入：

```text
Languages/ChineseSimplified (简体中文)/
```

英文放入：

```text
Languages/English/
```

### 9.2 贴图预加载

按项目规范预加载命令贴图：

```csharp
[StaticConstructorOnStartup]
public static class OuterrealmLinkTex
{
    public static readonly Texture2D IconPlanOuterrealmLink =
        ContentFinder<Texture2D>.Get("UI/Commands/PlanOuterrealmLink", false) ?? BaseContent.WhiteTex;
}
```

### 9.3 路线预览

规划时使用：

- `GenDraw.DrawWorldLineBetween` 绘制已选路线。
- 当前鼠标 tile 合法时画预览线。
- 非相邻 tile 或无效 tile 用红色提示。

## 10. 推荐源码结构

```text
Defs/
  DefModExtension_OuterrealmLinkRoad.cs
DefOfs/
  OuterrealmRoadDefOf.cs
  OuterrealmTerrainDefOf.cs
Buildings/
  Building_OuterrealmLinkProjector.cs
World/
  OuterrealmLinkPlanner.cs
  OuterrealmLinkUtility.cs
Patches/
  Patch_WorldPathGrid_CalculatedMovementDifficultyAt.cs
  Patch_WorldPathing_FindPath.cs
  Patch_RoadDefGenStep_Place.cs
UI/
  Dialog_ConfirmOuterrealmLink.cs
  OuterrealmLinkTex.cs
```

Def 文件：

```text
Defs/RoadDefs/OuterrealmLinkRoadDefs.xml
Defs/TerrainDefs/OuterrealmLinkTerrainDefs.xml
Defs/ThingDefs/OuterrealmLinkProjector.xml
Defs/RoadWorldLayerDefs/OuterrealmRoadWorldLayerDefs.xml
```

语言文件：

```text
Languages/ChineseSimplified (简体中文)/Keyed/OuterrealmLink.xml
Languages/English/Keyed/OuterrealmLink.xml
Languages/ChineseSimplified (简体中文)/DefInjected/RoadDef/OuterrealmLinkRoadDefs.xml
Languages/English/DefInjected/RoadDef/OuterrealmLinkRoadDefs.xml
```

## 11. 实现顺序

### 阶段 1：最小可用版本

1. 定义 `OuterrealmTech_OuterrealmLink` RoadDef。
2. 定义 `OuterrealmLinkTerrain`。
3. 定义 `OuterrealmTech_OuterrealmLinkProjector` 建筑。
4. 实现建筑 gizmo 和世界地图路线规划。
5. 实现确认窗口。
6. 确认后瞬间写入整条路线的双向 `SurfaceTile.RoadLink`。
7. patch `WorldPathGrid.CalculatedMovementDifficultyAt`，让拥有超维链路的任意不可通行 tile 变为可通行。
8. 刷新世界道路显示和寻路缓存。

### 阶段 2：局部地图特殊生成

1. 海洋/深水道路生成重型桥梁。
2. 不可通行山地道路清出无岩顶空旷直线。
3. 普通地形和冰面生成超维链路路面。
4. 测试进入海洋、山地、冰面 tile 后地图生成是否符合预期。

### 阶段 3：严格边级寻路

1. 实现 `CanTraverseWorldEdge(from, to)`。
2. patch `WorldPathing.FindPath`。
3. 确保海洋/山脉 tile 只能沿超维链路进出，不会从路面外漏。

### 阶段 4：后续资源消耗

后续再添加：

- 建筑能量消耗。
- 材料消耗。
- 施工时间。
- 维护成本。
- 取消/回收/拆除道路逻辑。

当前版本明确不实现这些内容。

## 12. 测试清单

### 12.1 世界道路写入

- 规划普通陆地路线，确认后立即生成超维链路。
- 规划跨海路线，确认后立即生成超维链路。
- 规划穿越不可通行山脉路线，确认后立即生成超维链路。
- 规划冰面/禁路生态圈路线，确认后立即生成超维链路。
- 存档并读档后，超维链路仍存在。
- 重复铺设同一段不会产生重复 RoadLink。
- 超维链路能覆盖低优先级道路。

### 12.2 世界寻路

- 车队能沿超维链路跨海。
- 车队能沿超维链路穿过不可通行山脉。
- 车队能沿超维链路穿过原本无法铺路的冰面/禁路生态圈。
- 如果启用边级限制，车队不能从海洋/山脉中的超维链路 tile 随意离开到没有 RoadLink 的邻居。

### 12.3 局部地图生成

- 全深水/海洋地图中，道路位置生成重型桥梁。
- 不可通行山地地图中，道路位置清除岩石和岩顶，形成无岩顶空旷直线。
- 普通地形中生成超维链路路面。
- 冰面地形中生成超维链路路面。
- 道路位置不被迷雾、岩石、屋顶残留阻断。

### 12.4 性能

- 路线规划只检查当前点击 tile 和上一 tile。
- 瞬间建造时按路线长度线性写入，不扫描全世界。
- 寻路 patch 不分配临时集合，不使用 LINQ，不做反射。
- 局部地图生成只处理道路范围，不扫描无关区域，除非后续清理步骤确实需要。

## 13. 关键风险

- `WorldPathing.FindPath` 的 transpiler 对 RimWorld 1.6 小版本更新敏感。初版可以先不做边级限制，等核心功能稳定后再补。
- RotR 已 patch `SurfaceTile.Roads` 返回 `potentialRoads`，本项目可以受益，但内部查路仍建议直接读 `potentialRoads` 或 `GetRoadDef(..., visibleOnly: false)`。
- 局部地图中直接清除不可通行山地的岩石和岩顶，会改变地图生成强度，可能影响虫巢、古代威胁、山体结构。初版应只清道路带，不做大范围清理。
- 全深水地图生成重型桥梁时，要避免全局修改水地形 affordance，优先只在超维链路道路生成位置强制设地形。
- 当前不消耗资源，平衡性很强，需要在说明或后续版本中补资源/能量成本。

## 14. 推荐最终方案

当前版本按以下方式实现：

1. 只添加 `OuterrealmTech_OuterrealmLink` 一种 RoadDef。
2. 速度设为 `movementCostMultiplier = 0.10`，快于 RotR `GlitterRoad`。
3. 允许任意世界地形。
4. 玩家通过“超维链路投射器”在世界地图规划路线。
5. 确认后瞬间写入整条路线的双向 `SurfaceTile.RoadLink`。
6. 暂不消耗资源，资源逻辑以后再做。
7. 世界寻路先通过 `WorldPathGrid.CalculatedMovementDifficultyAt` 让有超维链路的不可通行 tile 变为可走。
8. 局部地图中：
   - 深水/海洋生成重型桥梁。
   - 不可通行山地清出无岩顶空旷直线。
   - 其他地形生成超维链路路面。
9. 后续再补边级寻路限制和资源/能量消耗。
