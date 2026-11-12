using System;
using SeaPower;

namespace PerformanceOptimizer;

internal static class GameAssemblyCompatibility
{
    private static readonly Guid VerifiedBuild = new("875e9708-6803-45c5-96b4-008c762358eb");

    internal static Guid Current => typeof(ObjectBase).Module.ModuleVersionId;

    internal static void RequireVerified(Guid build)
    {
        if (build != VerifiedBuild)
            throw new NotSupportedException(
                $"Sea Power assembly {build} has not been verified. The supported build is 0.9.1-261112.");
    }
}
