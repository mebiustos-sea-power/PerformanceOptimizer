using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PerformanceOptimizer.Optimizations;
using PerformanceOptimizer.Simulation;
using static Program;

internal static class ScratchTests
{
    private delegate void Begin(object owner, out ScratchPool.Lease state);
    private static MethodInfo Hook(string name) => typeof(SonarScratchReuse).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly Begin begin = (Begin)Hook("PassivePrefix").CreateDelegate(typeof(Begin));
    private static readonly Begin commit = (Begin)Hook("CommitPrefix").CreateDelegate(typeof(Begin));
    private static readonly Action<ScratchPool.Lease> finish = (Action<ScratchPool.Lease>)Hook("Finish").CreateDelegate(typeof(Action<ScratchPool.Lease>));

    internal sealed class Snapshot
    {
        internal readonly int[] Targets;
        internal readonly string[] Values;
        internal readonly bool[] Flags;
        public Snapshot(int[] targets, string[] values, bool[] flags) { Targets = targets; Values = values; Flags = flags; }
        internal string Contents() => string.Join(",", Targets) + ":" + string.Join(",", Values) + ":" + string.Join(",", Flags);
    }

    internal sealed class Builder
    {
        internal int Seed;
        internal bool Fail;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Snapshot Build(int count)
        {
            var targets = new List<int>(count);
            var values = new List<string>(count);
            var flags = new List<bool>(count);
            for (int i = 0; i < count; i++)
            { targets.Add(Seed + i); values.Add((Seed + i).ToString()); flags.Add((Seed + i) % 2 == 0); }
            if (Fail) throw new InvalidOperationException("Native builder failure.");
            return new Snapshot(targets.ToArray(), values.ToArray(), flags.ToArray());
        }
    }

    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> code) =>
        CollectionRewrite.Constructors(code, typeof(List<>), new[] { typeof(int), typeof(string), typeof(bool) },
            new[] { typeof(int) }, Hook("PassiveList"));

    internal static void Run()
    {
        RepeatedBuilders(); AiDecisions(); NestedAndOwners(); DictionarySnapshots(); PrivateSnapshotType(); WarmAllocation();
    }

    private static Snapshot Invoke(Func<Builder, int, Snapshot> operation, Builder owner, int count)
    {
        begin(owner, out var scope);
        try { return operation(owner, count); }
        finally { finish(scope); }
    }

    private static void RepeatedBuilders()
    {
        var fixture = new NativeSimulationFixture();
        var target = typeof(Builder).GetMethod(nameof(Builder.Build))!;
        var native = fixture.Compile<Func<Builder, int, Snapshot>>(target);
        var changed = fixture.Compile<Func<Builder, int, Snapshot>>(target, (code, _) => Rewrite(code));
        var builder = new Builder(); Snapshot? old = null; string? oldContents = null;
        foreach (int count in new[] { 0, 4, 20, 3, 0, 8 })
        {
            builder.Seed += 100;
            var expected = native(builder, count); var observed = Invoke(changed, builder, count);
            Require(expected.Contents() == observed.Contents(), "Scratch reuse changed target order, lengths or values after growth/shrinkage.");
            if (old != null) Require(old.Contents() == oldContents, "A later invocation overwrote a worker-owned snapshot.");
            old = observed; oldContents = old.Contents();
        }
        builder.Fail = true;
        Throws<InvalidOperationException>(() => Invoke(changed, builder, 4));
        builder.Fail = false;
        Require(Invoke(changed, builder, 2).Contents() == native(builder, 2).Contents(), "A failed invocation left stale data for the next update.");
        Throws<ArgumentOutOfRangeException>(() => Invoke(changed, builder, -1));
        Require(Invoke(changed, builder, 1).Contents() == native(builder, 1).Contents(), "A constructor failure leaked its scratch scope.");
        Console.WriteLine("PASS production sonar builders: growth/shrinkage/empty targets, snapshot independence and exception recovery");
    }

    internal sealed class AiProbe
    {
        public int[] Local = { 1, 1, 2 }, Shared = { 3, 4, 4, 5 };
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Evaluate(bool useLocal)
        {
            int[] input;
            if (useLocal) input = Local;
            else input = Shared;
            var seen = new HashSet<int>();
            foreach (int value in input) seen.Add(value);
            return seen.Count;
        }
    }

    private static MethodInfo AiHook(string name) => typeof(AiContactScratchReuse).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static IEnumerable<CodeInstruction> AiRewrite(IEnumerable<CodeInstruction> code) =>
        CollectionRewrite.Constructors(code, typeof(HashSet<>), new[] { typeof(int) }, Type.EmptyTypes, AiHook("Set"));

    private static void AiDecisions()
    {
        var fixture = new NativeSimulationFixture(); var target = typeof(AiProbe).GetMethod(nameof(AiProbe.Evaluate))!;
        var native = fixture.Compile<Func<AiProbe, bool, int>>(target);
        var changed = fixture.Compile<Func<AiProbe, bool, int>>(target, (code, _) => AiRewrite(code));
        var enter = (Begin)AiHook("Begin").CreateDelegate(typeof(Begin));
        var leave = (Action<ScratchPool.Lease>)AiHook("Finish").CreateDelegate(typeof(Action<ScratchPool.Lease>));
        var ai = new AiProbe();
        foreach (bool local in new[] { true, false, true, false })
        {
            enter(ai, out var scope);
            try { Require(changed(ai, local) == native(ai, local), "AI scratch reuse changed deduplication or retained another decision's contacts."); }
            finally { leave(scope); }
        }
        Console.WriteLine("PASS production AI scratch hooks: both contact-input branches and repeated decision deduplication match");
    }

    private static void PrivateSnapshotType()
    {
        var nativeType = SonarScratchReuse.Passive.DeclaringType!.GetNestedType("ContactSnapshot", BindingFlags.NonPublic | BindingFlags.Public)!;
        var method = new DynamicMethod("NativePrivateSnapshotList", typeof(object), new[] { typeof(object) }, typeof(ScratchTests).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, Hook("PassiveList").MakeGenericMethod(nativeType)); il.Emit(OpCodes.Ret);
        var list = (Func<object, object>)method.CreateDelegate(typeof(Func<object, object>));
        var owner = new object(); begin(owner, out var scope);
        try
        {
            var values = (System.Collections.IList)list(owner);
            values.Add(Activator.CreateInstance(nativeType));
            Require(values.Count == 1, "The private native sonar snapshot type could not use production scratch storage.");
        }
        finally { finish(scope); }
        Console.WriteLine("PASS native private sonar snapshot type: production generic builder can construct and clear its list");
    }

    private static void NestedAndOwners()
    {
        var pool = new ScratchPool(); var a = new object(); var b = new object();
        List<int> outerList;
        using (pool.Enter(a))
        {
            outerList = pool.Current(a).List<int>(8); outerList.Add(10);
            var outerSet = pool.Current(a).Set<int>(); outerSet.Add(20);
            using (pool.Enter(a))
            {
                var nested = pool.Current(a).List<int>(2); nested.Add(30);
                Require(!ReferenceEquals(outerList, nested), "A reentrant update reused its caller's work list.");
                Require(pool.Current(a).Set<int>().Count == 0, "A reentrant update saw the caller's work set.");
            }
            using (pool.Enter(b))
            {
                var other = pool.Current(b).List<int>(1); other.Add(40);
                Require(!ReferenceEquals(other, outerList), "Two units shared work storage.");
            }
            Require(ReferenceEquals(pool.Current(a), pool.Current(a)) && outerList.SequenceEqual(new[] { 10 }) && outerSet.SetEquals(new[] { 20 }),
                "Ending a nested/other-unit update cleared its caller's data.");
        }
        using (pool.Enter(a))
        {
            var next = pool.Current(a).List<int>(2);
            Require(ReferenceEquals(next, outerList) && next.Count == 0 && next.Capacity >= 8 && pool.Current(a).Set<int>().Count == 0,
                "Work storage was not reused/reset between decisions.");
        }
        using (pool.Enter(a))
        {
            var inFlight = pool.Current(a).List<int>(); inFlight.Add(50);
            pool.Clear();
            Require(inFlight[0] == 50 && ReferenceEquals(pool.Current(a), pool.Current(a)), "Removal invalidated an in-flight update.");
        }
        using (pool.Enter(a)) Require(!ReferenceEquals(pool.Current(a).List<int>(), outerList), "Disposed optimization retained its old workspace.");
        Console.WriteLine("PASS scratch ownership: separate units, reentrant calls, clearing and removal preserve invocation isolation");
    }

    private static void DictionarySnapshots()
    {
        var snapshot = (Func<IEnumerable<KeyValuePair<int, float>>, object, List<KeyValuePair<int, float>>>)Hook("CommitSnapshot")
            .MakeGenericMethod(typeof(KeyValuePair<int, float>)).CreateDelegate(typeof(Func<IEnumerable<KeyValuePair<int, float>>, object, List<KeyValuePair<int, float>>>));
        var seen = (Func<object, HashSet<int>>)Hook("CommitSet").MakeGenericMethod(typeof(int)).CreateDelegate(typeof(Func<object, HashSet<int>>));
        var owner = new object(); var source = new Dictionary<int, float> { [1] = 1f, [2] = 2f };
        for (int round = 0; round < 4; round++)
        {
            var expected = source.ToList(); commit(owner, out var scope);
            try
            {
                var marks = seen(owner);
                Require(marks.Count == 0 && marks.Add(round) && !marks.Add(round), "Sonar scratch membership leaked from another update.");
                var values = snapshot(source, owner);
                Require(values.SequenceEqual(expected), "The dictionary snapshot changed enumeration order or captured values.");
                foreach (var entry in values) { source.Remove(entry.Key); source[entry.Key + 10] = entry.Value + 1; }
                Require(values.SequenceEqual(expected), "Updating the dictionary changed the original snapshot being consumed.");
            }
            finally { finish(scope); }
        }
        Console.WriteLine("PASS production sonar dictionary snapshot: native ordering/values survive dictionary mutation across repeated updates");
    }

    private static void WarmAllocation()
    {
#if NET10_0
        var pool = new ScratchPool(); var owner = new object();
        void Step()
        {
            using (pool.Enter(owner))
            {
                var workspace = pool.Current(owner);
                workspace.List<int>(16).Add(1); workspace.Set<int>().Add(2);
            }
        }
        for (int i = 0; i < 200; i++) Step();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Step();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated == 0, "Warmed scratch reuse still allocates per decision: " + allocated);
        Console.WriteLine("PASS warmed scratch storage: 1000 list/set updates allocate 0 managed bytes (.NET test host)");
