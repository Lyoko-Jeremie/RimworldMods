using System;
using System.Reflection;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>可选兼容只绑定已核实的完整签名；同名方法不代表协议兼容。</summary>
    internal static class OuterrealmOptionalSignature
    {
        internal static MethodInfo Find(Type type, string name, Type result, string[] names, Type[] types)
        {
            if (type == null || result == null || names.Length != types.Length) return null;
            for (int i = 0; i < types.Length; i++) if (types[i] == null) return null;
            MethodInfo found = null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != name || method.ReturnType != result || method.ContainsGenericParameters) continue;
                ParameterInfo[] args = method.GetParameters();
                if (args.Length != types.Length) continue;
                bool match = true;
                for (int i = 0; i < args.Length; i++)
                    if (args[i].Name != names[i] || args[i].ParameterType != types[i]) { match = false; break; }
                if (!match) continue;
                if (found != null) return null;
                found = method;
            }
            return found;
        }
    }
}
