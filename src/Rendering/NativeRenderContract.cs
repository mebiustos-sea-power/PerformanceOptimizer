using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace PerformanceOptimizer.Rendering;

internal static class NativeRenderContract
{
    internal static void Validate(params MethodInfo[] methods)
    {
        foreach (MethodInfo method in methods)
        {
            var patches = Harmony.GetPatchInfo(method);
            if (patches != null && patches.Owners.Any())
                throw new InvalidOperationException("Another patch already owns the image-effect callback: " + method.DeclaringType!.FullName + "." + method.Name);
        }
    }

    internal static MethodInfo Required(Type type, string name, params Type[] arguments) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, arguments, null) ?? throw new MissingMethodException(type.FullName, name);

    internal static Action<T> Callback<T>(string name) =>
        (Action<T>)Required(typeof(T), name).CreateDelegate(typeof(Action<T>));
}
