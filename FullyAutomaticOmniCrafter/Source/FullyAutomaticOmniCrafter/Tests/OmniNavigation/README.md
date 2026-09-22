# 代理导航回归测试

运行 `dotnet run --project Tests/OmniNavigation/OmniNavigation.csproj -c Release`。

测试直接编译生产导航、失败缓存及补丁，链接本地 .NET 10 版 Harmony；游戏工程继续使用
.NET Framework 版 Harmony。GameDoubles / PatchDoubles 仅提供地图、Job 和 pather 替身。
替身中的原版移动入口故意抛异常，证明代理流程不会落回普通区域检查或异步寻路。

覆盖范围与实际游戏验收清单见 `../../Docs/OmniWorkstationNavigation.md`。
这些测试不能证明游戏原程序集的 Harmony 补丁排序或真实存档运行正确。
