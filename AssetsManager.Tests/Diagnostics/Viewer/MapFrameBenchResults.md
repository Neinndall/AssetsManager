# Base_SRX viewport rendering audit

Measured on 2026-10-03 using the extracted desktop Map11 project, application full-resource loading, NVIDIA RTX 4060 Ti, OpenGL 3.3 / driver 610.47, Release builds and a 1280 x 720 offscreen framebuffer. Reference production code: c195f5ce. No changes to VFX parsing, simulation, emission, timing, particle capacity, quality or shader source.

Each configuration restarts particle graphs, warms 90 frames and measures 90 completed frames. Simulation advances by 1/60 s; moving cases rotate the camera. `gl.Finish` belongs only to the diagnostic. Timings vary with JIT warmup and machine load; these are sampled renderer costs, not an interactive WPF latency or LTK FPS comparison.

| Moving camera | Original median / p95 (ms) | Optimized median / p95 (ms) | Original / optimized managed bytes per frame |
|---|---:|---:|---:|
| Overview, shaders | 2.18 / 4.28 | 1.85 / 3.00 | 240040 / 81288 |
| Overview, VFX | 14.47 / 17.14 | 13.40 / 16.16 | 842458 / 371125 |
| Overview, shaders + VFX | 16.55 / 19.58 | 14.06 / 17.86 | 1068778 / 438693 |
| Close, shaders | 2.09 / 3.85 | 1.91 / 3.09 | 240040 / 81288 |
| Close, VFX | 5.91 / 7.60 | 5.50 / 7.90 | 364395 / 151608 |
| Close, shaders + VFX | 7.13 / 10.26 | 6.55 / 10.18 | 598059 / 226520 |

The overview with both enabled spends about 15% less median frame time and allocates about 59% fewer managed bytes. Close-view tail times are not consistently improved. Effects still add substantial work compared with their disabled state.

Changes remove repeated shader-state resets, unchanged uniform and constant-buffer uploads, temporary sampler/texture lookup allocations, duplicate stock texture preparation before native passes, and per-particle material preparation within a mesh emitter. Dynamic particle buffers replace their store so in-flight draws can retain previous data. Stock resources, custom materials and per-particle mesh animation remain supported.

The LTK source audit found instanced particle meshes. Base_SRX overview had 185 active mesh emitters / 284 mesh instances and 589 other active emitters at the final moving frame. Mesh instancing was not introduced in this change; material preparation is shared without changing particle draw order or skinning.

Validation:

- Release and normal application builds: zero errors and warnings.
- Viewer regression suite: 1300 passed, 7 skipped. The two exhaustive unsupported-query permutation cases were excluded because the shader translator is unchanged; an earlier unfiltered run was stopped during that sweep.
- Eight stationary RGBA captures (overview/close, all shader/VFX combinations) are byte-identical to the original production code.
- Native + stock wire overlay and standalone Wireframe render the real scene without OpenGL errors.
- The original structure renderer wrote a stock uniform while a native program was active; the optimized run eliminates that observed OpenGL invalid-location error.

The diagnostic exercises map geometry, structures, VFX and map post effects. It does not reproduce the WPF host, input dispatch, sky, bloom composition, terrain depth capture or antialiasing. Final interactive smoothness requires checking the Release executable in the actual viewport.

Run from the application repository:

```powershell
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -c Release -- map-frame-bench "<extracted Map11 root>" "<capture output directory>"
```

Captures are 1280 x 720 RGBA8 in OpenGL row order. `--allow-gl-errors` as the third diagnostic argument permits inspection of an older failing renderer and skips wire validation. Base_SRX selection and standard Riot installation paths are currently specific to this local diagnostic.

## Debug navigation follow-up

The user reported remaining camera stutter in Debug after the first optimization. GLWpfControl 4.3.6 draws synchronously on the WPF dispatcher, including graphics interop synchronization. Its continuous visual invalidation can schedule rendering ahead of pending input. 3D Studio now coalesces composition-driven frame requests at Background priority, allowing queued Input operations to run before invalidation, pauses requests while invisible, and cancels them on deactivation/disposal. GL drawing and simulation remain on their original thread. Timeline refreshes are coalesced at Background priority and skipped while the timeline is hidden.

