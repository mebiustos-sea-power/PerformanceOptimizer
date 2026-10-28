using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace PerformanceOptimizer.Rendering;

internal static class NativeRenderContract
{
    internal static void Validate(params MethodInfo[] methods)
    {
        // Renderer gating depends on the complete native inactive branch and
        // lifecycle, not just a method signature. Re-audit these on game updates.
        var supported = new Guid("45d05d04-62a4-4675-99dd-8be00c74e539");
        if (typeof(FullscreenShaderToggleCycle).Module.ModuleVersionId != supported)
            throw new NotSupportedException("The image-effect routing contract requires Sea Power 0.8.7-261021; this game assembly has not been verified.");
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
