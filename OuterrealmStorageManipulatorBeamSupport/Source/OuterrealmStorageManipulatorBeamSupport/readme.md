# OuterrealmStorageManipulatorBeamSupport 设计文档

超维存储（OuterrealmStorage）与牵引光束搬运器（Manipulator Beam）之间的**独立适配 mod**。
本文记录当前实现的事实、与主 mod 的契约边界、光束协议假设，以及光束更新后的核对流程。

> 核心原则：**主 mod 不含任何光束代码**。本 mod 通过主 mod 提供的中立预留契约参与库存口径；
> 未安装本 mod 时，超维存储的全部功能（除光束本身）必须照常工作。

---

## 1. 为什么独立成 mod

旧实现把光束适配内嵌在主 mod（`Patch_ManipulatorBeamCompat.cs` / `OuterrealmBeamAdapter.cs` /
`OuterrealmBeamLedger.cs` / `OuterrealmOptionalSignature.cs`）。问题是：

- 光束的 `IBeamOperator` 协议在 2026 年的更新中**删除了 `TryBuildBatchFromCell` /
  `TryBuildBatchFromCellAuto`**，`BeamHaulBatch` 沦为死类型；
- 旧适配器在 `Install()` 中第一个 `Require(...)` 就抛 `MissingMethodException`，
  触发 `UnpatchAll` **整组回滚** —— 兼容性静默失效，只在日志留一行错误；
- 每次光束更新都会让主 mod 承担一次「改不改、怎么改」的风险。

因此把适配完全外移到本 mod：主 mod 只保留一个**中立的第三方预留接入点**，
本 mod 只支持**当前最新版**光束，不再保留任何旧版（Legacy）协议代码。

---

## 2. 文件职责

```
Source/OuterrealmStorageManipulatorBeamSupport/
├─ OuterrealmBeamAdapter.cs           边界适配器：反射绑定 + 10 个 Harmony 边界
├─ OuterrealmBeamLedger.cs            每局预留账本，实现主 mod 的 IOuterrealmExternalReservation
├─ OuterrealmBeamSupportComponent.cs  每局状态载体 + [StaticConstructorOnStartup] 安装入口
├─ Properties/AssemblyInfo.cs
└─ Tests/BeamCompat/                  签名审计 + 行为测试（不参与游戏运行）
   ├─ SignatureAudit.cs               直接读取真实 dll 元数据核对签名
   ├─ Doubles.cs                      游戏与主 mod 的类型替身
   ├─ Program.cs                      行为断言
   └─ BeamCompat.csproj
```

构建产物由 csproj 的 `CopyToAssemblies` Target 自动复制到 `../../Assemblies/`。

---

## 3. 与主 mod 的契约

### 3.1 主 mod 提供的预留契约（本 mod 实现它）

`FullyAutomaticOmniCrafter.OuterrealmStorage.OuterrealmExternalReservation.cs`：

```csharp
public interface IOuterrealmExternalReservation
{
    long ReservedFor(OuterrealmEntry entry);                                    // 本系统占用的未兑现数量
    bool IsExtracting(OuterrealmEntry entry);                                   // 提交中：整条目对外不可用
    bool HasForeignReservation(Pawn caller, Thing query, OuterrealmEntry entry);// 他人名下占用（唯一锚点互斥）
    long AdjustAvailable(Pawn caller, Thing query, long available);             // CanReserve 可用量修正
    int  AdjustRequest(Pawn caller, Thing query, int requested, long available);// CanReserve 请求量修正
    void ForgetVault(Building_OuterrealmVault vault);                           // 仓库注销清理
    void ForgetMap(Map map);                                                    // 地图移除清理
}

public static class OuterrealmExternalReservationRegistry
{
    void Register(IOuterrealmExternalReservation provider);
    void Unregister(IOuterrealmExternalReservation provider);
    long ReservedTotal(OuterrealmEntry entry);
    bool AnyReserved(OuterrealmEntry entry);
    bool AnyExtracting(OuterrealmEntry entry);
    // 由主 mod 在 CanReserve 补丁中调用：
    bool HasForeignReservation(Pawn caller, Thing query, OuterrealmEntry entry);
    long AdjustAvailable(Pawn caller, Thing query, long available);
    int  AdjustRequest(Pawn caller, Thing query, int requested, long available);
    // 生命周期通知由主 mod 转发给全部提供者：
    void NotifyVaultRemoved(Building_OuterrealmVault vault);
    void NotifyMapRemoved(Map map);
    // 供提供者回调主 mod：
    void NotifyReservationChanged();                        // ← 账本 changed 回调
    void NotifyIdentityReservationReleased(Thing query);    // ← 账本 released 回调
}
```

