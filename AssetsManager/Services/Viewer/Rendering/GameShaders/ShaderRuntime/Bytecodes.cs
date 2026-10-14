using System;
using System.Collections.Generic;
using System.Text;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    internal sealed partial class GameShaderRuntime
    {
        internal readonly record struct BytecodeRequest(GameMaterialKind Kind, string Vertex, string Pixel, string Defines);

        private readonly Dictionary<BytecodeRequest, GameShaderProgramResolver.ShaderBytecodeRead> _bytecodeReads = new();
        private long _cachedBytecodeBytes;
        private const long MaxCachedBytecodeBytes = 16 * 1024 * 1024;

        internal static BytecodeRequest BytecodeRequestFor(GameMaterialPass pass, GameMaterialKind kind)
        {
            var defines = new StringBuilder();
            foreach (GameMaterialDefine define in GameShaderProgramResolver.BuildDefineList(pass, kind, lowQuality: false))
            {
                // Length prefixes preserve authored names/values containing separators.
                defines.Append(define.Name.Length).Append(':').Append(define.Name)
                    .Append(define.Value?.Length ?? -1).Append(':').Append(define.Value)
                    .Append(':').Append((int)define.Source).Append(';');
            }
            return new BytecodeRequest(kind,
                !string.IsNullOrWhiteSpace(pass?.VertexShaderPath) ? pass.VertexShaderPath : pass?.ShaderPath,
                !string.IsNullOrWhiteSpace(pass?.PixelShaderPath) ? pass.PixelShaderPath : pass?.ShaderPath,
                defines.ToString());
        }

        private GameShaderProgramResolver.ShaderBytecodeMaterialProgram ReadCachedProgram(GameMaterialProgram program)
        {
            if (program == null) return null;
            IReadOnlyList<GameMaterialPass> passes = program.Passes ?? Array.Empty<GameMaterialPass>();
            var reads = new GameShaderProgramResolver.ShaderBytecodePassRead[passes.Count];
            for (int index = 0; index < passes.Count; index++)
            {
                GameMaterialPass pass = passes[index];
                BytecodeRequest request = BytecodeRequestFor(pass, program.Kind);
                if (!_bytecodeReads.TryGetValue(request, out var read))
                {
                    read = GameShaderProgramResolver.ReadFromWad(pass, program.Kind, _shaderCache, _shaderCachePath, lowQuality: false);
                    long bytes = (read.Program?.Vertex.LongLength ?? 0) + (read.Program?.Pixel.LongLength ?? 0);
                    if (_cachedBytecodeBytes + bytes > MaxCachedBytecodeBytes || _bytecodeReads.Count >= 512)
                    {
                        _bytecodeReads.Clear();
                        _cachedBytecodeBytes = 0;
                    }
                    if (bytes <= MaxCachedBytecodeBytes)
                    {
                        _bytecodeReads.Add(request, read);
                        _cachedBytecodeBytes += bytes;
                    }
                }
                // Parameters, textures and render state belong to this pass, even when code is shared.
                reads[index] = new GameShaderProgramResolver.ShaderBytecodePassRead(pass, read);
            }
            return new GameShaderProgramResolver.ShaderBytecodeMaterialProgram(program.Kind, program.Animated, reads);
        }
    }
}
