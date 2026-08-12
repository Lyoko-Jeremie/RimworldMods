# 人造人女仆「侍奉框架」设计方案（plan2）

> 项目：超维科技 OuterrealmTech / 人造人女仆 ArtificialMaid（RimWorld 1.6）
> 参考：渡鸦族 ZuoYao_RavenRace 的 Servitude 系统（反编译分析）
> 版本：v2.3（含 v1 基础方案、v2 征召/高维状态机、v2.1 猎杀守卫互斥、v2.2 柜中唤醒联动、v2.3 留守开关）
> 状态：**仅设计方案，未实现、未修改任何代码**

---

## 1. 目标与设计原则

### 1.1 功能需求

| 需求 | 说明 |
|---|---|
| 绑定主人 | 女仆与一名玩家殖民者建立"主-仆"关系（一仆一主、一主多仆） |
| 跟随 | 主人移动时保持距离跟随 |
| 保卫 | 主人受威胁时主动拦截/反击 |
| 伴随移动 | 主人跨地图/入商队时跟随转移（v1 传送伴随，v2 商队伴随） |
| 携带移动 | 主人倒地/昏迷时抱起并护送（v1），进阶"公主抱"（v2） |
| 携带 autoblink | 女仆携带主人时 blink 依然生效，且把主人一起瞬移 |
| 喂食 | 主人饥饿时喂食（复用原版 JobDriver_Feed） |
| 陪伴 | 膝枕/擦身等主动互动（Def 驱动，可无限扩展） |
| 主动勾引 | 低概率+冷却触发亲密互动（RJW 优先，原版兜底） |

### 1.2 征召 / 高维交互需求

1. 女仆在高维模式下、未被征召时，**只跟随主人**，不进行其他侍奉行为。
2. 女仆被征召时，**暂时不进行侍奉**，增加一个按钮：「女仆是否紧跟并守卫主人」。
3. 女仆未被征召而主人被征召时，**女仆立即自动征召并守卫主人**。
4. 女仆在展示柜中、展示柜允许唤醒（autoWake）时，主人进入征召 → 同样唤醒女仆并进入征召+守卫状态。
5. **留守开关**：打开后暂时**完全不执行侍奉系统的所有内容**（跟随/保卫/守卫/救援/喂食/陪伴/勾引/自动征召联动/高维跟随全部暂停），关闭后一切恢复正常。

### 1.3 三条硬性原则

1. **可扩展**：行为 = "ThinkNode 节点 + Def 条目 + 可选 Worker 类"三段式，加新行为不修改核心代码。
2. **兼容**：不侵入 AutoBlink/RJW 内部逻辑，只做"旁路 patch + 事件同步"；尊重原版活动区域、Job 预留、阵营规则。
3. **低性能**：一切行为判定放在思考树（JobGiver）而非每 tick 扫描；查询 O(1)；缓存复用；patch 仅低频触发点。

---

## 2. 总体架构

```
Source/OuterrealmTechRobot/
└── Features/Servitude/
    ├── ArtificialMaidServitudeManager.cs      // WorldComponent：绑定数据 + 事件总线 + 跨图伴随
    ├── ArtificialMaidServitudeUtility.cs      // 静态工具：缓存、判定、连线材质（[StaticConstructorOnStartup]）
    ├── ArtificialMaidServitudeDef.cs          // Def + DefModExtension：互动目录配置
    ├── ServitudeInteractionWorker.cs          // 抽象：可插拔互动逻辑
    ├── Jobs/
    │   ├── ThinkNode_JobGiver_ServitudeBase.cs // 公共守卫基类（快速失败 + 模式判定）
    │   ├── JobGiver_AMGuardMaster.cs          // 征召守卫（紧跟 + 拦截）
    │   ├── JobGiver_AMProtectMaster.cs        // 未征召保卫
    │   ├── JobGiver_AMRescueMaster.cs         // 携带救援（倒地）
    │   ├── JobGiver_AMCarryFollow.cs          // 携带跟随（抱着走）
    │   ├── JobGiver_AMFeedMaster.cs           // 喂食
    │   ├── JobGiver_AMCompanion.cs            // 陪伴互动（Def 驱动）
    │   ├── JobGiver_AMSeduce.cs               // 主动勾引
    │   ├── JobGiver_AMFollowMaster.cs         // 跟随（兜底，高维跟随复用）
    │   ├── JobDriver_AMFollowMaster.cs
    │   ├── JobDriver_AMCarryMaster.cs
    │   ├── JobDriver_AMLapPillow.cs
    │   └── JobDriver_AMSeduce.cs
    └── Harmony/
        ├── Patch_Pawn_GetGizmos_Servitude.cs           // 「建立侍奉关系」按钮
        ├── Patch_Pawn_DrawExtraSelectionOverlays_Servitude.cs // 主仆连线（仅选中时）
        ├── Patch_AutoBlink_CarrySync.cs                // 携带+blink 同步（核心联动）
        ├── Patch_Pawn_DraftController_Servitude.cs     // 主人征召 → 自动征召女仆（含柜中唤醒）
        ├── Patch_Hibernate_SkipWhenBound.cs            // 有主人时禁止自动休眠
        └── Patch_CaravanJoin_Servitude.cs              // v2：商队伴随

Defs/Features/Servitude/   ← ThinkTree / JobDef / Interaction / Thought / 设置
Languages/.../Keyed/       ← 全部显示文本
```

