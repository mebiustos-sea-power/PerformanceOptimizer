using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PerformanceOptimizer.Rendering;
using SeaPower;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PerformanceOptimizer.Optimizations;

internal sealed class PipOutputRouting : IOptimization
{
    private static PipOutputRouting? current;
    private readonly Dictionary<BlitToPipImage, PipOutputDriver> drivers = new();
    public string Id => nameof(PipOutputRouting);

    public void Install(Harmony patches)
    {
        current = this;
        InstallPatches(patches);
        foreach (var source in Resources.FindObjectsOfTypeAll<BlitToPipImage>())
            if (source.gameObject.scene.IsValid()) Attach(source);
    }

    internal void InstallPatches(Harmony patches)
    {
        Type type = typeof(BlitToPipImage);
        NativeRenderContract.Validate(NativeRenderContract.Required(type, "OnRenderImage", typeof(RenderTexture), typeof(RenderTexture)));
        patches.Patch(NativeRenderContract.Required(type, "Awake"), postfix: new HarmonyMethod(typeof(PipOutputRouting), nameof(Started)));
        patches.Patch(NativeRenderContract.Required(typeof(CameraManager), "RequestPipFrame", typeof(int), typeof(int)), postfix: new HarmonyMethod(typeof(PipOutputRouting), nameof(Refresh)));
        patches.Patch(NativeRenderContract.Required(typeof(CameraManager), "set_PipShown", typeof(bool)), postfix: new HarmonyMethod(typeof(PipOutputRouting), nameof(Refresh)));
    }

    private static void Started(BlitToPipImage __instance)
    {
        var owner = current;
        if (owner == null) return;
        try { owner.Attach(__instance); }
        catch (Exception e) { OptimizationInstaller.Failed(owner, e); }
    }

    private static void Refresh()
    {
        var owner = current;
        if (owner == null) return;
        try { foreach (var driver in owner.drivers.Values) if (driver) driver.Synchronize(); }
        catch (Exception e) { OptimizationInstaller.Failed(owner, e); }
    }

    private void Attach(BlitToPipImage source)
    {
        if (!source.enabled || drivers.ContainsKey(source)) return;
        var driver = source.gameObject.AddComponent<PipOutputDriver>();
        driver.hideFlags = HideFlags.HideAndDontSave;
        drivers.Add(source, driver);
        driver.Initialize(this, source);
    }

    internal void Forget(BlitToPipImage source) => drivers.Remove(source);

    public void Dispose()
    {
        current = null;
        var errors = new List<Exception>();
        foreach (var driver in drivers.Values.ToArray())
        {
            if (!driver) continue;
            try { driver.Release(); }
            catch (Exception e) { errors.Add(e); }
            finally { Object.Destroy(driver); }
        }
        drivers.Clear();
        if (errors.Count != 0) throw new AggregateException("PiP renderer restoration failed.", errors);
    }
}

public sealed class PipOutputDriver : MonoBehaviour
{
    private PipOutputRouting? owner;
    private BlitToPipImage? source;
    private Camera? camera;
    private RenderStageGate? gate;

    private sealed class Stage : IRenderStage
    {
        private readonly BlitToPipImage source;
        internal Stage(BlitToPipImage source) => this.source = source;
        public bool Enabled { get => source.enabled; set => source.enabled = value; }
    }

    internal void Initialize(PipOutputRouting owner, BlitToPipImage source)
    {
        this.owner = owner; this.source = source;
        camera = source.GetComponent<Camera>();
        if (!camera) throw new InvalidOperationException("BlitToPipImage has no camera.");
        gate = new RenderStageGate(new Stage(source));
        Synchronize();
    }

    internal void Synchronize()
    {
        if (gate == null || !source) return;
        bool required = false;
        if (CameraManager.InstanceExists(false))
        {
            var manager = CameraManager.Instance;
            required = manager.IsPipActive && manager.IsPipSource(camera) && manager.GetPipImage();
        }
        gate.SetRequired(required);
    }

    private void OnPreCull()
    {
        try { Synchronize(); }
        catch (Exception e) { if (owner != null) OptimizationInstaller.Failed(owner, e); }
    }

    internal void Release()
    {
        if (source) gate?.Dispose();
        gate = null; owner = null;
    }

    private void OnDestroy()
    {
        if (!ReferenceEquals(source, null)) owner?.Forget(source);
    }
}
