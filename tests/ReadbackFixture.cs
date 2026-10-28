using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using Ceto;
using Ceto.Common.Containers.Interpolation;
using HarmonyLib;
using PerformanceOptimizer.Optimizations;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// Execute the actual ReadFromGPU, tryRequest, tryGetData and dispatch guard IL.
// Only engine/GPU boundaries are replaced. No Unity object constructor runs.
internal sealed class ReadbackFixture
{
    internal sealed class State
    {
        internal CetoAsyncGpuBuffer<float> Buffer = null!;
        internal bool Active, Done, Error, Immediate;
        internal int Requests, Dispatches, Copies;
        internal float Converted, Captured;
    }

    internal sealed class ArraySource
    {
        internal InterpolatedArray2f[] Arrays = null!;
        public InterpolatedArray2f[] GetReadDisplacements() => Arrays;
    }

    internal sealed class Scene
    {
        // Names/types match the actual fields consumed by ReadFromGPU.
        public bool disableReadBack;
        public ComputeShader readSdr = Uninitialized<ComputeShader>();
        public RenderTexture[] m_displacementMaps = { Uninitialized<RenderTexture>(), Uninitialized<RenderTexture>() };
        public CetoAsyncGpuBuffer<float>[] m_readBuffer = null!;
        public int NUM_READBACK_GRIDS = 2;
        internal ArraySource Source = new ArraySource();
        public ArraySource GetBuffer() => Source;
    }

    private static readonly MethodInfo RequestMethod = typeof(CetoAsyncGpuBuffer<float>).GetMethod("tryRequest")!;
    private static readonly MethodInfo CollectMethod = typeof(CetoAsyncGpuBuffer<float>).GetMethod("tryGetData")!;
    private static readonly Func<CetoAsyncGpuBuffer<float>, bool> nativeRequest = Make<Func<CetoAsyncGpuBuffer<float>, bool>>(RequestMethod);
    private static readonly Func<CetoAsyncGpuBuffer<float>, bool> nativeCollect = Make<Func<CetoAsyncGpuBuffer<float>, bool>>(CollectMethod);
    private static readonly Action<RenderTexture, int, ComputeBuffer, ComputeShader, CetoAsyncGpuBuffer<float>> guard =
        Make<Action<RenderTexture, int, ComputeBuffer, ComputeShader, CetoAsyncGpuBuffer<float>>>(OceanReadbackDispatch.Guard);
    private readonly Action<Scene, int> step;
    internal readonly Scene World = new Scene();
    internal readonly State[] Buffers = { new State(), new State() };
    internal readonly List<string> Events = new List<string>();
    internal float Input;
    internal bool ThrowDispatch;
    private State current = null!;
    private static ReadbackFixture active = null!;
    private readonly Dictionary<ComputeBuffer, State> states = new Dictionary<ComputeBuffer, State>();

