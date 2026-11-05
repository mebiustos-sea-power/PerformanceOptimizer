using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerformanceOptimizer;
using static Program;

internal static class SettingsTests
{
    public sealed class Manager
    {
        public static Manager Instance { get; } = new Manager();
        public List<Entry> Directories { get; } = new List<Entry>();
    }
    public sealed class Entry
    {
        public DirectoryInfo DirectoryInfo { get; set; } = null!;
        public bool IsChecked { get; set; }
    }

    private static void IndividualOptimizations(string directory)
    {
        string configDirectory = Path.Combine(directory, "BepInEx", "config");
        string path = Path.Combine(configDirectory, "mebiustos.performanceoptimizer.ini");
        string legacy = Path.Combine(directory, "optimizations.ini");
        File.WriteAllText(legacy, "[Optimizations]\nOceanReadbackDispatch=false\n");
        var defaults = Plugin.LoadOptimizations(configDirectory);
        Require(defaults.All(entry => entry.Enabled) && !Directory.Exists(configDirectory),
            "Absent shared configuration did not retain enabled defaults or created files.");
        Directory.CreateDirectory(configDirectory);
        foreach (var selected in defaults)
        {
            string text = "[Optimizations]\n" + selected.Optimization.Id.ToLowerInvariant() + "=false\n";
            File.WriteAllText(path, text);
            var configured = Plugin.LoadOptimizations(configDirectory);
            Require(configured.Length == defaults.Length && configured.All(entry =>
                entry.Enabled == (entry.Optimization.Id != selected.Optimization.Id)),
                "A production optimization could not be disabled independently: " + selected.Optimization.Id);
            Require(File.ReadAllText(path) == text && File.ReadAllText(legacy).Contains("OceanReadbackDispatch=false"),
                "Loading configuration changed the user's shared or legacy settings.");
        }
        File.WriteAllText(path, "[Optimizations]\n" + string.Join("\n", defaults.Select(entry => entry.Optimization.Id + "=false")));
        Require(Plugin.LoadOptimizations(configDirectory).All(entry => !entry.Enabled),
            "Disabling every optimization left an item enabled.");
        File.WriteAllText(path, "[Optimizations]\n" + string.Join("\n", defaults.Select(entry => entry.Optimization.Id + "=true")));
        Require(Plugin.LoadOptimizations(configDirectory).All(entry => entry.Enabled),
            "Re-enabling every optimization left an item disabled.");
        Console.WriteLine("PASS shared configuration: " + defaults.Length + " independent production switches, omitted defaults, case-insensitive IDs and no file rewrites");
    }

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PerformanceOptimizerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            IndividualOptimizations(directory);
            string path = Path.Combine(directory, "parser-tests.ini");
            string[] ids = { "OceanReadbackDispatch", "Other" };
            Require(OptimizationSettings.Load(path, ids).IsEnabled(ids[0]) && !File.Exists(path), "Default settings created or disabled a setting.");
            string text = "[Optimizations]\nOceanReadbackDispatch=false\nUnknown=true\nOther=invalid\n";
            File.WriteAllText(path, text);
            var settings = OptimizationSettings.Load(path, ids);
            Require(!settings.IsEnabled(ids[0]) && settings.IsEnabled(ids[1]) && settings.Warnings.Count == 2 &&
                File.ReadAllText(path) == text, "Unknown/invalid entries changed valid settings, hid warnings or rewrote the file.");
            File.WriteAllText(path, "[Optimizations]\nOther=false\nOther=true");
            Throws<FormatException>(() => OptimizationSettings.Load(path, ids));
            File.WriteAllText(path, "Other=false");
            Throws<FormatException>(() => OptimizationSettings.Load(path, ids));
            var entry = new Entry { DirectoryInfo = new DirectoryInfo(directory), IsChecked = false };
            Manager.Instance.Directories.Add(entry);
            Require(!ModActivation.IsEnabled(typeof(Manager), directory), "An unchecked native mod package was enabled.");
            entry.IsChecked = true;
            Require(ModActivation.IsEnabled(typeof(Manager), directory + Path.DirectorySeparatorChar), "An enabled native package was not recognized.");
            Throws<InvalidOperationException>(() => ModActivation.IsEnabled(typeof(Manager), Path.Combine(directory, "unregistered")));
        }
        finally { Manager.Instance.Directories.Clear(); Directory.Delete(directory, true); }
        Console.WriteLine("PASS settings/activation: defaults, all production item switches, warnings, invalid syntax and native checkbox gating");
    }
}
