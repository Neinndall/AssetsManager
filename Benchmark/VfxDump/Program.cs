using AssetsManager.Services.Viewer.Vfx;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.VfxDump;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: VfxDump <bin-file-or-directory> [output-file]");
            return 2;
        }

        string inputPath = Path.GetFullPath(args[0]);
        string? outputPath = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input path does not exist: {inputPath}");
            return 2;
        }

        using TextWriter? fileWriter = outputPath is null ? null : CreateOutput(outputPath);
        void WriteLine(string value = "")
        {
            Console.WriteLine(value);
            fileWriter?.WriteLine(value);
        }

        string[] files = Directory.Exists(inputPath)
            ? Directory.GetFiles(inputPath, "*.bin", SearchOption.AllDirectories)
            : new[] { inputPath };

        int parsedFiles = 0;
        int failedFiles = 0;
        int totalSystems = 0;
        int totalEmitters = 0;
        int totalClips = 0;
        int totalParticleEvents = 0;

        foreach (string file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                byte[] data = File.ReadAllBytes(file);
                IReadOnlyDictionary<uint, VfxSystemDefinition> systems = VfxGraphParser.ExtractAll(data);
                IReadOnlyDictionary<string, VfxAnimationClip> clips = VfxGraphParser.ExtractAnimationClips(data);
                IReadOnlyDictionary<uint, uint> resourceMap = VfxGraphParser.ExtractResourceMap(data);
                IReadOnlyList<string> dependencies = VfxGraphParser.ExtractDependencies(data);
                VfxCoverage coverage = InspectCoverage(data);

                parsedFiles++;
                totalSystems += systems.Count;
                totalEmitters += systems.Values.Sum(system => system.Emitters.Count);
                totalClips += clips.Count;
                totalParticleEvents += clips.Values.Sum(clip => clip.ParticleEvents.Count);

                WriteLine($"BIN {file}");
                WriteLine($"  systems={systems.Count} emitters={systems.Values.Sum(system => system.Emitters.Count)} clips={clips.Count} particleEvents={clips.Values.Sum(clip => clip.ParticleEvents.Count)} resourceMap={resourceMap.Count} dependencies={dependencies.Count}");
                WriteLine($"  primitives: {string.Join(", ", coverage.Primitives.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}={pair.Value}"))}");
                WriteLine($"  authoredFeatures: {string.Join(", ", coverage.Features.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}={pair.Value}"))}");

                foreach (string dependency in dependencies)
                    WriteLine($"  dependency: {dependency}");

                foreach ((uint pathHash, VfxSystemDefinition system) in systems.OrderBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase))
                {
                    WriteLine($"  system 0x{pathHash:x8} name={system.Name} path={system.ParticlePath} emitters={system.Emitters.Count}");
                    for (int index = 0; index < system.Emitters.Count; index++)
                    {
                        VfxEmitterDefinition emitter = system.Emitters[index];
                        WriteLine(
                            $"    emitter[{index}] name={emitter.Name} texture={ValueOrNone(emitter.TexturePath)} " +
                            $"mesh={ValueOrNone(emitter.MeshPath)} meshPrimitive={emitter.IsMeshPrimitive} " +
                            $"primitive={emitter.PrimitiveKind} erosion={ValueOrNone(emitter.AlphaErosion?.TexturePath)} " +
                            $"children={emitter.ChildParticleSet?.Children.Count ?? 0} childOnDeath={emitter.ChildParticleSet?.EmitOnDeath ?? false} " +
                            $"texDiv={emitter.TexDiv.X}x{emitter.TexDiv.Y} frames={emitter.NumFrames} " +
                            $"blend={emitter.BlendMode} pass={emitter.RenderState?.RenderPass ?? 0}");
                        WriteLine(
                            $"      timing rate={Format(emitter.Rate)} particleLife={Format(emitter.ParticleLifetime)} " +
                            $"emitterLife={(emitter.EmitterLifetime?.ToString("0.###") ?? "loop")} delay={emitter.TimeBeforeFirstEmission:0.###} " +
                            $"linger={emitter.ParticleLinger:0.###} " +
                            $"single={emitter.IsSingleParticle} disabled={emitter.Disabled}");
                        WriteLine(
                            $"      motion position={Format(emitter.EmitterPosition)} spawn={Format(emitter.SpawnShape?.EmitOffset)} " +
                            $"spawnKind={emitter.SpawnShape?.Kind.ToString() ?? "(none)"} " +
                            $"spawnSize={emitter.SpawnShape?.Size.ToString() ?? "(none)"} " +
                            $"spawnRadius={emitter.SpawnShape?.Radius ?? 0:0.###} " +
                            $"spawnHeight={emitter.SpawnShape?.Height ?? 0:0.###} " +
                            $"spawnAxes={emitter.SpawnShape?.RotationAxes.Count ?? 0} " +
                            $"spawnAngles={FormatCurves(emitter.SpawnShape?.RotationAngles)} " +
                            $"velocity={Format(emitter.BirthVelocity)} acceleration={Format(emitter.Acceleration)} " +
                            $"birthScale={Format(emitter.BirthScale)} scale={Format(emitter.ScaleOverLife)} " +
                            $"birthColor={Format(emitter.BirthColor)} color={Format(emitter.ColorOverLife)} " +
                            $"rotation={Format(emitter.BirthRotation)} rotationLife={Format(emitter.RotationOverLife)} " +
                            $"disableCull={emitter.RenderState?.DisableBackfaceCull ?? false} " +
                            $"emitterSpace={emitter.IsEmitterSpace} localOrientation={emitter.IsLocalOrientation} " +
                            $"particleLocalOrientation={emitter.ParticleIsLocalOrientation} terrain={emitter.IsFollowingTerrain} " +
                            $"ground={emitter.IsGroundLayer} uniformScale={emitter.IsUniformScale}");
                        if (emitter.ChildParticleSet is { } childSet)
                        {
                            foreach (var child in childSet.Children)
                            {
                                uint resolvedHash = child.SystemHash;
                                if (resolvedHash == 0 && child.EffectKey != 0)
                                    resourceMap.TryGetValue(child.EffectKey, out resolvedHash);
                                systems.TryGetValue(resolvedHash, out VfxSystemDefinition? resolvedSystem);
                                WriteLine($"      child name={ValueOrNone(child.Name)} system=0x{child.SystemHash:x8} " +
                                          $"key=0x{child.EffectKey:x8} resolved=0x{resolvedHash:x8} " +
                                          $"resolvedName={ValueOrNone(resolvedSystem?.Name)} onDeath={childSet.EmitOnDeath}");
                            }
                        }
                    }
                }

                foreach ((string clipKey, VfxAnimationClip clip) in clips.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    WriteLine($"  clip key={clipKey} animation={ValueOrNone(clip.AnimationName)} particleEvents={clip.ParticleEvents.Count}");
                    foreach (VfxAnimationEvent particleEvent in clip.ParticleEvents)
                        WriteLine($"    event frame={particleEvent.StartFrame} effectHash=0x{particleEvent.EffectHash:x8} effect={ValueOrNone(particleEvent.EffectName)} bone={ValueOrNone(particleEvent.BoneName)}");
                }
            }
            catch (Exception exception)
            {
                failedFiles++;
                WriteLine($"ERROR {file}: {exception.Message}");
            }
        }

        WriteLine();
        WriteLine($"SUMMARY files={parsedFiles} failed={failedFiles} systems={totalSystems} emitters={totalEmitters} clips={totalClips} particleEvents={totalParticleEvents}");
        return failedFiles == 0 ? 0 : 1;
    }

    private static StreamWriter CreateOutput(string outputPath)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return new StreamWriter(outputPath, append: false);
    }

    private static string ValueOrNone(string? value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string Format(VfxCurveF curve)
        => curve.Values is { Length: > 0 }
            ? $"{curve.Values.Min():0.###}..{curve.Values.Max():0.###}"
            : curve.Constant.ToString("0.###");

    private static string Format(VfxCurve3? curve)
    {
        if (curve is null) return "(none)";
        if (curve.Value.Values is not { Length: > 0 }) return curve.Value.Constant.ToString();
        var min = new System.Numerics.Vector3(
            curve.Value.Values.Min(value => value.X),
            curve.Value.Values.Min(value => value.Y),
            curve.Value.Values.Min(value => value.Z));
        var max = new System.Numerics.Vector3(
            curve.Value.Values.Max(value => value.X),
            curve.Value.Values.Max(value => value.Y),
            curve.Value.Values.Max(value => value.Z));
        return $"{min}..{max}";
    }

    private static string Format(VfxCurve4? curve)
    {
        if (curve is null) return "(none)";
        if (curve.Value.Values is not { Length: > 0 }) return curve.Value.Constant.ToString();
        var min = new System.Numerics.Vector4(
            curve.Value.Values.Min(value => value.X),
            curve.Value.Values.Min(value => value.Y),
            curve.Value.Values.Min(value => value.Z),
            curve.Value.Values.Min(value => value.W));
        var max = new System.Numerics.Vector4(
            curve.Value.Values.Max(value => value.X),
            curve.Value.Values.Max(value => value.Y),
            curve.Value.Values.Max(value => value.Z),
            curve.Value.Values.Max(value => value.W));
        return $"{min}..{max}";
    }

    private static string FormatCurves(IReadOnlyList<VfxCurveF>? curves)
        => curves is not { Count: > 0 }
            ? "(none)"
            : string.Join(",", curves.Select(Format));

    private sealed record VfxCoverage(Dictionary<string, int> Primitives, Dictionary<string, int> Features);

    private static VfxCoverage InspectCoverage(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        var tree = new BinTree(stream);
        uint systemClass = Fnv1a("VfxSystemDefinitionData");
        uint emitterClass = Fnv1a("VfxEmitterDefinitionData");
        uint primitiveField = Fnv1a("primitive");
        var primitiveNames = new[]
        {
            "VfxPrimitiveCameraQuad", "VfxPrimitiveCameraUnitQuad", "VfxPrimitiveArbitraryQuad",
            "VfxPrimitiveMesh", "VfxPrimitiveAttachedMesh", "VfxPrimitiveCameraTrail",
            "VfxPrimitiveArbitraryTrail", "VfxPrimitiveBeam", "VfxPrimitiveCameraSegmentBeam",
            "VfxPrimitiveRay", "VfxPrimitivePlanarProjection"
        }.ToDictionary(Fnv1a, name => name);
        string[] featureNames =
        {
            "childParticleSetDefinition", "fieldCollectionDefinition", "emissionSurfaceDefinition",
            "emissionMeshName", "flexRate", "flexParticleLifetime", "flexBirthVelocity",
            "flexBirthUVOffset", "flexBirthUVScrollRate", "particleUVScrollRate",
            "particleUVRotateRate", "rotation0", "birthRotationalAcceleration", "velocity",
            "uvScale", "uvRotation", "birthUVOffset", "alphaErosionDefinition",
            "softParticleParams", "falloffTexture", "CustomMaterial", "materialOverrideDefinitions",
            "isLocalOrientation", "particleIsLocalOrientation", "IsEmitterSpace", "isFollowingTerrain",
            "bindWeight", "birthUvRotateRate", "ChanceToNotExist", "colorLookUpOffsets",
            "colorLookUpScales", "colorRenderFlags", "depthBiasFactors", "directionVelocityMinScale",
            "directionVelocityScale", "doesLifetimeScale", "doesParticleLifetimeScale",
            "emissionMeshScale", "emitterUvScrollRate", "FlexInstanceScale", "flexScaleBirthScale",
            "FlexShapeDefinition", "hasPostRotateOrientation", "HasVariableStartTime", "isGroundLayer",
            "isRotationEnabled", "isTexturePixelated", "isUniformScale", "Linger",
            "MaximumRateByVelocity", "meshRenderFlags", "miscRenderFlags", "modulationFactor",
            "offsetLifeScalingSymmetryMode", "offsetLifetimeScaling", "paletteDefinition",
            "ParticlesShareRandomValue", "period", "postRotateOrientationAxis", "rateByVelocityFunction",
            "renderPhaseOverride", "rotationOverride", "scaleOverride", "sliceTechniqueRange",
            "SortEmittersByPos", "timeActiveDuringPeriod", "translationOverride",
            "useEmissionMeshNormalForBirth", "useNavmeshMask", "uvMode", "uvParallaxScale",
            "uvTransformCenter", "WriteAlphaOnly"
        };
        var featureHashes = featureNames.ToDictionary(Fnv1a, name => name);
        var primitives = new Dictionary<string, int>(StringComparer.Ordinal);
        var features = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (BinTreeObject system in tree.Objects.Values.Where(value => value.ClassHash == systemClass))
        {
            foreach (BinTreeContainer container in system.Properties.Values.OfType<BinTreeContainer>())
            {
                foreach (BinTreeStruct emitter in container.Elements.OfType<BinTreeStruct>().Where(value => value.ClassHash == emitterClass))
                {
                    if (emitter.Properties.TryGetValue(primitiveField, out BinTreeProperty? property) && property is BinTreeStruct primitive)
                    {
                        string name = primitiveNames.TryGetValue(primitive.ClassHash, out string? known)
                            ? known
                            : $"0x{primitive.ClassHash:x8}";
                        primitives[name] = primitives.GetValueOrDefault(name) + 1;
                    }
                    else
                    {
                        primitives["(default)"] = primitives.GetValueOrDefault("(default)") + 1;
                    }

                    foreach (uint fieldHash in emitter.Properties.Keys)
                    {
                        if (featureHashes.TryGetValue(fieldHash, out string? feature))
                            features[feature] = features.GetValueOrDefault(feature) + 1;
                    }
                }
            }
        }

        return new VfxCoverage(primitives, features);
    }

    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261;
        foreach (char value in text.ToLowerInvariant())
        {
            hash ^= value;
            hash *= 16777619;
        }
        return hash;
    }
}
