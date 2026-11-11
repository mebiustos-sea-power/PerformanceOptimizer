using System;
using SeaPower;

namespace PerformanceOptimizer;

internal static class GameAssemblyCompatibility
{
    internal static readonly Guid SeaPower087 = new("45d05d04-62a4-4675-99dd-8be00c74e539");
    internal static readonly Guid SeaPower090 = new("cc2f4c48-9fd9-47f0-a37c-896481c22c45");

    internal static Guid Current => typeof(ObjectBase).Module.ModuleVersionId;

    internal static void RequireVerified(Guid build)
    {
        if (build != SeaPower087 && build != SeaPower090)
            throw new NotSupportedException(
                $"Sea Power assembly {build} has not been verified. Supported builds are 0.8.7-261021 and 0.9.0-261111.");
    }
}