> 新增 `.cs` 需同步登记到 `OuterrealmTechRobot.csproj`（项目既有规范）。

**关键决策**：行为不写进 `CompArtificialMaid.CompTick`（该组件已承担资源回复/治疗/狩猎等 30/60/250 tick 分频任务）。改为独立 ThinkTree 注入 + WorldComponent 管理，与 `CompArtificialMaid` 只通过 `GetCompCached`（现有 `ConditionalWeakTable` 缓存）单向读取。

---

## 3. 数据模型（绑定关系）

### 3.1 存储：WorldComponent（全局、跨地图、随存档）

```csharp
public class ArtificialMaidServitudeManager : WorldComponent
{
    private Dictionary<Pawn, Pawn> servantToMaster;         // 侍奉者 → 主人（主表，LookMode.Reference）
    private Dictionary<Pawn, List<Pawn>> masterToServants;  // 反向索引，PostLoadInit 重建，不落盘
    private Dictionary<int, int> interactionCooldowns;      // HashCombine(servant.thingID, jobDef) → 到期tick

    public Pawn GetMaster(Pawn servant);                    // O(1)
    public List<Pawn> GetServants(Pawn master);
    public bool IsServant(Pawn p);  public bool IsMaster(Pawn p);
    public bool TryBind(Pawn master, Pawn servant);         // 重复绑定=覆盖（先解旧）
    public void Unbind(Pawn servant);  public void UnbindAll(Pawn master);
}
```

- 用 `Dictionary<Pawn,Pawn>`（O(1)、`LookMode.Reference` 由原版序列化，卸载后仍为 WorldPawn 对象）；不用 thingID 方案（解析需全图扫描）。
- `masterToServants` 仅 `PostLoadInit` 重建；`interactionCooldowns` 容量极小。
- 清理：分频 Tick 移除 `DestroyedOrNull` 的 Pawn；`TryBind` 自动解旧（一仆一主）。
- 事件总线（可扩展性核心）：`event Action<Pawn,Pawn> Bound/Unbound`；`MasterDowned/MasterAttacked/MasterHungry/ThreatNearMaster` 由各模块触发。

### 3.2 绑定范围

- 侍奉者：默认仅 `ArtificialMaid` 种族（Def 配置可放开）。
- 主人：玩家阵营殖民者（按 Def 开关）。

---

## 4. 行为状态机（征召 / 高维 / 守卫）

### 4.1 模式矩阵（v2.2 全表）

| 模式 | 女仆位置 | 女仆高维 | 女仆征召 | 主人征召 | 女仆行为 |
|---|---|---|---|---|---|
| **A 完整侍奉** | 地图 | ✗ | ✗ | ✗ | 猎杀关 → 全套侍奉（跟随/保卫/救援/喂食/陪伴/勾引）；猎杀开 → 自主猎杀 |
| **B 高维跟随** | 地图 | ✓ | ✗ | ✗ | **只跟随主人**；猎杀/守卫/其余侍奉全部暂停 |
| **C 守卫模式** | 地图 | 任意 | ✓ | 任意 | 侍奉暂停；`guardModeEnabled` → 紧跟+拦截威胁；否则完全交还原版征召 AI / 玩家指挥；猎杀在征召下不自动执行 |
| **D' 自动征召（含唤醒）** | 柜中或地图 | 任意 | ✗ | ✓ | 柜中且 `autoWake` → 唤醒（`WakeContainedMaid`）；然后征召 + `SetGuardMode(true)`（自动关猎杀）→ 进入 C |
| **E 柜中休眠** | 柜中 | — | — | — | `autoWake=false` 时不唤醒，保持休眠（尊重收纳） |

**优先级**：留守（总闸）> 征召维度 > 高维维度 > 常规维度；主人被征召是触发 D' 的唯一外部事件。

### 4.2 模式判定（守卫基类唯一入口）

```csharp
public abstract class ThinkNode_JobGiver_ServitudeBase : ThinkNode_JobGiver
{
    protected sealed override Job TryGiveJob(Pawn pawn)
    {
        // 快速失败链（全部 O(1)/廉价，任何一步失败即 null）
        if (pawn.def != ArtificialMaidDefOf.ArtificialMaid) return null;
        if (pawn.Dead || pawn.Downed || !pawn.Spawned) return null;
        var mgr = ServitudeManager.Get(); if (mgr == null) return null;
        Pawn master = mgr.GetMaster(pawn); if (master == null) return null;
        if (master.Map != pawn.Map || master.Dead) return null;
        // 活动区域尊重（沿用渡鸦 v0.9.5 规则）
        if (pawn.playerSettings?.RespectsAllowedArea == true)
        {
            var area = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
            if (area != null && !area[master.Position]) return null;
        }
        var comp = CompArtificialMaid.GetCompCached(pawn);
        if (comp == null) return null;

        // ⓪ 留守维度（v2.3 总开关）：打开后侍奉系统完全不执行
        if (comp.standbyMode) return null;

        // ① 征召维度：draft 时暂停全部侍奉；守卫行为由 JobGiver_AMGuardMaster 单独负责
        if (pawn.Drafted) return null;
        // ② 高维维度：高维 + 未征召 = 只跟随
        if (comp.isHighDim) return TryGiveHighDimFollowJob(pawn, mgr);
        // ③ 常规完整侍奉链（见 §5）
        ...
    }
    protected abstract Job TryGiveServitudeJob(Pawn pawn, Pawn master, ServitudeManager mgr);
}
```

