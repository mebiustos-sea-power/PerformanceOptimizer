using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PerformanceOptimizer.Simulation;
using SeaPower;

namespace PerformanceOptimizer.Optimizations;

internal sealed class SonarScratchReuse : IOptimization
{
    public string Id => nameof(SonarScratchReuse);
    internal static readonly MethodInfo Passive = NativeSimulationContract.Required(typeof(Sonar), "SchedulePassiveBatch");
    internal static readonly MethodInfo Active = NativeSimulationContract.Required(typeof(Sonar), "ScheduleActiveBatch");
    internal static readonly MethodInfo Commit = NativeSimulationContract.Required(typeof(Sonar), "CommitPassiveBatch");
    private static readonly ScratchPool passive = new(), active = new(), commit = new();

    public void Install(Harmony patches)
    {
        NativeSimulationContract.Validate(Passive, Active, Commit);
        foreach (var pair in new[] { (Passive, nameof(PassivePrefix)), (Active, nameof(ActivePrefix)), (Commit, nameof(CommitPrefix)) })
            patches.Patch(pair.Item1, prefix: new HarmonyMethod(typeof(SonarScratchReuse), pair.Item2),
                transpiler: new HarmonyMethod(typeof(SonarScratchReuse), pair.Item1 == Commit ? nameof(TranspileCommit) : nameof(TranspileSchedule)),
                finalizer: new HarmonyMethod(typeof(SonarScratchReuse), nameof(Finish)));
    }

    public void Dispose() { passive.Clear(); active.Clear(); commit.Clear(); }
    private static void PassivePrefix(object __instance, out ScratchPool.Lease __state) => __state = passive.Enter(__instance);
    private static void ActivePrefix(object __instance, out ScratchPool.Lease __state) => __state = active.Enter(__instance);
    private static void CommitPrefix(object __instance, out ScratchPool.Lease __state) => __state = commit.Enter(__instance);
    private static void Finish(ScratchPool.Lease __state) => __state.Dispose();

    // Only the temporary builders are reused. The native ToArray copies and all
    // worker-owned arrays, tasks, completion waits and result collection remain.
    private static List<T> PassiveList<T>(int capacity, object owner) => passive.Current(owner).List<T>(capacity);
    private static List<T> ActiveList<T>(int capacity, object owner) => active.Current(owner).List<T>(capacity);
    private static HashSet<T> CommitSet<T>(object owner) => commit.Current(owner).Set<T>();
    private static List<T> CommitSnapshot<T>(IEnumerable<T> source, object owner) => commit.Current(owner).Snapshot(source);

    internal static IEnumerable<CodeInstruction> TranspileSchedule(IEnumerable<CodeInstruction> code, MethodBase __originalMethod)
    {
        var snapshot = typeof(Sonar).GetNestedType("ContactSnapshot", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Sonar).FullName, "ContactSnapshot");
        bool isPassive = __originalMethod == Passive;
        if (!isPassive && __originalMethod != Active) throw NativeSimulationContract.Incompatible("unknown sonar schedule");
        return CollectionRewrite.Constructors(code, typeof(List<>), isPassive ?
            new[] { typeof(ObjectBase), snapshot, typeof(bool) } : new[] { typeof(ObjectBase), snapshot },
            new[] { typeof(int) }, Factory(isPassive ? nameof(PassiveList) : nameof(ActiveList)));
    }

    internal static IEnumerable<CodeInstruction> TranspileCommit(IEnumerable<CodeInstruction> code)
    {
        var key = typeof(Sonar).GetField("_nbIntegration", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .FieldType.GetGenericArguments()[0];
        return CollectionRewrite.Snapshot(CollectionRewrite.Constructors(code, typeof(HashSet<>), new[] { key },
            Type.EmptyTypes, Factory(nameof(CommitSet))), Factory(nameof(CommitSnapshot)));
    }

    private static MethodInfo Factory(string name) => typeof(SonarScratchReuse).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
}