    internal ReadbackFixture(bool optimized)
    {
        World.m_readBuffer = new CetoAsyncGpuBuffer<float>[2];
        World.Source.Arrays = new InterpolatedArray2f[4];
        for (int index = 0; index < 4; index++)
        {
            var array = Uninitialized<InterpolatedArray2f>();
            typeof(InterpolatedArray2f).GetField("m_data", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(array, new[] { -1f, -1f, -1f });
            World.Source.Arrays[index] = array;
        }
        for (int index = 0; index < 2; index++)
        {
            var buffer = Uninitialized<CetoAsyncGpuBuffer<float>>();
            var compute = Uninitialized<ComputeBuffer>();
            buffer._isComputeBuffer = true;
            typeof(CetoAsyncGpuBuffer<float>).GetField("_computeBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(buffer, compute);
            World.m_readBuffer[index] = Buffers[index].Buffer = buffer;
            states.Add(compute, Buffers[index]);
        }
        var code = InstructionReader.Read(OceanReadbackDispatch.Target);
        if (optimized) code = OceanReadbackDispatch.Transpile(code).ToList();
        step = Make<Action<Scene, int>>(OceanReadbackDispatch.Target, code);
    }

    internal void Step(float input, int grids = 4)
    {
        Input = input; active = this; step(World, grids);
    }

    internal string Snapshot() => string.Join("|", Buffers.Select(s =>
        s.Buffer._requested + "," + s.Buffer._hasError + "," + s.Requests + "," + s.Copies + "," + s.Captured)) +
        ":" + string.Join(";", World.Source.Arrays.Select(a => string.Join(",", a.Data))) +
        ":" + string.Join(";", Events.Where(e => !e.StartsWith("dispatch", StringComparison.Ordinal)));

    private static T Uninitialized<T>()
    {
#pragma warning disable SYSLIB0050
        var result = (T)FormatterServices.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050
        GC.SuppressFinalize(result!);
        return result;
    }

    public static bool Equal(UnityEngine.Object? a, UnityEngine.Object? b) => ReferenceEquals(a, b);
    public static bool Unequal(UnityEngine.Object? a, UnityEngine.Object? b) => !ReferenceEquals(a, b);
    public static int ShaderLevel() => 50;
    public static bool ComputeSupported() => true;
    public static void Warning(string message) => active.Events.Add("warning:" + message);

    public static void Dispatch(RenderTexture texture, int channels, ComputeBuffer destination, ComputeShader shader)
    {
        var state = active.states[destination];
        int index = Array.IndexOf(active.Buffers, state);
        Program.Require(ReferenceEquals(texture, active.World.m_displacementMaps[index]) &&
            ReferenceEquals(shader, active.World.readSdr) && channels == 3, "Conversion arguments changed.");
        active.Events.Add("dispatch:" + index);
        if (active.ThrowDispatch) throw new InvalidOperationException("Simulated conversion failure.");
        state.Dispatches++; state.Converted = active.Input + index * 100;
    }

    public static bool Request(CetoAsyncGpuBuffer<float> buffer)
    {
        active.current = active.Buffers.Single(s => ReferenceEquals(s.Buffer, buffer));
        active.Events.Add("request:" + Array.IndexOf(active.Buffers, active.current));
        return nativeRequest(buffer);
    }

    public static bool Collect(CetoAsyncGpuBuffer<float> buffer)
    {
        active.current = active.Buffers.Single(s => ReferenceEquals(s.Buffer, buffer));
        active.Events.Add("collect:" + Array.IndexOf(active.Buffers, active.current));
        return nativeCollect(buffer);
    }

    public static int LayerCount(ref AsyncGPUReadbackRequest request) => active.current.Active ? 1 : 0;
    public static bool Done(ref AsyncGPUReadbackRequest request) => active.current.Done;
    public static bool HasError(ref AsyncGPUReadbackRequest request) => active.current.Error;
    public static AsyncGPUReadbackRequest Submit(ref NativeArray<float> values, ComputeBuffer source, Action<AsyncGPUReadbackRequest>? callback)
    {
        Program.Require(ReferenceEquals(active.current, active.states[source]) && callback == null, "Readback destination/callback changed.");
        var state = active.current;
        state.Active = true; state.Done = state.Immediate; state.Error = false;
        state.Captured = state.Converted; state.Requests++;
        active.Events.Add("submit:" + Array.IndexOf(active.Buffers, state) + ":" + state.Captured);
        return default;
    }

    public static void Copy(ref NativeArray<float> values, float[] destination)
    {
        active.current.Copies++;
        for (int i = 0; i < destination.Length; i++) destination[i] = active.current.Captured;
        active.Events.Add("copy:" + Array.IndexOf(active.Buffers, active.current));
    }

    public static void Guard(RenderTexture texture, int channels, ComputeBuffer destination, ComputeShader shader, CetoAsyncGpuBuffer<float> buffer) =>
        guard(texture, channels, destination, shader, buffer);

    private static T Make<T>(MethodInfo original, List<CodeInstruction>? supplied = null) where T : Delegate
    {
        var signature = typeof(T).GetMethod("Invoke")!;
        var method = new DynamicMethod("ManagedBoundary_" + original.Name, signature.ReturnType,
            signature.GetParameters().Select(p => p.ParameterType).ToArray(), typeof(ReadbackFixture).Module, true);
        var il = method.GetILGenerator();
        foreach (var local in original.GetMethodBody()!.LocalVariables) il.DeclareLocal(local.LocalType);
        var labels = new Dictionary<Label, Label>();
        Label LabelFor(Label old)
        { if (!labels.TryGetValue(old, out var mapped)) labels[old] = mapped = il.DefineLabel(); return mapped; }
        foreach (var instruction in supplied ?? InstructionReader.Read(original))
        {
            foreach (Label label in InstructionReader.Labels(instruction)) il.MarkLabel(LabelFor(label));
            object? operand = instruction.operand;
            var opcode = instruction.opcode;
            if (operand is FieldInfo field && field.DeclaringType == typeof(WaveSpectrum))
                operand = typeof(Scene).GetField(field.Name) ?? throw new InvalidOperationException("Unmapped native field " + field);
            if (operand is MethodInfo called)
            {
                string? replacement = null;
                if (called == OceanReadbackDispatch.Conversion) replacement = nameof(Dispatch);
                else if (called == OceanReadbackDispatch.Guard) replacement = nameof(Guard);
                else if (called == RequestMethod) replacement = nameof(Request);
                else if (called == CollectMethod) replacement = nameof(Collect);
                else if (called.DeclaringType == typeof(UnityEngine.Object)) replacement = called.Name == "op_Equality" ? nameof(Equal) : nameof(Unequal);
                else if (called.DeclaringType == typeof(SystemInfo)) replacement = called.Name == "get_graphicsShaderLevel" ? nameof(ShaderLevel) : nameof(ComputeSupported);
                else if (called.DeclaringType == typeof(Ocean) && called.Name == "LogWarning") replacement = nameof(Warning);
                else if (called.DeclaringType == typeof(AsyncGPUReadbackRequest))
                    replacement = called.Name == "get_layerCount" ? nameof(LayerCount) : called.Name == "get_done" ? nameof(Done) :
                        called.Name == "get_hasError" ? nameof(HasError) : throw new InvalidOperationException("Unmapped request boundary " + called);
                else if (called.DeclaringType == typeof(AsyncGPUReadback) && called.Name == "RequestIntoNativeArray") replacement = nameof(Submit);
                else if (called.DeclaringType == typeof(NativeArray<float>) && called.Name == "CopyTo") replacement = nameof(Copy);
                if (replacement != null)
                {
                    operand = typeof(ReadbackFixture).GetMethod(replacement)!; opcode = OpCodes.Call;
                }
                else if (called.DeclaringType == typeof(WaveSpectrum) && called.Name == "get_DisplacementBuffer")
                    operand = typeof(Scene).GetMethod(nameof(Scene.GetBuffer))!;
                else if (called.DeclaringType == typeof(IDisplacementBuffer) && called.Name == "GetReadDisplacements")
                    operand = typeof(ArraySource).GetMethod(nameof(ArraySource.GetReadDisplacements))!;
                else if (!(called.DeclaringType == typeof(InterpolatedArray2f) ||
                    (called.DeclaringType == typeof(CetoAsyncGpuBuffer<float>) && called.Name == "getComputeBuffer")))
                    throw new InvalidOperationException("An unmapped native dependency would run: " + called);
            }
            if (operand == null) il.Emit(opcode);
            else if (operand is Label label) il.Emit(LongBranch(opcode), LabelFor(label));
            else if (operand is FieldInfo f) il.Emit(opcode, f);
            else if (operand is MethodInfo m) il.Emit(opcode, m);
            else if (operand is int n)
            {
                if (opcode.OperandType == OperandType.InlineVar || opcode.OperandType == OperandType.ShortInlineVar) il.Emit(opcode, (short)n);
                else il.Emit(opcode, n);
            }
            else if (operand is sbyte small) il.Emit(opcode, small);
            else if (operand is string text) il.Emit(opcode, text);
            else throw new InvalidOperationException("Unmapped instruction " + instruction);
        }
        return (T)method.CreateDelegate(typeof(T));
    }

    // Rewriting engine boundaries can lengthen the original short branches.
    private static OpCode LongBranch(OpCode opcode) => opcode == OpCodes.Br_S ? OpCodes.Br :
        opcode == OpCodes.Brtrue_S ? OpCodes.Brtrue : opcode == OpCodes.Brfalse_S ? OpCodes.Brfalse :
        opcode == OpCodes.Blt_S ? OpCodes.Blt : opcode;
}