**此设计把"暂停侍奉"做成守卫基类的一行判断**，所有子 JobGiver（喂食/膝枕/勾引/救援）自动继承，零侵入。

---

## 5. 行为实现（按 JobGiver 分层，ThinkTree 优先级即节点顺序）

### 5.1 ThinkTree 注入

```xml
<ThinkTreeDef>
  <defName>ArtificialMaidServitude</defName>
  <insertTag>Humanlike_PostDuty</insertTag>   <!-- 原版空闲判定链 -->
  <insertPriority>200</insertPriority>        <!-- 高于既有注入，抢在自动休眠前 -->
  <thinkRoot Class="ThinkNode_Priority">
    <subNodes>
      <li Class="...JobGiver_AMGuardMaster"/>    <!-- ① 征召守卫（draft时唯一） -->
      <li Class="...JobGiver_AMProtectMaster"/>   <!-- ② 保卫 -->
      <li Class="...JobGiver_AMRescueMaster"/>    <!-- ③ 携带救援 -->
      <li Class="...JobGiver_AMCarryFollow"/>     <!-- ④ 携带跟随 -->
      <li Class="...JobGiver_AMFeedMaster"/>      <!-- ⑤ 喂食 -->
      <li Class="...JobGiver_AMCompanion"/>       <!-- ⑥ 陪伴互动 -->
      <li Class="...JobGiver_AMSeduce"/>          <!-- ⑦ 主动勾引 -->
      <li Class="...JobGiver_AMFollowMaster"/>    <!-- ⑧ 跟随（兜底） -->
    </subNodes>
  </thinkRoot>
</ThinkTreeDef>
```

- 子节点顺序即优先级（战斗 > 救援 > 生存 > 情感）；扩展新行为 = 往 `<subNodes>` 插节点，零侵入。
- 与 `ArtificialMaidHibernate`（`Humanlike_PostMain`，进柜休眠）协调：`Patch_Hibernate_SkipWhenBound` 让**有在地图上的主人时休眠 JobGiver 直接返回 null**。

### 5.2 各行为要点

**① 征召守卫 `JobGiver_AMGuardMaster`**（模式 C）
- 仅 `pawn.Drafted && comp.guardModeEnabled` 时工作。
- 拦截：主人 12 格内 `HostileTo(master)` 存活 Pawn；远距 → 复用 `CompArtificialMaid.TryBlinkToTarget`（AutoBlink `BlinkToCellDirect`，冷却内置）后 `AttackMelee`。
- 无威胁 → 紧跟：距离 > 4 格 → FollowMaster Job（与模式 B 同一 driver）。
- 与玩家指挥的优先级：依赖原版 `Humanlike_PostDuty` 在 draft 无命令时可达（**实现时必须实测**，见 §9 风险 1）；`JobGiver_Orders`（玩家右键，playerForced）与 `JobGiver_AIDefendSelf`（自卫）优先于本节点 → 守卫是"兜底行为"，玩家右键永远优先。

**② 保卫 `JobGiver_AMProtectMaster`**（模式 A）
- 每思考节拍 + 250 tick 分频：主人 12 格内威胁；近 → `AttackMelee`，远 → blink 跳脸后攻击。
- 与 HuntMode 关系：有主人时保卫优先；HuntMode 是"主动狩猎一切敌对"，两者互不干扰（且 v2.1 后互斥）。

**③ 携带救援 `JobGiver_AMRescueMaster` + `JobDriver_AMCarryMaster`**
- 触发：主人 `Downed`（或昏迷，可扩展）且未被携带。
- 流程：走近 → `pawn.carryTracker.TryStartCarry(master)` → 携带跟随模式 → 附近有床 → `CarryToBed`；否则持续抱着跟随/跑向安全点。
- 活人"公主抱"（v2 可选）：临时 `Hediff` + carryTracker + 放置移除 Hediff。

**④ 携带跟随 `JobGiver_AMCarryFollow`**
- 触发：女仆正抱着主人且无更高优先级目标。
- 行为：抱着主人跟随移动目标，不丢下主人。

**⑤ 喂食 `JobGiver_AMFeedMaster`**
- 触发：`master.needs.food.CurLevelPercentage < PercentageThreshHungry`。
- 执行：`FoodUtility.TryFindBestFoodSourceFor(maid, master)` → 自定义 FeedMaster JobDef，driver 复用原版 `JobDriver_Feed`。

