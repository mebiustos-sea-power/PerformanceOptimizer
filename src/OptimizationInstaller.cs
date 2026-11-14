using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace PerformanceOptimizer;

internal interface IOptimization : IDisposable
{
    string Id { get; }
    void Install(Harmony patches);
}

internal static class OptimizationInstaller
{
    private static readonly Dictionary<IOptimization, Harmony> installed = new();

    internal static void Install(
        IEnumerable<(string Id, Func<IOptimization> Create, bool RequiresVerifiedBuild, bool Enabled)> optimizations, Guid build)
    {
        bool verified = GameAssemblyCompatibility.IsVerified(build);
        var skipped = new List<string>();
        foreach (var entry in optimizations)
        {
            if (!entry.Enabled)
                ModLog.Info(entry.Id + ": disabled in " + OptimizationSettings.FileName + ".");
            else if (entry.RequiresVerifiedBuild && !verified)
                skipped.Add(entry.Id);
            else
                Install(entry.Id, entry.Create);
        }

        if (skipped.Count > 0)
            ModLog.Warning($"Sea Power assembly {build} has not been verified for {string.Join(", ", skipped)}. " +
                $"These optimizations were skipped. The supported build is {GameAssemblyCompatibility.SupportedBuild}.");
    }

    private static void Install(string id, Func<IOptimization> create)
    {
        IOptimization? optimization = null;
        try
        {
            optimization = create();
            var patches = new Harmony(ModInfo.Id + "." + id);
            installed.Add(optimization, patches);
            optimization.Install(patches);
            if (installed.ContainsKey(optimization)) ModLog.Info(id + ": installed.");
        }
        catch (Exception failure)
        {
            if (optimization == null)
                ModLog.Error(id + ": could not initialize optimization", failure);
            else
                Failed(optimization, failure);
        }
    }

    internal static void Failed(IOptimization optimization, Exception failure)
    {
        ModLog.Error(optimization.Id + ": stopped after an error; restoring native behavior", failure);
        Remove(optimization);
    }

    private static void Remove(IOptimization optimization)
    {
        if (!installed.TryGetValue(optimization, out var patches)) return;
        installed.Remove(optimization);
        try { optimization.Dispose(); }
        catch (Exception cleanup) { ModLog.Error(optimization.Id + ": native state restoration failed", cleanup); }
        try { patches.UnpatchSelf(); }
        catch (Exception cleanup) { ModLog.Error(optimization.Id + ": patch rollback failed", cleanup); }
    }

    internal static void Shutdown()
    {
        foreach (var optimization in installed.Keys.ToArray()) Remove(optimization);
    }
}
