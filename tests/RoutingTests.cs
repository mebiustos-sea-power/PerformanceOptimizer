using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PerformanceOptimizer.Rendering;
using static Program;

internal static class RoutingTests
{
    internal static void Run()
    {
        BothUpdateOrders();
        LogicalLifecycleAndHistory();
        NativeDisableAndExternalChanges();
        ControllerErrorsAreVisible();
        Console.WriteLine("PASS render routing: native control callbacks survive idle rendering, transitions, first-frame ordering, lifecycle and restoration");
    }

    private sealed class Optical : IOpticalController
    {
        internal OpticalControllerBridge Bridge = null!;
        internal bool Effect, Material = true, Toggle;
        internal bool History, ThrowUpdate;
        internal int Frame, Enables, Disables;
        internal readonly Dictionary<int, int> Updates = new(), Lates = new(), Guis = new();
        private bool enabled = true;
        public bool Enabled
        {
            get => enabled;
            set
            {
                if (enabled == value) return;
                enabled = value;
                if (Bridge.IsChangingRenderer) return;
                if (value) OnEnable(); else OnDisable();
            }
        }
        public bool RequiresRendering => Effect && Material;
        public void Update()
        {
            if (!Bridge.Allows(OpticalCallback.Update, Frame)) return;
            Add(Updates);
            if (ThrowUpdate) throw new InvalidOperationException("Native control failure");
            if (Toggle) { Effect = !Effect; Toggle = false; History = false; }
        }
        public void LateUpdate() { if (Bridge.Allows(OpticalCallback.LateUpdate, Frame)) Add(Lates); }
        public void Gui() { if (Bridge.Allows(OpticalCallback.Gui, Frame)) Add(Guis); }
        public void OnEnable() { Enables++; }
        public void OnDisable() { Disables++; History = false; }
        public void ResetHistory() => History = false;
        private void Add(Dictionary<int, int> values) { values.TryGetValue(Frame, out int count); values[Frame] = count + 1; }
    }

    private static Optical Make()
    {
        var native = new Optical();
        native.Bridge = new OpticalControllerBridge(native);
        return native;
    }

    private static void BothUpdateOrders()
    {
        foreach (bool nativeFirst in new[] { true, false })
        {
            var n = Make();
            for (int frame = 0; frame < 60; frame++)
            {
                n.Frame = frame;
                n.Toggle = frame == 3 || frame == 17 || frame == 24 || frame == 48;
                n.Material = frame < 30 || frame >= 34;
                if (nativeFirst && n.Enabled) n.Update();
                n.Bridge.Run(OpticalCallback.Update, frame);
                if (!nativeFirst && n.Enabled) n.Update();
                if (nativeFirst && n.Enabled) n.LateUpdate();
                n.Bridge.Run(OpticalCallback.LateUpdate, frame);
                if (!nativeFirst && n.Enabled) n.LateUpdate();
                for (int guiEvent = 0; guiEvent < 2; guiEvent++)
                {
                    if (nativeFirst && n.Enabled) n.Gui();
                    n.Bridge.Run(OpticalCallback.Gui, frame);
                    if (!nativeFirst && n.Enabled) n.Gui();
                }
                Require(n.Updates[frame] == 1 && n.Lates[frame] == 1 && n.Guis[frame] == 2,
                    "A control callback was lost or repeated when renderer participation changed.");
                Require(n.Enabled == n.RequiresRendering, "An idle renderer stayed in the image chain or an active effect disappeared.");
            }
            Require(n.Enables == 0 && n.Disables == 0, "Renderer gating ran semantic native lifecycle callbacks.");
            n.Bridge.Dispose();
            Require(n.Enabled && n.Enables == 0, "Restoration changed controller state instead of only returning renderer ownership.");
        }
    }

    private static void LogicalLifecycleAndHistory()
    {
        var n = Make();
        n.Bridge.Run(OpticalCallback.Update, 0);
        n.History = true;
        n.Bridge.Synchronize();
        Require(!n.History && !n.Enabled, "The inactive native branch stopped invalidating temporal history.");
        n.Bridge.OnDisable(); n.Bridge.OnEnable();
        Require(n.Disables == 1 && n.Enables == 1, "A logically enabled controller lost GameObject lifecycle while its renderer was idle.");
        n.Effect = true; n.Bridge.Synchronize(); n.History = true;
        n.Bridge.Synchronize();
        Require(n.History && n.Disables == 1 && n.Enables == 1, "Active temporal history was reset by renderer synchronization.");
        // Unity sends the native lifecycle when the actual renderer is enabled.
        n.OnDisable(); n.Bridge.OnDisable(); n.OnEnable(); n.Bridge.OnEnable();
        Require(n.Disables == 2 && n.Enables == 2, "Active native lifecycle was forwarded a second time.");
        n.Bridge.Dispose();
    }

    private sealed class Stage : IRenderStage
    {
        public bool Enabled { get; set; }
    }

    private static void NativeDisableAndExternalChanges()
    {
        var disabled = new Stage { Enabled = false };
        using (var gate = new RenderStageGate(disabled))
        { gate.SetRequired(true); Require(!disabled.Enabled, "A game-disabled effect was enabled by routing."); }
        Require(!disabled.Enabled, "A game-disabled effect was enabled during restoration.");
        var native = new Stage { Enabled = true };
        var owned = new RenderStageGate(native);
        owned.SetRequired(false); owned.SetRequired(true); owned.SetRequired(false);
        owned.Dispose(); owned.Dispose();
        Require(native.Enabled, "Removing the optimization failed to restore the native image chain.");
        var external = new RenderStageGate(native);
        external.SetRequired(false); native.Enabled = true;
        Throws<InvalidOperationException>(() => external.SetRequired(false));
        external.Dispose();
        Require(native.Enabled, "An observed external change was overwritten.");
    }

    private static void ControllerErrorsAreVisible()
    {
        var n = Make(); n.ThrowUpdate = true;
        Throws<InvalidOperationException>(() => n.Bridge.Run(OpticalCallback.Update, 0));
        Require(!n.Bridge.Allows(OpticalCallback.Update, 0), "A failing controller left its forwarding scope open.");
        n.Bridge.Dispose();
        Require(n.Enabled, "A failed controller could not return the renderer to native ownership.");
    }

    internal sealed class DelegateProbe
    {
        internal int Calls;
        [MethodImpl(MethodImplOptions.NoInlining)] public void Update() => Calls++;
    }
    private static bool allowDelegate;
    private static bool DelegatePrefix() => allowDelegate;

    internal static void InstallOnMono()
    {
        var method = typeof(DelegateProbe).GetMethod(nameof(DelegateProbe.Update))!;
        var invoke = (Action<DelegateProbe>)method.CreateDelegate(typeof(Action<DelegateProbe>));
        var patches = new Harmony("mebiustos.performanceoptimizer.tests.controller-delegates");
        try
        {
            patches.Patch(method, prefix: new HarmonyMethod(typeof(RoutingTests), nameof(DelegatePrefix)));
            var probe = new DelegateProbe();
            allowDelegate = false; invoke(probe); probe.Update();
            Require(probe.Calls == 0, "A cached native control delegate bypassed Harmony's callback ownership guard.");
            allowDelegate = true; invoke(probe);
            allowDelegate = false; probe.Update();
            Require(probe.Calls == 1, "Forwarded control was lost or duplicated on the game's Mono runtime.");
        }
        finally { patches.UnpatchSelf(); }
        Console.WriteLine("PASS game Mono: cached open delegates obey Harmony control callback ownership");
    }
}
