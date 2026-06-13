# 超级人造人 (Artificial Maid) 设计方案

本方案旨在设计一个基于 Human 原型的超级人造人女仆。该种族只有女性，拥有无限的工作效率、无需求、无崩溃风险以及无敌躯体，同时支持心灵能力和血源质。

使用 `Humanoid Alien Races (HAR)` 来简化实现。

## 1. 核心种族定义 (ThingDef)
*   **基础原型**: `Human` (ThingDef)
*   **种族名称**: `ArtificialMaid`
*   **生成规则**: `natural` 为 false，不会在野外或袭击中自然出现。
*   **外观与装备**: 使用人类身体模型，完全支持所有人类装备和防具。

## 2. 属性与能力实现

### 2.1 无限工作效率
*   **实现方式**: 在 `statBases` 或种族特有的 `Hediff` 中修改。
*   **关键属性**: 
    *   `WorkSpeedGlobal`: 设置为极高值（如 999999%）。
    *   `MoveSpeed`: 设置为极高值（如 100 cells/s）。
*   **工作能力等级**：99级。

### 2.2 需求消除 (Needs)
*   **消除项**: 进食 (Food)、休息 (Rest)等。
*   **实现方案**: 
    *   使用 `GeneDef` (如果支持 Biotech) 或自定义 `RaceProperties` 中的 `needsProperties` 禁用对应需求。
    *   设置 `FoodType` 为 `None`。

### 2.3 精神状态与稳定性
*   **精神崩溃**: `MentalBreakThreshold` 设置为 0。
*   **情绪**: 锁定在最高值。

### 2.4 无敌躯体与受伤免疫
*   **伤害减免**: `IncomingDamageFactor` 设置为 0。
*   **状态效果**: 免疫所有疾病、感染、失血、疼痛。

### 2.5 心灵能力与心灵熵
*   **心灵熵**: `PsychicEntropyMax` 设置为极高，`PsychicEntropyRecoveryRate` 设置为极高。恢复速度设为极高。

### 2.6 血源质 (Hemogen)
*   **血源质获取**: 自带 `Hemogenic` 基因/属性。
*   **持续供应**: 高速恢复血源质。

### 2.7 机械师
*   **控制带宽**：超高的控制带宽

## 3. 制造系统

### 3.1 制造建筑 (OmniArtificialMaidFabricator)
*   **功能**: 制造超级人造人的专用工作台或培养舱。
*   **消耗资源**: 高级零部件、超织物等。

## 4. 技术实现建议 (C#)
*   使用 Harmony 拦截伤害逻辑。
*   使用组件 (Comp) 实时补满各项资源。

## 5. 多语言支持 (i18n)
*   支持中文、英文等多语言配置。

