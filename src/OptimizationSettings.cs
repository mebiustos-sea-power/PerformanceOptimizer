using System;
using System.Collections.Generic;
using System.IO;

namespace PerformanceOptimizer;

internal sealed class OptimizationSettings
{
    private readonly Dictionary<string, bool> enabled;
    internal IReadOnlyList<string> Warnings { get; }
    private OptimizationSettings(Dictionary<string, bool> values, IReadOnlyList<string>? warnings = null)
    { enabled = values; Warnings = warnings ?? Array.Empty<string>(); }
    internal bool IsEnabled(string id) => enabled[id];

    internal static OptimizationSettings Load(string path, IEnumerable<string> optimizationIds)
    {
        var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in optimizationIds) values.Add(id, true);
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (FileNotFoundException) { return new OptimizationSettings(values); }
        catch (DirectoryNotFoundException) { return new OptimizationSettings(values); }

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        bool section = false;
        int number = 0;
        foreach (string raw in lines)
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
            if (string.Equals(line, "[Optimizations]", StringComparison.OrdinalIgnoreCase)) { section = true; continue; }
            int separator = line.IndexOf('=');
            if (!section || separator <= 0)
                throw new FormatException(path + ":" + number + ": expected [Optimizations] or OptimizationId = true/false.");
            string id = line.Substring(0, separator).Trim();
            // Unknown entries can remain after an optimization is removed or come from another mod version.
            if (!values.ContainsKey(id))
            {
                warnings.Add(path + ":" + number + ": unknown optimization ignored: " + id);
                continue;
            }
            if (!bool.TryParse(line.Substring(separator + 1).Trim(), out bool value))
            {
                warnings.Add(path + ":" + number + ": invalid Boolean for " + id + "; entry ignored.");
                continue;
            }
            if (!assigned.Add(id)) throw new FormatException(path + ":" + number + ": duplicate optimization: " + id);
            values[id] = value;
        }
        return new OptimizationSettings(values, warnings.AsReadOnly());
    }
}
