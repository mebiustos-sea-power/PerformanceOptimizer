using System;

namespace PerformanceOptimizer;

internal static class GameAssemblyCompatibility
{
    private static readonly Guid VerifiedBuild = new("0dc1c9db-9ee8-4834-a6c4-20986512fbf3");

    internal const string SupportedBuild = "0.9.2-261113";
    internal static bool IsVerified(Guid build) => build == VerifiedBuild;
}
