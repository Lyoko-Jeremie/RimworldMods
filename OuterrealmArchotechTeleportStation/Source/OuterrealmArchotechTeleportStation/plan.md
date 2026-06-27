# 超维科技远古传送站：世界地图传送门设计方案

本文档基于 RimWorld 1.6 原版源码检索结果编写，用于实现一种“世界地图随机地标 + 传送门网络”的 Mod 机制：

1. 世界地图上随机生成若干“超维科技远古传送站”地标。
2. 玩家远行队到达任一传送站后，可以在世界地图右键该地标，选择其他传送站并立即传送。
3. 玩家也可以进入传送站小地图，使用地图内的传送门建筑选择目的地并传送。

## 原版机制结论

### 世界地图右键菜单

原版世界地图远行队右键菜单入口是 `FloatMenuMakerWorld`：

- `TryMakeFloatMenu(Caravan caravan)` 只处理玩家控制的远行队。
- `ChoicesAtFor(mousePos, caravan)` 会取鼠标下所有 `WorldObject`，调用每个对象的 `GetFloatMenuOptions(caravan)`。
- `MapParent.GetFloatMenuOptions(caravan)` 默认会继续提供进入地图选项，即 `CaravanArrivalAction_Enter.GetFloatMenuOptions(caravan, mapParent)`。

因此传送站地标最适合实现为自定义 `MapParent` 或挂 `WorldObjectComp` 的 `WorldObjectDef`。右键地标时可在 `GetFloatMenuOptions` 中追加“传送到 XXX”的选项。

### 远行队世界地图传送

原版 `CompAbilityEffect_Farskip` 已经使用过类似逻辑：

- 远行队传送到世界目标：`caravan.Tile = target.Tile; caravan.pather.StopDead();`
- 传送后应调用或触发相关通知，例如 `caravan.Notify_Teleported()`，避免路径、补给缓存和显示状态残留。

本 Mod 的世界地图直接传送可以采用同类思路：

```csharp
caravan.Tile = destination.Tile;
caravan.pather.StopDead();
caravan.Notify_Teleported();
```

需要额外处理：

- 目的地必须是其他有效传送站。
- 起点远行队必须已经位于当前传送站 tile。
- 若目的地 tile 已有玩家远行队，可选择“移动到同 tile 后保持独立远行队”，也可合并；推荐第一版保持独立，减少意外物品/成员合并。
- 如果 `caravan.ImmobilizedByMass`，推荐仍允许传送，因为传送站不是行走；若设计上要限制，可给出 i18n 文本原因。

### 进入地标小地图

原版 `MapParent` 的 `MapGeneratorDef` 和 `GetOrGenerateMapUtility.GetOrGenerateMap` 支持为世界对象生成地图：

- `WorldObjectDef.mapGenerator` 指定地图生成器。
- `WorldObjectDef.overrideMapSize` 可指定地图尺寸。
- `MapParent.MapGeneratorDef` 默认读取 `def.mapGenerator`。
- `MapParent.ExtraGenStepDefs` 可由自定义类追加额外 `GenStep`。

因此传送站地标可作为 `MapParent` 拥有一个很小的标准地图，例如 `31x31`、`35x35` 或 `41x41`。地图生成器只放必要地形、清雾、玩家出生点和传送门建筑，不使用 `Encounter` 那种完整地形/植物/动物/遗迹生成流程。

### 是否使用口袋地图

不推荐使用口袋地图作为方法 2 的主实现。

原因：

- `PocketMapUtility.GeneratePocketMap` 生成的是 `PocketMapParent`，并保存到 `Find.World.pocketMaps`，它需要 `sourceMap`，语义上是“某个已有地图的附属空间”，不是世界地图地标自身的地图。
- `Map.IsPocketMap` 会改变很多系统行为。原版 `Map.CanEverExit` 对口袋地图返回 false。
- `CaravanExitMapUtility.FindCaravanToJoinFor` 中如果 `pawn.Map.IsPocketMap` 会直接返回 null，普通离图加入/创建远行队逻辑不能在口袋地图中直接使用。
- 原版口袋地图主要配套 `MapPortal`、`PocketMapExit`、`Dialog_EnterPortal`、`JobDriver_EnterPortal` 使用，逻辑包含装载清单、附属入口、出口回源地图等状态。它适合“建筑进入地下/异空间副本”，不适合“世界地标快速加载的小地图”。

