
当前dll注入逻辑的等价xml实现
 
```xml
<Operation Class="PatchOperationAdd">
    <xpath>Defs/ThingDef[thingClass="Building_PlantGrower"]/comps</xpath>
    <success>Always</success>
    <value>
        <li Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics">
            <defaultAutoHarvest>false</defaultAutoHarvest>
            <defaultAutoSow>false</defaultAutoSow>
            <defaultAutoStore>false</defaultAutoStore>
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
   <defaultAutoStore>false</defaultAutoStore>
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
            <defaultAutoStore>false</defaultAutoStore>
        </li>
    </value>
</nomatch>
</Operation>
```

请使用以下方法手动添加标签
```xml

<comps>
    <li Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics">
        <defaultAutoHarvest>false</defaultAutoHarvest>
        <defaultAutoSow>false</defaultAutoSow>
        <defaultAutoStore>false</defaultAutoStore>
    </li>
</comps>

```

请采用如下代码来阻止注入
```xml

<comps>
    <li Class="FullyAutoHydroponicsThingComp.CompProperties_No_FullyAutoHydroponics"/>
</comps>
``` 
