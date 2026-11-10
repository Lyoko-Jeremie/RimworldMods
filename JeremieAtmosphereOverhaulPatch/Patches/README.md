# Patches

行为逻辑都在 C# 兼容模块里，这里只放**静态 Def 数据**补丁。

当前文件：

| 文件 | 目标 | 作用 |
| --- | --- | --- |
| `VteFacilities.xml` | `VanillaExpanded.Temperature` | 给风管/风阀加 `airLeakFactor=0`（管道不该漏气），给温控设备补耐压/抗风参数 |

每个文件都必须用 `PatchOperationFindMod` 包一层，目标 Mod 不存在时整段跳过，例如：

```xml
<?xml version="1.0" encoding="utf-8"?>
<Patch>
  <Operation Class="PatchOperationFindMod">
    <mods>
      <li>GOS.GoodClimate</li>
    </mods>
    <match Class="PatchOperationAddModExtension">
      <xpath>Defs/ThingDef[defName="Vent"]</xpath>
      <value>
        <li Class="AtmosphereOverhaul.Simulation.BuildingAtmosphereExtension">
          <airLeakFactor>0</airLeakFactor>
        </li>
      </value>
    </match>
  </Operation>
</Patch>
```

## 快速回退

1. 关闭对应模块开关 + 重启（推荐，最干净）；
2. 或在设置里点「立即重装补丁」（当前会话生效）；
3. 或注释掉 `../LoadFolders.xml` 中对应 `<li IfModActive="...">`：
   该目标的条件目录不再加载，兼容 DLL 等同于不存在（下次启动生效）；
4. 或直接删除 `../1.7/Compat/<目标>/` 整个目录（含 DLL、Def、Patches、语言）；
5. 或把本目录下的 XML 改名/删除，并在设置里关闭 C# 模块；
6. 极端情况下直接停用本 Mod——它不修改存档结构，删除是安全的。

## 注意

- 本 Mod 只支持 1.7；不要把它放进 1.6 的 Mod 列表。
- 不要静态引用目标 Mod 的 DLL；所有跨 Mod 访问都走 `Api/ReflectionCache`。
