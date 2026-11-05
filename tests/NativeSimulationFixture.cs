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

// Execute original/transformed game IL with managed data and engine boundaries.
// No Unity object is instantiated and no game process is contacted.
internal sealed class NativeSimulationFixture
{
    internal sealed class Value<T>
    {
        internal T Data = default!;
        internal Action? OnRead;
        internal readonly List<T> Writes = new();
        public T ValueData { get { OnRead?.Invoke(); return Data; } set { Data = value; Writes.Add(value); } }
    }
    internal sealed class Owner
    {
        public Parameters _obp = new();
        public Dictionary<string, List<Weapon>> _ammunitionToWeaponSystemsDictionary = new();
        public Formation? Formation { get; set; }
        public Value<int> NumberOfFreeJamChannels { get; } = new();
        public Value<int> NumberOfFreeLaserChannels { get; } = new();
    }
    internal sealed class Parameters { public Collection<Sensor> _sensorSystems = new(); }
    internal sealed class Weapon { internal readonly int Id; internal Weapon(int id) { Id = id; } }
    internal sealed class Formation
    {
        internal Station? Result;
        internal int Queries;
        public Station? GetStationForUnit(Owner owner) { Queries++; return Result; }
    }
    internal sealed class Station { public Value<bool> UnitActsIndependently { get; } = new(); }
    internal class Sensor
    {
        public int _systemType, _targetChannels;
        public List<Owner> _associatedTargets = new();
        public Value<bool> IsOn { get; } = new();
    }
    internal sealed class EcmSensor : Sensor
    {
        public Ecm _ecm = new();
        public Owner? _associatedTarget = null;
        public bool _geoPositionTargeted;
    }
    internal sealed class LaserSensor : Sensor { }
    internal sealed class Ecm { public EcmParameters _ep = new(); }
    internal sealed class EcmParameters { public int _type; }

    private readonly Dictionary<Type, Type> types = new();
    internal NativeSimulationFixture()
    {
        var game = typeof(ObjectBase).Assembly;
        void Add(string native, Type managed) => types.Add(game.GetType(native, true)!, managed);
        Add("SeaPower.ObjectBase", typeof(Owner)); Add("SeaPower.ObjectBaseParameters", typeof(Parameters));
        Add("SeaPower.WeaponSystem", typeof(Weapon)); Add("SeaPower.UnitFormation", typeof(Formation)); Add("SeaPower.Station", typeof(Station));
        Add("SeaPower.BaseSystem", typeof(Sensor)); Add("SeaPower.SensorSystem", typeof(Sensor));
        Add("SeaPower.SensorSystemECM", typeof(EcmSensor)); Add("SeaPower.SensorSystemLaserDesignator", typeof(LaserSensor));
        Add("SeaPower.ECM", typeof(Ecm)); Add("SeaPower.ECMParameters", typeof(EcmParameters));
        Add("TrulyObservableCollection`1", typeof(Collection<>));
        var reactive = NativeSimulationContract.Required(typeof(ObjectBase), "get_NumberOfFreeJamChannels").ReturnType.GetGenericTypeDefinition();
        types.Add(reactive, typeof(Value<>));
    }

