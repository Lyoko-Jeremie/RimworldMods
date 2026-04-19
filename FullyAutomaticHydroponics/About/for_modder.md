
为了解决某些mod会篡改原版 水栽培植物盆 HydroponicsBasin 导致此 mod 无效的问题，使用dll在xml patch之后再进行一次检查
 

当前dll注入逻辑的等价xml实现
 
```xml
<Operation Class="PatchOperationAdd">
    <xpath>Defs/ThingDef[thingClass="Building_PlantGrower"]/comps</xpath>
    <success>Always</success>
    <value>
        <li Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics">
            <defaultAutoHarvest>false</defaultAutoHarvest>
            <defaultAutoSow>false</defaultAutoSow>
        </li>
    </value>
</Operation>

<Operation Class="PatchOperationAdd">
<xpath>Defs/ThingDef[thingClass="Building_PlantGrower"][not(comps)]</xpath>
<success>Always</success>
<value>
 <comps>
  <li Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics">
   <defaultAutoHarvest>false</defaultAutoHarvest>
   <defaultAutoSow>false</defaultAutoSow>
  </li>
 </comps>
</value>
</Operation>

<Operation Class="PatchOperationConditional">
<xpath>
    Defs/ThingDef[defName="HydroponicsBasin"]/comps/li[@Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics"]
</xpath>

<nomatch Class="PatchOperationAdd">
    <xpath>Defs/ThingDef[defName="HydroponicsBasin"]/comps</xpath>
    <value>
        <li Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics">
            <defaultAutoHarvest>false</defaultAutoHarvest>
            <defaultAutoSow>false</defaultAutoSow>
        </li>
    </value>
</nomatch>
</Operation>
```

手动注入请使用以下方法
```xml

<comps>
    <li Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics">
        <defaultAutoHarvest>false</defaultAutoHarvest>
        <defaultAutoSow>false</defaultAutoSow>
    </li>
</comps>

```

请采用如下代码来阻止自动添加标签
```xml

<comps>
    <li Class="FullyAutoHydroponicsThingComp.CompProperties_No_FullyAutoHydroponics"/>
</comps>
``` 