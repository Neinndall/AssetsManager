using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Shaders;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    internal sealed partial class GameShaderRuntime
    {
        private sealed record ParticleMaterial(GameMaterialProgram Program);
        private readonly Dictionary<VfxEmitterDefinition, ParticleMaterial[]> _particleMaterials =
            new(ReferenceEqualityComparer.Instance);

        private (CacheEntry Cache, ParticleMaterial Material) ParticleEntry(VfxEmitterDefinition emitter, bool mesh)
        {
            if (!_particleMaterials.TryGetValue(emitter, out var materials))
                _particleMaterials.Add(emitter, materials = new ParticleMaterial[2]);
            int slot = mesh ? 1 : 0;
            if (materials[slot] == null)
                materials[slot] = new ParticleMaterial(emitter.HasResolvedCustomMaterial
                    ? emitter.CustomMaterial.Program : emitter.PaletteDefinition is { PaletteCount: <= 0 }
                        ? null : GameParticleProgramResolver.Create(emitter, mesh));
            var material = materials[slot];
            return (material.Program == null ? null : GetOrCreate(material, material.Program, mesh), material);
        }

        internal (bool Depth, bool Color) ParticleSceneInputs(VfxEmitterDefinition emitter, bool mesh)
        {
            bool depth = false, color = false;
            var cache = ParticleEntry(emitter, mesh).Cache;
            if (cache != null)
                foreach (var pass in cache.Passes)
                    foreach (var sampler in pass.Program.Samplers)
                    {
                        depth |= sampler.TextureName == "sDepthTexture_SharedTexture";
                        color |= sampler.TextureName == "SAMPLER_BACK_BUFFER_COPY_SharedTexture";
                    }
            return (depth, color);
        }

        internal int GetParticlePassCount(VfxEmitterDefinition emitter, bool mesh) =>
            _disposed ? 0 : ParticleEntry(emitter, mesh).Cache?.Passes.Count ?? 0;

        internal GameMaterialPassState GetParticlePassState(VfxEmitterDefinition emitter, bool mesh, int index)
        {
            var passes = ParticleEntry(emitter, mesh).Cache?.Passes;
            return passes != null && index >= 0 && index < passes.Count ? passes[index].Pass.State : null;
        }

        internal uint UseParticleProgram(VfxEmitterDefinition emitter, bool mesh, int index)
        {
            var cache = ParticleEntry(emitter, mesh).Cache;
            if (cache == null || index < 0 || index >= cache.Passes.Count) return 0;
            uint handle = cache.Passes[index].Program.Program;
            _gl.UseProgram(handle);
            return handle;
        }

        internal void BindParticle(VfxEmitterDefinition emitter, bool mesh, int index, in Frame frame,
            IReadOnlyDictionary<string, Vector4> parameters, Func<string, uint?> textures)
        {
            var entry = ParticleEntry(emitter, mesh).Cache.Passes[index];
            UpdateBlocks(entry.Program, entry.Globals, null, frame, null, emitter, parameters);
            foreach (var sampler in entry.Program.Samplers)
            {
                // A texture resolved on demand may upload, which binds on the active unit: activate this one first.
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + sampler.Unit));
                string own = sampler.TextureName.EndsWith(MaterialTextureSuffix, StringComparison.Ordinal)
                    ? sampler.TextureName[..^MaterialTextureSuffix.Length] : sampler.TextureName;
                var declared = entry.Pass.Textures?.FirstOrDefault(texture => string.Equals(texture.Name, own, StringComparison.Ordinal));
                uint? texture = ResolveParticleTexture(emitter.HasResolvedCustomMaterial, sampler.TextureName, declared, textures);
                TextureTarget target = sampler.Dimension switch
                {
                    GameShaderTranslator.TextureDimension.Cube => TextureTarget.TextureCubeMap,
                    GameShaderTranslator.TextureDimension.Texture3D => TextureTarget.Texture3D,
                    GameShaderTranslator.TextureDimension.Texture2DArray or GameShaderTranslator.TextureDimension.CubeArray => TextureTarget.Texture2DArray,
                    _ => TextureTarget.Texture2D
                };
                if (!texture.HasValue || texture.Value == 0)
                {
                    if (!emitter.HasResolvedCustomMaterial && sampler.Dimension == GameShaderTranslator.TextureDimension.Texture2D &&
                        own is "TEXTURE" or "TEXTUREMULT" or "PARTICLE_COLOR_TEXTURE" or "sPalettesTexture" or "sAlphaErosionTexture")
                        texture = NeutralWhite2D();
                    else
                        (texture, target) = NeutralFor(sampler.Dimension,
                            !emitter.HasResolvedCustomMaterial || sampler.TextureName.EndsWith(SharedTextureSuffix, StringComparison.Ordinal));
                }
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + sampler.Unit));
                _gl.BindTexture(target, texture.Value);
                // VFX decoded textures carry one level, as in LTK's useVfxTextures.
                int mode = own switch
                {
                    "TEXTURE" => emitter.RenderState?.TextureAddressMode ?? 0,
                    "TEXTUREMULT" => emitter.TextureMultAddressMode,
                    "sPalettesTexture" => emitter.PaletteDefinition?.AddressMode ?? 0,
                    "sAlphaErosionTexture" => emitter.AlphaErosion?.AddressMode ?? 0,
                    _ => 2
                };
                uint samplerObject;
                if (sampler.Dimension == GameShaderTranslator.TextureDimension.Buffer)
                    samplerObject = ResolveNeutralSampler(true, sampler.Dimension);
                else if (emitter.HasResolvedCustomMaterial && declared?.Sampler != null)
                    samplerObject = ResolveSampler(declared.Sampler with { SharedSampler = "No_Mip" });
                else
                {
                    var wrap = mode == 0 ? MapTextureWrap.Repeat : mode == 1 ? MapTextureWrap.Mirror : MapTextureWrap.Clamp;
                    samplerObject = ResolveSampler(new GameMaterialSamplerState("No_Mip", wrap, wrap, wrap, true, true));
                }
                _gl.BindSampler(sampler.Unit, samplerObject);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
            if (emitter.HasResolvedCustomMaterial)
                ApplyPassState(entry.Pass.State, meshDoubleSided: false);
            else
                _doubleSidedTransparent = false;
            if (emitter.IsGroundLayer) _doubleSidedTransparent = false;
        }

        internal static uint? ResolveParticleTexture(bool custom, string samplerName,
            GameMaterialTexture declared, Func<string, uint?> textures)
        {
            string own = samplerName.EndsWith(MaterialTextureSuffix, StringComparison.Ordinal)
                ? samplerName[..^MaterialTextureSuffix.Length] : samplerName;
            string path = declared?.Texture?.VirtualPath;
            if (string.IsNullOrWhiteSpace(path) && declared?.Texture?.PathHash > 0)
                path = declared.Texture.PathHash.ToString("x16");
            // A custom pass owns its texture; an emitter's TEXTURE alias must not replace it.
            if (custom && !samplerName.EndsWith(SharedTextureSuffix, StringComparison.Ordinal))
                return !string.IsNullOrWhiteSpace(path) ? textures(path) : null;
            return textures(own) ?? (!string.IsNullOrWhiteSpace(path) ? textures(path) : null);
        }

        internal void ClearParticlePrograms()
        {
            foreach (var contexts in _particleMaterials.Values)
                foreach (var context in contexts)
                    if (context != null) _programs.Remove(context);
            _particleMaterials.Clear();
            if (_programs.Count == 0)
            {
                foreach (var program in _sharedPrograms.Values) program.Dispose();
                _sharedPrograms.Clear();
            }
        }
    }

}
