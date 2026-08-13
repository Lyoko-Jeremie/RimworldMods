# 设计审查报告（修订状态跟踪）

> 本文件是 `GlobalStorageDesign.md` 的问题跟踪清单。**第一版审查发现的全部问题（13 高 + 18 中 + 10 低/勘误）已按建议修复到设计文档正文**，本节从问题清单转为**修订状态与剩余待办**。

---

## 修订状态：已全部修复 ✓

| 原问题 | 修复落点（GlobalStorageDesign.md） |
|---|---|
| H1 M10 无限搬运循环 | §1.6（锁定语义裁决：废弃"低→高自动流出"）、§5.2 #6（升必需，短路即锁定）、§6.3（优先级分析重写，旧 M10 决策废弃）、§9（#6 失效残余风险）、§11 V1 |
| H2 无限容量取出侧不可见 | §3.2（stackLimit 上限只服务合并假设）、§5.2 #9（数量替换 patch：搜索/取料/计数）、§3.3（SplitOff postfix 即时补回）、§1.3-2、§8 P1 |
| H3 SplitOff 双触发/双扣/回滚漂移/校正错位 | §3.3（单点 patch、`__result == __instance` 防双扣、TryAbsorbStack 回滚补偿、残余竞态说明）、§5.2 #5 |
| H4 view owner 命门 | §1.6、§3.2 构造强制项 1、§4 表 view 字段注释 |
| H5 食物/治疗路径断言错误 | §1.3-3（如实改写）、§4.1c、§5.2 #7（目标改为 FoodUtility/HealthAIUtility）、§5.3 |
| H6 读档时序（FinalizeInit 赋值 Instance） | §1.5（先例陷阱警告）、§3.3 读档规则（版本号重置前移至 ExposeData(PostLoadInit)） |
| H7 温度 patch Harmony 写法 | §5.1（ref float temperature + out bool __result 正确写法 + postfix 备选） |
| H8 预留记账 -1 语义 | §3.3（-1 = StackCount_All 按副本当前 stackCount 计，删解析表）、§5.2 #8 |
| H9 组 filter 通知链断点 | §4.1e 语义边界表（原版断链行）、§3.3（设置签名检查通道，零额外 patch） |
| H10 60 tick vs Rare=250 | §4.1 def（tickerType=Normal + IsHashIntervalTick(60)）、§3.4/§9 措辞统一 |
| H11 退休列表无定义 | §3.3 新增"退休副本生命周期"小节（退休条件/销毁时机/多副本约束/job 中断清理）、§11 E1 修正 |
| H12 TryAdd bool 重载遗漏 | §3.2 override 表（bool 版为实际入口 + 返回值契约 + int 版）、吸收同步单一入口 |
| H13 §7 产物 toil 张冠李戴 | §7.2-3（FinishRecipeAndStartStoringProduct 三分支）、§7.3 A3（patch 点修正）、A2（private static 注明）、§7.4（原生 WorkTableAutonomous 分支） |
| 中 1 ListerHaulables 频率 | §3.4/§9（每 tick 全量 Check、轮转覆盖缺口） |
| 中 2 #6 与 §1.6 语义互斥 | §1.6/§5.2 #6/§6.3（锁定语义统一裁决，#6 排除放行条目） |
| 中 3 playerForced 绕过 | §3.3/§5.2 #8（CanReserve 无条件检查） |
| 中 4 Log.Error 刷屏 | §3.3（静默返回 false、"放弃该 job"措辞） |
| 中 5 CanReserveStack | §5.2 #8 纳入 patch 清单 |
| 中 6 MinifiedThing stackLimit=1 | §3.2 MinifiedThing 例外行 |
| 中 7 视图深存负收益 | §4 表存档行（只存 settings）、§3.4 存档行 |
| 中 8 dontTickContents 未强制 | §3.2 构造强制项 2、§1.1/§1.4 表述修正 |
| 中 9 取消链接写回定性 | §4.1e 语义边界表 + E6 段（对齐原版 SetStorageGroup 写回、覆盖 gizmo 特判缺口、幂等） |
| 中 10 def 缺 inspectorTabs | §4.1 def XML 补 `<inspectorTabs>` |
| 中 11 Notify_* 缺 Spawned 守卫 | §4 表 Notify_ItemAdded/Removed 行 |
| 中 12 HaulFromSource 需主动调用 | §4 表新增 GetFloatMenuOptions 行 |
| 中 13 缺 Notify_SettingsChanged | §4 表新增行（含 haulDestinationManager 重排序） |
| 中 14 SpaceRemainingFor 语义 | §4 表 SpaceRemainingFor 行 |
| 中 15 多选同步表述误导 | §4.1d 第 1 条（InheritInteractionsFrom 门控） |
| 中 16 SplitOff 属性复制 | §6.5 新增行（L2 避开 SplitOff） |
| 中 17 ITab 滑条/丢弃 | §3.2 UI 显示行（一并自定义） |
| 中 18 禁止取出与 CountProducts | §4.1c noWithdraw 行（已文档化） |
| 低 1-10 行号/措辞勘误 | §1.2（73→75、124→131、参照措辞）、§1.3、§6.3（IsInValidBestStorage）、§6.5（GenPlace 自动拆分）、§3.3（对账方向）、§3.2（placedThings 引用）、§4 表（EverStorableFixedSettings 全允许）、§4.1d（groupKeyIgnoreContent）、§4.1b（Notify_MinifiedThingAboutToBeDestroyed 理由） |

---

## 剩余待办（未修复，登记于设计文档 §11）

| # | 项 | 状态 |
|---|---|---|
| V1 | M10 循环的消除依赖 §5.2 #6 锁定短路 patch——实现期须实测验证 #6 在目标 mod 环境生效（第三方 mod 冲突会使其失效、M10 回归） | 待 P4 实测 |
| V2 | 数量替换 patch（§5.2 #9）的工作台吞吐：单份需求 > stackLimit 的账单可开工、连续制作无 8-10 秒空转 | 待 P1/P4 实测 |
| E2-E5 | 可选增强（复制全部设置 gizmo、管理面板远程开关、MapOverlay 图标、SlotGroup 兼容模式） | 未拍板，保持原状 |

---

> 验证通过的核心断言与审查方法记录见设计文档 §11 引言及正文各处行号引用；本文件不再重复。