#endif
    }

    internal static void InstallOnMono()
    {
        var patches = new Harmony("mebiustos.performanceoptimizer.tests.scratch-finalizers");
        try
        {
            patches.Patch(typeof(Builder).GetMethod(nameof(Builder.Build))!, prefix: new HarmonyMethod(Hook("PassivePrefix")),
                transpiler: new HarmonyMethod(typeof(ScratchTests).GetMethod(nameof(Rewrite), BindingFlags.NonPublic | BindingFlags.Static)!),
                finalizer: new HarmonyMethod(Hook("Finish")));
            var builder = new Builder { Seed = 10 };
            Require(builder.Build(3).Targets.SequenceEqual(new[] { 10, 11, 12 }), "Production scratch prefix did not open its scope on Mono.");
            builder.Fail = true; Throws<InvalidOperationException>(() => builder.Build(3)); builder.Fail = false;
            Require(builder.Build(1).Targets.SequenceEqual(new[] { 10 }), "Harmony finalizer failed to clean scratch data after an exception.");
            patches.Patch(typeof(AiProbe).GetMethod(nameof(AiProbe.Evaluate))!, prefix: new HarmonyMethod(AiHook("Begin")),
                transpiler: new HarmonyMethod(typeof(ScratchTests).GetMethod(nameof(AiRewrite), BindingFlags.NonPublic | BindingFlags.Static)!),
                finalizer: new HarmonyMethod(AiHook("Finish")));
            var ai = new AiProbe();
            Require(ai.Evaluate(true) == 2 && ai.Evaluate(false) == 3 && ai.Evaluate(true) == 2, "Production AI Harmony scope retained an earlier decision's contacts.");
        }
        finally { patches.UnpatchSelf(); new SonarScratchReuse().Dispose(); new AiContactScratchReuse().Dispose(); }
        Console.WriteLine("PASS game Mono: production scratch prefix/finalizer preserves normal results and recovers after native exceptions");
    }
}
