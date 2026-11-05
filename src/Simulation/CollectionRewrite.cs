using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PerformanceOptimizer.Simulation;

internal static class CollectionRewrite
{
    internal static IEnumerable<CodeInstruction> Constructors(IEnumerable<CodeInstruction> instructions,
        Type collection, Type[] elements, Type[] arguments, MethodInfo factory)
    {
        var code = NativeSimulationContract.Copy(instructions);
        var sites = code.Select((i, n) => (i, n)).Where(p => p.i.opcode == OpCodes.Newobj &&
            p.i.operand is ConstructorInfo c && c.DeclaringType!.IsGenericType &&
            c.DeclaringType.GetGenericTypeDefinition() == collection).ToArray();
        if (sites.Length != elements.Length) throw NativeSimulationContract.Incompatible("collection constructor count changed");
        for (int j = 0; j < sites.Length; j++)
        {
            var site = sites[j];
            var ctor = (ConstructorInfo)site.i.operand;
            if (ctor.DeclaringType!.GetGenericArguments()[0] != elements[j] ||
                !ctor.GetParameters().Select(p => p.ParameterType).SequenceEqual(arguments))
                throw NativeSimulationContract.Incompatible("collection constructor signature/order changed");
            NativeSimulationContract.NoExceptionBoundary(site.i);
        }
        for (int j = sites.Length - 1; j >= 0; j--)
        {
            int at = sites[j].n;
            var owner = NativeSimulationContract.OwnerArgument(code[at]);
            code[at] = new CodeInstruction(OpCodes.Call, factory.MakeGenericMethod(elements[j]));
            code.Insert(at, owner);
        }
        return code;
    }

    internal static IEnumerable<CodeInstruction> Snapshot(IEnumerable<CodeInstruction> instructions, MethodInfo factory)
    {
        var code = NativeSimulationContract.Copy(instructions);
        int[] sites = code.Select((i, n) => (i, n)).Where(p => p.i.operand is MethodInfo m &&
            m.IsGenericMethod && m.DeclaringType == typeof(Enumerable) && m.Name == nameof(Enumerable.ToList))
            .Select(p => p.n).ToArray();
        if (sites.Length != 1) throw NativeSimulationContract.Incompatible("dictionary snapshot count changed");
        int at = sites[0];
        var owner = NativeSimulationContract.OwnerArgument(code[at]);
        var method = (MethodInfo)code[at].operand;
        code[at] = new CodeInstruction(OpCodes.Call, factory.MakeGenericMethod(method.GetGenericArguments()));
        code.Insert(at, owner);
        return code;
    }
}
