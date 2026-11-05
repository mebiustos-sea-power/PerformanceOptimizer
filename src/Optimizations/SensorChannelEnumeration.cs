using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PerformanceOptimizer.Simulation;
using SeaPower;

namespace PerformanceOptimizer.Optimizations;

internal sealed class SensorChannelEnumeration : IOptimization
{
    public string Id => nameof(SensorChannelEnumeration);
    internal static readonly MethodInfo Jam = NativeSimulationContract.Required(typeof(ObjectBase), "UpdateNumberOfFreeJamChannels");
    internal static readonly MethodInfo Laser = NativeSimulationContract.Required(typeof(ObjectBase), "UpdateNumberOfFreeLaserChannels");
    public void Dispose() { }

    public void Install(Harmony patches)
    {
        NativeSimulationContract.Validate(Jam, Laser);
        // Resolve the runtime's backing-storage/version contract before patching.
        var probe = VersionedCursor<SensorSystem>.Begin(new Collection<SensorSystem>());
        VersionedCursor<SensorSystem>.Dispose(ref probe);
        foreach (var method in new[] { Jam, Laser })
            patches.Patch(method, transpiler: new HarmonyMethod(typeof(SensorChannelEnumeration), nameof(Transpile)));
    }

    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator) =>
        Rewrite<SensorSystem>(instructions, generator);

    internal static IEnumerable<CodeInstruction> Rewrite<T>(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = NativeSimulationContract.Copy(instructions);
        var get = typeof(Collection<T>).GetMethod("GetEnumerator")!;
        int[] sites = code.Select((i, n) => (i, n)).Where(p => p.i.Calls(get)).Select(p => p.n).ToArray();
        if (sites.Length != 1) throw NativeSimulationContract.Incompatible("sensor enumeration count changed");
        int site = sites[0];
        if (site + 1 >= code.Count || !code[site + 1].opcode.Name!.StartsWith("stloc", StringComparison.Ordinal))
            throw NativeSimulationContract.Incompatible("sensor enumerator is not stored locally");
        int local = NativeSimulationContract.Local(code[site + 1]);
        var current = typeof(IEnumerator<T>).GetProperty("Current")!.GetGetMethod()!;
        var move = typeof(IEnumerator).GetMethod("MoveNext")!;
        var dispose = typeof(IDisposable).GetMethod("Dispose")!;
        int reads = 0, moves = 0, disposals = 0, guards = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (NativeSimulationContract.Local(code[i]) != local || i == site + 1) continue;
            if (!NativeSimulationContract.LoadsLocal(code[i]) || i + 1 >= code.Count)
                throw NativeSimulationContract.Incompatible("unexpected sensor enumerator use");
            var next = code[i + 1];
            if (next.Calls(current)) reads++;
            else if (next.Calls(move)) moves++;
            else if (next.Calls(dispose)) disposals++;
            else if (next.opcode == OpCodes.Brfalse || next.opcode == OpCodes.Brfalse_S) guards++;
            else throw NativeSimulationContract.Incompatible("unexpected sensor enumerator consumer");
        }
        if (reads != 1 || moves != 1 || disposals != 1 || guards != 1)
            throw NativeSimulationContract.Incompatible("sensor loop control flow changed");
        var cursor = generator.DeclareLocal(typeof(VersionedCursor<T>));
        MethodInfo Cursor(string name) => typeof(VersionedCursor<T>).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        code[site].opcode = OpCodes.Call; code[site].operand = Cursor("Begin");
        code[site + 1].opcode = OpCodes.Stloc; code[site + 1].operand = cursor;
        for (int i = 0; i < code.Count; i++)
        {
            if (i == site + 1 || NativeSimulationContract.Local(code[i]) != local) continue;
            var next = code[i + 1];
            if (next.opcode == OpCodes.Brfalse || next.opcode == OpCodes.Brfalse_S)
            { code[i].opcode = OpCodes.Ldc_I4_1; code[i].operand = null; }
            else
            {
                code[i].opcode = OpCodes.Ldloca; code[i].operand = cursor;
                next.opcode = OpCodes.Call;
                next.operand = Cursor(next.Calls(current) ? "Current" : next.Calls(move) ? "MoveNext" : "Dispose");
            }
        }
        return code;
    }
}
