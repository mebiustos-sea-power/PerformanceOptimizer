using System;

namespace PerformanceOptimizer.Rendering;

internal enum OpticalCallback { None, Update, LateUpdate, Gui }

internal interface IOpticalController : IRenderStage
{
    bool RequiresRendering { get; }
    void Update();
    void LateUpdate();
    void Gui();
    void OnEnable();
    void OnDisable();
    void ResetHistory();
}

// The native component keeps its place in the image-effect chain. Its original
// controller methods are driven independently, including while its renderer is off.
internal sealed class OpticalControllerBridge : IDisposable
{
    private readonly IOpticalController controller;
    private readonly RenderStageGate gate;
    private bool disposed;
    private OpticalCallback forwarding;
    private bool drivesUpdate, drivesLate;
    private int nativeUpdateFrame = -1, nativeLateFrame = -1;
    internal bool IsChangingRenderer => gate.IsChanging;
    internal bool IsDriving => drivesUpdate;
    internal bool Allows(OpticalCallback callback, int frame)
    {
        if (forwarding == callback) return true;
        // Start can add the driver during Unity's current update traversal.
        // Let the original callback run until the driver actually takes over,
        // and remember it so that frame is never updated twice.
        if (callback == OpticalCallback.Update && !drivesUpdate)
        { nativeUpdateFrame = frame; return true; }
        if (callback == OpticalCallback.LateUpdate && !drivesLate)
        { nativeLateFrame = frame; return true; }
        return callback == OpticalCallback.Gui && !drivesUpdate;
    }

    internal OpticalControllerBridge(IOpticalController controller)
    {
        this.controller = controller;
        gate = new RenderStageGate(controller);
    }

    internal void Run(OpticalCallback callback, int frame)
    {
        if (disposed) throw new ObjectDisposedException(nameof(OpticalControllerBridge));
        if (forwarding != OpticalCallback.None)
            throw new InvalidOperationException("An optical controller callback was re-entered.");
        if (callback == OpticalCallback.Gui && !drivesUpdate) return;
        if (callback == OpticalCallback.Update && !drivesUpdate)
        {
            drivesUpdate = true;
            if (nativeUpdateFrame == frame) { Synchronize(); return; }
        }
        if (callback == OpticalCallback.LateUpdate && !drivesLate)
        {
            drivesLate = true;
            if (nativeLateFrame == frame) { Synchronize(); return; }
        }
        forwarding = callback;
        try
        {
            switch (callback)
            {
                case OpticalCallback.Update: controller.Update(); break;
                case OpticalCallback.LateUpdate: controller.LateUpdate(); break;
                case OpticalCallback.Gui: controller.Gui(); break;
                default: throw new ArgumentOutOfRangeException(nameof(callback));
            }
        }
        finally { forwarding = OpticalCallback.None; }
        if (callback != OpticalCallback.Gui) Synchronize();
    }

    internal void Synchronize()
    {
        if (disposed) return;
        bool required = controller.RequiresRendering;
        // The native inactive image branch resets this flag. Retain that behavior
        // even though that branch no longer participates in the image chain.
        if (!required) controller.ResetHistory();
        gate.SetRequired(required);
    }

    internal void OnEnable()
    {
        if (!controller.Enabled) controller.OnEnable();
    }

    internal void OnDisable()
    {
        if (!controller.Enabled) controller.OnDisable();
    }

    public void Dispose()
    {
        if (disposed) return;
        gate.Dispose();
        disposed = true;
    }
}