**未注册任何提供者时**，主 mod 的行为与「不带本 mod」逐条一致：
`ReservedTotal` 返回 0、`AnyReserved`/`AnyExtracting`/`HasForeignReservation` 返回 false、
`AdjustAvailable`/`AdjustRequest` 为恒等变换。

### 3.2 主 mod 已开放的 public 面（本 mod 依赖）

| 成员 | 用途 |
|---|---|
| `GameComponent_OuterrealmStorage.Instance` | 全局库存入口 |
| `.ReservedCountOf(entry)` | 全局预留读数（含所有外部提供者） |
| `.IsTransferring(entry)` | DoBill 提交隔离 |
| `.RegistrationsFor(vault)` | 按仓的唯一锚点注册集合（只读遍历） |
| `.Deposit(thing, vault)` / `.VaultsForReading` / `.HasVaultOnMap(map)` | 回存与目的地搜索 |
| `OuterrealmSourceResolver.TryResolve(thing, out OuterrealmSource)` | 只读来源解析 |
| `OuterrealmSourceResolver.Checkout(in OuterrealmSource, int)` | 最终所有权转移（唯一取出边界） |
| `OuterrealmSource`（`Vault`/`Entry`/`QueryThing`/`IsVaultQuery`） | 来源身份 |
| `OuterrealmVaultUtil.IsProjection` / `.IsVaultStoredThing` / `.IsProtectedFromAutomaticDeposit` | 查询对象识别 |
| `Building_OuterrealmVault`：`view` / `HaulDestinationEnabled` / `HaulSourceEnabled` / `AllowTakeForUse` / `Frozen` / `CanShow` / `Accepts` / `GetStoreSettings` | 权限与目的地门控 |
| `OuterrealmVaultViewThingOwner.InnerListForReading` / `TryAdd` | 视图枚举与即时入库 |
| `OuterrealmRuntimeRegistration` / `OuterrealmRuntimeRegistrationKind` | 锚点注册类型 |

**保持 internal、不对外承诺**：`OuterrealmStorageRuntimeState`、`OuterrealmBillResourceLedger`、
`OuterrealmPatchUtil`、`OuterrealmIdentityRouting`、`OuterrealmAnchorState`。

### 3.3 依赖声明

`About/About.xml`：

```xml
<loadAfter>
  <li>Jeremie.Fully.Automatic.OmniCrafter</li>
  <li>natsuki.manipulatorbeam</li>
</loadAfter>
<modDependencies>   <!-- Harmony / Manipulator Beam Emitter / OuterrealmTech -->  </modDependencies>
```

---

## 4. 新版光束协议与安装的 10 个边界

### 4.1 为什么不需要「借种子 + 注入候选」

| | 旧版协议 | 新版协议 |
|---|---|---|
| 源扫描 | `TryBuildBatchFromCell` 按 `cell.GetThingList`（**thingGrid**）逐格扫描 | `FillTransferQueue` / `ScanForAnyHaulWork` 按 `map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)` 扫描 |
| 能否看见 vault 物品 | 不能（投影刻意不进 thingGrid）→ 必须借 1 个种子 + 反射注入 batch | **能**：投影与唯一锚点都经 `map.listerThings.Add` 注册为伪 Spawned |