推荐方案：

- 方法 2 使用高度自定义的标准 `MapParent` 地图。
- 地图尺寸做小，生成步骤极简。
- 地图内建筑只负责选择目的地并传送到其他传送站对应地图或世界 tile。
- 如果未来需要“传送站内部异空间副本”玩法，再单独引入口袋地图，不作为当前核心路径。

## 推荐总体架构

### Def 设计

新增以下 Def：

- `WorldObjectDef OuterrealmArchotechTeleportStation`
  - `worldObjectClass`: `OuterrealmArchotechTeleportStationWorldObject`
  - `canHaveMap`: true
  - `mapGenerator`: `OuterrealmArchotechTeleportStationMap`
  - `overrideMapSize`: 推荐 `35,1,35`
  - `selectable`: true
  - `expandingIcon`/`texture`: 使用传送站世界地图图标

- `MapGeneratorDef OuterrealmArchotechTeleportStationMap`
  - 只包含极少数自定义 `GenStep`：
    - 生成基础地形。
    - 生成一小块平台/道路。
    - 放置传送门建筑。
    - 设置 `MapGenerator.PlayerStartSpot`。
    - 清雾。
  - 不包含动物、植物、矿物、遗迹、复杂岩层等高成本步骤。

- `GenStepDef OuterrealmArchotechTeleportStationMapLayout`
  - `genStepClass`: `GenStep_OuterrealmTeleportStationLayout`
  - 负责实际布置地图。

- `ThingDef OuterrealmArchotechTeleportPortal`
  - `thingClass`: `Building_OuterrealmArchotechTeleportPortal`
  - 可交互建筑，显示选择目的地命令。
  - 使用 `[StaticConstructorOnStartup]` 预加载命令图标贴图。

- `GeneratedLocationDef OuterrealmArchotechTeleportStationGeneratedLocation`
  - 可复用原版 `WorldComponent_LocationGenerator` 的随机生成机制。
  - `worldObjectDef`: `OuterrealmArchotechTeleportStation`
  - `layerMaximum`: 控制每个世界层最多生成数量。
  - `weight`: 控制与其他 GeneratedLocation 的权重。
  - `LayerDefs`: 通常先只放 `Surface`。

如果 `GeneratedLocationDef` 不能满足“开局立刻固定数量/距离分布”的需求，再实现自定义 `WorldComponent` 或 `WorldGenStep`。

### C# 类型设计

建议新增源码文件：

- `Defs/OuterrealmDefOf.cs`
  - `[DefOf]` 缓存 `WorldObjectDef`、`ThingDef`、`MapGeneratorDef`、`GenStepDef`。

- `World/OuterrealmArchotechTeleportStationWorldObject.cs`
  - 继承 `MapParent`。
  - 重写 `GetFloatMenuOptions(Caravan caravan)`。
  - 保留 base 进入地图选项。
  - 如果远行队位于当前传送站 tile，追加“传送到其他传送站”选项。
  - 可重写 `ShouldRemoveMapNow`，当地图空置时允许卸载地图但保留世界地标。

- `World/OuterrealmTeleportNetworkUtility.cs`
  - 提供传送站枚举、目标过滤、目的地排序、传送执行。
  - 避免在每 tick 扫描；只在菜单打开/命令点击时扫描 `Find.WorldObjects.AllWorldObjects`。
  - 可使用静态临时 `List<OuterrealmArchotechTeleportStationWorldObject>` 但不要跨线程暴露或长期缓存。

- `World/CaravanArrivalAction_UseOuterrealmTeleportStation.cs`（可选）
  - 若希望玩家在远行队不在传送站 tile 时右键目的地也能“先走到传送站再打开/执行传送”，可实现到达动作。
  - 第一版可不做，降低复杂度。

