using System;

namespace PerformanceOptimizer;

internal static class GameAssemblyCompatibility
{
    private static readonly Guid VerifiedBuild = new("f8b0a944-adb6-4c08-bf6c-352196ebb729");

    internal const string SupportedBuild = "0.9.3-261116";
    internal static bool IsVerified(Guid build) => build == VerifiedBuild;
}