## 6. 参考以下物品的属性来添加参数
```xml
        <statBases>
            <!-- 基础生存属性 -->
            <MaxHitPoints>99999</MaxHitPoints>

            <Flammability>0.0</Flammability> <!-- 绝对不可燃 -->

            <!-- 原始防御数值 (非CE环境下) -->
            <!-- 10000 代表 1,000,000%，在原版算法下是绝对无敌 -->
            <ArmorRating_Sharp>10000</ArmorRating_Sharp>
            <ArmorRating_Blunt>10000</ArmorRating_Blunt>
            <ArmorRating_Heat>10000</ArmorRating_Heat>

            <!-- 极端环境抗性：能让小人在绝对零度或恒星表面生存 -->
            <Insulation_Cold>10000</Insulation_Cold>
            <Insulation_Heat>10000</Insulation_Heat>
            <EquipDelay>0</EquipDelay> <!-- 即时穿戴 -->

            <!-- 能量护盾属性 -->
            <EnergyShieldRechargeRate>100000</EnergyShieldRechargeRate> <!-- 充能极快，瞬间回满 -->
            <EnergyShieldEnergyMax>1500000</EnergyShieldEnergyMax> <!-- 护盾容量极巨化 -->

        </statBases>

        <comps>
            <!-- 护盾组件：注意这里使用的是自定义的 Armorshield 类，需确保前置 Mod 存在 -->
            <li Class="CompProperties_Shield">
                <compClass>Armorshield.CompShieldRanged</compClass>
            </li>
            <!-- AutoBlink 传送 -->
            <li Class="CompProperties_CauseHediff_Apparel" MayRequire="rabiosus.autoblink">
                <hediff>SNS_TemporalBarrier_AutoBlink_Hediff</hediff>
            </li>

        </comps>

        <equippedStatOffsets>
            <!-- 核心战斗加成 -->
            <IncomingDamageFactor>0.00015</IncomingDamageFactor> <!-- 受到伤害乘数降至极低 -->
            <AimingDelayFactor>-0.999</AimingDelayFactor> <!-- 近乎瞬发瞄准 (1 - 0.995 = 0.005) -->
            <RangedCooldownFactor>-0.999</RangedCooldownFactor> <!-- 近乎没有冷却 (1 - 0.999 = 0.001) -->
            <ShootingAccuracyPawn>100</ShootingAccuracyPawn> <!-- 射击精度补正 -->
            <MoveSpeed>+200</MoveSpeed> <!-- 极速移动 -->

            <!-- 近战属性 -->
            <MeleeHitChance>1</MeleeHitChance> <!-- 近战命中率 -->
            <MeleeDodgeChance>1</MeleeDodgeChance> <!-- 近战闪避率 -->
            <MeleeCooldownFactor>-1</MeleeCooldownFactor> <!-- 近战无冷却 -->
            <MeleeDamageFactor>1</MeleeDamageFactor> <!-- 额外伤害加成 -->
            <StaggerDurationFactor>-1</StaggerDurationFactor> <!-- 免疫被击中后的减速僵直 -->

            <!-- 精神与生存 -->
            <ToxicResistance>1</ToxicResistance> <!-- 免疫毒性 -->
            <MentalBreakThreshold>-2</MentalBreakThreshold> <!-- 永不崩溃 -->
            <PsychicSensitivity>1</PsychicSensitivity> <!-- 精神敏感度保持标准 -->
            <PsychicEntropyMax MayRequire="Ludeon.RimWorld.Ideology">200000</PsychicEntropyMax> <!-- 精神熵上限（阈值） -->
            <PsychicEntropyRecoveryRate MayRequire="Ludeon.RimWorld.Ideology">+10000</PsychicEntropyRecoveryRate> <!-- 精神熵恢复（消散）速度 -->
            <MeditationFocusGain MayRequire="Ludeon.RimWorld.Ideology">30</MeditationFocusGain> <!-- 冥想效力乘数 -->
            <EatingSpeed>1</EatingSpeed> <!-- 进食速度 -->
            <RestFallRateFactor>-0.5</RestFallRateFactor> <!-- 睡眠需求减半 -->
            <FilthRate>-1.5</FilthRate> <!-- 不产生污垢 -->

            <!-- 机械师 (Biotech) 相关属性 -->
            <SubcoreEncodingSpeed>+30</SubcoreEncodingSpeed> <!-- 次级核心编码/扫描速度 -->
            <MechRepairSpeed>+300</MechRepairSpeed> <!-- 机械体修理速度 -->
            <MechControlGroups>9</MechControlGroups> <!-- 机械控制组数量 -->
            <MechRemoteShieldEnergy>+2500</MechRemoteShieldEnergy> <!-- 远程机械盾能量加成 -->
            <MechBandwidth>+600</MechBandwidth> <!-- 机械带宽 -->
            <WorkSpeedGlobalOffsetMech>+110</WorkSpeedGlobalOffsetMech> <!-- 受控机械体的全局工作速度偏移 -->
            <MechFormingSpeed>+120</MechFormingSpeed> <!-- 机械体妊娠/制造速度 -->

            <!-- 生产、搬运与工作 -->
            <WorkSpeedGlobal>100000.0</WorkSpeedGlobal> <!-- 瞬间完成所有工作 -->
            <CarryingCapacity>10000000</CarryingCapacity> <!-- 极大的携带量 -->
            <RI_MassCarryCapacity MayRequire="RI.RimImmortal.Core">100000</RI_MassCarryCapacity> <!-- 负重上限增加 -->
            <ButcheryMechanoidEfficiency>1</ButcheryMechanoidEfficiency> <!-- 拆解机械体产出效率 -->
            <ConstructSuccessChance>1</ConstructSuccessChance> <!-- 建造成功率 -->

            <!-- 特殊环境抗性 -->
            <VacuumResistance MayRequire="Ludeon.RimWorld.Odyssey">1</VacuumResistance> <!-- SOS2/Odyssey 真空抗性 -->
            <Flammability>-1</Flammability> <!-- 绝对不可燃 -->
        </equippedStatOffsets>
```

## 7. 基于 Humanoid Alien Races (HAR) 的具体实现思考

为了实现 Artificial Maid，我们将利用 HAR 提供的强大 XML 配置能力来简化开发并确保与原版及其他 Mod 的兼容性。

### 7.1 种族基础定义 (`ThingDef_AlienRace`)
*   **类名**: 使用 `AlienRace.ThingDef_AlienRace`。
*   **性别限制**: 
    *   设置 `<maleGenderProbability>0</maleGenderProbability>` 确保生成的小人全是女性。
*   **外观锁定**:
    *   在 `<graphicPaths>` 中指定人类的基础路径（`Things/Pawn/Humanlike/Bodies/` 和 `Things/Pawn/Humanlike/Heads/`），确保可以使用人类的所有发型、胡须和服装。
    *   可以通过 `<skinColor>` 锁定肤色，或使用 `<colorChannels>` 增加自定义颜色选项。

### 7.2 思想与情绪控制 (`thoughtSettings`)
*   **禁用崩溃风险相关思想**:
    *   使用 `<cannotReceiveThoughts>` 列表屏蔽会导致情绪下降的常见思想（如“睡在地上”、“吃生肉”、“亲人去世”等）。
    *   或者设置 `<cannotReceiveThoughtsAtAll>true</cannotReceiveThoughtsAtAll>` 并通过 `<canStillReceiveThoughts>` 白名单保留必要的社交思想。
*   **思想替换 (`replacerList`)**:
    *   将原版负面思想替换为正面或中性思想，以体现其“人造人”的逻辑思维。

### 7.3 限制与特权 (`raceRestriction`)
*   **装备许可**:
    *   确保 `<apparelList>` 和 `<weaponList>` 包含所有人类可用的装备，或者不设置限制以默认允许所有。
*   **基因与种族特性**:
    *   利用 `<blackEndogènes>` 或 `<whiteEndogènes>` (Biotech) 确保其天生具备特定的基因（如血源质需求、高带宽等）。

### 7.4 需求消除的具体配置
*   虽然 HAR 可以通过 `GeneDef` 影响需求，但最直接的方式是在 `AlienRace.ThingDef_AlienRace` 的 `race` 属性中，将 `needsProperties` 的某些项移除。
*   或者通过添加一个永不消失的 `Hediff`（如在 `lifeStageAges` 中初始赋予），该 `Hediff` 带有 `causesNoNeed` 效果。

### 7.5 渲染优化
*   使用 HAR 的 `AlienPartGenerator` 可以为 Artificial Maid 添加独特的视觉特征（如机械接缝、发光瞳孔等），而无需修改原版贴图。
