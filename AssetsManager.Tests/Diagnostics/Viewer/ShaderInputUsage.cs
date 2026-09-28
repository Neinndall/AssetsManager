using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Shaders;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// Aggregates the engine inputs translated game shaders actually read (attributes, engine block
    /// members and shared textures), to compare against what the preview runtime feeds them.
    /// </summary>
    internal sealed class ShaderInputUsage
    {
        private readonly Dictionary<string, HashSet<string>> _attributes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _members = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _sharedTextures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenParticlePrograms = new(StringComparer.Ordinal);

        public int Passes { get; private set; }
        public int Translated { get; private set; }

        /// <summary>Reads, translates and tracks every pass of a material program.</summary>
        public void Collect(GameMaterialProgram program, string owner, AppSettings settings)
        {
            if (program?.Passes == null)
                return;

            foreach (GameMaterialPass pass in program.Passes)
            {
                Passes++;
                GameShaderProgramResolver.ShaderBytecodeRead read =
                    GameShaderProgramResolver.Read(pass, program.Kind, settings);
                if (!read.Ready)
                {
                    Fail("bytecode: " + read.Failure);
                    continue;
                }

                GameShaderTranslator.TranslationRead translated = GameShaderTranslator.Translate(
                    read.Program.Vertex,
                    read.Program.VertexReflection,
                    read.Program.Pixel,
                    read.Program.PixelReflection);
                if (!translated.Ready)
                {
                    Fail("translate: " + translated.Failure);
                    continue;
                }

                Translated++;
                string label = $"{owner}[{pass.ShaderPath?.Split('/').LastOrDefault()}]";
                Dump(translated.Program, label);
                Track(translated.Program, label);
            }
        }

        /// <summary>
        /// Tracks the stock particle programs (and authored custom materials) the emitters resolve to,
        /// once per distinct shader pair and define set.
        /// </summary>
        public void CollectParticles(IEnumerable<VfxEmitterDefinition> emitters, string owner, AppSettings settings)
        {
            foreach (VfxEmitterDefinition emitter in emitters)
            {
                GameMaterialProgram program = emitter.CustomMaterial?.Program ??
                                              GameParticleProgramResolver.Create(emitter, emitter.IsMeshPrimitive);
                GameMaterialPass first = program?.Passes?.FirstOrDefault();
                if (first == null)
                    continue;

                string key = first.ShaderPath + "|" + string.Join(";", first.Defines.Select(define => define.Name));
                if (_seenParticlePrograms.Add(key))
                    Collect(program, $"{owner}:{string.Join("+", first.Defines.Select(define => define.Name))}", settings);
            }
        }

        public void Track(GameShaderTranslator.TranslatedProgram program, string owner)
        {
            foreach ((string stage, GameShaderTranslator.TranslatedStage translatedStage) in new[] { ("vs", program.Vertex), ("ps", program.Pixel) })
            {
                foreach (GameShaderTranslator.UniformBlock block in translatedStage.Sidecar.Blocks.Where(block => block.Name != "$Globals"))
                foreach (GameShaderTranslator.BlockMember member in block.Members.Where(member => member.Used))
                    Add(_members, $"{stage}:{block.Name}.{member.Name}@{member.Offset / sizeof(float)}+{member.Size / sizeof(float)}", owner);
                foreach (GameShaderTranslator.TextureBinding texture in translatedStage.Sidecar.Textures.Where(texture => texture.Name.EndsWith("_SharedTexture", StringComparison.Ordinal)))
                    Add(_sharedTextures, $"{stage}:{texture.Name}", owner);
            }
            foreach (GameShaderTranslator.AttributeBinding attribute in program.Vertex.Sidecar.Attributes)
                Add(_attributes, $"{attribute.Semantic}{attribute.Index}", owner);
        }

        public void Print(string tag)
        {
            Console.WriteLine($"[{tag}] passes={Passes} translated={Translated}/{Passes}.");
            foreach ((string failure, int count) in _failures.OrderByDescending(pair => pair.Value).Take(10))
                Console.WriteLine($"[{tag}] FAIL x{count}: {failure}");
            Print(tag, "attribute", _attributes);
            Print(tag, "engine", _members);
            Print(tag, "shared", _sharedTextures);
        }

        /// <summary>Writes the GLSL of programs whose label contains `AM_SHADER_DUMP` into `AM_SHADER_DUMP_DIR`.</summary>
        private static void Dump(GameShaderTranslator.TranslatedProgram program, string label)
        {
            string filter = Environment.GetEnvironmentVariable("AM_SHADER_DUMP");
            string directory = Environment.GetEnvironmentVariable("AM_SHADER_DUMP_DIR");
            if (string.IsNullOrWhiteSpace(filter) || string.IsNullOrWhiteSpace(directory) ||
                !label.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(directory);
            string stem = Path.Combine(directory, string.Concat(label.Split(Path.GetInvalidFileNameChars())));
            File.WriteAllText(stem + ".vs.glsl", program.Vertex.Glsl);
            File.WriteAllText(stem + ".ps.glsl", program.Pixel.Glsl);
        }

        private void Fail(string failure)
        {
            string key = string.IsNullOrWhiteSpace(failure) ? "unknown" : failure;
            _failures[key] = _failures.GetValueOrDefault(key) + 1;
        }

        private static void Add(IDictionary<string, HashSet<string>> map, string key, string owner)
        {
            if (!map.TryGetValue(key, out HashSet<string> owners))
                map[key] = owners = new HashSet<string>(StringComparer.Ordinal);
            owners.Add(owner);
        }

        private static void Print(string tag, string kind, IDictionary<string, HashSet<string>> map)
        {
            foreach ((string key, HashSet<string> owners) in map.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                Console.WriteLine($"[{tag}Use] {kind} {key} programs={owners.Count} e.g. {string.Join(", ", owners.Take(3))}");
        }
    }
}