The scalar/vector uniform cache now compares primitive fields directly and rejects inactive locations before converting floats. Native quad camera calculations and matrix uploads are shared per program within each render pass, with camera state reset for each pass. Debug optimization settings, VFX simulation and shader sources remain unchanged.

Same-scene Debug measurements, before this follow-up versus its final build:

| Moving camera | Before median / p95 (ms) | After median / p95 (ms) |
|---|---:|---:|
| Overview, VFX | 27.51 / 33.17 | 27.73 / 32.39 |
| Overview, shaders + VFX | 31.81 / 40.55 | 31.86 / 38.95 |
| Close, shaders + VFX | 15.00 / 19.51 | 14.15 / 18.48 |

There is no demonstrated overview median-time improvement from this follow-up. Input scheduling is separate from renderer throughput; the offscreen benchmark does not measure actual mouse latency. Debug overview rendering remains substantially more expensive than Release and cannot support a claim of disabled-effects smoothness.

Validation: Debug and Release builds have zero errors/warnings; the Viewer suite passes 1303 tests with 7 skipped and the same two exhaustive translator cases excluded. Dispatcher checks cover pending input updating a camera before drawing, coalescing frame requests, repeated activation, stopping/restarting, and disposal. Eight Debug RGBA captures match the pre-follow-up captures byte for byte. Native/stock wire transitions pass without OpenGL errors. Interactive validation in the actual WPF viewport remains necessary.

## Shader preparation and WPF input follow-up

The linked-program cache already shared compatible GPU programs, but new material owners still read and reflected the same WAD bytecode before checking that cache. A bounded read cache now shares stage bytecode and reflection for equivalent shader paths, material kind and compile defines. Each material retains its own parameters, runtime switches, textures and render state. Cache retention is capped at 512 requests and 16 MiB of raw bytecode; reflection and request metadata add overhead. Disposal releases the read cache. Shader selection, translation and fallback rules are unchanged.

Cold navigation circles the map twice for 540 frames per lap. With an already-warmed driver cache, sampled first-frame preparation fell from 384.54 to 245.45 ms after this change. Median lap-zero time was 19.85 versus 20.95 ms; warmed lap-one time was 19.76 versus 18.81 ms. This supports reduced initial preparation, not a consistent steady-state improvement. An earlier 679.18 ms first frame also included driver compilation; it is not a controlled comparison. The warmed lap had no frames above 50 ms in these runs. These measurements do not establish the cause of every intermittent application pause.

The visible WPF diagnostic uses GLWpfControl 4.3.6 and the production map, structure, VFX, post-effect, terrain-depth and FXAA renderers. A timer queues coalesced camera updates at Input priority every 16 ms. Each mode renders 240 frames, with the first 30 excluded from distributions. No diagnostic `gl.Finish` is used. Callback intervals include dispatcher and graphics interop delays; input delay measures queued callback execution, not OS mouse-to-screen latency or presented FPS.

| Debug WPF mode | Draw median / p95 (ms) | Callback interval median / p95 (ms) | Input samples | Input delay median / p95 / max (ms) |
|---|---:|---:|---:|---:|
| Automatic, shaders | 3.13 / 3.61 | 6.04 / 6.45 | 80 | 0.94 / 3.88 / 10.40 |
| Automatic, shaders + VFX | 26.21 / 28.27 | 27.17 / 34.99 | 10 | 545.19 / 568.02 / 568.02 |
| Input-priority scheduler, shaders + VFX | 26.91 / 30.46 | 27.80 / 32.22 | 210 | 20.61 / 28.86 / 38.49 |

