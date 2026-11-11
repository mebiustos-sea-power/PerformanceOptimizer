using System;
using System.IO;
using System.Linq;
using AnchorChain;
using BepInEx;
using PerformanceOptimizer.Optimizations;
using UnityEngine;

namespace PerformanceOptimizer;

[ACPlugin(ModInfo.Id, ModInfo.Name, ModInfo.Version)]
public sealed class Plugin : IAnchorChainMod
{
    private static bool initialized;

    public void TriggerEntryPoint()
    {
        if (initialized) return;
        initialized = true;
        try
        {
            var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Seapower-Scripts");
            string package = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)
                ?? throw new InvalidOperationException("The mod package directory is unavailable.");
            // Anchor Chain also discovers unchecked packages. Respect the game's
            // checkbox before installing any patches; changes require a restart.
            if (!ModActivation.IsEnabled(game.GetType("SeaPower.FileManager", true)!, package))
            { ModLog.Info("Disabled in the mod manager; no optimizations installed."); return; }

            string configDirectory = Paths.ConfigPath;
            string settingsPath = Path.Combine(configDirectory, OptimizationSettings.FileName);
            ModLog.Info("Configuration: " + settingsPath);
            if (File.Exists(Path.Combine(package, "optimizations.ini")))
                ModLog.Warning("The legacy optimizations.ini beside the mod DLL is no longer read. Move its settings to " + settingsPath + ".");
            OptimizationInstaller.Install(LoadOptimizations(configDirectory));
            Application.quitting += OptimizationInstaller.Shutdown;
        }
        catch (Exception error)
        {
            ModLog.Error("Initialization failed; no further optimizations will be installed", error);
        }
    }

    internal static (IOptimization Optimization, bool Enabled)[] LoadOptimizations(string configDirectory)
    {
        IOptimization[] optimizations = {
            new OceanReadbackDispatch(),
            new PipOutputRouting(),
            new OpticalEffectRouting(),
            new SonarScratchReuse(),
            new AiContactScratchReuse(),
            new FormationStationLookup(),
            new WeaponLookupAllocation(),
            new SensorChannelEnumeration()
        };
        var settings = OptimizationSettings.Load(Path.Combine(configDirectory, OptimizationSettings.FileName),
            optimizations.Select(optimization => optimization.Id));
        foreach (string warning in settings.Warnings) ModLog.Warning(warning);
        bool nativeWeaponLookupOptimized = GameAssemblyCompatibility.Current == GameAssemblyCompatibility.SeaPower090;
        if (nativeWeaponLookupOptimized && settings.IsEnabled(nameof(WeaponLookupAllocation)))
            ModLog.Info("WeaponLookupAllocation: Sea Power 0.9.0 already avoids the temporary list.");
        return optimizations
            .Where(optimization => !nativeWeaponLookupOptimized || optimization is not WeaponLookupAllocation)
            .Select(optimization => (optimization, settings.IsEnabled(optimization.Id))).ToArray();
    }
}