结论：新版**自然发现** vault 物品，因此本适配器**删除**了 `ScanForAnyHaulWork` 补丁与
`BatchPostfix` 的源注入 / Cursor 轮转逻辑。不要重新引入它们。

### 4.2 安装的 10 个边界

`PatchId = "Jeremie.OuterrealmStorage.ManipulatorBeamSupport"`，全部安装成功后才置 `enabled = true`；
任一 `Require` 抛出即 `UnpatchAll` 回滚并写日志，**主 mod 不受影响**。

| # | 目标方法 | 补丁 | 职责 |
|---|---|---|---|
| 1 | `BeamManipulatorUtility.CanBeamTransferThing` | Prefix + Finalizer | 源有效性、剩余数量与自身预留上下文；维护 ThreadStatic `query` |
| 2 | `BeamManipulatorUtility.TryFindStorageDestinationFor` | Prefix + Finalizer | 源是 vault 物品时置 `vaultStorageSearch`，排除其他 vault 目的地，避免共享库存循环搬运 |
| 3 | `BeamManipulatorUtility.TryClaimAndEnqueue` | Prefix + Finalizer | 目的地格→容器改写；申请条目级数量预留；失败清理队列、排除集合与双方 claim |
| 4 | `Building_BeamManipulator.TryLiftForTransfer` | Prefix + Finalizer | 最终权限与设备身份复查；Checkout 后孤立实物同步回存 |
| 5 | `Building_BeamManipulator.ExtractThingForTransfer` | Prefix | 普通投影与唯一锚点统一 Checkout（覆盖整堆不经 SplitOff 的分支） |
| 6 | `BeamClaimUtility.ReleaseClaim` | Finalizer | 取消或完成时幂等释放数量预留 |
| 7 | `BeamClaimUtility.ReleaseAllClaimsForOwner` | Finalizer | 设备整机取消时释放本设备未兑现预留 |
| 8 | `BeamClaimUtility.TryClaimDestinationContainer` | Prefix | vault 是无限容量吸收端，豁免第三方的容器独占锁 |
| 9 | `BeamManipulatorUtility.FinishTransfer` | Prefix | 以 vault 的 `Deposit` 结果为提交结果；MEC 装载直写 `innerContainer` |
| 10 | `BeamManipulatorUtility.IsBeamStorageGroupAllowed` | Postfix | 遵守 `HaulDestinationEnabled`（禁止存入、冻结） |

签名解析统一走 `RequiredType(...)` + `Require(type, name, isStatic, result, paramNames, paramTypes)`：
**同名方法存在不代表兼容** —— 参数名、参数个数、out/ref、返回值与静态性全部核对。

---

## 5. 三条核心机制

### 5.1 源发现与候选放行

`CanBeamTransferThing` 的 Prefix：

- 非超维存储对象 → 直接放行原逻辑；
- 是投影/锚点 → `OuterrealmSourceResolver.TryResolve` + `Allowed(source, forUse = true)` 判定权限；
- 可用量 = `Entry.Count - Storage.ReservedCountOf(Entry) + 自身已占额度`；
- 同时设置 `query = { Thing, Pawn, Entry, Own }` 线程局部上下文，供后续 `CanReserve` 复查使用，
  Finalizer 无条件恢复。

`Allowed` 要求：仓库已 Spawn、未冻结、查询物在地图且位置合法、`CanShow`，
且 `HaulSourceEnabled`（普通搬出）；拿取使用路径额外允许 `AllowTakeForUse`。

### 5.2 预留参与（账本）

`OuterrealmBeamLedger` 保存「光束已申请但尚未兑现」的数量：

- 按 `transfer` 对象存明细，按 `OuterrealmEntry` 汇总总量，按 `(queryThing, owner)` 存自身额度；
- `TryAcquire` 在入队时申请；`Resize` 只允许**缩小**，绝不在入队后放大；
- `BeginExtraction` 置提交隔离（`Extracting`），提交期间主 mod 的 `ReservedCountOf` 返回整条目，
  不对外暴露暂时释放的数量；外层提取结束后 `Release(transfer, finishExtraction: true)` 统一释放；
