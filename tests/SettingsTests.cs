using System;
using System.Collections.Generic;
using System.IO;
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

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PerformanceOptimizerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "optimizations.ini");
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
        Console.WriteLine("PASS settings/activation: defaults, per-item disable, warnings, invalid syntax and native checkbox gating");
    }
}