**⑥ 陪伴互动 `JobGiver_AMCompanion`**（Def 驱动，见 §6）
- v1 内置膝枕：移植渡鸦 `JobDriver_LapPillow` 强化版——主人 `Wait_MaintainPosture`（2600 tick）+ `posture=LayingInBed` 强制躺卧、双方互视、2500 tick 内主人休息值持续回复（渡鸦约 +11.4%，女仆版上调至 +20% 体现"超维科技"设定）、每 100 tick 心形粒子、结束自动释放主人。
- 互动触发前走 `interactionCooldowns` + `Rand.Chance(baseChance × 设置倍率)` + 前置状态检查；触发发 i18n 信件（设置可关）。

**⑦ 主动勾引 `JobGiver_AMSeduce`**
- 概率低（0.01~0.02）、冷却长（30000~60000 tick）；主人战斗/倒地/关键 Job 中不触发。
- 执行链：`RJWCompatibility.Active` → RJW 亲密接口（反射层加 `TryStartSex`）；否则原版 `JobDefOf.Lovin`。
- 本质是陪伴互动的特例（Def 配置区分），仅在模式 A 触发。

**⑧ 跟随 `JobGiver_AMFollowMaster`**（兜底 + 模式 B 复用）
- 触发：空闲、无更优侍奉行为、距主人 > 4 格。
- `JobDriver_AMFollowMaster`：`FailOn`（主人死亡/换图）→ 250 tick 循环：距离 > 半径 → `pather.StartPath(master.Position, ClosestTouch)`；≤ 半径 → `StopDead()`。
- 模式 B（高维跟随）：直接复用此 driver；高维下 `Flying=true`、寻路走 `PathGridDefs_HighDim`，落点选格复用 `ArtificialMaidHighDimUtility.TryFindExitCell` 的"可站立+未占用"判定思路。

---

## 6. 可扩展性机制（三段式）

| 扩展点 | 加新行为的方式 | 需要写代码 |
|---|---|---|
| Def 互动目录 | 已有 Worker/JobDriver 的新组合（新概率/冷却/状态/文案） | 仅 XML + i18n |
| 新互动 Job | 新 JobDef + 新 JobDriver（参考 `JobDriver_AMLapPillow`） | 1 个 C# 类 |
| 新行为类别 | 新 `ThinkNode_JobGiver_ServitudeBase` 子类 + ThinkTreeDef 插节点 | 1 个 C# 类 + XML |
| 第三方/自研逻辑 | 订阅 `ServitudeManager` 事件 | 事件订阅类 |

```csharp
public abstract class ServitudeInteractionWorker
{
    public abstract bool CanTrigger(Pawn servant, Pawn master, ServitudeManager mgr);
    public abstract Job TryMakeJob(Pawn servant, Pawn master);
}
```

互动目录 Def 示意：

```xml
<ArtificialMaidServitudeDef>
  <defName>AM_ServitudeBond</defName>
  <modExtensions><li Class="ArtificialMaidServitudeExtension">
    <interactions>
      <li Class="ArtificialMaidServitudeInteraction">
        <jobDef>AM_Job_LapPillow</jobDef>
        <requiredMasterState>Resting</requiredMasterState>
        <baseChance>0.05</baseChance>
        <cooldownTicks>30000</cooldownTicks>
        <letterLabelKey>...</letterLabelKey>
      </li>
    </interactions>
  </li></modExtensions>
</ArtificialMaidServitudeDef>
```

---

## 7. 携带 autoblink（核心联动）

### 7.1 现状（基于 AutoBlink 反编译）

- `CompAutoBlink.CompTick()` 12 tick 分频；blink 条件含 `pather.Moving`、`excludedJobDefsCached`（默认含 `Carried`/`GotoWander`）、不拖 ropees、`stances.FullBodyBusy` 等。
- 女仆携带主人时，女仆的 Job 是 `CarryXXX`（不是 `Carried`），**不在排除列表 → AutoBlink 照常触发**。
- 两个最终执行点：`ExecuteBlink(target, resumeDest, now)` 与 `BlinkToCellDirect(cell)`（女仆现有 `TryBlinkToTarget` 走的就是它）。
- 缺陷：执行点只做 `pawn.Position = target; Notify_Teleported()`，**被携带的主人不会一起移动**。

### 7.2 方案：旁路 Postfix 同步被携带者

```csharp
[HarmonyPatch(typeof(AutoBlink.CompAutoBlink), "ExecuteBlink")]
[HarmonyPatch(typeof(AutoBlink.CompAutoBlink), "BlinkToCellDirect")]
public static class Patch_AutoBlink_CarrySync
{
    static void Postfix(AutoBlink.CompAutoBlink __instance, IntVec3 target /* 或 cell */)
    {
        Pawn carrier = __instance.parent as Pawn;
        if (carrier?.carryTracker?.CarriedThing is not Pawn carried) return; // 廉价判定
        carried.Position = target;
        carried.Notify_Teleported(false);
    }
}
```

