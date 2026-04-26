# RimWorld Mod 设计方案：物质-能量转化仪 (Matter-Energy Converter)

本 Mod 旨在引入一种高端科技设备，能够将实体物质彻底分解并转化为纯粹的电能，存储于其内部的无限电容中。

## 1. 核心逻辑：电能存储系统
由于原版 `CompPowerBattery` 的上限在 XML 中是固定的，实现“无上限”需要通过 C# 编写自定义电力组件。

* **动态上限机制**：
    * 继承 `CompPowerBattery` 类。
    * **逻辑**：在每一帧（Tick）或电量变动时，监测当前存储值。当 `storedEnergy` 大于等于 `storedEnergyMax` 时，自动通过代码提升 `storedEnergyMax` 的数值。（参考 OmniCrafterSmartInfiniteBattery.cs ）
    * ~~**UI 优化**：重写 `CompInspectStringExtra` 方法，当电量巨大时，将数值缩写（如 `1.2GWd`），防止信息栏文本溢出。~~

## 2. 三种回收交互方式

### 方式 A：载入模式 (Load & Process)
* **交互逻辑**：模仿原版“空投仓”或“远征队”的装载界面。
* **实现建议**：
    * 为主建筑添加 `CompTransporter` 组件。
    * 玩家点击“选择回收目标”按钮，弹出标准物品选择列表。
    * 殖民者会将选中的物品搬运至转换仪。
    * 当物品进入转换仪的容器 (`ThingOwner`) 后，触发转化逻辑并销毁物品。

### 方式 B：存储区模式 (Storage & Batch)
* **交互逻辑**：建筑本身作为一个“存储位”，支持设置过滤清单。
* **实现建议**：
    * 主类继承 `Building_Storage`。
    * 玩家可以在“存储”选项卡中像设置仓库一样勾选允许回收的分类。
    * 小人会利用空闲时间将杂物搬运至机器上方。
    * 添加一个自定义指令（Gizmo）按钮：“开始批量转换”。点击后，遍历建筑坐标格上的所有物品进行转化。

### 方式 C：光标强制模式 (Direct Conversion)
* **交互逻辑**：类似“拆除”指令，但点击即瞬间消失并加电。
* **实现建议**：
    * 使用 `Find.Targeter.BeginTargeting` 开启瞄准模式。
    * 设置 `TargetingParameters` 以允许选中建筑（Building）和物品（Item）。
    * **左键回调**：执行 `Recycle(target.Thing)`。
    * **右键回调**：由系统默认处理取消瞄准。

## 3. 转化能量计算公式

能量产出将严格遵循以下算法逻辑：

$$E = (V_{market} + W_{mass} + HP_{max}) \times M_{quality}$$

* **$V_{market}$ (价值)**：物品当前的市价。
* **$W_{mass}$ (重量)**：物品的重量数值。
* **$HP_{max}$ (耐久)**：如果是建筑或有耐久的物品，取其最大生命值。
* **$M_{quality}$ (品质乘数)**：
    * **劣质 (Awful)**: 0.5x
    * **平庸 (Poor)**: 0.8x
    * **普通 (Normal)**: 1.0x
    * **优秀 (Good)**: 1.2x
    * **极佳 (Excellent)**: 1.5x
    * **大师 (Masterwork)**: 2.0x
    * **传说 (Legendary)**: 3.0x

## 4. 开发注意事项（技术要点）

1.  **C# 类引用**：
    * 需引用 `RimWorld` 和 `Verse` 命名空间。
    * 计算价值需使用 `thing.GetStatValue(StatDefOf.MarketValue)`。
    * 获取品质需使用 `thing.TryGetQuality(out QualityCategory qc)`。
2.  **安全黑名单**：
    * **强制回收（方式 C）** 建议排除掉 `Pawn`（小人/动物）以及 `MapMeshFlag.Buildings` 中标记为不可拆除的物体，防止误点导致基地崩盘。
3.  **平衡性调节**：
    * 建议在 XML 中提供一个 `energyConversionEfficiency`（转化效率）参数，方便玩家自行调节数值倍率（例如乘以 0.1 或 10）。
4.  **音效与特效**：
    * 转化瞬间建议调用 `MoteMaker.ThrowLightningGlow` 或产生“消散”粒子效果，并播放 `Power_AbruptOn` 类似的音效，以增强打击感。


