using System;
using Verse;

// 警告：本文件只是 1.7 AutoBlink 的「编译期参考垫片」，方法体全部是占位实现，运行期永远不会被调用。
//
// 背景：1.7（Waylith）的 AutoBlink.dll 面向 .NET 10 编译（引用 System.Runtime 10.0.0.0 等），
// 而本桥接工程按 Mod 规范使用 .NET Framework 4.8.1 旧式工程编译；
// 直接引用真实 AutoBlink.dll 时，csc 会因 System.Runtime 10.0.0.0 高于 4.8.1 引用程序集而报 CS1705。
// 因此这里定义一份「程序集名 AutoBlink、版本 2.0.0.0」的参考垫片，仅提供桥接代码所需的类型与方法签名：
// 桥接程序集编译后引用的是 AutoBlink, Version=2.0.0.0；游戏运行时该引用会解析到 workshop 中的真实
// AutoBlink.dll（程序集名与版本相同），类型与方法签名与真实实现完全一致，因此行为正确。
// 修改本文件时必须与真实 AutoBlink 的 public API 保持同步（类型全名、方法签名）。
namespace AutoBlink
{
    /// <summary>1.7 AutoBlink 观察者接口（垫片声明，签名与真实实现一致）。</summary>
    public interface IAutoBlinkObserver
    {
        void OnBlinked(in BlinkEffectContext context);
    }

    /// <summary>
    /// 1.7 AutoBlink 瞬移效果上下文（真实实现为 readonly record struct）。
    /// 垫片仅声明桥接代码访问的 Pawn / Destination 两个属性；运行时由真实类型的同名 getter 提供。
    /// 注意：真实类型是 struct，垫片必须同样是 struct，否则 `in` 参数访问属性的 IL 会不匹配。
    /// </summary>
    public readonly struct BlinkEffectContext
    {
        public Pawn Pawn => null;

        public IntVec3 Destination => default;
    }

    /// <summary>1.7 AutoBlink 对外 API（垫片声明，签名与真实实现一致）。</summary>
    public static class AutoBlinkApi
    {
        public static IDisposable RegisterObserver(string ownerId, IAutoBlinkObserver observer, int priority = 0)
        {
            return null;
        }

        public static bool TryGetCooldownRemaining(Pawn pawn, out int remainingTicks)
        {
            remainingTicks = 0;
            return false;
        }
    }

    /// <summary>1.7 AutoBlink 的 GameComponent（垫片声明，签名与真实实现一致）。</summary>
    public sealed class AutoBlinkGameComponent : GameComponent
    {
        public AutoBlinkGameComponent(Game game)
        {
        }
    }
}