- 一个 Postfix 覆盖两个入口：自动 blink 与手动/女仆主动 blink 全部同步携带。
- 性能：方法只在真正 blink 时调用（低频），Postfix 首行空判定即返回，每 tick 零开销。
- 兼容：csproj 已有 `ProjectReference` 强引用 AutoBlink，无需反射；不碰其冷却/排除/路径逻辑；未装 AutoBlink 时 `TryBlinkToTarget` 已有 null 守卫。
- 边界：女仆被携带（Job=`Carried`）时 AutoBlink 自身已排除，无需处理。
- 组合玩法：主人倒地 → blink 到身边 → 抱起 → 下一跳连人瞬移回安全点（"战场救援瞬移"）。

---

## 8. 自动征召（模式 D'，事件驱动）

### 8.1 patch 点：`Pawn_DraftController.Drafted` setter（主人侧）

```csharp
[HarmonyPatch(typeof(Pawn_DraftController), "Drafted", MethodType.Setter)]
public static class Patch_Pawn_DraftController_Servitude
{
    static void Postfix(Pawn_DraftController __instance, bool value)
    {
        Pawn master = __instance.pawn;
        if (master == null || !mgr.HasServants(master)) return;
        foreach (Pawn maid in mgr.GetServants(master))
        {
            if (maid == master || maid.Dead || maid.Destroyed) continue;

            // —— 柜中分支（v2.2 新增）——
            if (maid.ParentHolder is Building_ArtificialMaidDisplayCase dc)
            {
                if (!dc.autoWake && !settings.wakeIgnoresAutoWake) continue; // 尊重收纳
                dc.WakeContainedMaid(true);   // 复用现有公开 API：allowAutoHibernate=false、autoHibernate=false、EjectContents
            }

            var comp = CompArtificialMaid.GetCompCached(maid);
            if (comp == null) continue;
            // 留守开关（v2.3）：不自动征召、不唤醒
            if (comp.standbyMode) continue;
            // 柜内女仆未 Spawned，IsColonistPlayerControlled 不可用 → 用 Faction 判定
            if (maid.Faction != Faction.OfPlayer) continue;

            if (value)   // 主人被征召 → 自动征召 + 守卫
            {
                if (maid.Drafted) { comp.SetGuardMode(true); continue; }
                maid.drafter.Drafted = true;
                comp.SetGuardMode(true);              // v2.1 互斥入口，自动关猎杀
                maid.jobs.EndCurrentJob(JobCondition.InterruptForced);
                Messages.Message(自动征召 i18n, maid, NeutralEvent);
            }
            else if (settings.autoUndraftWithMaster)  // 主人解除征召 → 同步解除（默认开）
            {
                if (maid.Drafted) maid.drafter.Drafted = false;
                // guardModeEnabled 保留开关状态，解除后回到 A/B 侍奉
            }
        }
    }
}
```

### 8.2 关键点

1. **唤醒复用现有 API**：`WakeContainedMaid(true)` 一次性完成取出、`AddComponentsForSpawn`、`allowAutoHibernate=false`（防被自动休眠立刻抓回）。
2. **判定修正**：柜内女仆未 Spawned，`IsColonistPlayerControlled` 不可用，走 `maid.Faction == Faction.OfPlayer`。
3. **时序**：先唤醒（Spawned）→ 后 `Drafted=true` → 再守卫；`WakeContainedMaid` 已补齐组件，`drafter` 可用；若极端为 null，跳过征召只留守卫开关（防御性分支）。
4. **防循环**：只写女仆、不写主人；只处理"状态确实变化"的女仆。
5. **高维衔接**：高维女仆同样自动征召（需求无高维限制），征召后进入 C（高维叠加 = 穿墙守卫）。
6. **多女仆**：同一主人全部绑定女仆一起征召（默认；可加"仅最近者"设置）。
7. **尊重玩家**：玩家手动解除某女仆征召（主人仍征召）不强制重新征召（只响应主人状态变化）。

---

## 9. 猎杀模式 × 守卫模式互斥（v2.1）

### 9.1 互斥规则（唯一权威入口）

**规则：`guardModeEnabled` 与 `enableHuntMode` 任何时候不能同时为 true。任何路径把其中一个置 true，另一个强制置 false。**

```csharp
public void SetGuardMode(bool on)   // CompArtificialMaid 方法
{
    if (on == guardModeEnabled) return;
    guardModeEnabled = on;
    if (on) enableHuntMode = false;   // 互斥：开守卫 → 关猎杀
}

public void SetHuntMode(bool on)
{
    if (on == enableHuntMode) return;
    enableHuntMode = on;
    if (on) guardModeEnabled = false; // 互斥：开猎杀 → 关守卫
}
```

- 禁止散落直接赋值，全部收敛到 `SetGuardMode` / `SetHuntMode`（实现时需搜索现有 `enableHuntMode` 赋值点统一替换）。
- 两个字段继续 Scribe 保存。

### 9.2 执行端双保险

`CompTick` 猎杀调用加守卫（双保险，防状态被绕过）：

