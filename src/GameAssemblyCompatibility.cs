using System;
using SeaPower;

namespace PerformanceOptimizer;

internal static class GameAssemblyCompatibility
{
    private static readonly Guid VerifiedBuild = new("cc2f4c48-9fd9-47f0-a37c-896481c22c45");

    internal static Guid Current => typeof(ObjectBase).Module.ModuleVersionId;

    internal static void RequireVerified(Guid build)
    {
        if (build != VerifiedBuild)
            throw new NotSupportedException(
                $"Sea Power assembly {build} has not been verified. The supported build is 0.9.0-261111.");
    }
}
