# 个人温度回归检查

运行：`dotnet run --project Tests/PersonalTemperature/PersonalTemperature.csproj -c Release`。

测试直接链接生产 `CompPersonalTemperature.cs` 和 `Patch_PersonalTemperature.cs`，通过精简游戏替身在无 Unity 运行时环境验证行为。替身存档仅检查保存字段与回读行为，不模拟完整游戏的引用解析。真实游戏界面、Harmony 安装及读档检查见 `Docs/PersonalTemperature.md`。
