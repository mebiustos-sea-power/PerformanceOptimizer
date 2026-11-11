# Performance Optimizer

An Anchor Chain mod for Sea Power that reduces redundant work while preserving graphics settings and native simulation timing.

## Simulation allocation optimizations (0.3.0)

The simulation optimizations are enabled by default wherever they apply. Each has a setting in the configuration file:

- **SonarScratchReuse** reuses per-sonar temporary builders, a membership set and a dictionary snapshot in the improved sonar model. Native worker-owned arrays, snapshot copies, tasks, completion waits and result collection are retained.
- **AiContactScratchReuse** reuses the temporary contact-membership set for each AI instance. Contact information is cleared after each decision; no decision result is cached between updates.
- **FormationStationLookup** uses a single station lookup within each duplicated expression. It does not cache stations across calls or frames.
- **WeaponLookupAllocation** removes an empty list that is immediately overwritten by the native dictionary lookup on Sea Power 0.8.7. Sea Power 0.9.0 already omits that allocation and uses its native lookup.
- **SensorChannelEnumeration** avoids boxed enumerators in the native jammer/laser channel totals. The original list order, conditions and reactive-property writes remain; list-version checks retain mutation errors.

Scratch storage is isolated by owner and invocation, including reentrant calls. Lists/sets are cleared on normal return and exceptions; retained capacity is released with the owner or optimization. This trades retained capacity for less repeated managed allocation. Judgment intervals, contact order, formulas, random draws and simulation scheduling are unchanged. The verified game assembly is required, and pre-existing patches to these target methods are rejected with a logged error.

These changes target redundant CPU work and temporary allocations. They do not establish that garbage collection causes a particular scenario's stuttering or guarantee smoother play.

## Optimizations

Version 0.2.0 introduced two image-effect routing changes alongside the ocean optimization. All three are enabled by default and can now be disabled independently.

- **PipOutputRouting** removes the PiP forwarding stage from cameras that are not currently providing a requested PiP image. The native PiP image, source-camera selection, downsampling and viewport remain responsible for the displayed result.
- **OpticalEffectRouting** retains the native optical renderer in its original position, but includes it only while a valid image effect is active. A separate driver invokes the original input, late-update and GUI methods, preserving mode changes, transitions and temporal history while the renderer is idle. Internal renderer switches do not invoke semantic enable/disable resets; actual GameObject lifecycle is forwarded when needed.
- **OceanReadbackDispatch** skips the displacement texture-to-buffer conversion while that buffer has an outstanding request. Native request/collection/copy/clearing order is retained.

The compatibility checks for image-effect routing and simulation optimizations recognize Sea Power 0.8.7-261021 and 0.9.0-261111. They reject other game assemblies and pre-existing patches to the methods they must own. Errors are logged, affected components are restored and only that optimization is removed. Graphics quality, depth/normal requirements, ocean detail, frame caps and simulation scheduling are unchanged. There is no promised GPU/power reduction; results depend on view, resolution and active effects.

## Installation

Requires BepInEx and Anchor Chain. When ready to install, copy the completed `dist/PerformanceOptimizer` folder to `Sea Power_Data/StreamingAssets/PerformanceOptimizer`, enable Anchor Chain and Performance Optimizer in the game's mod manager, then restart Sea Power.

The build never deploys files to the game or starts/attaches to a game process. Do not install while another development session is using the game.

## Configuration

Each applicable optimization can be disabled independently. The settings file is stored outside the installed mod and Workshop folders:

```text
BepInEx/config/mebiustos.performanceoptimizer.ini
```

This path is relative to the game directory for a standard BepInEx installation; the mod uses BepInEx's configured `Paths.ConfigPath` and logs the full path at startup. Copy the included `mebiustos.performanceoptimizer.ini.example` there, removing `.example` from the name. The example lists every available setting:

```ini
[Optimizations]
OceanReadbackDispatch=true
PipOutputRouting=true
OpticalEffectRouting=true
SonarScratchReuse=true
AiContactScratchReuse=true
FormationStationLookup=true
WeaponLookupAllocation=true
SensorChannelEnumeration=true
```

Set any item to `false` to disable only that optimization. If the file or an entry is absent, the applicable optimization is enabled. Sea Power 0.9.0 accepts the existing `WeaponLookupAllocation` setting and uses its native lookup regardless of that value. The file is not automatically created or overwritten. Settings are read only at startup to decide which patches to install, so changing them requires a game restart and adds no per-frame configuration checks. Disabling the mod in the game's mod manager still prevents all its optimizations from being installed.

If you previously created `optimizations.ini` beside the mod DLL, move it to the new location and rename it to `mebiustos.performanceoptimizer.ini` to retain your settings. If the new file already exists, merge your old values into it. The old file is no longer read; a startup warning identifies the new path when an old file remains. Missing entries in an older file use the enabled default.

Unknown IDs and invalid Boolean values generate startup warnings and retain the existing/default setting. Malformed syntax or duplicate valid settings stop initialization with an error. Each optimization has its own Harmony owner; an installation failure is logged and its patches are rolled back while other optimizations can continue.

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

## Simulation verification

The original and transformed game IL is compared at managed boundaries for weapon lookup, formation following and sensor-channel totals. Tests cover ordering, duplicates, mutable copies, missing keys, on/off and occupied channels, property writes, and same-count list mutations. Production scratch helpers are exercised with growing/shrinking/empty inputs, independent snapshots, separate owners, nested calls, exceptions and removal during an active invocation. Warmed scratch-list/set updates allocate zero managed bytes in the .NET test host; this is not a measurement of game-wide allocation or GC pauses.

The separate game-Mono process also executes these comparisons and installs the production scratch prefix/finalizer on managed fixtures, including exception recovery and the game's private sonar snapshot type. Preparing detours for full native simulation methods can initialize Unity-dependent globals, which a standalone host cannot supply. Live Unity verification is required in addition to these offline checks.

On 2026-11-05, the 0.3.0 candidate loaded all five new optimizations and the three existing ones in two separate normal launches of Sea Power 0.8.7-261021. B6 contained 26 units, 16 sonars and nine formation memberships. Query checks matched for 11 units with weapon mappings, nine formation memberships and 22 living units' channel totals, before and after normal simulation. AI contact evaluation and both passive/active improved-sonar scheduling were observed over approximately six minutes of sampled normal progress; no exceptions were recorded in the monitored methods. Restarting and loading B6 again succeeded. Same-process return-to-menu/reload, explicit formation reassignment and destruction stress tests are not covered. This was a bounded functional check, not an ON/OFF performance or GC-stutter comparison.

## Rendering verification

On Sea Power 0.8.7-261021, paired captures of the same paused B6 scene matched exactly in displayed RGB and alpha with routing enabled/disabled: normal 3840x2160, native LLLTV-BW material profile, native PiP 640x360, and PiP with that optical profile. Native Update/LateUpdate/GUI body counts matched; the two idle image callbacks were absent and frame draw calls decreased by two in the normal view. The temporary helper held the native PiP visibility request for texture capture and exercised native optical configuration/cycle/toggle methods. This is bounded rendering verification, not exhaustive scene/underwater/multi-display coverage or proof of GPU savings.