```csharp
if (this.parent.IsHashIntervalTick(30)
    && enableHuntMode
    && !guardModeEnabled      // 守卫开 → 猎杀不执行
    && !isHighDim             // 高维只跟随 → 猎杀不执行
    && !Pawn.Drafted)         // 征召下猎杀不自动执行（战斗交给守卫/玩家指挥）
{
    this.ApplyHuntMode();
}
```

### 9.3 两种模式语义边界（互斥前提）

| 模式 | 生效条件 | 行为 | 执行点 |
|---|---|---|---|
| 猎杀（自由自主战斗） | 未征召 且 未高维 | 自动搜索敌对 → blink 跳脸 → 攻击；无目标 3000 tick 超时自动关闭 | `CompTick` 30 tick 分频 |
| 守卫（征召保护任务） | 征召 且 `guardModeEnabled` | 拦截主人 12 格内威胁 + 紧跟主人（≤4 格） | `JobGiver_AMGuardMaster`（思考树） |

### 9.4 Gizmo 交互

| 按钮 | 显示条件 | 行为 |
|---|---|---|
| 猎杀模式（现有） | 任意 | `SetHuntMode(!enableHuntMode)`；打开时若守卫开着 → 自动关守卫 |
| 「紧跟并守卫主人」（新增） | 仅征召且有主人 | `SetGuardMode(!guardModeEnabled)`；打开时若猎杀开着 → 自动关猎杀 |
| 「留守模式」（新增，v2.3） | 任意 | `SetStandbyMode(!standbyMode)`；打开时若正在执行侍奉 Job → 立即 `EndCurrentJob(InterruptForced)`；不影响猎杀/守卫开关状态（仅暂停执行） |

- 未征召时守卫按钮不显示（守卫只在征召语义下存在）。
- 描述文案注明互斥关系（i18n）："开启后将关闭猎杀模式"。
- 留守开关与猎杀/守卫**不互斥**（它是总闸，只暂停执行、不改开关状态）；留守打开时猎杀仍可手动开启并生效（猎杀不属于侍奉系统），守卫/侍奉一律暂停。

### 9.5 留守开关（总闸，v2.3）

```csharp
public void SetStandbyMode(bool on)   // CompArtificialMaid 方法
{
    if (on == standbyMode) return;
    standbyMode = on;
    if (on && Pawn?.CurJob != null && ServitudeJobDefs.Contains(Pawn.CurJob.def))
    {
        Pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);  // 立即中断侍奉 Job
    }
}
```

- 语义：打开 = 侍奉系统完全静默（守卫基类 ⓪ 短路 + 自动征召跳过 + 立即中断当前侍奉 Job）；关闭 = 恢复，下一思考节拍一切正常。
- 与猎杀/守卫**不互斥**：留守是总闸，只暂停"以主人为中心的侍奉行为"；猎杀（自主战斗）不受影响；守卫开关状态保留但留守期间不生效。
- 存储：`CompArtificialMaid.standbyMode`（Scribe 保存）；Gizmo 任意状态可见。
- 征召状态下打开留守：守卫暂停，女仆表现为普通征召小人（玩家右键指挥完全正常）——这正是"留守"的意义。
- 覆盖清单：跟随、未征召保卫、征召守卫、携带救援、携带跟随、喂食、陪伴、勾引、高维跟随、自动征召联动（含柜中唤醒）、主仆连线绘制。

---

## 10. 状态存储与设置项

### 10.1 `CompArtificialMaid` 新增字段（Scribe 保存）

| 字段 | 默认 | 说明 |
|---|---|---|
| `bool guardModeEnabled` | false | 征召守卫开关（Gizmo 控制，自动征召时置 true） |
| `bool standbyMode` | false | **留守总开关**（v2.3）：打开后侍奉系统完全不执行（守卫基类短路 + 自动征召跳过 + 立即中断侍奉 Job） |
| `bool autoDraftWithMaster` | true | 主人征召时自动征召（设置可关） |
| `bool autoUndraftWithMaster` | true | 主人解除时同步解除 |
| `int lastMasterDraftTick` | -1 | 防抖/调试用（可选） |

### 10.2 Mod 设置（Servitude 分组）

- 自动征召开关（`autoDraftWithMaster`）
- 同步解除开关（`autoUndraftWithMaster`）
- 守卫拦截半径（默认 12 格）、紧跟距离（默认 4 格）
- 高维跟随半径（默认 4 格）
- 显示侍奉主从连线、显示互动信件、互动触发率倍率、互动冷却倍率（渡鸦式）
- **v2.2 新增**：
  - `wakeMaidOnMasterDraft`（默认 true）：主人征召时允许唤醒柜中女仆
  - `wakeIgnoresAutoWake`（默认 false）：无视展示柜 `autoWake` 强制唤醒（默认尊重收纳）
  - `undraftAlsoPutsBack`（默认 false）：解除征召时柜中唤醒的女仆是否自动回柜（默认不回，保持在地图侍奉）

### 10.3 i18n

自动征召消息、唤醒消息、Gizmo 标签/描述、设置项、互动 letter——全部走 `Languages/ChineseSimplified(简体中文)/Keyed` 与 `Languages/English/Keyed`，禁止硬编码（项目规范）。

---

## 11. 兼容性设计

