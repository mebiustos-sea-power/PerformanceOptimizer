using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PerformanceOptimizer.Simulation;
using SeaPower;

namespace PerformanceOptimizer.Optimizations;

internal sealed class FormationStationLookup : IOptimization
{
    public string Id => nameof(FormationStationLookup);
    internal static readonly MethodInfo Update = NativeSimulationContract.Required(typeof(ObjectBase), "OnLazyUpdate");
    internal static readonly MethodInfo Following = NativeSimulationContract.Required(typeof(ObjectBase), "get_FollowingFormation");
    internal static readonly MethodInfo Lookup = NativeSimulationContract.Required(typeof(UnitFormation), "GetStationForUnit", typeof(ObjectBase));
    internal static readonly MethodInfo Formation = NativeSimulationContract.Required(typeof(ObjectBase), "get_Formation");
    public void Dispose() { }

    public void Install(Harmony patches)
    {
        NativeSimulationContract.Validate(Update, Following);
        foreach (var method in new[] { Update, Following })
            patches.Patch(method, transpiler: new HarmonyMethod(typeof(FormationStationLookup), nameof(Transpile)));
    }

    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = NativeSimulationContract.Copy(instructions);
        int[] sites = code.Select((i, n) => (i, n)).Where(p => p.i.Calls(Lookup)).Select(p => p.n).ToArray();
        if (sites.Length != 2 || sites[1] != sites[0] + 5)
            throw NativeSimulationContract.Incompatible("adjacent formation lookups changed");
        int first = sites[0], second = sites[1];
        if ((code[first + 1].opcode != OpCodes.Brfalse && code[first + 1].opcode != OpCodes.Brfalse_S) ||
            code[second - 3].opcode != OpCodes.Ldarg_0 || !code[second - 2].Calls(Formation) ||
            code[second - 1].opcode != OpCodes.Ldarg_0)
            throw NativeSimulationContract.Incompatible("formation lookup inputs/guard changed");
        for (int i = first + 1; i <= second; i++) NativeSimulationContract.Unbranched(code[i]);
        var station = generator.DeclareLocal(typeof(Station));
        code[second - 3] = new CodeInstruction(OpCodes.Ldloc, station);
        for (int i = second - 2; i <= second; i++) code[i] = new CodeInstruction(OpCodes.Nop);
        // The local lasts only for this expression. Station ordering, Unity null
        // comparisons and a later frame's formation changes remain native.
        code.InsertRange(first + 1, new[] { new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Stloc, station) });
        return code;
    }
}
