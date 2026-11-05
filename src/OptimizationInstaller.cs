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

    internal static void Install(IEnumerable<(IOptimization Optimization, bool Enabled)> optimizations)
    {
        foreach (var entry in optimizations) Install(entry.Optimization, entry.Enabled);
    }

    internal static void Install(IOptimization optimization, bool enabled)
    {
        if (!enabled)
        { ModLog.Info(optimization.Id + ": disabled in " + OptimizationSettings.FileName + "."); return; }
        try
        {
            var patches = new Harmony(ModInfo.Id + "." + optimization.Id);
            installed.Add(optimization, patches);
            optimization.Install(patches);
            if (installed.ContainsKey(optimization)) ModLog.Info(optimization.Id + ": installed.");
        }
        catch (Exception failure) { Failed(optimization, failure); }
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
