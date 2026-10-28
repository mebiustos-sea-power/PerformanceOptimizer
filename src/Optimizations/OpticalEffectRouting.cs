using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PerformanceOptimizer.Rendering;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PerformanceOptimizer.Optimizations;

internal sealed class OpticalEffectRouting : IOptimization
{
    private static OpticalEffectRouting? current;
    private readonly Dictionary<FullscreenShaderToggleCycle, OpticalControllerDriver> drivers = new();
    public string Id => nameof(OpticalEffectRouting);

    public void Install(Harmony patches)
    {
        current = this;
        InstallPatches(patches);
        foreach (var source in Resources.FindObjectsOfTypeAll<FullscreenShaderToggleCycle>())
            if (source.gameObject.scene.IsValid() && NativeOpticalController.HasStarted(source)) Attach(source);
    }

    internal void InstallPatches(Harmony patches)
    {
        Type type = typeof(FullscreenShaderToggleCycle);
        string[] callbacks = { "Update", "LateUpdate", "OnGUI", "OnEnable", "OnDisable" };
        NativeRenderContract.Validate(callbacks.Select(n => NativeRenderContract.Required(type, n))
            .Concat(new[] { NativeRenderContract.Required(type, "OnRenderImage", typeof(RenderTexture), typeof(RenderTexture)) }).ToArray());
        // Resolve private native methods before attaching any driver. Delegates
        // retain the original control implementation; input logic is not copied.
        _ = NativeOpticalController.NativeUpdate;
        patches.Patch(NativeRenderContract.Required(type, "Start"), postfix: new HarmonyMethod(typeof(OpticalEffectRouting), nameof(Started)));
        patches.Patch(NativeRenderContract.Required(type, "Update"), prefix: new HarmonyMethod(typeof(OpticalEffectRouting), nameof(UpdatePrefix)));
        patches.Patch(NativeRenderContract.Required(type, "LateUpdate"), prefix: new HarmonyMethod(typeof(OpticalEffectRouting), nameof(LatePrefix)));
        patches.Patch(NativeRenderContract.Required(type, "OnGUI"), prefix: new HarmonyMethod(typeof(OpticalEffectRouting), nameof(GuiPrefix)));
        foreach (string name in new[] { "OnEnable", "OnDisable" })
            patches.Patch(NativeRenderContract.Required(type, name), prefix: new HarmonyMethod(typeof(OpticalEffectRouting), nameof(LifecyclePrefix)));
        foreach (string name in new[] { "Configure", "ClearOpticalConfiguration", "ToggleEffectImmediately", "CycleMaterial", "UpdatePowerReveal" })
        {
            var method = type.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?? throw new MissingMethodException(type.FullName, name);
            patches.Patch(method, postfix: new HarmonyMethod(typeof(OpticalEffectRouting), nameof(StateChanged)));
        }
    }

    private static OpticalControllerDriver? Find(FullscreenShaderToggleCycle source) =>
        current != null && current.drivers.TryGetValue(source, out var driver) ? driver : null;
    private static bool UpdatePrefix(FullscreenShaderToggleCycle __instance) => Find(__instance)?.Allows(OpticalCallback.Update) ?? true;
    private static bool LatePrefix(FullscreenShaderToggleCycle __instance) => Find(__instance)?.Allows(OpticalCallback.LateUpdate) ?? true;
    private static bool GuiPrefix(FullscreenShaderToggleCycle __instance) => Find(__instance)?.Allows(OpticalCallback.Gui) ?? true;
    private static bool LifecyclePrefix(FullscreenShaderToggleCycle __instance) => Find(__instance)?.AllowsLifecycle ?? true;

    private static void Started(FullscreenShaderToggleCycle __instance)
    {
        var owner = current;
        if (owner == null) return;
        try { owner.Attach(__instance); }
        catch (Exception e) { OptimizationInstaller.Failed(owner, e); }
    }

    private static void StateChanged(FullscreenShaderToggleCycle __instance)
    {
        var driver = Find(__instance);
        if (driver) driver.SynchronizeSafely();
    }

    private void Attach(FullscreenShaderToggleCycle source)
    {
        if (!source.enabled || drivers.ContainsKey(source)) return;
        var driver = source.gameObject.AddComponent<OpticalControllerDriver>();
        driver.hideFlags = HideFlags.HideAndDontSave;
        drivers.Add(source, driver);
        driver.Initialize(this, source);
    }

    internal void Forget(FullscreenShaderToggleCycle source) => drivers.Remove(source);

    public void Dispose()
    {
        var errors = new List<Exception>();
        // Keep the registry while restoring: these internal enabled changes must
        // not reset native rotation ghosting or release its temporal history.
        foreach (var driver in drivers.Values.ToArray())
        {
            if (!driver) continue;
            try { driver.Release(); }
            catch (Exception e) { errors.Add(e); }
            finally { Object.Destroy(driver); }
        }
        drivers.Clear(); current = null;
        if (errors.Count != 0) throw new AggregateException("Optical renderer restoration failed.", errors);
    }
}

public sealed class OpticalControllerDriver : MonoBehaviour
{
    private OpticalEffectRouting? owner;
    private FullscreenShaderToggleCycle? source;
    private OpticalControllerBridge? controller;
    internal bool Allows(OpticalCallback callback) => controller?.Allows(callback, Time.frameCount) ?? true;
    internal bool AllowsLifecycle => controller?.IsChangingRenderer != true;

    internal void Initialize(OpticalEffectRouting owner, FullscreenShaderToggleCycle source)
    {
        this.owner = owner; this.source = source;
        controller = new OpticalControllerBridge(new NativeOpticalController(source));
    }

    private void Update() => Run(OpticalCallback.Update);
    private void LateUpdate() => Run(OpticalCallback.LateUpdate);
    private void OnGUI() => Run(OpticalCallback.Gui);
    private void OnPreCull() => SynchronizeSafely();

    private void Run(OpticalCallback callback)
    {
        if (controller == null || !source) return;
        try { controller.Run(callback, Time.frameCount); }
        catch (Exception e) { if (owner != null) OptimizationInstaller.Failed(owner, e); }
    }

    internal void SynchronizeSafely()
    {
        if (controller == null || !controller.IsDriving || !source) return;
        try { controller.Synchronize(); }
        catch (Exception e) { if (owner != null) OptimizationInstaller.Failed(owner, e); }
    }

    private void OnEnable()
    {
        if (controller == null || !source) return;
        try { controller.OnEnable(); }
        catch (Exception e) { if (owner != null) OptimizationInstaller.Failed(owner, e); }
    }

    private void OnDisable()
    {
        if (controller == null || !source) return;
        try { controller.OnDisable(); }
        catch (Exception e) { if (owner != null) OptimizationInstaller.Failed(owner, e); }
    }

    internal void Release()
    {
        if (source) controller?.Dispose();
        controller = null; owner = null;
    }

    private void OnDestroy()
    {
        if (!ReferenceEquals(source, null)) owner?.Forget(source);
    }
}