- `changed` / `released` 回调映射到主 mod 的 `NotifyReservationChanged` /
  `NotifyIdentityReservationReleased`。

**双向可见性**是本机制的意义：光束预留被普通 Pawn 预留看到（避免超卖），
普通 Pawn 预留也被光束扣减（`Enqueue` 时 `available` 已扣 `ReservedCountOf`）。

> 普通 Pawn 搬运与 DoBill 预算**不需要账本**：前者走原版 `ReservationManager`（由主 mod 的
> `CanReserve` 补丁与 `reservedTotals` 承载），后者由主 mod 的 `Runtime.Bills` 承载。
> 只有当第三方绕过原版预约系统时才需要外部预留提供者。

### 5.3 目的地交付与「格 → 容器」改写

**时机很关键**：新版 `FillTransferQueue` 在构造 transfer 后**立即**调用
`TryClaimAndEnqueue`，因此不能在其 Postfix 替换 transfer 对象（会丢失已登记的 claim）。
改写放在 `TryClaimAndEnqueue` 的 **Prefix** 内、claim 之前：

```csharp
setDestinationContainer(transfer, vault);   // 字段赋值，transfer 身份保持不变
```

这样 `TryClaimDestinationContainer` 与 `FinishTransfer` 都能识别容器目的地，
走 `view.TryAdd → Deposit` 的**即时入库**路径，而不是「落格 + 等 vault tick 吸收」。

`FinishPrefix` 对 vault 目的地的处理有两点必须保留：

1. 先 `ReleaseInTransitThing` 再从光束在途容器解除，禁止 finally 的
   `ReturnInTransitThing` + `EnsureCarriedThingLanded` 把已入库的权威实例再放回地图；
2. 以 `vault.view.TryAdd` 的返回值为提交结果 —— 因为 vault 的不可堆叠权威实例（尸体等）
   按设计保持未生成且无 holder，若沿用光束的 `Destroyed/stackCount/holdingOwner` 判据会误判失败。

---

## 6. 生命周期与线程

- `OuterrealmBeamSupportComponent : GameComponent` 由 `Game.FillComponents` 自动实例化
  （与原版一致：构造器自行保存 `Game` 引用，**不能**写 `: base(game)`）；
- 构造时先 `Unregister` 上一存档的账本，再 `Register` 本的账本，避免静态注册表残留旧局；
- `Current` 用 `ownerGame` 校验缓存，失效时 `game.GetComponent<>()` 重新解析；
- 地图移除 / 仓库注销由主 mod 转发 `NotifyMapRemoved` / `NotifyVaultRemoved` → 账本 `ReleaseMatching`；
- `[StaticConstructorOnStartup] OuterrealmBeamSupportBootstrap` 负责安装补丁，异常就地隔离；
- 线程：`EnqueuePrefix` / `LiftPrefix` 要求 `UnityData.IsInMainThread`；
  `query` / `vaultStorageSearch` / `lift` 均为 `[ThreadStatic]`，不得改成普通静态字段。

---

## 7. 测试

```powershell
# 1) 签名审计 + 行为测试（传入真实 dll 路径）
cd F:\SteamLibrary\steamapps\common\RimWorld\Mods\OuterrealmStorageManipulatorBeamSupport\Source\OuterrealmStorageManipulatorBeamSupport\Tests\BeamCompat
dotnet run -c Release -- "F:\294100\294100\3683998684\Assemblies\ManipulatorBeam.dll"

# 2) 两个工程编译
dotnet build -c Debug   # 本 mod
cd F:\SteamLibrary\steamapps\common\RimWorld\Mods\FullyAutomaticOmniCrafter\Source\FullyAutomaticOmniCrafter
dotnet build -c Debug   # 主 mod
```

**测试覆盖**（当前 10 个测试 / 17 个断言）：

