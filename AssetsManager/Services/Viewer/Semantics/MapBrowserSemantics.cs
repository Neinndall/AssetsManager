using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    internal sealed record MapCharacterClipSelection(
        MapCharacterRuntimeGroup Group,
        AnimationClipDefinition Clip);

    /// <summary>
    /// Projects one loaded MAP runtime into the semantic tree shared by VFX Studio.
    /// </summary>
    internal static class MapBrowserSemantics
    {
        internal static MapBrowserNode Build(MapSceneRuntime runtime)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            MapSceneData scene = runtime.Scene;

            var root = new MapBrowserNode(
                Leaf(scene.Source?.Map?.Value) ?? "Map",
                MapBrowserNodeKind.Map,
                "Map",
                scene,
                inspectorSummary: $"{scene.Geometry?.Meshes?.Count ?? 0} meshes · {scene.Materials?.Count ?? 0} materials · {runtime.CharacterGroups?.Count ?? 0} skins · {scene.ParticleSystems?.Groups?.Count ?? 0} VFX systems")
            {
                IsExpanded = true
            };

            root.Children.Add(new MapBrowserNode(
                "MapGeometry",
                MapBrowserNodeKind.Geometry,
                $"{scene.Geometry?.Meshes?.Count ?? 0} meshes",
                scene.Geometry,
                inspectorSummary: GeometrySummary(scene.Geometry)));

            root.Children.Add(BuildMaterials(scene));
            root.Children.Add(BuildChunks(scene));
            root.Children.Add(BuildCharacters(runtime));
            root.Children.Add(BuildParticles(scene));
            return root;
        }

        private static MapBrowserNode BuildMaterials(MapSceneData scene)
        {
            var branch = new MapBrowserNode(
                "Materials",
                MapBrowserNodeKind.Materials,
                $"{scene.Materials?.Count ?? 0}",
                scene.MaterialsDocument,
                inspectorSummary: $"{scene.Materials?.Count ?? 0} authored materials");

            foreach (MapMaterialDefinition material in scene.Materials ?? Array.Empty<MapMaterialDefinition>())
            {
                if (material == null) continue;
                branch.Children.Add(new MapBrowserNode(
                    string.IsNullOrWhiteSpace(material.Name) ? $"0x{material.PathHash:x8}" : material.Name,
                    MapBrowserNodeKind.Material,
                    string.IsNullOrWhiteSpace(material.ShaderPath) ? null : Leaf(material.ShaderPath),
                    material,
                    inspectorSummary: MaterialSummary(material)));
            }
            return branch;
        }

        private static MapBrowserNode BuildChunks(MapSceneData scene)
        {
            var branch = new MapBrowserNode(
                "Chunks",
                MapBrowserNodeKind.Chunks,
                $"{scene.Outline?.Count ?? 0}");

            foreach (MapOutlineChunkData chunk in scene.Outline ?? Array.Empty<MapOutlineChunkData>())
            {
                if (chunk == null) continue;
                var chunkNode = new MapBrowserNode(
                    string.IsNullOrWhiteSpace(chunk.Label) ? chunk.Name : chunk.Label,
                    MapBrowserNodeKind.Chunk,
                    $"{chunk.Items?.Count ?? 0}",
                    chunk,
                    canHide: chunk.Items?.Any(i => i?.IsDrawable == true) == true,
                    inspectorSummary: $"{chunk.Items?.Count ?? 0} placeables");

                foreach (MapOutlineItemData item in chunk.Items ?? Array.Empty<MapOutlineItemData>())
                {
                    if (item == null) continue;
                    chunkNode.Children.Add(new MapBrowserNode(
                        string.IsNullOrWhiteSpace(item.Name) ? $"0x{item.KeyHash:x8}" : item.Name,
                        MapBrowserNodeKind.Placeable,
                        item.ClassName,
                        item,
                        canHide: item.IsDrawable,
                        inspectorSummary: $"{item.ClassName ?? "Placeable"} · {PositionSummary(item.Position)}"));
                }
                branch.Children.Add(chunkNode);
            }
            return branch;
        }

        private static MapBrowserNode BuildCharacters(MapSceneRuntime runtime)
        {
            var branch = new MapBrowserNode(
                "Characters",
                MapBrowserNodeKind.Characters,
                $"{runtime.CharacterGroups?.Count ?? 0}");

            foreach (MapCharacterRuntimeGroup group in runtime.CharacterGroups ?? Array.Empty<MapCharacterRuntimeGroup>())
            {
                if (group == null) continue;
                string skinPath = group.Asset?.Skin?.Skin ?? group.Placements?.FirstOrDefault()?.Skin;
                var skin = new MapBrowserNode(
                    SkinLabel(skinPath),
                    MapBrowserNodeKind.CharacterSkin,
                    $"{group.Placements?.Count ?? 0} placements",
                    group,
                    inspectorSummary: CharacterSkinSummary(group))
                {
                    IsExpanded = false
                };

                AnimationGraphDefinition graph = group.Asset?.AnimationGraph;
                if (graph?.Clips?.Count > 0)
                {
                    var clips = new MapBrowserNode(
                        "Animation Clips",
                        MapBrowserNodeKind.Clips,
                        $"{graph.Clips.Count}",
                        graph,
                        inspectorSummary: $"{graph.Clips.Count} animation clips");
                    foreach (AnimationClipDefinition clip in graph.Clips)
                    {
                        if (clip == null) continue;
                        clips.Children.Add(new MapBrowserNode(
                            ClipLabel(clip),
                            MapBrowserNodeKind.Clip,
                            ClipSubtitle(clip),
                            new MapCharacterClipSelection(group, clip),
                            inspectorSummary: ClipSummary(clip)));
                    }
                    skin.Children.Add(clips);
                }

                foreach (MapCharacterData placement in group.Placements ?? Array.Empty<MapCharacterData>())
                {
                    if (placement == null) continue;
                    skin.Children.Add(new MapBrowserNode(
                        string.IsNullOrWhiteSpace(placement.Name) ? $"0x{placement.KeyHash:x8}" : placement.Name,
                        MapBrowserNodeKind.CharacterPlacement,
                        string.IsNullOrWhiteSpace(placement.Animation) ? "Placement" : placement.Animation,
                        placement,
                        canHide: true,
                        inspectorSummary: $"{Leaf(placement.Skin) ?? "Character"} · {PositionSummary(placement.Placeable.Position)}"));
                }
                branch.Children.Add(skin);
            }
            return branch;
        }

        private static MapBrowserNode BuildParticles(MapSceneData scene)
        {
            IReadOnlyList<MapParticleSystemGroupData> groups = scene.ParticleSystems?.Groups ?? Array.Empty<MapParticleSystemGroupData>();
            var branch = new MapBrowserNode(
                "Particles",
                MapBrowserNodeKind.Particles,
                $"{groups.Count}");

            foreach (MapParticleSystemGroupData group in groups)
            {
                if (group == null) continue;
                string title = !string.IsNullOrWhiteSpace(group.System?.Name)
                    ? group.System.Name
                    : $"0x{group.SystemHash:x8}";
                var system = new MapBrowserNode(
                    title,
                    MapBrowserNodeKind.ParticleSystem,
                    $"{group.Particles?.Count ?? 0} placements",
                    group,
                    inspectorSummary: $"{group.System?.Emitters?.Count ?? 0} emitters · {group.Particles?.Count ?? 0} placements");
                foreach (MapParticleData particle in group.Particles ?? Array.Empty<MapParticleData>())
                {
                    if (particle == null) continue;
                    system.Children.Add(new MapBrowserNode(
                        string.IsNullOrWhiteSpace(particle.Name) ? $"0x{particle.KeyHash:x8}" : particle.Name,
                        MapBrowserNodeKind.ParticlePlacement,
                        particle.StartDisabled ? "Disabled" : "Placement",
                        particle,
                        canHide: true,
                        inspectorSummary: $"System 0x{particle.SystemHash:x8} · {PositionSummary(particle.Position)}{(particle.StartDisabled ? " · disabled" : string.Empty)}"));
                }
                branch.Children.Add(system);
            }
            return branch;
        }

        private static string GeometrySummary(MapGeometryData geometry) =>
            geometry == null
                ? "No geometry"
                : $"{geometry.Positions?.Length ?? 0} vertices · {geometry.Indices?.Length ?? 0} indices · {geometry.Meshes?.Count ?? 0} meshes · {geometry.Submeshes?.Count ?? 0} submeshes";

        private static string MaterialSummary(MapMaterialDefinition material)
        {
            if (material == null) return "Material";
            string shader = Leaf(material.ShaderPath) ?? "no shader";
            string texture = Leaf(material.BaseTexture?.Texture?.VirtualPath) ?? "no base texture";
            return $"{shader} · {texture} · {material.RenderState?.Blending}";
        }

        private static string CharacterSkinSummary(MapCharacterRuntimeGroup group)
        {
            int placements = group?.Placements?.Count ?? 0;
            int clips = group?.Asset?.AnimationGraph?.Clips?.Count ?? 0;
            int systems = group?.Asset?.Vfx?.Systems?.Count ?? 0;
            return $"{placements} placements · {clips} clips · {systems} VFX systems";
        }

        private static string ClipSummary(AnimationClipDefinition clip)
        {
            if (clip == null) return "Animation clip";
            string animation = Leaf(clip.AnimationFilePath) ?? clip.ClassName ?? "composite";
            return $"{animation} · {clip.ParticleEventCount} particle events";
        }

        private static string PositionSummary(System.Numerics.Vector3 position) =>
            $"X {position.X:F0} · Y {position.Y:F0} · Z {position.Z:F0}";

        private static string SkinLabel(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "Character Skin";
            string normalized = path.Replace('\\', '/').Trim('/');
            string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
                return $"{parts[^3]} / {parts[^1]}";
            return Leaf(normalized) ?? normalized;
        }

        private static string ClipLabel(AnimationClipDefinition clip) =>
            !string.IsNullOrWhiteSpace(clip.ClipName)
                ? clip.ClipName
                : $"0x{clip.OwnerPathHash:x8}";

        private static string ClipSubtitle(AnimationClipDefinition clip)
        {
            if (!string.IsNullOrWhiteSpace(clip.AnimationFilePath))
                return Leaf(clip.AnimationFilePath);
            if (!string.IsNullOrWhiteSpace(clip.ClassName))
                return clip.ClassName;
            return null;
        }

        private static string Leaf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string normalized = path.Replace('\\', '/').TrimEnd('/');
            int slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized[(slash + 1)..] : normalized;
        }
    }
}