Automatic rendering starved queued Input operations under VFX load; the scheduler allowed them to execute between frames. Rendering cost remains substantial and is not consistently lower in the scheduled case. Modes run sequentially, so shader/driver warmup favors the later mode; warmup frames are excluded, but this is not a randomized throughput comparison. The diagnostic omits the full Studio UI, sky and champion bloom. Another application viewport was open during measurements. The initial diagnostic called control startup after its load event and produced invalid results; those runs are discarded. The corrected host starts before showing its window and all three modes complete without OpenGL errors.

Viewer regression validation: 1306 passed, 7 skipped, with the same two exhaustive translator cases excluded. New tests check that shader request equivalence preserves independent material state and isolates stage, kind and compile-define changes. All eight Debug stationary RGBA captures after the read-cache change match the pre-cache captures byte for byte. The completed real-map benchmark and wire transitions report no OpenGL errors. Debug and Release builds have zero warnings/errors; output is isolated to avoid overwriting the user's open application.

```powershell
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- map-frame-bench "<extracted Map11 root>" --cold-navigation
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- map-frame-bench "<extracted Map11 root>" --wpf-navigation
```

## Full application navigation trace

The user reproduced navigation with VFX enabled in the actual Debug application. A 30-second EventPipe sampled-thread-time trace captured 455 UI render segments: sampled median 13.58 ms, p95 40.81 ms and maximum 134.56 ms. Intervals between sampled callback starts reached 1997.90 ms, with p95 279.16 ms. Sampling can miss short callbacks and does not provide exact frame timing; this trace establishes significant gaps as well as expensive VFX draws, not actual presented FPS.

UI stacks attributed 6070 ms to VFX color rendering out of 8253 ms inside the viewport callback. Driver calls included 1377 ms in program binding and 1073 ms in capability queries. These are inclusive sampled wall-clock estimates, not pure CPU execution times. Eight GC collections (7 generation-zero, 1 generation-one, no generation-two) suspended the runtime for 10.06 ms total, with maximum 4.93 ms. Profiler-induced SuspendOther events are excluded from those GC figures. This trace does not support large GC pauses as the cause of the observed freezes.

WPF postpones Background/Input-priority dispatcher work while native input is pending. Scheduling all invalidation requests at Background priority left a redraw vulnerable to sustained input, a condition not exercised by the earlier timer-coalesced diagnostic. Requests now normally run after queued input, but a pool timer promotes an overdue request after an 8 ms eligibility threshold. This prevents indefinite input starvation of drawing; it does not cap FPS or promise an 8 ms completed frame. Timer and request work are canceled on stop/disposal. A dispatcher test keeps queuing Input work continuously and requires a pending frame to execute before that stream ends. The normal pending-input ordering and lifecycle tests remain.

Every particle emitter already establishes its program and culling state before drawing. Meshes no longer query/restore culling between emitters, and quad/mesh/projection draws no longer switch back to an unused stock program before the next emitter. Caller state remains restored at the enclosing render/batch boundary, with stock uniform references reset there as well. Four GPU tests cover mixed mesh/quad shaded and wire passes, enabled/disabled caller culling, and batched/standalone rendering, checking caller program, culling, winding and GL errors. Simulation, emission, lifetimes, shaders and authored draw order remain unchanged.

The corrected visible-host stress run kept the Input queue continuously occupied for 313459 updates and completed all 240 shaders/VFX frames. After 30 warmup frames, sampled draw median/p95/max was 21.73/23.90/28.07 ms; callback intervals were 30.91/34.09/39.31 ms, and timer-queued input delay was 12.16/22.84/27.64 ms. This is a deliberately sustained dispatcher workload, not a measurement of the user's physical mouse or a controlled comparison against the automatic mode. Cold activation still took 476.20 ms in that run. Manual navigation in the full application remains necessary.

The map's Sru_braziers_fire_temp graph was also advanced for 120 simulated seconds without rendering: live counts at 10/30/60/120 seconds were 55/49/48/56, with an observed peak of 59. Particles retire when their authored lifetime expires; this sampled fire system did not exhibit unbounded live-particle growth. The persistent brazier emitter is expected to continue spawning particles. This is a CPU lifetime check, not a GPU memory-leak audit.

