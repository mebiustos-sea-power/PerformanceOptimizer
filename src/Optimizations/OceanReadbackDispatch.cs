using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Ceto;
using Ceto.Common.Unity.Utility;
using HarmonyLib;
using UnityEngine;

namespace PerformanceOptimizer.Optimizations;

internal sealed class OceanReadbackDispatch : IOptimization
{
    public string Id => nameof(OceanReadbackDispatch);
    public void Dispose() { }

    internal static readonly MethodInfo Target = typeof(WaveSpectrum).GetMethod("ReadFromGPU",
        BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(int) }, null)
        ?? throw new MissingMethodException(typeof(WaveSpectrum).FullName, "ReadFromGPU");
    internal static readonly MethodInfo Conversion = typeof(CBUtility).GetMethod(nameof(CBUtility.ReadFromRenderTexture),
        new[] { typeof(RenderTexture), typeof(int), typeof(ComputeBuffer), typeof(ComputeShader) })
        ?? throw new MissingMethodException(typeof(CBUtility).FullName, nameof(CBUtility.ReadFromRenderTexture));
    internal static readonly MethodInfo Guard = typeof(OceanReadbackDispatch).GetMethod(nameof(DispatchIfAvailable),
        BindingFlags.Static | BindingFlags.NonPublic)!;

    public void Install(Harmony patches) =>
        patches.Patch(Target, transpiler: new HarmonyMethod(typeof(OceanReadbackDispatch), nameof(Transpile)));

    internal static void DispatchIfAvailable(RenderTexture texture, int channels, ComputeBuffer destination,
        ComputeShader shader, CetoAsyncGpuBuffer<float> readback)
    {
        // Native tryRequest cannot accept a new request while _requested is true,
        // even when the previous request has completed. Only the later native
        // tryGetData clears it. The next accepted request gets a fresh conversion.
        if (!readback._requested)
            CBUtility.ReadFromRenderTexture(texture, channels, destination, shader);
    }

    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int[] calls = code.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.Calls(Conversion)).Select(item => item.index).ToArray();
        if (calls.Length != 1) throw Incompatible("expected one displacement conversion");
        int call = calls[0];
        if (call < 12 || call + 6 >= code.Count) throw Incompatible("incomplete conversion/request sequence");
        var maps = RequiredField(typeof(WaveSpectrum), "m_displacementMaps", typeof(RenderTexture[]));
        var buffers = RequiredField(typeof(WaveSpectrum), "m_readBuffer", typeof(CetoAsyncGpuBuffer<float>[]));
        var shader = RequiredField(typeof(WaveSpectrum), "readSdr", typeof(ComputeShader));
        var getBuffer = typeof(CetoAsyncGpuBuffer<float>).GetMethod("getComputeBuffer")!;
        var request = typeof(CetoAsyncGpuBuffer<float>).GetMethod("tryRequest")!;
        int index = LocalIndex(code[call - 10]);
        if (index < 0 ||
            code[call - 12].opcode != OpCodes.Ldarg_0 || !code[call - 11].LoadsField(maps) ||
            code[call - 9].opcode != OpCodes.Ldelem_Ref || code[call - 8].opcode != OpCodes.Ldc_I4_3 ||
            code[call - 7].opcode != OpCodes.Ldarg_0 || !code[call - 6].LoadsField(buffers) ||
            LocalIndex(code[call - 5]) != index || code[call - 4].opcode != OpCodes.Ldelem_Ref ||
            !code[call - 3].Calls(getBuffer) || code[call - 2].opcode != OpCodes.Ldarg_0 ||
            !code[call - 1].LoadsField(shader) ||
            code[call + 1].opcode != OpCodes.Ldarg_0 || !code[call + 2].LoadsField(buffers) ||
            LocalIndex(code[call + 3]) != index || code[call + 4].opcode != OpCodes.Ldelem_Ref ||
            !code[call + 5].Calls(request) || code[call + 6].opcode != OpCodes.Pop)
            throw Incompatible("displacement conversion is no longer followed by the same buffer's native request");
        // Branching into this stack-producing sequence or crossing a new exception
        // boundary needs a fresh compatibility review, rather than a partial patch.
        for (int i = call - 11; i <= call + 6; i++)
            if (Metadata(code[i], "labels").Count != 0 || Metadata(code[i], "blocks").Count != 0)
                throw Incompatible("unexpected control-flow boundary");

        // Keep the native arguments, request/collection order, CPU copy and grid
        // clearing intact. Add just the matching readback as the guard's last arg.
        var result = new List<CodeInstruction>(code);
        result[call] = new CodeInstruction(OpCodes.Call, Guard);
        result.InsertRange(call, new[] {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, buffers),
            new CodeInstruction(code[call - 5].opcode, code[call - 5].operand),
            new CodeInstruction(OpCodes.Ldelem_Ref)
        });
        return result;
    }

    private static FieldInfo RequiredField(Type type, string name, Type fieldType)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null || field.FieldType != fieldType) throw Incompatible("missing or changed field " + name);
        return field;
    }

    private static int LocalIndex(CodeInstruction instruction)
    {
        if (instruction.opcode == OpCodes.Ldloc_0) return 0;
        if (instruction.opcode == OpCodes.Ldloc_1) return 1;
        if (instruction.opcode == OpCodes.Ldloc_2) return 2;
        if (instruction.opcode == OpCodes.Ldloc_3) return 3;
        if (instruction.opcode != OpCodes.Ldloc && instruction.opcode != OpCodes.Ldloc_S) return -1;
        if (instruction.operand is LocalBuilder builder) return builder.LocalIndex;
        if (instruction.operand is LocalVariableInfo local) return local.LocalIndex;
        return Convert.ToInt32(instruction.operand);
    }

    // The installed Harmony exposes Mono's Label through mscorlib, so use IList
    // rather than binding its label collection to the netstandard Label facade.
    private static IList Metadata(CodeInstruction instruction, string name) =>
        (IList)typeof(CodeInstruction).GetField(name)!.GetValue(instruction)!;
    private static InvalidOperationException Incompatible(string reason) =>
        new InvalidOperationException("OceanReadbackDispatch is incompatible with this game/patch layout: " + reason + ".");
}
