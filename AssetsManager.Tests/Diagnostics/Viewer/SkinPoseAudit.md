# Skin pose audit

Run from the repository root:

```powershell
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- skin-pose-audit Characters/SRU_Dragon_Earth/Skins/Skin0 --snapshots <output-directory>
```

Without skin arguments the diagnostic samples all seven installed dragon skins. `--verbose` prints tracks, clips and bind/animation joint transforms. CPU bounds flag non-finite or extremely small poses; they do not establish the correctness of every animation. Optional GPU snapshots compare the raw Attack1 pose against the graph-aware pose using the same camera and game material.

Earth Dragon Attack1 uses an additive track and contains deltas rather than a complete local pose. It has 106 matching animation tracks, unit scales, no clip events, and no skin pose modifiers. Its mesh and rig intentionally reference the Air Dragon assets. Replacing local transforms with the deltas collapses the model. Adding them over the same graph's Idle1_Base restores its body and wings. An absent reference falls back to the skeleton bind pose; track weight and mask still apply.

## Runtime responsibilities

- `GraphPoseAnimationAsset` interprets additive tracks for both VFX clip preparation and MAP character playback. Retimed playlists preserve the skeleton context. Shared source decoders serialize sampling because compressed ANMs have mutable seek cursors.
- `SkinPoseReader` reads only the effective selected skin's mesh properties, including form overlays. `SkinPoseDefinition` carries the data through material resolution and model loading.
- `SkinPoseRuntime`, `SkinSpringRuntime` and `SkinConformRuntime` apply pose dynamics and clip cues. Static conform sampling uses a bounded deterministic take; unit movement drives live springs. Scene changes and seeks reset live state.
- `SkinSocketResolver` resolves attachment points on the final pose without adding entries to the joint palette. Animation attachment sampling restores the displayed pose, including the MAP preview path.
- Viewer and 3D Studio supply the model transform; MAP ambient and selected clip playback supply their graph cues and masks. Model placement and spawn resolution keep their existing responsibility.

This is isolated clip preview, not a visual graph editor or the game's multi-track state machine. DynamicsChain physics and orientation drivers requiring live game state are preserved but not simulated.

## Upstream comparison

The local LTK Manager source reads track blend mode and weight in `crates/ltk-manager-game/src/skin.rs`. Its `SkinViewport.tsx` creates each pose from skeleton, clip and timing; `src/modules/viewport/animation/evaluation/pose.ts` replaces tracked local transforms and has no additive basis input. `MapCharacters.tsx` sequences those same raw clip poses without the skin editor's dynamics wrapper. Upstream's UI was not executed for this audit.

## Regression coverage

`GraphPoseTests` checks additive bone separation, basis composition, masks, graph isolation, VFX catalog preparation, MAP ambient cues, attachment sampling and shared decoder access. `SkinPoseTests` checks skin selection, sockets, snap attachments, springs, orientation locks, conform seeking and typed pose events. Existing animation, graph parser, clip catalog, material resolver and MAP runtime tests cover their integration contracts.
