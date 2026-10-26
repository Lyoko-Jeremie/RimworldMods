using System;
using System.Reflection;
using System.Runtime.InteropServices;

// 编译期参考垫片（Reference Shim）程序集：程序集名与版本刻意与真实 AutoBlink.dll 保持一致（AutoBlink, 2.0.0.0）。
// 它只声明 1.7 AutoBlink 中被桥接程序集使用的 public API 形状，不包含任何实现；
// 运行时游戏加载的是 workshop 中的真实 AutoBlink.dll，桥接程序集的 AssemblyRef 会自动绑定到它。
[assembly: AssemblyTitle("AutoBlink.ReferenceShim")]
[assembly: AssemblyDescription("1.7 AutoBlink 编译期参考垫片（仅用于编译，不分发、不参与运行）")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("AutoBlink.ReferenceShim")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: Guid("FA75C050-A102-4F98-87B0-7090EFF54B28")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