One combined isolated WPF/OpenGL test invocation stalled and was canceled. Each group passed when run separately. These new graphics regression groups now use a nonparallel collection to avoid concurrent native graphics initialization. The actual application was not terminated by the test cleanup.

Dispatcher behavior is documented in the [official WPF implementation](https://raw.githubusercontent.com/dotnet/wpf/main/src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Threading/Dispatcher.cs). Trace collection follows the [official dotnet-trace documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace).

```powershell
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- map-frame-bench "<extracted Map11 root>" --wpf-input-flood
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- map-frame-bench "<extracted Map11 root>" --fire-lifetime
```

Final validation: 1311 Viewer tests passed, 7 skipped, with the same two exhaustive translator cases excluded. The activation regression counts newly queued requests directly, allowing legitimate subsequent composition frames from the real WPF clock. Eight post-change Debug RGBA captures remain byte-identical to the pre-change captures, and all native/stock wire checks finish without OpenGL errors. The standard Debug application and isolated Release build compile without warnings/errors.


## Additional VFX draw binding and camera input comparison

Consecutive VFX draws now reuse the active GL program and indexed uniform-buffer bindings within an owned render pass. The cache resets at every pass boundary, is inactive outside drawing, and invalidates when a native program initializes its bindings. Stock/native/mesh/projection transitions share the same tracked bindings; uniform contents and draw order remain unchanged. No simulation, shader quality, particle count or lifetime change is involved.

The camera controller still consumes mouse deltas together. Both render hosts also consume any deltas received after the composition callback immediately before reading their camera matrices. This prevents already-received input from waiting for the next composition callback. It adds no camera inertia or smoothing delay and cannot remove the time required to render a heavy VFX frame.

Two separate-process measurements were noisy and did not establish a consistent total-frame improvement. A follow-up alternated caching off/on within one process for three rounds in each configuration, restarting particles identically each time (90 warmup and 90 measured frames, moving overview, full-resolution Base_SRX). Another application viewport remained open; these are completed offscreen Debug frames with diagnostic-only gl.Finish, not a measurement of the user's physical mouse or displayed FPS.

| Configuration | Round | Cache off median / p95 ms | Cache on median / p95 ms | VFX mean off / on ms |
| --- | --- | --- | --- | --- |
| VFX | 0 | 28.51 / 38.17 | 27.92 / 35.59 | 21.26 / 19.85 |
| VFX | 1 | 27.58 / 35.29 | 27.88 / 33.40 | 20.43 / 19.66 |
| VFX | 2 | 27.46 / 33.59 | 25.44 / 31.41 | 19.89 / 18.16 |
| Shaders + VFX | 0 | 30.99 / 37.03 | 29.61 / 35.42 | 20.33 / 19.34 |
| Shaders + VFX | 1 | 30.48 / 37.94 | 29.51 / 35.56 | 20.67 / 18.86 |
| Shaders + VFX | 2 | 31.74 / 38.79 | 28.76 / 34.95 | 21.07 / 18.47 |

Per-frame color-pass program binds fell from 799 to 173 and indexed uniform-buffer binds from 3387 to 669 in this view. Eight stationary RGBA captures (overview/close, shaders/VFX combinations) match the pre-change renderer byte for byte, and all 16 configurations plus native/stock wire transitions complete without OpenGL errors. GPU tests verify binding changes, per-slot buffer state and invalidation between draw owners; camera tests verify same-frame consumption, no repeated consumption and scene changes discarding pending input.

```powershell
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- map-frame-bench "<extracted Map11 root>" --binding-compare
```

Validation of the final source: 1340 Viewer/camera tests passed and 7 optional investigations skipped; the same two exhaustive unsupported-query translator cases remain excluded. Isolated Debug tests/app and Release app builds complete with zero warnings/errors. The normal Debug application is rebuilt after the user closed it.