- `Map/GenStep_OuterrealmTeleportStationLayout.cs`
  - 在小地图中心放置传送门建筑。
  - 铺设平台地形。
  - 让边缘到中心可达。
  - 设置 `MapGenerator.PlayerStartSpot`。

- `Buildings/Building_OuterrealmArchotechTeleportPortal.cs`
  - 继承 `Building` 或 `Building_Storage` 以外的普通建筑。
  - 重写 `GetGizmos()`，追加“选择传送目的地”命令。
  - 命令打开 `FloatMenu` 或自定义窗口，列出其他传送站。
  - 执行时把当前地图内可发送的玩家单位组成远行队，或把选中单位/全部地图内远行队成员传送。

- `UI/Dialog_OuterrealmTeleportDestination.cs`（可选）
  - 如果目的地数量较多，`FloatMenu` 会不够好用；可做成窗口，显示名称、距离、是否已有地图、是否已访问。
  - 第一版可先用 `FloatMenu`。

### 世界地图直接传送流程

触发条件：

- 右键的是 `OuterrealmArchotechTeleportStationWorldObject`。
- `caravan.IsPlayerControlled`。
- `caravan.Tile == station.Tile`。
- 存在至少一个其他已生成传送站。

菜单：

- 标签示例：`传送到 {destination.LabelCap}`
- 不可用原因：
  - 远行队未到达传送站。
  - 没有其他已激活传送站。
  - 目的地不可用/已销毁。

执行：

1. 再次验证起点、目的地、远行队状态。
2. 停止当前路径。
3. 设置 `caravan.Tile = destination.Tile`。
4. `caravan.pather.StopDead()`。
5. `caravan.Notify_Teleported()`。
6. 播放消息和可选音效。
7. 可选：如果目的地已经有地图，不自动进入；玩家可再右键进入。

### 世界创建后追加传送站

可以在游戏世界创建完成后，再追加一些传送站散落在世界地图上。

原版依据：

- `World.ConstructComponents()` 会实例化所有非抽象 `WorldComponent` 子类。
- `World.FinalizeInit(bool fromLoad)` 会调用所有 `WorldComponent.FinalizeInit(fromLoad)`。
- 原版 `WorldComponent_LocationGenerator` 在 `FinalizeInit(false)` 中生成世界地点，并在之后的 `WorldComponentTick()` 中每 90000 tick 检查数量，不足时继续生成。
- 追加世界对象的通用方式是：

```csharp
WorldObject obj = WorldObjectMaker.MakeWorldObject(OuterrealmDefOf.OuterrealmArchotechTeleportStation);
obj.Tile = tile;
Find.WorldObjects.Add(obj);
```

推荐实现：

- 新增 `WorldComponent_OuterrealmTeleportStationGenerator`。
- 在 `FinalizeInit(fromLoad)` 中：
  - 如果 `fromLoad == true`，只读档恢复，不生成新点。
  - 如果是新世界，等待原版世界对象、派系基地、初始地点完成后，补齐传送站数量。
- 在 `WorldComponentTick()` 中可低频补点：
  - 例如每 60000 或 90000 tick 检查一次。
  - 如果传送站数量低于目标值，就追加 1 个或补齐。
  - 如果不希望游戏中途自然增长，可不实现 tick 补点，只在新世界初始化时生成。
- 在 `ExposeData()` 中保存：
  - 是否已完成初始生成。
  - 已生成数量或生成批次版本。
  - 可选：玩家配置的目标数量快照，避免设置变化导致旧存档突然刷很多点。

选址建议：

- 使用 `TileFinder.TryFindNewSiteTile` 或 `TileFinder.TryFindTileWithDistance`。
- 必须过滤：
  - `!Find.WorldObjects.AnyWorldObjectAt(tile)`。
  - `!Find.World.Impassable(tile)`。
  - tile 所在 biome 允许建立地图，优先 `Find.WorldGrid[tile].PrimaryBiome.canBuildBase`。
  - 距离玩家初始基地/当前玩家定居点保持最小距离。
  - 传送站之间保持最小间距，避免扎堆。
