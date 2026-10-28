# Performance Optimizer

An Anchor Chain mod for Sea Power that reduces redundant work while preserving graphics settings and native simulation timing.

## Optimizations

Version 0.2.0 adds two image-effect routing changes. They are applied together when Performance Optimizer is enabled; there are no separate settings for these two changes.

- **PipOutputRouting** removes the PiP forwarding stage from cameras that are not currently providing a requested PiP image. The native PiP image, source-camera selection, downsampling and viewport remain responsible for the displayed result.
- **OpticalEffectRouting** retains the native optical renderer in its original position, but includes it only while a valid image effect is active. A separate driver invokes the original input, late-update and GUI methods, preserving mode changes, transitions and temporal history while the renderer is idle. Internal renderer switches do not invoke semantic enable/disable resets; actual GameObject lifecycle is forwarded when needed.
- **OceanReadbackDispatch** skips the displacement texture-to-buffer conversion while that buffer has an outstanding request. Native request/collection/copy/clearing order is retained.

Image-effect routing is verified against Sea Power 0.8.7-261021 and rejects an unverified game assembly or pre-existing patches to the callbacks it must own. Errors are logged, affected components are restored and only that optimization is removed. Graphics quality, depth/normal requirements, ocean detail, frame caps and simulation scheduling are unchanged. There is no promised GPU/power reduction; results depend on view, resolution and active effects.

## Installation

Requires BepInEx and Anchor Chain. When ready to install, copy the completed `dist/PerformanceOptimizer` folder to `Sea Power_Data/StreamingAssets/PerformanceOptimizer`, enable Anchor Chain and Performance Optimizer in the game's mod manager, then restart Sea Power.

The build never deploys files to the game or starts/attaches to a game process. Do not install while another development session is using the game.

## Configuration

The new image-effect routing is enabled with the mod. The existing ocean setting remains available. To disable the ocean item, copy `optimizations.ini.example` to `optimizations.ini` beside the installed DLL and use:

```ini
[Optimizations]
OceanReadbackDispatch=false
```

Restart the game after changes. The mod never rewrites this file. Unknown IDs and invalid Boolean values generate startup warnings and retain the existing/default setting. Malformed syntax or duplicate valid settings stop initialization with an error. Each optimization has its own Harmony owner; an installation failure is logged and its patches are rolled back while other optimizations can continue.

## Build and tests

Use the Windows .NET 10 SDK. Copy the following into an untracked `Directory.Build.local.props`, adjusting paths:

```xml
<Project><PropertyGroup>
  <SeaPowerDir>N:\Games\Steam\steamapps\common\Sea Power</SeaPowerDir>
  <AnchorChainDir>N:\Games\Steam\steamapps\workshop\content\1286220\3380210757</AnchorChainDir>
</PropertyGroup></Project>
```

Run `powershell -NoProfile -File build.ps1 -Test`. Outputs are written under this project only. The package contains no game, Unity, BepInEx or Anchor Chain binaries.

Tests execute the game's original and rewritten readback IL with managed replacements for GPU/engine boundaries, including the actual request and completion control flow. They compare request timing, delivered values, errors and independent grids, and verify that redundant conversions are removed. Routing tests exercise control updates, the first switching frame, GUI callbacks, native lifecycle, history and restoration. An additional separate process uses the game's bundled Mono to install and remove the actual ocean Harmony patch, test installation isolation and verify cached open-controller delegates obey Harmony guards. These tests do not initialize Unity rendering, attach to a running game, or prove live GPU savings.

The isolated Mono installation check may print unresolved Unity internal-call diagnostics for SystemInfo while JIT-compiling the real target. This test host has no Unity player to register those calls. The target is installed and removed but never executed against native graphics; GPU boundaries are simulated in the behavioral tests. These diagnostics alone do not indicate a damaged game installation.

## Rendering verification

On Sea Power 0.8.7-261021, paired captures of the same paused B6 scene matched exactly in displayed RGB and alpha with routing enabled/disabled: normal 3840x2160, native LLLTV-BW material profile, native PiP 640x360, and PiP with that optical profile. Native Update/LateUpdate/GUI body counts matched; the two idle image callbacks were absent and frame draw calls decreased by two in the normal view. The temporary helper held the native PiP visibility request for texture capture and exercised native optical configuration/cycle/toggle methods. This is bounded rendering verification, not exhaustive scene/underwater/multi-display coverage or proof of GPU savings.
