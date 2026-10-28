using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace PerformanceOptimizer.Rendering;

internal sealed class NativeOpticalController : IOpticalController
{
    internal static readonly Action<FullscreenShaderToggleCycle> NativeUpdate = NativeRenderContract.Callback<FullscreenShaderToggleCycle>("Update");
    internal static readonly Action<FullscreenShaderToggleCycle> NativeLateUpdate = NativeRenderContract.Callback<FullscreenShaderToggleCycle>("LateUpdate");
    internal static readonly Action<FullscreenShaderToggleCycle> NativeGui = NativeRenderContract.Callback<FullscreenShaderToggleCycle>("OnGUI");
    private static readonly Action<FullscreenShaderToggleCycle> Enable = NativeRenderContract.Callback<FullscreenShaderToggleCycle>("OnEnable");
    private static readonly Action<FullscreenShaderToggleCycle> Disable = NativeRenderContract.Callback<FullscreenShaderToggleCycle>("OnDisable");
    private static readonly Action<FullscreenShaderToggleCycle> Reset = NativeRenderContract.Callback<FullscreenShaderToggleCycle>("ResetTemporalHistory");
    private static readonly Func<FullscreenShaderToggleCycle, Material> Material =
        (Func<FullscreenShaderToggleCycle, Material>)NativeRenderContract.Required(typeof(FullscreenShaderToggleCycle), "GetCurrentMaterial")
            .CreateDelegate(typeof(Func<FullscreenShaderToggleCycle, Material>));
    private static readonly Func<FullscreenShaderToggleCycle, bool> EffectEnabled = Getter<bool>("effectEnabled");
    private static readonly Func<FullscreenShaderToggleCycle, Texture2D> Black = Getter<Texture2D>("blackTexture");
    private static readonly Func<FullscreenShaderToggleCycle, Texture2D> White = Getter<Texture2D>("whiteTexture");
    private readonly FullscreenShaderToggleCycle source;

    internal NativeOpticalController(FullscreenShaderToggleCycle source) => this.source = source;
    internal static bool HasStarted(FullscreenShaderToggleCycle source) => Black(source) && White(source);
    public bool Enabled { get => source.enabled; set => source.enabled = value; }
    public bool RequiresRendering => EffectEnabled(source) && Material(source);
    public void Update() => NativeUpdate(source);
    public void LateUpdate() => NativeLateUpdate(source);
    public void Gui() => NativeGui(source);
    public void OnEnable() => Enable(source);
    public void OnDisable() => Disable(source);
    public void ResetHistory() => Reset(source);

    private static Func<FullscreenShaderToggleCycle, T> Getter<T>(string name)
    {
        var field = typeof(FullscreenShaderToggleCycle).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(FullscreenShaderToggleCycle).FullName, name);
        if (field.FieldType != typeof(T)) throw new InvalidOperationException("Changed optical field: " + name);
        var instance = Expression.Parameter(typeof(FullscreenShaderToggleCycle), "source");
        return Expression.Lambda<Func<FullscreenShaderToggleCycle, T>>(Expression.Field(instance, field), instance).Compile();
    }
}