- 数量建议随世界覆盖率缩放：
  - 30% 星球：6-8 个。
  - 50% 星球：10-14 个。
  - 100% 星球：18-24 个。

是否使用 `GeneratedLocationDef`：

- 可用，但它由原版 `WorldComponent_LocationGenerator` 统一管理，目标数量取决于 `generatedLocationFactor` 和所有 `GeneratedLocationDef` 的权重，难以精确控制“传送站网络必须有 N 个点”。
- 推荐第一版使用自定义 `WorldComponent_OuterrealmTeleportStationGenerator`，保证数量、分布和存档兼容。
- `GeneratedLocationDef` 可作为轻量备选方案，用于“像原版随机地点一样自然出现”的模式。

### 小地图建筑传送流程

第一版推荐语义：

- 地图内传送门建筑传送“当前地图内可发送的玩家单位”，最终在目标传送站 tile 形成远行队。
- 不直接把单位生成到目标传送站地图内，避免目标地图未生成时产生额外加载。
- 如果目标传送站地图已存在，后续版本可提供“传送进入目标局部地图”的选项。

执行方式：

1. 玩家进入当前传送站小地图。
2. 选中传送门建筑，点击 gizmo。
3. 选择目标传送站。
4. 收集发送对象：
   - 推荐第一版使用地图上所有 `Faction.OfPlayer` 且可组成远行队的 pawn。
   - 或者用当前选中的 pawns；该方案需要更细的 UI 与可达性提示。
5. 调用类似 `CaravanExitMapUtility.ExitMapAndCreateCaravan(pawns, Faction.OfPlayer, currentMap.Tile, currentMap.Tile, destination.Tile)`。
6. 生成远行队后设置到目标 tile：
   - 如果 `ExitMapAndCreateCaravan` 已根据 `destinationTile` 开始移动，需要改为直接传送，避免它按普通路径行走。
   - 更稳妥：先创建远行队到当前 tile，再调用统一的 `OuterrealmTeleportNetworkUtility.TeleportCaravan(caravan, destination)`。
7. 如果当前传送站地图空了，允许卸载地图但不销毁世界地标。

需要注意：

- `CaravanExitMapUtility.FindCaravanToJoinFor` 在口袋地图中不可用，这也是不用口袋地图的原因之一。
- 标准小地图可以保留原版离图/组队逻辑，也可以在建筑命令中直接 `ExitMapAndCreateCaravan`。
- 小地图必须保证中心传送门到地图边缘可达，否则远行队进入/离开和寻路会出现问题。

### PrefabDef 小地图内容方案

传送站小地图内容使用原版 `PrefabDef` 实现。自定义 `GenStep` 不再手写每个建筑坐标，而是负责准备地图、选择 prefab、调用 `PrefabUtility.SpawnPrefab`、补充入口道路并做可达性校验。

原版依据：

- `PrefabDef` 可描述一组预制地形、建筑和子 prefab。
- `PrefabUtility.SpawnPrefab(prefab, map, pos, rot)` 可把 prefab 生成到地图中。
- `PrefabUtility.CanSpawnPrefab(prefab, map, pos, rot)` 可提前检查能否放置。
- `GenStep_ScatterGroupPrefabs` 展示了原版如何按权重选择 prefab、检查占用矩形、处理旋转和已使用区域。

推荐核心类：

- `GenStep_OuterrealmTeleportStationLayout`
  - 清理中心区域。
  - 选择一个传送站 `PrefabDef`。
  - 计算 prefab 根坐标和旋转。
  - 调用 `PrefabUtility.CanSpawnPrefab` 与 `PrefabUtility.SpawnPrefab`。
  - 找到生成出的主传送门建筑。
  - 设置 `MapGenerator.PlayerStartSpot`。
  - 补入口道路、清雾、验证可达性。