    private Type Map(Type type)
    {
        if (types.TryGetValue(type, out var mapped)) return mapped;
        if (type.IsByRef) return Map(type.GetElementType()!).MakeByRefType();
        if (type.IsArray) return Map(type.GetElementType()!).MakeArrayType();
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (types.TryGetValue(definition, out mapped)) definition = mapped;
            return definition.MakeGenericType(type.GetGenericArguments().Select(Map).ToArray());
        }
        return type;
    }

    private MethodInfo Map(MethodInfo method)
    {
        if (method.DeclaringType == typeof(UnityEngine.Object))
            return typeof(NativeSimulationFixture).GetMethod(method.Name == "op_Equality" ? nameof(Equal) : nameof(Unequal))!;
        if (method.DeclaringType?.FullName == "SeaPower.Utils" && method.Name == "CopyList")
            return typeof(NativeSimulationFixture).GetMethod(nameof(Copy))!.MakeGenericMethod(method.GetGenericArguments().Select(Map).ToArray());
        var owner = Map(method.DeclaringType!);
        string name = owner.IsGenericType && owner.GetGenericTypeDefinition() == typeof(Value<>)
            ? method.Name.Replace("_Value", "_ValueData") : method.Name;
        var arguments = method.GetParameters().Select(p => Map(p.ParameterType)).ToArray();
        foreach (var candidate in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (candidate.Name != name || candidate.IsGenericMethod != method.IsGenericMethod) continue;
            var closed = candidate;
            if (method.IsGenericMethod)
            {
                if (candidate.GetGenericArguments().Length != method.GetGenericArguments().Length) continue;
                closed = candidate.MakeGenericMethod(method.GetGenericArguments().Select(Map).ToArray());
            }
            if (closed.GetParameters().Select(p => p.ParameterType).SequenceEqual(arguments)) return closed;
        }
        throw new MissingMethodException(owner.FullName, name);
    }

    public static bool Equal(object? a, object? b) => ReferenceEquals(a, b);
    public static bool Unequal(object? a, object? b) => !ReferenceEquals(a, b);
    public static List<T> Copy<T>(List<T> source) => new List<T>(source);

    internal T Compile<T>(MethodInfo original, Func<IEnumerable<CodeInstruction>, ILGenerator, IEnumerable<CodeInstruction>>? rewrite = null) where T : Delegate
    {
        var plan = new DynamicMethod("PlanNativeLocals", typeof(void), Type.EmptyTypes).GetILGenerator();
        var locals = original.GetMethodBody()!.LocalVariables.Select(v => v.LocalType).ToList();
        foreach (var local in locals) plan.DeclareLocal(local);
        var code = (rewrite == null ? InstructionReader.Read(original) : rewrite(InstructionReader.Read(original), plan)).ToList();
        foreach (var builder in code.Select(i => i.operand).OfType<LocalBuilder>().OrderBy(b => b.LocalIndex))
            if (builder.LocalIndex >= locals.Count)
            {
                if (builder.LocalIndex != locals.Count) throw new InvalidOperationException("Missing planned local.");
                locals.Add(builder.LocalType);
            }
        var signature = typeof(T).GetMethod("Invoke")!;
        var dynamic = new DynamicMethod("ManagedNative_" + original.Name, signature.ReturnType,
            signature.GetParameters().Select(p => p.ParameterType).ToArray(), typeof(NativeSimulationFixture).Module, true);
        var il = dynamic.GetILGenerator();
        foreach (var local in locals) il.DeclareLocal(Map(local));
        var labels = new Dictionary<Label, Label>();
        Label LabelFor(Label old) { if (!labels.TryGetValue(old, out var label)) labels.Add(old, label = il.DefineLabel()); return label; }
        foreach (var instruction in code)
        {
            var blocks = (IList)typeof(CodeInstruction).GetField("blocks")!.GetValue(instruction)!;
            foreach (ExceptionBlock block in blocks)
            {
                if (block.blockType == ExceptionBlockType.BeginExceptionBlock) il.BeginExceptionBlock();
                else if (block.blockType == ExceptionBlockType.BeginFinallyBlock) il.BeginFinallyBlock();
                else if (block.blockType == ExceptionBlockType.BeginCatchBlock) il.BeginCatchBlock(Map(block.catchType));
                else if (block.blockType != ExceptionBlockType.EndExceptionBlock) throw new NotSupportedException(block.blockType.ToString());
            }
            foreach (Label label in InstructionReader.Labels(instruction)) il.MarkLabel(LabelFor(label));
            var opcode = instruction.opcode;
            object? operand = instruction.operand;
            if (opcode.OperandType == OperandType.ShortInlineBrTarget)
                opcode = (OpCode)typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Single(f => ((OpCode)f.GetValue(null)!).Name == opcode.Name!.Substring(0, opcode.Name.Length - 2)).GetValue(null)!;
            if (operand is LocalBuilder local) operand = local.LocalIndex;
            else if (operand is FieldInfo field)
                operand = Map(field.DeclaringType!).GetField(field.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    ?? throw new MissingFieldException(Map(field.DeclaringType!).FullName, field.Name);
            else if (operand is MethodInfo called)
            {
                operand = Map(called);
                if (((MethodInfo)operand).IsStatic) opcode = OpCodes.Call;
            }
            else if (operand is ConstructorInfo ctor)
                operand = Map(ctor.DeclaringType!).GetConstructor(ctor.GetParameters().Select(p => Map(p.ParameterType)).ToArray())!;
            else if (operand is Type type) operand = Map(type);
            if (opcode != OpCodes.Endfinally)
            {
                if (operand == null) il.Emit(opcode);
                else if (operand is Label label) il.Emit(opcode, LabelFor(label));
                else if (operand is Label[] branches) il.Emit(opcode, branches.Select(LabelFor).ToArray());
                else if (operand is FieldInfo field) il.Emit(opcode, field);
                else if (operand is MethodInfo method) il.Emit(opcode, method);
                else if (operand is ConstructorInfo ctor) il.Emit(opcode, ctor);
                else if (operand is Type type) il.Emit(opcode, type);
                else if (operand is int n)
                {
                    if (opcode.OperandType == OperandType.InlineVar || opcode.OperandType == OperandType.ShortInlineVar) il.Emit(opcode, (short)n);
                    else il.Emit(opcode, n);
                }
                else if (operand is sbyte small) il.Emit(opcode, small);
                else if (operand is float single) il.Emit(opcode, single);
                else if (operand is double number) il.Emit(opcode, number);
                else if (operand is long integer) il.Emit(opcode, integer);
                else if (operand is string text) il.Emit(opcode, text);
                else throw new NotSupportedException(instruction.ToString());
            }
            foreach (ExceptionBlock block in blocks)
                if (block.blockType == ExceptionBlockType.EndExceptionBlock) il.EndExceptionBlock();
        }
        return (T)dynamic.CreateDelegate(typeof(T));
    }
}
