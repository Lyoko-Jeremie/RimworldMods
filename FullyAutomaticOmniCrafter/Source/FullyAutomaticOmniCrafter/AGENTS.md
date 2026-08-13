# AGENTS.md

***Follow Content Must Read With UTF-8 Encoding***

## 项目说明
这是一个 Rimworld 1.6 的 Mod 项目

## 开发指南

使用 rimsage MCP 工具查询游戏原始代码，以便更好地理解游戏逻辑，并确认对应函数是否存在。

i18n语言目录在 `../../Languages` ，源代码中需要参与显示的硬编码字符串需要添加到此i18n目录，以支持多语言。

所有的Def在 `../../Defs` 目录下，Def是Rimworld中定义游戏元素的方式，如物品、建筑、角色等。可以在这里添加新的Def来引入当前Mod所添加的游戏元素。

当前环境是 PowerShell 。

所有源码内容使用 UTF-8 编码

当前操作系统上有 ripgrep (rg) ， 可以使用 `rg` 命令进行文件内容搜索

使用 `dotnet build -c Debug` 在完成所有修改后进行编译以确认修改正确。

如果新增了 `.cs` 源码，不要忘记在 `.csproj` 文件中添加引用。

可以使用 PowerShell 工具以 UTF-8 编码来操作 Defs 文件。

## 注意事项

### 禁止事项

- 不允许访问 `../../..` 目录
- 只能修改 `../..` 目录内的内容

### 性能问题
注意优化频繁调用的函数和被频繁执行的代码的性能问题：
1. 避免频繁分配内存
2. 采用性能更好的算法
3. 避免频繁执行反射操作

减少使用Linq等会分配琐碎内存的方法，尽可能使用for循环等替代。
避免使用transpiler直接对游戏打补丁，保证兼容性。


## 多线程问题

由于 Rimworld 1.6 采用了多线程架构，在寻路和渲染框架中都使用了多线程，因此在使用静态变量时需要注意数据竞争问题。

## 代码规范

### 注释
注释以中文编写

### 图片材质需要预加载
```csharp
    [StaticConstructorOnStartup]
    public static class XXXXXXXXTex
    {
        public static readonly Texture2D IconXxxxxxx =
            ContentFinder<Texture2D>.Get("UI/Commands/Xxxxxxxx", false) ?? BaseContent.WhiteTex;
    }
```

## 此项目的专有词汇
- 超维科技 (OuterrealmTech)
