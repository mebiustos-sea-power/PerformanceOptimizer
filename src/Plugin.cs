using System;
using System.IO;
using System.Linq;
using AnchorChain;
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

            var ocean = new OceanReadbackDispatch();
            var settings = OptimizationSettings.Load(Path.Combine(package, "optimizations.ini"), new[] { ocean.Id });
            foreach (string warning in settings.Warnings) ModLog.Warning(warning);
            OptimizationInstaller.Install(new (IOptimization Optimization, bool Enabled)[] {
                (ocean, settings.IsEnabled(ocean.Id)),
                (new PipOutputRouting(), true),
                (new OpticalEffectRouting(), true),
                (new SonarScratchReuse(), true),
                (new AiContactScratchReuse(), true),
                (new FormationStationLookup(), true),
                (new WeaponLookupAllocation(), true),
                (new SensorChannelEnumeration(), true)
            });
            Application.quitting += OptimizationInstaller.Shutdown;
        }
        catch (Exception error)
        {
            ModLog.Error("Initialization failed; no further optimizations will be installed", error);
        }
    }
}
