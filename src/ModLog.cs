using System;
using System.Diagnostics;
using BepInEx.Logging;

namespace PerformanceOptimizer;

internal static class ModLog
{
    private static ManualLogSource? source;
    internal static void Info(string message) => Write(LogLevel.Info, () => message);
    internal static void Warning(string message) => Write(LogLevel.Warning, () => message);
    internal static void Error(string context, Exception error) => Write(LogLevel.Error, () => context + ": " + error);

    private static void Write(LogLevel level, Func<string> message)
    {
        try { (source ??= Logger.CreateLogSource(ModInfo.Name)).Log(level, message()); }
        catch (Exception loggingError)
        {
            // A failing log listener must not propagate an installation failure
            // into Anchor Chain or prevent other optimizations from installing.
            try { Trace.TraceError(ModInfo.Name + ": " + message() + "; logging failed: " + loggingError); }
            catch (Exception) { }
        }
    }
}
