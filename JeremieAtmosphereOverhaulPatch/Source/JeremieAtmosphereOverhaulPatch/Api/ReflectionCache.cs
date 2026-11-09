using System;
using System.Collections.Generic;
using System.Reflection;

namespace Jeremie.AtmosphereOverhaulPatch.Api
{
    /// <summary>
    /// 轻量反射缓存。
    /// 本 Mod 不静态引用任何目标 Mod 的程序集，所有类型/成员都按名称查找；
    /// 查找结果缓存，避免每帧反射。
    /// </summary>
    internal static class ReflectionCache
    {
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();
        private static readonly object Gate = new object();

        /// <summary>按完整名查找类型；找不到返回 null（不抛异常）。</summary>
        public static Type Type(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return null;
            }

            lock (Gate)
            {
                Type cached;
                if (Types.TryGetValue(fullName, out cached))
                {
                    return cached;
                }

                Type found = null;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    try
                    {
                        found = assemblies[i].GetType(fullName, false);
                    }
                    catch
                    {
                        found = null;
                    }

                    if (found != null)
                    {
                        break;
                    }
                }

                Types[fullName] = found;
                return found;
            }
        }

        /// <summary>按名称与参数个数查找方法；parameterCount 为 -1 表示不限制。</summary>
        public static MethodInfo Method(Type type, string name, int parameterCount = -1)
        {
            if (type == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            try
            {
                MethodInfo[] all = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                MethodInfo fallback = null;
                for (int i = 0; i < all.Length; i++)
                {
                    MethodInfo method = all[i];
                    if (method.Name != name)
                    {
                        continue;
                    }

                    if (parameterCount >= 0 && method.GetParameters().Length != parameterCount)
                    {
                        continue;
                    }

                    if (method.IsStatic)
                    {
                        return method;
                    }

                    if (fallback == null)
                    {
                        fallback = method;
                    }
                }

                return fallback;
            }
            catch
            {
                return null;
            }
        }

        public static FieldInfo Field(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            try
            {
                return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            }
            catch
            {
                return null;
            }
        }

        public static PropertyInfo Property(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            try
            {
                return type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>取字段值；失败返回 null。</summary>
        public static object GetFieldValue(FieldInfo field, object instance)
        {
            if (field == null)
            {
                return null;
            }

            try
            {
                return field.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }
    }
}
