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
            OptimizationInstaller.Install(LoadOptimizations(configDirectory), game.ManifestModule.ModuleVersionId);
            Application.quitting += OptimizationInstaller.Shutdown;
        }
        catch (Exception error)
        {
            ModLog.Error("Initialization failed; no further optimizations will be installed", error);
        }
    }

    internal static (string Id, Func<IOptimization> Create, bool RequiresVerifiedBuild, bool Enabled)[] LoadOptimizations(string configDirectory)
    {
        // The routing and simulation patches depend on audited game behavior.
        // Resolve their patch targets only after the installer checks the build.
        (string Id, Func<IOptimization> Create, bool RequiresVerifiedBuild)[] optimizations = {
            (nameof(OceanReadbackDispatch), () => new OceanReadbackDispatch(), false),
            (nameof(PipOutputRouting), () => new PipOutputRouting(), true),
            (nameof(OpticalEffectRouting), () => new OpticalEffectRouting(), true),
            (nameof(SonarScratchReuse), () => new SonarScratchReuse(), true),
            (nameof(AiContactScratchReuse), () => new AiContactScratchReuse(), true),
            (nameof(FormationStationLookup), () => new FormationStationLookup(), true),
            (nameof(SensorChannelEnumeration), () => new SensorChannelEnumeration(), true)
        };
        var settings = OptimizationSettings.Load(Path.Combine(configDirectory, OptimizationSettings.FileName),
            optimizations.Select(optimization => optimization.Id));
        foreach (string warning in settings.Warnings) ModLog.Warning(warning);
        return optimizations.Select(optimization => (optimization.Id, optimization.Create,
            optimization.RequiresVerifiedBuild, settings.IsEnabled(optimization.Id))).ToArray();
    }
}