- `OuterrealmTeleportStationPrefabDef`
  - 自定义轻量 Def，用来包装原版 `PrefabDef` 的额外元数据。
  - 字段建议：
    - `PrefabDef prefab`
    - `float weight`
    - `IntVec2 portalOffset`
    - `IntVec2 playerStartOffset`
    - `RotEnum allowedRotations`
    - `bool fallback`
    - `List<BiomeDef> allowedBiomes`
    - `List<BiomeDef> disallowedBiomes`
  - 原版 `PrefabDef` 自身不保存“哪个格子是主传送门”这类语义，所以用包装 Def 保存这些关系最清晰。

示例结构：

```xml
<OuterrealmTeleportStationPrefabDef>
  <defName>OATS_PrefabEntry_Ring</defName>
  <prefab>OATS_Prefab_TeleportStation_Ring</prefab>
  <weight>1.0</weight>
  <portalOffset>(8,8)</portalOffset>
  <playerStartOffset>(8,11)</playerStartOffset>
  <allowedRotations>All</allowedRotations>
</OuterrealmTeleportStationPrefabDef>
```

对应原版 prefab：

```xml
<PrefabDef>
  <defName>OATS_Prefab_TeleportStation_Ring</defName>
  <size>(17,17)</size>
  <rotations>All</rotations>
  <terrain>
    <!-- 平台、道路、装饰地面 -->
  </terrain>
  <things>
    <!-- 主传送门、能量柱、控制台、残骸、墙体等 -->
  </things>
</PrefabDef>
```

地图内容生成顺序建议：

1. 选 prefab：
   - 从所有 `OuterrealmTeleportStationPrefabDef` 中按 biome、tile 条件过滤。
   - 按 `weight` 随机选择。
   - 选择一个允许旋转方向。
2. 准备生成区域：
   - 以地图中心为目标点。
   - 用 `PrefabUtility.GetRoot` 或等效逻辑计算 prefab 根坐标。
   - 清理 prefab 占用矩形及周边 2-3 格内的天然岩石、树、物品和不可通行建筑。
   - 必要时先把占用区域地形替换为支持重型建筑的基础地形。
3. 生成 prefab：
   - 先调用 `PrefabUtility.CanSpawnPrefab`。
   - 成功则 `PrefabUtility.SpawnPrefab(prefab, map, root, rot, faction: null, spawned: spawnedThings)`。
   - 失败则换另一个 prefab 或使用 fallback prefab。
4. 定位主传送门：
   - 优先使用 `portalOffset` 计算主传送门位置。
   - 校验该位置存在 `OuterrealmArchotechTeleportPortal`。
   - 如果不存在，从 `spawnedThings` 中查找第一个主传送门建筑。
5. 生成入口道路：
   - 从玩家进入点或地图边缘到主传送门铺 2-3 格宽道路。
   - 道路不需要放进 prefab，可由 `GenStep` 根据实际地图尺寸补齐。
6. 设置玩家进入点：
   - 优先用 `playerStartOffset`。
   - 如果该格不可站立，搜索主传送门附近最近可站立格。
   - 设置 `MapGenerator.PlayerStartSpot`。
7. 清雾和验证：
   - 清中心建筑群、入口道路和出生点附近的雾。
   - 验证出生点到传送门可达，传送门附近到地图边缘可达。
   - 失败时回退到 `fallback` prefab 或强制铺直路。

### 预制传送站建筑群结构

第一版直接使用 `PrefabDef` 预制几种不同的传送站建筑群结构。

建议第一版提供 3-5 个 prefab：

- `OATS_Prefab_TeleportStation_Ring`
  - 圆环平台，主传送门居中，四周四个能量柱。
- `OATS_Prefab_TeleportStation_Line`
  - 线性长廊，传送门在尽头，入口道路直连。
- `OATS_Prefab_TeleportStation_Cross`
  - 十字平台，四向道路，适合最可靠的可达性。
- `OATS_Prefab_TeleportStation_Ruined`
  - 半损毁结构，部分装饰建筑随机缺失。
