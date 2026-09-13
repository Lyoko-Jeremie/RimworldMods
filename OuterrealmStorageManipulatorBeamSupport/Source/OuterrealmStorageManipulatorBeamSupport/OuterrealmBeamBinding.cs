using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;

namespace OuterrealmStorageManipulatorBeamSupport
{
    /// <summary>
    /// 光束边界安装用的反射绑定与 Harmony 辅助。
    ///
    /// 第三方类型只经 AccessTools 字符串解析（TypeByName / GetMethod），运行时调用使用
    /// 表达式树编译委托，不重复反射。`Require` 会同时核对参数名、参数个数、out/ref、
    /// 返回值与静态性——**同名方法存在不代表兼容**。
    /// </summary>
    internal static class OuterrealmBeamBinding
    {
        internal static Type RequiredType(string name) => AccessTools.TypeByName("ManipulatorBeam." + name)
            ?? throw new MissingMemberException(name);

        internal static MethodInfo Require(Type type, string name, bool isStatic, Type result, string[] names, params Type[] types)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, types, null);
            if (method == null || method.ReturnType != result || method.IsStatic != isStatic || method.ContainsGenericParameters)
                throw new MissingMethodException(type.FullName, name);
            ParameterInfo[] args = method.GetParameters();
            for (int i = 0; i < args.Length; i++)
                if (args[i].Name != names[i] || args[i].IsOut != types[i].IsByRef || args[i].IsIn)
                    throw new MissingMethodException(type.FullName, name + " parameter " + names[i]);
            return method;
        }

        internal static Func<object, T> Getter<T>(Type type, string name, bool field)
        {
            ParameterExpression arg = Expression.Parameter(typeof(object));
            Expression member = field ? (Expression)Expression.Field(Expression.Convert(arg, type), name)
                : Expression.Property(Expression.Convert(arg, type), name);
            if (!typeof(T).IsAssignableFrom(member.Type)) throw new MissingMemberException(type.FullName, name);
            return Expression.Lambda<Func<object, T>>(Expression.Convert(member, typeof(T)), arg).Compile();
        }

        internal static Action<object, T> Setter<T>(Type type, string name)
        {
            ParameterExpression arg = Expression.Parameter(typeof(object));
            ParameterExpression value = Expression.Parameter(typeof(T));
            return Expression.Lambda<Action<object, T>>(Expression.Assign(Expression.Field(Expression.Convert(arg, type), name), value), arg, value).Compile();
        }

        internal static T Bind<T>(MethodInfo method) where T : Delegate
        {
            ParameterInfo[] signature = typeof(T).GetMethod("Invoke").GetParameters();
            ParameterInfo[] target = method.GetParameters();
            ParameterExpression[] args = new ParameterExpression[signature.Length];
            Expression[] call = new Expression[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                args[i] = Expression.Parameter(signature[i].ParameterType, signature[i].Name);
                call[i] = signature[i].ParameterType == target[i].ParameterType ? (Expression)args[i] : Expression.Convert(args[i], target[i].ParameterType);
            }
            return Expression.Lambda<T>(Expression.Call(method, call), args).Compile();
        }

        /// <summary>补丁方法一律以 OuterrealmBeamAdapter 为宿主（Prefix/Postfix/Finalizer 都在那里）。</summary>
        internal static void Patch(Harmony harmony, MethodInfo target, string prefix = null, string postfix = null, string finalizer = null)
        {
            harmony.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(OuterrealmBeamAdapter), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(OuterrealmBeamAdapter), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(OuterrealmBeamAdapter), finalizer));
        }
    }
}