- **AutoBlink**：强引用 + 2 处低频 Postfix；不碰其内部状态；未装时全部 null 守卫。
- **RJW**：复用现有 `RJWCompatibility` 反射层，勾引走 RJW 接口，未装走原版 Lovin。
- **HAR**：女仆为 `ThingDef_AlienRace`，跟随/携带/Lovin 全部走原版 Job 系统，天然兼容。
- **原版 Job 生态**：所有自建 Job 设 `casualInterruptible`、`checkOverrideOnDamage`；预留/寻路失败即放弃；尊重 `RespectsAllowedArea`。
- **高维（HighDim）并存**：高维下未征召只跟随（守卫基类分支）；高维×征召×守卫正交叠加；进入/退出高维不改变征召状态。
- **HuntMode / Hibernate / 展示柜**：猎杀×守卫互斥（§9）；有主人时 Hibernate 让位；柜中女仆唤醒联动（§8）。
- **存档**：`Scribe_Collections` + null 防护 + legacy 迁移字段；mod 卸载由原版对未知 WorldComponent 容错。
- **i18n**：全部显示文本双语言。

---

## 12. 性能设计（汇总）

| 措施 | 说明 |
|---|---|
| 行为判定放思考树 | JobGiver 只在思考节拍执行，天然低频，不进每 tick 路径 |
| 快速失败链 | 守卫基类按"def 短路 → 死亡/生成 → 关系 → 同图 → 活动区"顺序，全部廉价判定 |
| O(1) 关系查询 | `Dictionary<Pawn,Pawn>` + 反向索引，无扫描 |
| 威胁检测分频 | 250 tick + 12 格小半径，不做全图扫描 |
| 自动征召/唤醒 | patch setter 事件驱动，仅征召瞬间执行，**零轮询** |
| 互斥判定 | `Set` 方法仅模式切换瞬间执行 + `CompTick` 三个布尔短路（O(1)，30 tick 分频） |
| 冷却表 | `Dictionary<int,int>`，容量小 |
| 材质/Def 缓存 | 连线材质 `[StaticConstructorOnStartup]`；Def 与 BlinkComp 缓存（`ConditionalWeakTable` 模式） |
| 无 LINQ/少分配 | 热路径 for 循环 + 复用列表；距离平方计算 |
| 连线仅选中绘制 | 原版 `DrawExtraSelectionOverlays` 机制 |
| AutoBlink patch 低频 | Postfix 仅在 blink 瞬间执行，首行空判定即返回 |

---

## 13. 边界情况核对

| 场景 | 处理 |
|---|---|
| 主人被征召瞬间女仆正在勾引/膝枕 | `EndCurrentJob(InterruptForced)` 干净中断，JobDriver 均有 FailOn 兜底 |
| 女仆刚被征召、正在进柜途中（EnterDisplayCase 未完成） | ParentHolder 尚非柜子 → 普通分支：打断进柜，直接征召 |
| 柜所在图与主人异图 | 唤醒+征召照常；守卫待命（异图发不出守卫 Job）；已实现跨图伴随则传送后守卫 |
| 展示柜被摧毁/未生成 | `dc == null` 守卫跳过；`WakeContainedMaid` 内部空安全 |
| 多女仆多柜 | 逐女仆独立判定各自柜的 `autoWake` |
| 唤醒后 autoHibernate 干扰 | `WakeContainedMaid(true)` 已关 `autoHibernate` |
| 玩家手动唤醒中的女仆 | `Drafted` 已 true 则跳过（不重复征召） |
| 征召下点猎杀（守卫关） | 猎杀开但**不自动执行**（`!Pawn.Drafted` 守卫），全交玩家指挥；互斥仍成立 |
| 高维 + 猎杀开 | 高维下猎杀不执行（只跟随）；退出高维后若守卫关则猎杀恢复 |
| 高维 + 征召 + 守卫 | 高维正交叠加，穿墙守卫成立 |
| 主人解除征召 | 女仆同步解除（默认）；`guardModeEnabled` 保留但不生效；不回柜（默认） |
| 留守开 + 主人被征召 | 女仆**不自动征召**、不唤醒（自动征召 patch 跳过）；若女仆已被玩家手动征召 → 守卫暂停，表现为普通征召小人，玩家指挥不受影响 |
| 留守开 + 正执行侍奉 Job | 打开瞬间 `EndCurrentJob(InterruptForced)` 中断跟随/膝枕/喂食等；关闭后下一思考节拍恢复 |
| 留守开 + 猎杀 | 猎杀照常生效（独立于侍奉系统）；守卫开关状态保留，恢复由互斥入口决定 |
| 留守开 + 高维 | 高维下"只跟随"也暂停（完全无侍奉行为）；退出高维后仍留守直到手动关闭 |
| 留守 + 展示柜 | 留守不阻止玩家手动唤醒/进柜（收纳是原版交互）；仅不触发自动征召联动 |
| 主人倒地瞬间与救援重复触发 | 防抖：救援目标进入 cooldown，避免反复抱-放-抱 |

---

## 14. 风险与实现时验证点