- `OATS_Prefab_TeleportStation_Fortified`
  - 有围墙/门/防御残骸，但仍保证玩家可直接到达传送门。

布局选择规则：

- 每个 `OuterrealmTeleportStationPrefabDef` 有 `weight`。
- 可按世界 tile 条件过滤：
  - 山地更容易出现半埋/废墟布局。
  - 沙漠/冰原使用少植物、少水体布局。
  - 靠近玩家初始基地的传送站使用低威胁布局。
- 第一版不需要做复杂过滤，只按权重随机即可。

可达性和失败回退：

- 生成后必须验证：
  - `portal.Position.Standable(map)` 附近存在可站立交互格。
  - `map.reachability.CanReachMapEdge(portal.Position, TraverseParms.For(TraverseMode.PassDoors))` 或从玩家出生点能到达传送门。
- 如果失败：
  - 删除或忽略当前 prefab。
  - 使用 `fallback` prefab，例如 `OATS_Prefab_TeleportStation_Cross`。
  - 再次失败则强制铺一条 3 格宽直路到地图边缘。

BaseGen/Sketch 暂不推荐第一版使用。它适合聚落、古代复合体这类复杂随机建筑；对小型固定传送站来说过重，调试成本更高。

## 小地图性能设计

目标：进入传送站地图时加载尽可能快。

推荐参数：

- 地图尺寸：`35x35` 起步；如果建筑和进出点足够，最低可试 `31x31`。
- `MapGeneratorDef.disableShadows = true`。
- `MapGeneratorDef.ignoreAreaRevealedLetter = true`。
- 不生成动物、植物、矿物、遗迹、派系基地、复杂岩层。
- `GenStep` 只做清场、选择 prefab、调用 `PrefabUtility.SpawnPrefab`、补道路和校验，不调用 BaseGen 大型符号系统。
- 传送站地图不设为玩家基地，不产生基地规模事件。

自定义 `GenStep` 建议：

1. 全图铺轻量地形，如石地/金属地板外围混合。
2. 中心区域清场，并保证 prefab 占用矩形可放置。
3. 按权重选择 `OuterrealmTeleportStationPrefabDef`。
4. 通过 `PrefabUtility.SpawnPrefab` 生成主建筑群。
5. 从 prefab 出入口或主传送门附近铺 2-3 格宽道路到边缘。
6. 设置 `MapGenerator.PlayerStartSpot`。
7. 清除中心与道路雾。

## i18n 计划

所有玩家可见字符串必须进入 `Languages`：

中文：

- `../../Languages/ChineseSimplified (简体中文)/Keyed/OuterrealmArchotechTeleportStation.xml`

英文：

- `../../Languages/English/Keyed/OuterrealmArchotechTeleportStation.xml`

建议 key：

- `OATS_CommandTeleportToStation`
- `OATS_CommandTeleportToStationDesc`
- `OATS_SelectTeleportDestination`
- `OATS_CannotTeleportNotAtStation`
- `OATS_CannotTeleportNoDestinations`
- `OATS_CannotTeleportInvalidDestination`
- `OATS_MessageCaravanTeleported`
- `OATS_MessagePawnsTeleportedFromMap`
- `OATS_OuterrealmTeleportStationLabel`
- `OATS_OuterrealmTeleportPortalLabel`

## 实施步骤

### 阶段 1：基础工程整理

1. 删除或确认 `.csproj` 中对其他 Mod 项目的 `ProjectReference` 是否必要；当前引用 `FullyAutomaticOmniCrafter`，实现传送站不应依赖它。
2. 建立源码目录：
   - `Defs`
   - `World`
   - `Map`
   - `Buildings`
   - `UI`（可选）
3. 更新 `.csproj` 包含新增 `.cs` 文件。
4. 保持 UTF-8 编码。

### 阶段 2：世界地标与生成

