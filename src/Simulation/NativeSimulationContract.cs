using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;

namespace PerformanceOptimizer.Simulation;

internal static class NativeSimulationContract
{
    internal static MethodInfo Required(Type type, string name, params Type[] arguments) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, arguments, null) ?? throw new MissingMethodException(type.FullName, name);

    internal static void Validate(params MethodInfo[] methods)
    {
        if (typeof(ObjectBase).Module.ModuleVersionId != new Guid("45d05d04-62a4-4675-99dd-8be00c74e539"))
            throw new NotSupportedException("Simulation allocation optimizations require the verified Sea Power 0.8.7-261021 assembly.");
        foreach (var method in methods)
            if (Harmony.GetPatchInfo(method)?.Owners.Any() == true)
                throw new InvalidOperationException("Another patch already owns the simulation method: " + method);
    }

    internal static List<CodeInstruction> Copy(IEnumerable<CodeInstruction> code) =>
        code.Select(i => new CodeInstruction(i)).ToList();

    internal static void Unbranched(CodeInstruction instruction)
    {
        // The installed Harmony exposes Mono's Label type; inspect metadata through IList.
        foreach (string name in new[] { "labels", "blocks" })
            if (((IList)typeof(CodeInstruction).GetField(name)!.GetValue(instruction)!).Count != 0)
                throw Incompatible("unexpected control-flow boundary");
    }

    internal static void NoExceptionBoundary(CodeInstruction instruction)
    {
        if (((IList)typeof(CodeInstruction).GetField("blocks")!.GetValue(instruction)!).Count != 0)
            throw Incompatible("unexpected exception boundary at a collection allocation");
    }

    internal static CodeInstruction OwnerArgument(CodeInstruction original)
    {
        NoExceptionBoundary(original);
        var argument = new CodeInstruction(OpCodes.Ldarg_0);
        var source = (IList)typeof(CodeInstruction).GetField("labels")!.GetValue(original)!;
        var destination = (IList)typeof(CodeInstruction).GetField("labels")!.GetValue(argument)!;
        foreach (object label in source) destination.Add(label);
        source.Clear();
        return argument;
    }

    internal static int Local(CodeInstruction i)
    {
        if (i.opcode == OpCodes.Ldloc_0 || i.opcode == OpCodes.Stloc_0) return 0;
        if (i.opcode == OpCodes.Ldloc_1 || i.opcode == OpCodes.Stloc_1) return 1;
        if (i.opcode == OpCodes.Ldloc_2 || i.opcode == OpCodes.Stloc_2) return 2;
        if (i.opcode == OpCodes.Ldloc_3 || i.opcode == OpCodes.Stloc_3) return 3;
        if (i.opcode != OpCodes.Ldloc && i.opcode != OpCodes.Ldloc_S && i.opcode != OpCodes.Ldloca &&
            i.opcode != OpCodes.Ldloca_S && i.opcode != OpCodes.Stloc && i.opcode != OpCodes.Stloc_S) return -1;
        if (i.operand is LocalBuilder builder) return builder.LocalIndex;
        if (i.operand is LocalVariableInfo local) return local.LocalIndex;
        return Convert.ToInt32(i.operand);
    }

    internal static bool LoadsLocal(CodeInstruction i) => i.opcode.Name!.StartsWith("ldloc", StringComparison.Ordinal);
    internal static InvalidOperationException Incompatible(string reason) =>
        new InvalidOperationException("Simulation optimization rejected an incompatible IL layout: " + reason + ".");
}
