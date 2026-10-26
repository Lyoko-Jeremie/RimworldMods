using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace OuterrealmTechRobot.AutoBlink17
{
    /// <summary>
    /// 1.7 版 AutoBlink 桥接实现。
    /// 1.7 的 AutoBlink 已完全重写：旧版 CompAutoBlink 只剩空壳，ExecuteBlink / BlinkToCellDirect 已被删除，
    /// 新架构为 BlinkController + RimWorldMovementAdapter.Teleport + AutoBlinkApi（含 observer 钩子）。
    /// 因此本桥接按以下方式对接：
    /// 1) 被动钩子：AutoBlinkApi.RegisterObserver 注册携带同步 observer，瞬移成功后回调；
    /// 2) 冷却查询：直接调用 AutoBlinkApi.TryGetCooldownRemaining；
    /// 3) 主动瞬移：1.7 没有公开的手动瞬移 API，只能反射调用 AutoBlinkGameComponent.TryGet（internal）
    ///    取得 BlinkController，再调用其 public 的 CanManualBlink / ManualBlink。
    /// 注意：BlinkController 在 AutoBlink.dll 中是 internal 类型，桥接程序集无法静态引用，必须反射；
    /// 反射结果（MethodInfo）在首次使用时解析一次并缓存，运行期不再查找，避免频繁反射开销。
    /// </summary>
    public sealed class AutoBlinkBridge17 : OuterrealmTechRobot.IAutoBlinkBridge
    {
        /// <summary>observer 所有者标识：用于 AutoBlink 的日志与故障隔离，需保持全局唯一。</summary>
        private const string ObserverOwnerId = "Jeremie.Outerrealm.Tech.ArtificialMaid";

        /// <summary>observer 注册句柄，静态保存防止被回收导致观察者失效。</summary>
        private static IDisposable observerRegistration;

        /// <summary>以下三个 MethodInfo 是反射缓存，只在首次使用时解析一次。</summary>
        private static bool reflectionCacheInitialized;
        private static bool reflectionCacheReady;
        private static MethodInfo tryGetControllerMethod;
        private static MethodInfo canManualBlinkMethod;
        private static MethodInfo manualBlinkMethod;

        /// <summary>
        /// 初始化桥接：注册 1.7 AutoBlink observer。
        /// 1.7 不再需要 Harmony patch（旧版 ExecuteBlink / BlinkToCellDirect 已删除），
        /// 因此 harmony 参数保留但未使用，仅用于满足主工程 IAutoBlinkBridge 接口。
        /// </summary>
        public bool Initialize(Harmony harmony)
        {
            try
            {
                EnsureReflectionCache();

                if (observerRegistration != null)
                {
                    // 已注册过（例如读档重进），直接复用。
                    return true;
                }

                observerRegistration = AutoBlink.AutoBlinkApi.RegisterObserver(
                    ObserverOwnerId, new AutoBlinkCarrySyncObserver(), 0);

                if (observerRegistration == null)
                {
                    Log.Error("[OuterrealmTechRobot] 1.7 AutoBlink observer 注册返回空句柄，携带同步功能禁用。");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                observerRegistration = null;
                Log.Error("[OuterrealmTechRobot] 1.7 AutoBlink observer 注册失败，携带同步功能禁用: " + ex);
                return false;
            }
        }

        /// <summary>查询 pawn 的 AutoBlink 剩余冷却；pawn 没有 AutoBlink 能力时返回 false。</summary>
        public bool TryGetCooldownRemaining(Pawn pawn, out int remainingTicks)
        {
            remainingTicks = 0;
            if (pawn == null)
            {
                return false;
            }

            try
            {
                return AutoBlink.AutoBlinkApi.TryGetCooldownRemaining(pawn, out remainingTicks);
            }
            catch (Exception ex)
            {
                // AutoBlink API 本身很薄，正常不会抛异常；这里只做防御，避免拖垮调用方。
                remainingTicks = 0;
                Log.WarningOnce(
                    "[OuterrealmTechRobot] 查询 1.7 AutoBlink 冷却失败（已忽略）: " + ex, 85321002);
                return false;
            }
        }

        /// <summary>
        /// 尝试让 pawn 瞬移到 cell（是否允许由 AutoBlink 的 BlinkController 自行判断冷却/可达/成本）。
        /// 返回 true 表示 AutoBlink 已实际执行瞬移。
        /// </summary>
        public bool TryBlinkToCell(Pawn pawn, IntVec3 cell)
        {
            if (pawn == null || pawn.Destroyed || pawn.Map == null || !cell.IsValid)
            {
                return false;
            }

            EnsureReflectionCache();
            if (!reflectionCacheReady)
            {
                // 反射缓存不可用（日志已在 EnsureReflectionCache 中输出一次），直接放弃本次请求。
                return false;
            }

            try
            {
                Game game = Current.Game;
                if (game == null)
                {
                    return false;
                }

                // AutoBlinkGameComponent 是 public 的 GameComponent，可直接按泛型取得。
                AutoBlink.AutoBlinkGameComponent component =
                    game.GetComponent<AutoBlink.AutoBlinkGameComponent>();
                if (component == null)
                {
                    return false;
                }

                // 反射调用 internal bool TryGet(Pawn pawn, out BlinkController controller)。
                // out 参数在 Invoke 后写回 args[1]；controller 的真实类型是 internal 的 BlinkController。
                object[] tryGetArgs = { pawn, null };
                if (!(bool)tryGetControllerMethod.Invoke(component, tryGetArgs))
                {
                    return false;
                }

                object controller = tryGetArgs[1];
                if (controller == null)
                {
                    return false;
                }

                object[] cellArgs = { cell };
                if (!(bool)canManualBlinkMethod.Invoke(controller, cellArgs))
                {
                    return false;
                }

                return (bool)manualBlinkMethod.Invoke(controller, cellArgs);
            }
            catch (Exception ex)
            {
                // 反射链路（internal 方法/类型）一旦因 AutoBlink 更新而失效，只记录一次并返回 false，
                // 不能把异常抛回主工程，避免影响女仆其他逻辑。
                Log.WarningOnce(
                    "[OuterrealmTechRobot] 1.7 AutoBlink 主动瞬移反射调用失败（已忽略本次请求）: " + ex, 85321003);
                return false;
            }
        }

        /// <summary>
        /// 解析并缓存反射信息（幂等）。AutoBlinkGameComponent.TryGet 是 internal，但所在类型是 public；
        /// 它唯一的 out 参数类型就是 internal 的 BlinkController，从该参数类型上再取 public 的
        /// CanManualBlink / ManualBlink，即可在不引用 internal 类型的前提下完成调用。
        /// </summary>
        private static void EnsureReflectionCache()
        {
            if (reflectionCacheInitialized)
            {
                return;
            }

            reflectionCacheInitialized = true;
            try
            {
                tryGetControllerMethod = typeof(AutoBlink.AutoBlinkGameComponent).GetMethod(
                    "TryGet", BindingFlags.Instance | BindingFlags.NonPublic);
                if (tryGetControllerMethod == null)
                {
                    Log.Error("[OuterrealmTechRobot] 未找到 AutoBlinkGameComponent.TryGet，1.7 AutoBlink 主动瞬移功能禁用。");
                    return;
                }

                ParameterInfo[] parameters = tryGetControllerMethod.GetParameters();
                if (parameters.Length != 2 || parameters[0].ParameterType != typeof(Pawn) || !parameters[1].IsOut)
                {
                    Log.Error("[OuterrealmTechRobot] AutoBlinkGameComponent.TryGet 签名与预期不符，1.7 AutoBlink 主动瞬移功能禁用。");
                    tryGetControllerMethod = null;
                    return;
                }

                Type controllerType = parameters[1].ParameterType;
                // out 参数在反射元数据里是 byref 类型（BlinkController&），必须取元素类型才能继续找方法。
                if (controllerType.IsByRef)
                {
                    controllerType = controllerType.GetElementType();
                }

                if (controllerType == null)
                {
                    Log.Error("[OuterrealmTechRobot] 无法解析 AutoBlinkGameComponent.TryGet 的 out 参数类型，1.7 AutoBlink 主动瞬移功能禁用。");
                    tryGetControllerMethod = null;
                    return;
                }

                canManualBlinkMethod = controllerType.GetMethod(
                    "CanManualBlink", BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(IntVec3) }, null);
                manualBlinkMethod = controllerType.GetMethod(
                    "ManualBlink", BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(IntVec3) }, null);

                if (canManualBlinkMethod == null || manualBlinkMethod == null)
                {
                    Log.Error("[OuterrealmTechRobot] 未找到 BlinkController.CanManualBlink / ManualBlink，1.7 AutoBlink 主动瞬移功能禁用。");
                    return;
                }

                reflectionCacheReady = true;
            }
            catch (Exception ex)
            {
                reflectionCacheReady = false;
                Log.Error("[OuterrealmTechRobot] 解析 1.7 AutoBlink 反射信息失败，主动瞬移功能禁用: " + ex);
            }
        }
    }
}