1. 添加 `WorldObjectDef`、`MapGeneratorDef`、`GenStepDef`、`ThingDef`。
2. 实现 `WorldComponent_OuterrealmTeleportStationGenerator`，新世界初始化时补齐传送站数量。
3. 实现 `OuterrealmArchotechTeleportStationWorldObject`。
4. 实现 `OuterrealmTeleportNetworkUtility`。
5. 完成世界地图右键传送。
6. 可选：添加 `GeneratedLocationDef` 作为“自然地点生成”模式，但不作为第一版主生成器。

### 阶段 3：小地图生成

1. 实现 `OuterrealmTeleportStationPrefabDef`，包装原版 `PrefabDef` 的权重、主传送门偏移、出生点偏移和过滤条件。
2. 实现 `GenStep_OuterrealmTeleportStationLayout`。
3. 添加至少 3 个 `PrefabDef` 传送站建筑群模板。
4. 在 `GenStep` 中选择 prefab、清场、生成 prefab、补道路、设置出生点。
5. 验证远行队进入地图后出生点合理、可到达传送门、可离开地图。
6. 验证地图空置后可卸载且地标不消失。
7. 验证 fallback prefab 和强制直路回退逻辑。

### 阶段 4：建筑传送

1. 实现 `Building_OuterrealmArchotechTeleportPortal.GetGizmos()`。
2. 使用 `FloatMenu` 或窗口选择目标。
3. 收集可发送 pawns。
4. 创建远行队并传送到目标传送站。
5. 增加消息、音效、目标校验和失败提示。

### 阶段 5：本地化与资源

1. 添加中文和英文 keyed 文本。
2. 添加世界图标、建筑贴图、命令图标。
3. 命令图标用 `[StaticConstructorOnStartup]` 预加载：

```csharp
[StaticConstructorOnStartup]
public static class OuterrealmTeleportStationTex
{
    public static readonly Texture2D Teleport =
        ContentFinder<Texture2D>.Get("UI/Commands/OuterrealmTeleport", false) ?? BaseContent.WhiteTex;
}
```

### 阶段 6：验证

1. `dotnet build -c Debug`。
2. 新游戏开局，确认世界上生成传送站。
3. 远行队到达传送站后右键菜单显示目的地。
4. 传送后远行队位置、路径、显示和消息正确。
5. 进入传送站小地图，确认加载速度、地图尺寸、传送门位置。
6. 建筑传送后 pawns 不丢失、库存不丢失、远行队状态正确。
7. 保存/读取后传送站网络和已生成地图仍正常。

## 风险与处理

- 远行队传送后路径缓存残留：统一使用 `pather.StopDead()` 和 `Notify_Teleported()`。
- 世界对象扫描成本：只在菜单打开和命令执行时扫描，不在 tick 中扫描。
- 小地图过小导致出生点/边缘不可达：保持至少 `31x31`，道路连通到边缘。
- 世界创建后追加传送站重复刷点：`WorldComponent` 保存初始生成完成标记和已生成数量，读档时不重复初始化。
- 传送站扎堆：选址时检查与其他传送站的世界路径距离或 traversal distance。
- 预制布局阻断寻路：布局生成后做可达性验证，失败时回退到十字道路保底布局。
- 地图空置后地标被销毁：自定义 `ShouldRemoveMapNow` 只移除地图，不移除 `WorldObject`。
- 口袋地图兼容成本高：第一版不使用口袋地图；如果未来要做异空间玩法，再单独设计 `MapPortal` 派生类。
- 多语言遗漏：所有命令、失败原因、消息都走 keyed i18n。

## 当前建议结论

方法 1 使用自定义 `MapParent.GetFloatMenuOptions` 直接追加世界地图传送选项。

方法 2 使用高度自定义的小尺寸标准地图，不使用口袋地图。标准地图与世界地标一一对应，语义正确，能复用原版 `MapParent`、`GetOrGenerateMapUtility`、`CaravanEnterMapUtility`、`CaravanExitMapUtility`，并且通过极简 `MapGeneratorDef` 达到接近口袋地图的加载速度，同时避免口袋地图的离图、源地图、入口出口绑定等限制。
