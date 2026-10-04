using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Hashing;
using Serilog;
using Silk.NET.OpenGL;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Tests.Diagnostics.Viewer;

/// <summary>Installed character pose coverage and CPU extents, with optional GPU snapshots.</summary>
internal static class SkinPoseAuditDiagnostic
{
    internal static async Task Run(string[] args)
    {
        string install = InstalledSkins.FindInstall() ?? throw new InvalidOperationException("League installation not found.");
        var settings = InstalledSkins.Settings(install);
        using var logger = new LoggerConfiguration().CreateLogger();
        var log = new LogService(logger);
        var provider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new DirectoriesCreator(), new SvgParser());
        var resolver = new MapAssetResolver(provider, settings);
        var loader = new MapCharacterLoadingService(resolver, new MapCharacterSkinParser(), new MapCharacterMeshDecoder(), null, log);
        int snapshotAt = Array.IndexOf(args, "--snapshots");
        string snapshots = snapshotAt >= 0 && snapshotAt + 1 < args.Length ? args[snapshotAt + 1] : null;
        bool verbose = args.Contains("--verbose");
        string[] requested = args.Where(arg => arg.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)).ToArray();
        string[] skins = requested.Length > 0 ? requested : new[] { "Air", "Earth", "Fire", "Water", "Hextech", "Chemtech", "Elder" }
            .Select(kind => $"Characters/SRU_Dragon_{kind}/Skins/Skin0").ToArray();
        foreach (string skin in skins)
        {
            MapCharacterAssetData asset = await loader.LoadAsync(skin, null);
            if (asset == null) { Console.WriteLine($"{skin}: unresolved"); continue; }
            SkinPoseDefinition definition = asset.Materials.PoseDefinition;
            if (verbose)
                foreach (var track in asset.AnimationGraph.Tracks) Console.WriteLine($"  TRACK {track}");
            Console.WriteLine($"  mesh={asset.Skin.Mesh}, rig={asset.Skin.Skeleton}, bindSize={Size(asset.Mesh.Positions)}");
            Console.WriteLine($"{skin}: joints={asset.Skeleton.Joints.Count}, springs={definition.Springs.Count}, conforms={definition.Conforms.Count}, orientations={definition.Orientations.Count}, sockets={definition.Sockets.Count}, unsupported={string.Join(',', definition.UnsupportedClasses.Select(hash => $"{hash:x8}"))}");
            using var runtime = new MapCharacterAnimationRuntime(resolver, null);
            int clips = 0, collapsed = 0;
            float worstRatio = float.PositiveInfinity;
            string worstClip = null;
            float bindExtent = Extent(asset.Mesh.Positions);
            foreach (AnimationClipDefinition clip in asset.AnimationGraph?.Clips ?? Array.Empty<AnimationClipDefinition>())
            {
                if (!await runtime.PrepareClipAsync(asset, clip)) continue;
                clips++;
                float duration = runtime.PreparedClipDuration(clip);
                if (verbose) Console.WriteLine($"  clip={clip.ClipName} hash={clip.OwnerPathHash:x8} animation={clip.AnimationFilePath} duration={duration:0.000} flags={clip.Flags} track={clip.Track}");
                for (int frame = 0; frame < 12; frame++)
                {
                    Matrix4x4[] palette = runtime.EvaluateClip(asset, clip, duration * frame / 12f);
                    float ratio = PosedExtent(asset, palette) / Math.Max(bindExtent, 1e-6f);
                    if (ratio < worstRatio) { worstRatio = ratio; worstClip = clip.ClipName ?? clip.OwnerPathHash.ToString("x8"); }
                    if (!float.IsFinite(ratio) || ratio < 0.1f)
                    {
                        collapsed++;
                        Console.WriteLine($"  INVALID_OR_TINY {clip.ClipName} frame={frame}/12 ratio={ratio:0.0000}");
                    }
                }
                if (clip.AnimationFilePath?.Contains("attack", StringComparison.OrdinalIgnoreCase) == true ||
                    clip.OwnerPathHash == Fnv1a.HashLower("Attack1"))
                {
                    Matrix4x4[] palette = runtime.EvaluateClip(asset, clip, 1.27f);
                    Vector3[] posed = Pose(asset, palette);
                    Vector3 min = posed.Aggregate(Vector3.Min), max = posed.Aggregate(Vector3.Max);
                    Console.WriteLine($"  ATTACK t=1.27 min={min} max={max} size={max - min} cues={runtime.PreparedClipCues(clip).Count}");
                    var source = await resolver.ResolveReferenceAsync(MapCharacterAnimationRuntime.ReferenceFromAnimation(clip.AnimationFilePath), null);
                    await using var input = await resolver.OpenReadAsync(source);
                    using var animation = AnimationAsset.Load(input);
                    var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
                    animation.Evaluate(1.27f, pose);
                    if (snapshots != null)
                    {
                        using var raw = new AnimationService();
                        Matrix4x4[] original = raw.EvaluateSkinningTransforms(1.27f, animation, asset.Skeleton);
                        using var context = new HiddenWglContext();
                        using GL gl = GL.GetApi(context.GetProcAddress);
                        using var renderer = new SkinSubmeshRenderer(gl, settings, SceneElements.LoadGenericSkyCube(settings, log), 512);
                        var range = asset.Mesh.Ranges.First();
                        var material = asset.Materials.ResolveMaterialDefinition(range.Name);
                        Matrix4x4[] ShaderBones(Matrix4x4[] matrices) => asset.Skeleton.Influences.Select(joint => matrices[joint]).ToArray();
                        foreach (var sample in new[] { (Name: "raw", Bones: original), (Name: "additive", Bones: palette) })
                        {
                            var render = renderer.Render(asset, range, material, true, bones: ShaderBones(sample.Bones));
                            renderer.SavePng(render.Pixels, Path.Combine(snapshots, $"{skin.Split('/')[1]}_Attack1_{sample.Name}.png"));
                            Console.WriteLine($"  SNAPSHOT {sample.Name}: bound={render.Bound}, covered={render.Covered}, invalid={render.NonFinite}");
                        }
                    }
                    Console.WriteLine($"  rawType={animation.GetType().Name}, tracks={pose.Count}, matched={asset.Skeleton.Joints.Count(j => pose.ContainsKey(Elf.HashLower(j.Name)))}, scaleMin={pose.Values.Select(p => p.Scale).Aggregate(Vector3.Min)}, scaleMax={pose.Values.Select(p => p.Scale).Aggregate(Vector3.Max)}");
                    foreach (var joint in verbose ? asset.Skeleton.Joints.Take(12) : Enumerable.Empty<LeagueToolkit.Core.Animation.Joint>())
                    {
                        pose.TryGetValue(Elf.HashLower(joint.Name), out var local);
                        Matrix4x4.Decompose(joint.LocalTransform, out var scale, out var rotation, out var translation);
                        Console.WriteLine($"  joint={joint.Name} parent={joint.ParentId} bindT={translation} clipT={local.Translation} bindS={scale} clipS={local.Scale} clipQ={local.Rotation}");
                    }
                }
            }
            Console.WriteLine($"  clips={clips}, invalidOrTinySamples={collapsed}, smallestExtentRatio={worstRatio:0.0000}, clip={worstClip}");
        }
    }

    private static float PosedExtent(MapCharacterAssetData asset, Matrix4x4[] palette)
    {
        return Extent(Pose(asset, palette));
    }

    private static Vector3[] Pose(MapCharacterAssetData asset, Matrix4x4[] palette)
    {
        var positions = new Vector3[asset.Mesh.VertexCount];
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            for (int influence = 0; influence < 4; influence++)
            {
                int at = vertex * 4 + influence;
                int shaderSlot = asset.Mesh.SkinIndices[at];
                float weight = asset.Mesh.SkinWeights[at];
                if (weight <= 0f) continue;
                int joint = shaderSlot < asset.Skeleton.Influences.Count ? asset.Skeleton.Influences[shaderSlot] : -1;
                if (joint >= 0 && joint < palette.Length)
                    positions[vertex] += Vector3.Transform(asset.Mesh.Positions[vertex], palette[joint]) * weight;
            }
        }
        return positions;
    }

    private static float Extent(IReadOnlyList<Vector3> positions)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (Vector3 position in positions)
        {
            if (!SkinPoseReader.IsFinite(position)) return float.NaN;
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }
        return (max - min).Length();
    }

    private static Vector3 Size(IReadOnlyList<Vector3> positions) => positions.Aggregate(Vector3.Max) - positions.Aggregate(Vector3.Min);
}
