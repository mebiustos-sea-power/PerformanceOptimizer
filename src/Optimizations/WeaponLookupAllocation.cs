using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PerformanceOptimizer.Simulation;
using SeaPower;

namespace PerformanceOptimizer.Optimizations;

internal sealed class WeaponLookupAllocation : IOptimization
{
    public string Id => nameof(WeaponLookupAllocation);
    internal static readonly MethodInfo Target = NativeSimulationContract.Required(typeof(ObjectBase), "GetWeaponSystemsForAmmunition", typeof(string), typeof(bool));
    public void Dispose() { }
    public void Install(Harmony patches)
    {
        NativeSimulationContract.Validate(Target);
        patches.Patch(Target, transpiler: new HarmonyMethod(typeof(WeaponLookupAllocation), nameof(Transpile)));
    }

    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = NativeSimulationContract.Copy(instructions);
        var ctor = typeof(List<WeaponSystem>).GetConstructor(Type.EmptyTypes)!;
        var query = typeof(Dictionary<string, List<WeaponSystem>>).GetMethod("TryGetValue")!;
        if (code.Count < 9 || code[0].opcode != OpCodes.Newobj || !Equals(code[0].operand, ctor) ||
            NativeSimulationContract.Local(code[1]) != 0 || code[1].opcode != OpCodes.Stloc_0 ||
            code[2].opcode != OpCodes.Ldarg_0 || code[3].opcode != OpCodes.Ldfld ||
            ((FieldInfo)code[3].operand).Name != "_ammunitionToWeaponSystemsDictionary" ||
            code[4].opcode != OpCodes.Ldarg_1 || code[5].opcode != OpCodes.Ldloca_S ||
            NativeSimulationContract.Local(code[5]) != 0 || !code[6].Calls(query))
            throw NativeSimulationContract.Incompatible("weapon lookup's overwritten initial list changed");
        for (int i = 1; i <= 6; i++) NativeSimulationContract.Unbranched(code[i]);
        // TryGetValue overwrites the out local on both success and failure.
        // Keep its native result, missing-key behavior and optional copy intact.
        code[0].opcode = OpCodes.Ldnull;
        code[0].operand = null;
        return code;
    }
}
