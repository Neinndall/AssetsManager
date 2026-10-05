using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private string StandaloneRunKey(VfxSystemDiagnosticItem systemItem)
        {
            string document = _activeBundle?.PrimaryBinPath ?? _model.RootPath ?? string.Empty;
            return $"{document}|{systemItem?.PathHash ?? 0:X8}";
        }

        private void RememberStandaloneRun(VfxSystemDiagnosticItem systemItem)
        {
            if (systemItem == null) return;

            _standaloneRunMemory[StandaloneRunKey(systemItem)] = new StandaloneRunMemory(
                Seed: _model.PlaybackSeed,
                Playhead: _model.CurrentTime,
                Speed: _model.Speed,
                RigSettings: (_vfxRenderer?.RigSettings ?? VfxRigSettings.ForPreset(_model.RigPreset)) with { IsLooping = false },
                Muted: _model.Emitters.Where(emitter => emitter.IsMuted).Select(emitter => emitter.SourceOrder).ToArray(),
                Soloed: _model.Emitters.Where(emitter => emitter.IsSolo).Select(emitter => emitter.SourceOrder).ToArray());
        }

        private StandaloneRunMemory RecallStandaloneRun(VfxSystemDiagnosticItem systemItem)
            => systemItem != null &&
               _standaloneRunMemory.TryGetValue(StandaloneRunKey(systemItem), out StandaloneRunMemory memory)
                ? memory
                : null;

        internal static double RememberedPlayhead(double playhead, double span)
        {
            double safe = double.IsFinite(playhead) ? Math.Max(0d, playhead) : 0d;
            return span > 0d && double.IsFinite(span) ? Math.Min(safe, span) : safe;
        }

        internal static int NextPlaybackSeed(int seed) => unchecked(seed + 1);

        private void InspectSystem(VfxSystemDiagnosticItem systemItem)
        {
            var def = systemItem.Definition;
            if (def == null) return;
            _pendingSpell = null;
            _activeSpellPlan = null;

            StandaloneRunMemory remembered = RecallStandaloneRun(systemItem);
            int playbackSeed = remembered?.Seed ?? StandalonePlaybackSeed;
            float playbackSpeed = remembered?.Speed ?? 1f;
            var inferredRig = VfxSystemRigResolver.Resolve(def, _activeBundle, VfxLoadingService == null ? null : VfxLoadingService.ResolveBinEntryPath);
            VfxRigSettings rigSettings =
                (remembered?.RigSettings ?? VfxRigSettings.ForPreset(inferredRig.Preset)) with
                {
                    IsLooping = _model.IsPreviewLoopEnabled
                };
            VfxRigPreset rigPreset = rigSettings.Preset;
            if (remembered == null)
                _model.LogMessages.Add($"[RIG] Auto {rigPreset}: {inferredRig.Reason}");
            HashSet<int> muted = remembered?.Muted?.ToHashSet() ?? new HashSet<int>();
            HashSet<int> soloed = remembered?.Soloed?.ToHashSet() ?? new HashSet<int>();

            _model.SelectedEmitter = null;
            _model.Emitters.Clear();
            _model.Textures.Clear();
            _model.LogMessages.Add($"[INSPECT] Selected VFX: {systemItem.Name} (Hash: 0x{systemItem.PathHash:X8})");

            string searchDir = _model.RootPath;
            if (!string.IsNullOrEmpty(searchDir) && File.Exists(searchDir))
            {
                searchDir = Path.GetDirectoryName(searchDir) ?? searchDir;
            }

            // 1. Prepare playback in OpenGL Viewport
            var systemModel = new VfxSystemModel
            {
                Name = systemItem.Name,
                Definition = def,
                SystemCatalog = _activeBundle?.Systems ?? new Dictionary<uint, VfxSystemDefinition>(),
                ResourceMap = _activeBundle?.ResourceMap ?? new Dictionary<uint, uint>(),
                SearchDirectory = searchDir,
                OwnerSceneContext = _activeBundle?.OwnerSceneContext,
                PlaybackSeed = playbackSeed,
                TotalDuration = VfxDurationCalculator.SystemSpan(def),
                Speed = playbackSpeed
            };

            _model.CurrentTime = 0;
            _model.PlaybackSeed = playbackSeed;
            string playbackContext = "standalone system";
            _vfxRenderer?.SetWorldTransform(CharacterVfxPlacement());
            _vfxRenderer?.SetVfxSystem(systemModel);
            ApplyChampionBindPose();
            if (_vfxRenderer != null)
            {
                _vfxRenderer.RigSettings = rigSettings;
                _model.RigPreset = rigPreset;
            }
            else
            {
                _model.RigPreset = rigPreset;
            }
            UpdateRigControlValues();
            SetPlaybackSpeed(playbackSpeed);

            double rigDuration = _vfxRenderer?.RigDuration ?? VfxRigMotion.RunLength(_model.RigPreset, def);
            double timelineMax = ResolveTimelineDuration(rigDuration);
            ResetPreviewLoopRange(timelineMax);

            // 2. Audit Emitters
            for (int emitterIndex = 0; emitterIndex < def.Emitters.Count; emitterIndex++)
            {
                var emitter = def.Emitters[emitterIndex];
                if (emitter.Disabled) continue;

                string texPath = emitter.TexturePath;
                string meshPath = emitter.MeshPath;

                BitmapSource tex = string.IsNullOrEmpty(texPath) ? null : VfxLoadingService.ResolveTexture(texPath, searchDir);
                var mesh = emitter.IsMeshPrimitive
                    ? VfxLoadingService.ResolveMesh(meshPath, searchDir)
                    : null;

                (string textureStatus, Brush textureStatusBrush) = DescribeTextureStatus(emitter, tex);

                string primKind = emitter.IsMeshPrimitive
                    ? "MESH"
                    : (emitter.IsGroundLayer ? "GROUND" : (emitter.Trail != null ? "TRAIL" : "QUAD"));

                var emitterDiagnostic = new VfxEmitterDiagnosticItem
                {
                    Name = string.IsNullOrWhiteSpace(emitter.Name) ? "Emitter" : emitter.Name,
                    SourceOrder = emitterIndex,
                    IsEnabled = true,
                    IsSolo = soloed.Contains(emitterIndex),
                    IsMuted = muted.Contains(emitterIndex),
                    PrimitiveKindName = primKind,
                    EmitterDef = emitter,
                    ImagePreview = tex,
                    TexturePath = texPath ?? "N/A",
                    TextureSources = DescribeTextureSources(emitter),
                    TextureStatus = textureStatus,
                    TextureStatusBrush = textureStatusBrush,
                    MeshPath = emitter.IsMeshPrimitive ? (meshPath ?? "N/A") : "N/A",
                    MeshStatus = emitter.IsMeshPrimitive ? (mesh != null ? "Resolved" : "MISSING") : "N/A",
                    MeshStatusBrush = emitter.IsMeshPrimitive ? (mesh != null ? Brushes.LightGreen : Brushes.OrangeRed) : Brushes.Gray,
                    BlendMode = GetBlendModeName(emitter.BlendMode),
                    TexDiv = $"{emitter.TexDiv.X} x {emitter.TexDiv.Y}",
                    IsMeshPrimitive = emitter.IsMeshPrimitive,
                    DisableBackfaceCull = emitter.RenderState?.DisableBackfaceCull ?? false
                };

                emitterDiagnostic.OnEnabledChanged += (item, enabled) =>
                {
                    _vfxRenderer?.SetEmitterVisibility(item.SourceOrder, enabled);
                    _model.LogMessages.Add($"[EMITTER TOGGLE] {item.Name} set to {(enabled ? "ENABLED" : "DISABLED")}");
                };
                emitterDiagnostic.OnVisibilityStateChanged += item =>
                {
                    if (!_isBulkEmitterStateChange)
                        UpdateEmittersVisibility();
                };

                _model.Emitters.Add(emitterDiagnostic);

                // Add to texture audit
                if (!string.IsNullOrEmpty(texPath) && !_model.Textures.Any(t => t.AuthoredPath == texPath))
                {
                    _model.Textures.Add(new VfxTextureDiagnosticItem
                    {
                        AuthoredPath = texPath,
                        ResolvedPath = tex != null ? "Resolved on disk" : "Missing",
                        Status = tex != null ? "OK" : "MISSING",
                        StatusBrush = tex != null ? Brushes.LightGreen : Brushes.Red,
                        Width = tex?.PixelWidth ?? 0,
                        Height = tex?.PixelHeight ?? 0,
                        ImagePreview = tex,
                        TexDiv = $"{emitter.TexDiv.X}x{emitter.TexDiv.Y}"
                    });
                }
            }

            UpdateEmittersVisibility();

            double restoredTime = remembered == null
                ? 0d
                : RememberedPlayhead(remembered.Playhead, timelineMax);
            _vfxRenderer?.Seek(restoredTime);
            if (_vfxRenderer?.ActiveSystem != null)
                _model.CurrentTime = _vfxRenderer.PlaybackTime;
            else
                _model.CurrentTime = restoredTime;
            bool play = !TakeStartPreviewPaused();
            if (play) _vfxRenderer?.Play();
            _model.IsPlaying = play;

            TryLoadChampionModelAsync(searchDir);

            UpdateTimelineTrackMetrics();
            UpdatePlayheadPosition();

            _model.StatusText = $"{systemItem.Name} · {playbackContext}.";
            _inspectedSystem = systemItem;
        }
    }
}