1. **【必须实测】draft 下 `Humanlike_PostDuty` 插入点是否可达**：原版 draft 无命令时会走到 `Humanlike_PostDuty`（渡鸦族 JobGiver 同点，可参考其 draft 行为验证）。若实测不可达，**后备方案**：守卫降级为 `CompTick` 60 tick 分频轮询——`if (Drafted && guardModeEnabled && (jobs.curJob == null || !jobs.curJob.playerForced))` → `TryTakeOrderedJob(跟随/攻击, JobTag.Misc)`，以 `playerForced` 尊重玩家指挥。
2. **自动征召打断时序**：`drafter.Drafted = true` 后需显式 `EndCurrentJob(InterruptForced)`，否则旧侍奉 Job 残留；实测原版 setter 副作用。
3. **柜内女仆判定**：实测柜内 `IsColonistPlayerControlled` 取值（预期 false，故用 `Faction` 判定）；`WakeContainedMaid` 弹出后 `drafter` 可用性。
4. **携带 blink 时序**：`ExecuteBlink` 内部 `pather.StartPath` 续走，Postfix 需在 teleport 后同步被携带者，避免下一帧路径以旧位置计算。
5. **多女仆同主**：全部征召可能喧宾夺主，默认全征召 + 设置"仅跟随最近者"（迭代项）。
6. **守卫模式的自杀式冲锋**：女仆本体近无敌（`IncomingDamageFactor` 极低），激进守卫可接受；未来开放限制则加"距离主人 ≤ N 格"钳制。
7. **唤醒→征召同帧副作用**：原 `EjectContents` 已含组件补齐，预期无寻路/渲染副作用，实测确认。

---

## 15. 实施路线图

| 里程碑 | 内容 | 验证 |
|---|---|---|
| **M1 地基** | Manager + Def + Gizmo 绑定/解除 + 跟随 + 主人心情 + 连线 + 设置 | 绑定后女仆空闲跟随；心情生效；存档读档不丢关系 |
| **M2 生存** | 喂食 + 未征召保卫（含 blink 跳脸）+ Hibernate 让位 | 主人饿→喂食；主人遇袭→女仆拦截 |
| **M2.5 模式骨架** | `SetGuardMode`/`SetHuntMode`/`SetStandbyMode` 方法 + 现有猎杀赋值点统一改造 + `guardModeEnabled`/`standbyMode` 字段 + 守卫/留守 Gizmo + 设置项 | 互斥开关联动正确；Gizmo 仅征召显示守卫；留守总闸生效（打开即完全静默、关闭即恢复） |
| **M3 救援** | 携带救援 + 携带跟随 + **AutoBlink 携带同步 patch** | 主人倒地→抱起→blink 连人瞬移回安全点 |
| **M3.5 自动征召** | `Patch_Pawn_DraftController` setter + 自动征召/解除 + i18n | 主人征召→女仆自动征召+守卫 |
| **M3.5a 唤醒联动** | 自动征召 patch 加入柜中分支（`WakeContainedMaid`）+ Faction 判定修正 + 3 个新设置项 + i18n | `autoWake=true`→唤醒征召；`false`→保持休眠 |
| **M3.5b 时序验证** | 唤醒后 `drafter` 可用性、`EndCurrentJob` 时序、柜内 `IsColonistPlayerControlled` 取值 | 实测通过 |
| **M4 情感** | 膝枕 + 陪伴互动框架 + 主动勾引（RJW/原版） | 主人休息→膝枕回复；概率触发互动与信件 |
| **M4.5 守卫行为** | `JobGiver_AMGuardMaster`（拦截+紧跟）+ `CompTick` 猎杀守卫条件 + draft 可达性实测（含后备轮询方案） | 征召+守卫→拦截/紧跟；右键指挥优先 |
| **M5 伴随** | 跨图传送伴随 + 商队伴随（v2）+ 高维只跟随 + 高维×征召×守卫×猎杀四维组合验证 | 主人换图/入商队→女仆跟随到位 |

**每阶段验收**：`dotnet build -c Debug` 通过；新增 `.cs` 登记 csproj；i18n 双语言补齐；性能自查（无每 tick 扫描、无反射热路径、无频繁分配）。

---

## 16. 版本演进记录

| 版本 | 内容 |
|---|---|
| v1 | 基础侍奉框架：绑定/跟随/保卫/携带/喂食/陪伴/勾引/跨图伴随/携带blink/可扩展三段式 |
| v2 | 征召/高维状态机：A 完整侍奉、B 高维只跟随、C 征召守卫（Gizmo 开关）、D 自动征召（事件驱动） |
| v2.1 | 猎杀×守卫互斥：`Set` 唯一入口 + 执行端双保险 + Gizmo 联动 |
| v2.2 | 柜中唤醒联动：模式 D' 扩展（`autoWake` → `WakeContainedMaid` → 征召守卫）、Faction 判定修正、3 个新设置项 |
| v2.3 | 留守总开关：`standbyMode` + `SetStandbyMode`（守卫基类短路 + 自动征召跳过 + 立即中断侍奉 Job + 连线隐藏）；与猎杀/守卫不互斥，仅暂停执行 |
