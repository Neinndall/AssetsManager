using AssetsManager.Services.Viewer.Vfx;

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

                parsedFiles++;
                totalSystems += systems.Count;
                totalEmitters += systems.Values.Sum(system => system.Emitters.Count);
                totalClips += clips.Count;
                totalParticleEvents += clips.Values.Sum(clip => clip.ParticleEvents.Count);

                WriteLine($"BIN {file}");
                WriteLine($"  systems={systems.Count} emitters={systems.Values.Sum(system => system.Emitters.Count)} clips={clips.Count} particleEvents={clips.Values.Sum(clip => clip.ParticleEvents.Count)} resourceMap={resourceMap.Count} dependencies={dependencies.Count}");

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
                            $"texDiv={emitter.TexDiv.X}x{emitter.TexDiv.Y} frames={emitter.NumFrames} " +
                            $"blend={emitter.BlendMode} pass={emitter.RenderState?.RenderPass ?? 0}");
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
}
