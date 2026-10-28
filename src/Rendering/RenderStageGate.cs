using System;

namespace PerformanceOptimizer.Rendering;

internal interface IRenderStage
{
    bool Enabled { get; set; }
}

// Controls participation in Unity's image-effect chain, not the contents of an
// OnRenderImage destination. The original enabled state belongs to the game.
internal sealed class RenderStageGate : IDisposable
{
    private readonly IRenderStage stage;
    private readonly bool original;
    private bool expected;
    private bool disposed;
    internal bool IsChanging { get; private set; }

    internal RenderStageGate(IRenderStage stage)
    {
        this.stage = stage;
        original = expected = stage.Enabled;
    }

    internal void SetRequired(bool required)
    {
        if (disposed) throw new ObjectDisposedException(nameof(RenderStageGate));
        if (stage.Enabled != expected)
            throw new InvalidOperationException("The image-effect enabled state changed outside Performance Optimizer.");
        Set(original && required);
    }

    private void Set(bool enabled)
    {
        if (stage.Enabled == enabled) return;
        IsChanging = true;
        expected = enabled;
        try { stage.Enabled = enabled; }
        finally { IsChanging = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        // Preserve an observed external change instead of overwriting its owner.
        if (stage.Enabled == expected) Set(original);
        disposed = true;
    }
}