- 候选放行：纯查询投影可被光束取用
- 入队申请条目级预留并反映到全局可用量
- 目的地格 → 容器改写（vault 格改写、非 vault 不改写）
- 实际抓取只 Checkout 一次并释放预留
- 禁止取出时无候选
- 冻结 / 禁止存入的仓库不能作为目的地
- 入队失败时预留与 claim 原子回滚
- 目的地缩量同步释放额度，且不能扩大

**测试的性质**：`Doubles.cs` 提供游戏与主 mod 的类型替身，`BeamCompat.csproj` 直接编译
`OuterrealmBeamAdapter.cs` 与 `OuterrealmBeamLedger.cs` 生产源码。它验证的是
反射绑定、协议时序与账本逻辑，**不能替代**实机 Harmony 绑定、寻路、Comp 回调与存档读写。

---

## 8. 维护手册：光束更新后的核对流程

1. **取得新协议的真相**。优先读 mod 自带源码（如 `...\3683998684\Source\ManipulatorBeam\`）；
   只有 dll 时用反编译，例如：
   ```powershell
   ilspycmd "<path>\ManipulatorBeam.dll" -o "$env:TEMP\mb_decompile"
   ```
   注意 `$env:TEMP` 每次会话可能变化，别依赖旧路径。

2. **核对第 4.2 节的 10 个方法**是否仍存在、参数名/类型/out-ref/静态性是否一致；
   同时核对 `IBeamOperator` 成员（`Map`/`Pawn`/`OwnerKey`/`Manipulator`）、
   `BeamTransfer` 字段（`thing`/`destinationContainer`/`count`/`isStripJob`/`destination`）。
   这些字段名由 `Getter/Setter` 表达式树直接绑定，改名即启动失败。

3. **跑测试**（第 7 节命令 1）。`SignatureAudit` 会直接报出缺失或不符的方法。

4. **写坏存档的排查**：若光束更新后出现物品复制或丢失，先确认
   `FinishPrefix` 与 `ExtractPrefix` 是否仍在 `enabled` 状态下生效
   （日志：`IBeamOperator compatibility installed (10 boundaries)`）。

5. **新增边界的原则**：只补**最终消费/所有权转移边界**。不要在 Reserve 阶段生成实物；
   不要为了「让光束看见」而借出实物或注入批次 —— 新版按 `listerThings` 扫描，不需要。

6. **不要删除的注释与不变量**：
   - `RewriteVaultDestination` 必须在 claim 之前；
   - `setDestinationContainer` 必须用字段赋值，不能替换 transfer 对象；
   - `FinishPrefix` 必须自行 `ReleaseInTransitThing`；
   - `query` 上下文必须由 Finalizer 恢复。

---

## 9. 已知限制与未验证项

| 项 | 状态 |
|---|---|
| 游戏内实机回归（Harmony 启动绑定、寻路、Comp 回调、保存→读取→保存） | **未验证**，须看游戏日志与实机操作 |
| 不装本 mod 时的实机行为与存档兼容 | 代码路径已按等价设计收敛，**未实机确认** |
| 自动型设备 `Building_BeamManipulatorAuto` 的实际搬运路径 | 未单独测试（共用同一 `Building_BeamManipulator` 边界） |
| 不经 `TryClaimAndEnqueue` 的入库路径 | 若存在则退回「落格 + vault tick 吸收」（功能正确但有延迟） |
| 光束 dll 的 `HintPath` | 8 级相对路径指向 `F:\294100\...`，换环境需调整 |
| 只支持最新版协议 | 旧版（`TryBuildBatchFromCell` 时代）**不再支持**，相关代码已删除 |

---

## 10. 相关文档

- 主 mod 架构与不变量：`..\..\..\FullyAutomaticOmniCrafter\Source\FullyAutomaticOmniCrafter\OuterrealmStorage\readme_OuterrealmStorage.md`
  （其中「牵引光束兼容备注」一节记录了本次分离）
- 主 mod 预留契约实现：`OuterrealmStorage\OuterrealmExternalReservation.cs`
