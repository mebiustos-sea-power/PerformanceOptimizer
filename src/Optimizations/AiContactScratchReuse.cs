using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PerformanceOptimizer.Simulation;
using SeaPower;

namespace PerformanceOptimizer.Optimizations;

internal sealed class AiContactScratchReuse : IOptimization
{
    public string Id => nameof(AiContactScratchReuse);
    internal static readonly MethodInfo Target = NativeSimulationContract.Required(typeof(AI), "ProcessContacts");
    private static readonly ScratchPool scratch = new();

    public void Install(Harmony patches)
    {
        NativeSimulationContract.Validate(Target);
        patches.Patch(Target, prefix: new HarmonyMethod(typeof(AiContactScratchReuse), nameof(Begin)),
            transpiler: new HarmonyMethod(typeof(AiContactScratchReuse), nameof(Transpile)),
            finalizer: new HarmonyMethod(typeof(AiContactScratchReuse), nameof(Finish)));
    }

    public void Dispose() => scratch.Clear();
    private static void Begin(object __instance, out ScratchPool.Lease __state) => __state = scratch.Enter(__instance);
    private static void Finish(ScratchPool.Lease __state) => __state.Dispose();
    private static HashSet<T> Set<T>(object owner) => scratch.Current(owner).Set<T>();
    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> code) =>
        CollectionRewrite.Constructors(code, typeof(HashSet<>), new[] { typeof(ObjectBase) }, System.Type.EmptyTypes,
            typeof(AiContactScratchReuse).GetMethod(nameof(Set), BindingFlags.Static | BindingFlags.NonPublic)!);
}
