using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    public sealed partial class VfxOpenGlRenderer
    {
        private void EnsureMeshProgram()
        {
            if (_meshProgram == 0)
            {
                _meshProgram = GlShaderCompiler.CreateProgram(_gl, _gles, VfxShaderSource.MeshVertex, VfxShaderSource.MeshFragment);
            _meshUniforms = _stockMeshUniforms = new VfxShaderUniforms(_gl, _meshProgram);

                uint boneBlock = _gl.GetUniformBlockIndex(_meshProgram, "VfxBoneTransforms");
                if (boneBlock != uint.MaxValue)
                    _gl.UniformBlockBinding(_meshProgram, boneBlock, OwnerBoneBinding);
                _meshBoneBuffer = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.UniformBuffer, _meshBoneBuffer);
                _gl.BufferData(
                    BufferTargetARB.UniformBuffer,
                    new ReadOnlySpan<float>(new float[GpuSkinningData.MaxBones * 16]),
                    BufferUsageARB.DynamicDraw);
                _drawBindings.BindUniformBuffer(OwnerBoneBinding, _meshBoneBuffer);
                _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
            }
        }

        internal bool HasEmitterMesh(float[] positions, bool skinning)
            => _ready && _meshResources?.Contains(positions, skinning) == true;

        public void UploadEmitterMesh(
            VfxPlaybackRuntime.EmitterState es,
            float[] positions,
            float[] normals,
            float[] uvs,
            float[] colors,
            uint[] indices = null,
            float[] boneIndices = null,
            float[] boneWeights = null)
        {
            if (!_ready) return;
            EnsureMeshProgram();
            _meshResources.Upload(es, positions, normals, uvs, colors, indices, boneIndices, boneWeights);
        }

        internal void SetOwnerSkinningMatrices(Matrix4x4[] matrices)
        {
            _ownerSkinningMatrices = matrices;
            _ownerSkinningCount = Math.Min(matrices?.Length ?? 0, GpuSkinningData.MaxBones);
            if (!_ready || _ownerSkinningCount == 0)
                return;

            EnsureMeshProgram();
            UploadMeshBonePalette(new ReadOnlySpan<Matrix4x4>(matrices, 0, _ownerSkinningCount));
        }

        internal void SetOwnerWorldTransform(Matrix4x4 transform)
            => _ownerWorldTransform = transform;

        private void UploadMeshBonePalette(ReadOnlySpan<Matrix4x4> matrices)
        {
            if (_meshBoneBuffer == 0 || matrices.Length == 0) return;
            int count = Math.Min(matrices.Length, GpuSkinningData.MaxBones);
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, _meshBoneBuffer);
            _gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, matrices[..count]);
            _drawBindings.BindUniformBuffer(OwnerBoneBinding, _meshBoneBuffer);
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        }

        internal void SetOwnerHiddenSubmeshes(IEnumerable<uint> hashes)
        {
            _ownerHiddenSubmeshes.Clear();
            if (hashes == null) return;
            foreach (uint hash in hashes)
                _ownerHiddenSubmeshes.Add(hash);
        }

        private void ReleaseMeshes()
            => _meshResources.Clear();

        private void RenderMeshEmitter(
            VfxPlaybackRuntime.EmitterState es,
            Matrix4x4 viewProj,
            Vector3 camPos,
            Vector3 camUp,
            ReadOnlySpan<float> instances,
            int instanceCount,
            float sharedPalettePhase,
            bool wireframePass,
            float wireframeOpacity, int passIndex = 0)
        {
            if (es.MeshVao == 0 || es.MeshVertexCount == 0) return;
            bool isDistortion = es.Def.DrawsAsDistortion && !wireframePass;
            // The game draws the scene behind a distorting emitter at any strength, so it always needs the frame.
            bool warpsFrame = isDistortion;
            if (warpsFrame && _capture.ColorTexture == 0) return;
            EnsureMeshProgram();
            bool native = UseGameParticle(es, true, passIndex, wireframePass);
            _meshUniforms.Uniform1(_meshUniforms.WireframePass, wireframePass ? 1 : 0);
            _meshUniforms.Uniform4(
                _meshUniforms.WireframeColor,
                PreviewWireColor.X,
                PreviewWireColor.Y,
                PreviewWireColor.Z,
                Math.Clamp(wireframeOpacity, 0f, 1f));
            _gl.BindVertexArray(es.MeshVao);
            _gl.UniformMatrix4(_meshUniforms.ViewProj, 1, false, in viewProj.M11);
            _gl.UniformMatrix4(_meshUniforms.OwnerWorld, 1, false, in _ownerWorldTransform.M11);
            _meshUniforms.Uniform3(_meshUniforms.CamPos, camPos.X, camPos.Y, camPos.Z);
            _meshUniforms.Uniform3(_meshUniforms.CamUp, camUp.X, camUp.Y, camUp.Z);
            bool attachedMesh = es.Def.PrimitiveKind == VfxPrimitiveKind.AttachedMesh;
            bool useOwnerSkinning = attachedMesh && es.MeshHasSkinning && _ownerSkinningCount > 0;
            bool useParticleMeshSkinning = !attachedMesh && es.MeshHasSkinning && es.MeshAnimation is not null;
            bool useSkinning = useOwnerSkinning || useParticleMeshSkinning;
            _meshUniforms.Uniform1(_meshUniforms.UseSkinning, useSkinning ? 1 : 0);
            if (useOwnerSkinning && _ownerSkinningMatrices is { Length: > 0 })
                UploadMeshBonePalette(new ReadOnlySpan<Matrix4x4>(_ownerSkinningMatrices, 0, _ownerSkinningCount));

            // Direction-oriented mesh particles take precedence over camera alignment in LTK.
            bool cameraAlignedMesh = !attachedMesh && !es.Def.IsDirectionOriented &&
                (es.Def.MeshAlignPitchToCamera || es.Def.MeshAlignYawToCamera);
            _meshUniforms.Uniform1(_meshUniforms.AlignPitchToCamera, cameraAlignedMesh && es.Def.MeshAlignPitchToCamera ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.AlignYawToCamera, cameraAlignedMesh && es.Def.MeshAlignYawToCamera ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.MeshSkinned, es.Def.MeshIsSkinned ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.IsGroundLayer, es.Def.IsGroundLayer ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.Tex, 0);
            _meshUniforms.Uniform1(_meshUniforms.TexMult, 1);
            _meshUniforms.Uniform1(_meshUniforms.ColorMap, 7);
            _meshUniforms.Uniform1(_meshUniforms.PaletteMap, 8);
            _meshUniforms.Uniform1(_meshUniforms.ErosionTex, 4);
            _meshUniforms.Uniform1(_meshUniforms.ReflectionTex, 5);
            _meshUniforms.Uniform1(_meshUniforms.SceneDepthTex, 6);
            Vector2 texDiv = es.Def.TexDiv;
            _meshUniforms.Uniform2(_meshUniforms.TexDiv, texDiv.X <= 0f ? 1f : texDiv.X, texDiv.Y <= 0f ? 1f : texDiv.Y);
            _meshUniforms.Uniform2(_meshUniforms.TexSize, Math.Max(1f, es.TextureWidth), Math.Max(1f, es.TextureHeight));
            Vector2 uvCenter = es.Def.UvTransformCenter;
            _meshUniforms.Uniform2(_meshUniforms.UvTransformCenter, uvCenter.X, uvCenter.Y);
            _meshUniforms.Uniform1(_meshUniforms.HasTexMult, es.TextureMult != 0 ? 1 : 0);
            Vector2 textureMultTexDiv = es.Def.TextureMultTexDiv;
            _meshUniforms.Uniform2(
                _meshUniforms.TexDivMult,
                textureMultTexDiv.X <= 0f ? 1f : textureMultTexDiv.X,
                textureMultTexDiv.Y <= 0f ? 1f : textureMultTexDiv.Y);
            _meshUniforms.Uniform2(
                _meshUniforms.TexSizeMult,
                Math.Max(1f, es.TextureMultWidth),
                Math.Max(1f, es.TextureMultHeight));
            Vector2 uvCenterMult = es.Def.TextureMultTransformCenter;
            _meshUniforms.Uniform2(_meshUniforms.UvTransformCenterMult, uvCenterMult.X, uvCenterMult.Y);
            Vector2 emitterUvOffsetMult = VfxUvSemantics.Periodic(
                    es.Def.TextureMultEmitterUvScrollRate * es.RenderTime,
                    es.Def.TextureMultAddressMode);
            _meshUniforms.Uniform2(_meshUniforms.EmitterUvOffsetMult, emitterUvOffsetMult.X, emitterUvOffsetMult.Y);
            _meshUniforms.Uniform1(_meshUniforms.FlipUMult, es.Def.TextureMultFlipU ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.FlipVMult, es.Def.TextureMultFlipV ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.AddressModeMult, es.Def.TextureMultAddressMode);
            _meshUniforms.Uniform1(_meshUniforms.ClampUvMult, es.Def.TextureMultClampUvScroll ? 1 : 0);
            VfxAlphaErosionDefinition meshErosion = es.Def.AlphaErosion;
            bool meshErosionEnabled = meshErosion is not null;
            bool meshHasErosionMap = meshErosionEnabled && es.ErosionTexture != 0;
            Vector4 meshErosionDefault = meshErosion is not null && string.IsNullOrWhiteSpace(meshErosion.TexturePath)
                ? Vector4.One
                : Vector4.Zero;
            _meshUniforms.Uniform1(_meshUniforms.HasErosion, meshErosionEnabled ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.HasErosionMap, meshHasErosionMap ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.ErosionAddressMode, meshErosion?.AddressMode ?? 0);
            _meshUniforms.Uniform4(_meshUniforms.ErosionDefault, meshErosionDefault.X, meshErosionDefault.Y, meshErosionDefault.Z, meshErosionDefault.W);
            _meshUniforms.Uniform1(_meshUniforms.ErosionFeatherIn, meshErosion?.FeatherIn ?? 0f);
            _meshUniforms.Uniform1(_meshUniforms.ErosionFeatherOut, meshErosion?.FeatherOut ?? 0f);
            _meshUniforms.Uniform1(_meshUniforms.ErosionSliceWidth, meshErosion?.SliceWidth ?? 1.5f);
            if (!native)
            {
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _textures.FallbackTransparentTexture);
            }
            _meshUniforms.Uniform1(_meshUniforms.HasTex, ShouldSampleBaseTexture(es.Def, es.Texture) ? 1 : 0);
            var renderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
            ModelMaterialDefinition customMaterial = es.Def.HasResolvedCustomMaterial ? es.Def.CustomMaterial : null;
            ApplyCustomMaterialUniforms(
                customMaterial,
                _meshUniforms);
            if (!native) ApplyAddressMode(renderState.TextureAddressMode);
            float alphaCutoff = customMaterial?.AlphaCutoff ?? renderState.AlphaCutoff;
            _meshUniforms.Uniform1(_meshUniforms.AlphaCutoff, alphaCutoff);
            _meshUniforms.Uniform1(
                _meshUniforms.AlphaTest,
                customMaterial is not null
                    ? (alphaCutoff > 0f ? 1 : 0)
                    : (VfxBlendModes.ShouldAlphaTest(es.Def.BlendMode, renderState.AlphaReference) ? 1 : 0));
            _meshUniforms.Uniform1(_meshUniforms.EmissiveStrength, VfxBlendModes.ResolveEmissiveStrength(es.Def.BlendMode));
            _meshUniforms.Uniform1(_meshUniforms.IsDistortion, isDistortion ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.DistortionStrength, es.Def.Distortion?.Strength ?? 0f);
            _meshUniforms.Uniform1(_meshUniforms.DistortionTex, 2);
            _meshUniforms.Uniform1(_meshUniforms.SceneTex, 3);
            if (warpsFrame && !native)
            {
                _gl.ActiveTexture(TextureUnit.Texture2);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    es.DistortionTexture != 0 ? es.DistortionTexture : _textures.FallbackTransparentTexture);
                ApplyAddressMode(2);
                ApplyTextureSampling();
                _gl.ActiveTexture(TextureUnit.Texture3);
                _gl.BindTexture(TextureTarget.Texture2D, _capture.ColorTexture);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            _meshUniforms.Uniform1(_meshUniforms.HasColor, ShouldUseColorRamp(es.Def, es.ColorGradientTexture != 0) ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.RampAtMult, 0);
            _meshUniforms.Uniform1(_meshUniforms.UvMode, es.Def.UvMode);
            _meshUniforms.Uniform1(
                _meshUniforms.ColorRenderFlags,
                VfxBlendModes.ResolveColorRenderFlags(
                    es.Def.ColorRenderFlags,
                    !string.IsNullOrWhiteSpace(es.Def.ParticleColorTexturePath)));
            VfxPaletteDefinition meshPalette = es.Def.PaletteDefinition;
            bool meshHasPalette = es.PaletteTexture != 0 && meshPalette is { PaletteCount: > 0 };
            _meshUniforms.Uniform1(_meshUniforms.HasPalette, meshHasPalette ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.PaletteCount, Math.Max(1, meshPalette?.PaletteCount ?? 1));
            _meshUniforms.Uniform1(_meshUniforms.PaletteAddressMode, meshPalette?.AddressMode ?? 0);
            _meshUniforms.Uniform1(_meshUniforms.PaletteSelector, PaletteSelectorAtZero(meshPalette));
            Vector4 meshPaletteMask = meshPalette?.PaletteSourceMixColor ?? Vector4.Zero;
            _meshUniforms.Uniform4(_meshUniforms.PaletteMixMask, meshPaletteMask.X, meshPaletteMask.Y, meshPaletteMask.Z, meshPaletteMask.W);
            Vector2 meshPaletteScroll = new(
                meshPalette?.ScrollU?.Sample(sharedPalettePhase) ?? 0f,
                meshPalette?.ScrollV?.Sample(sharedPalettePhase) ?? 0f);
            _meshUniforms.Uniform2(_meshUniforms.PaletteScroll, meshPaletteScroll.X, meshPaletteScroll.Y);
            _meshUniforms.Uniform1(_meshUniforms.ColorLookUpTypeX, es.Def.ColorLookUpTypeX ?? 0);
            _meshUniforms.Uniform1(_meshUniforms.ColorLookUpTypeY, es.Def.ColorLookUpTypeY ?? 0);
            Vector2 meshColorLookUpScales = es.Def.ColorLookUpScales;
            _meshUniforms.Uniform2(_meshUniforms.ColorLookUpScales, meshColorLookUpScales.X, meshColorLookUpScales.Y);
            _meshUniforms.Uniform2(_meshUniforms.ColorLookUpOffsets, es.Def.ColorLookUpOffsets.X, es.Def.ColorLookUpOffsets.Y);
            _meshUniforms.Uniform1(_meshUniforms.FlipU, renderState.FlipU ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.FlipV, renderState.FlipV ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.AddressMode, renderState.TextureAddressMode);
            _meshUniforms.Uniform1(_meshUniforms.ClampUv, renderState.ClampUvScroll ? 1 : 0);
            if (!native)
            {
                ApplyTextureSampling();
                if (es.TextureMult != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture1);
                    _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                    ApplyAddressMode(es.Def.TextureMultAddressMode);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                if (es.ErosionTexture != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture4);
                    _gl.BindTexture(TextureTarget.Texture2D, es.ErosionTexture);
                    ApplyAddressMode(2);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
            }
            VfxReflectionDefinition reflection = es.Def.Reflection;
            bool hasReflectionCube = reflection is not null && es.ReflectionTexture != 0;
            _meshUniforms.Uniform1(_meshUniforms.HasReflection, hasReflectionCube ? 1 : 0);
            _meshUniforms.Uniform1(_meshUniforms.AttachedMesh, attachedMesh ? 1 : 0);

            Vector4 fresnelColor = reflection?.FresnelColor ?? Vector4.Zero;
            _meshUniforms.Uniform4(
                _meshUniforms.Fresnel,
                fresnelColor.X,
                fresnelColor.Y,
                fresnelColor.Z,
                reflection?.Fresnel ?? 1f);
            _meshUniforms.Uniform4(
                _meshUniforms.Reflection,
                reflection?.ReflectionFresnel ?? 1f,
                reflection?.DirectOpacity ?? 0f,
                reflection?.GlancingOpacity ?? 1f,
                0f);
            Vector4 reflectionColor = reflection?.ReflectionFresnelColor ?? Vector4.One;
            _meshUniforms.Uniform4(
                _meshUniforms.ReflectionColor,
                reflectionColor.X,
                reflectionColor.Y,
                reflectionColor.Z,
                reflectionColor.W);
            if (hasReflectionCube && !native)
            {
                _gl.ActiveTexture(TextureUnit.Texture5);
                _gl.BindTexture(TextureTarget.TextureCubeMap, es.ReflectionTexture);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            bool meshUsesSoftParticles = ShouldUseSoftParticles(es.Def, _capture.DepthTexture != 0);
            _meshUniforms.Uniform1(_meshUniforms.HasSoftParticle, meshUsesSoftParticles ? 1 : 0);
            Vector4 meshSoftParams = ResolveSoftParticleParams(es.Def.SoftParticle);
            Vector4 meshSoftControl = ResolveSoftParticleControl(es.Def.BlendMode);
            _meshUniforms.Uniform4(_meshUniforms.SoftParticleParams, meshSoftParams.X, meshSoftParams.Y, meshSoftParams.Z, meshSoftParams.W);
            _meshUniforms.Uniform4(_meshUniforms.SoftParticleControl, meshSoftControl.X, meshSoftControl.Y, meshSoftControl.Z, meshSoftControl.W);
            _meshUniforms.Uniform2(_meshUniforms.DepthProjection, _depthProjectionValue.X, _depthProjectionValue.Y);
            _meshUniforms.Uniform2(_meshUniforms.ViewportSize, (float)_capture.Width, (float)_capture.Height);
            if (!native)
            {
                if (_capture.DepthTexture != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture6);
                    _gl.BindTexture(TextureTarget.Texture2D, _capture.DepthTexture);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                _gl.ActiveTexture(TextureUnit.Texture7);
                _gl.BindTexture(TextureTarget.Texture2D, es.ColorGradientTexture != 0
                    ? es.ColorGradientTexture
                    : _textures.FallbackTransparentTexture);
                ApplyAddressMode(2);
                ApplyTextureSampling();
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 8));
                _gl.BindTexture(TextureTarget.Texture2D, es.PaletteTexture != 0
                    ? es.PaletteTexture
                    : _textures.FallbackTransparentTexture);
                ApplyAddressMode(2);
                ApplyTextureSampling();
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (wireframePass)
            {
                // LTK's wire twin is double-sided and alpha-blended independently from the
                // authored material, so mesh edges cannot inherit culling or additive modes.
                _gl.Disable(EnableCap.CullFace);
                _gl.DepthMask(false);
                _gl.DepthFunc(DepthFunction.Lequal);
                ApplyWireframeBlend();
            }
            else
            {
                if (customMaterial is not null)
                {
                    ApplyCustomMaterialCull(customMaterial.RenderState);
                }
                else
                {
                    // Riot meshes cull backfaces unless the authored emitter explicitly opts out.
                    if (es.Def.RenderState?.DisableBackfaceCull == true)
                        _gl.Disable(EnableCap.CullFace);
                    else
                    {
                        _gl.Enable(EnableCap.CullFace);
                        _gl.CullFace(TriangleFace.Back);
                    }
                }
                ApplyEmitterBlendState(es.Def, isDistortion);
            }

            Vector2 emitterUvOffset = VfxUvSemantics.Periodic(
                    es.Def.EmitterUvScrollRate * es.RenderTime,
                    renderState.TextureAddressMode);
            _meshUniforms.Uniform2(_meshUniforms.EmitterUvOffset, emitterUvOffset.X, emitterUvOffset.Y);
            // Material parameters and textures are shared by every particle in this emitter pass.
            if (native) BindGameParticle(es, true, passIndex, sharedPalettePhase);
            for (int i = 0; i < instanceCount; i++)
            {
                int o = i * Stride;
                _meshUniforms.Uniform3(_meshUniforms.WorldPos, instances[o], instances[o + 1], instances[o + 2]);
                _meshUniforms.Uniform3(_meshUniforms.PlacementRight, instances[o + 36], instances[o + 37], instances[o + 38]);
                _meshUniforms.Uniform3(_meshUniforms.PlacementUp, instances[o + 39], instances[o + 40], instances[o + 41]);
                _meshUniforms.Uniform3(_meshUniforms.PlacementForward, instances[o + 42], instances[o + 43], instances[o + 44]);
                Vector3 orbitRotation = i < es.Particles.Count
                    ? es.Particles[i].BirthOrbitalVelocity * es.Particles[i].Age
                    : Vector3.Zero;
                _meshUniforms.Uniform3(_meshUniforms.OrbitRotation, orbitRotation.X, orbitRotation.Y, orbitRotation.Z);
                float ownerScale = attachedMesh && float.IsFinite(es.MeshOwnerScale) && es.MeshOwnerScale > 0f
                    ? es.MeshOwnerScale
                    : 1f;
                float scaleX = ClampScale(instances[o + 3]) * ownerScale;
                float scaleY = ClampScale(instances[o + 4]) * ownerScale;
                float scaleZ = ClampScale(instances[o + 18]) * ownerScale;
                _meshUniforms.Uniform3(_meshUniforms.Scale, scaleX, scaleY, scaleZ);
                Vector3 meshRotation = new(
                    instances[o + 15],
                    instances[o + 16],
                    instances[o + 17]);
                _meshUniforms.Uniform3(
                    _meshUniforms.Rotation,
                    meshRotation.X,
                    meshRotation.Y,
                    meshRotation.Z);
                _meshUniforms.Uniform3(_meshUniforms.GameLookupDrivers, instances[o + 11],
                    new Vector3(instances[o + 12], instances[o + 13], instances[o + 14]).Length(), instances[o + 34]);
                _meshUniforms.Uniform4(_meshUniforms.Color, instances[o + 5], instances[o + 6], instances[o + 7], instances[o + 8]);
                _meshUniforms.Uniform2(_meshUniforms.BirthUvOffset, instances[o + 19], instances[o + 20]);
                _meshUniforms.Uniform2(_meshUniforms.UvScale, instances[o + 21], instances[o + 22]);
                _meshUniforms.Uniform1(_meshUniforms.UvRotation, instances[o + 23]);
                _meshUniforms.Uniform1(_meshUniforms.ErosionDrive, instances[o + 24]);
                _meshUniforms.Uniform4(_meshUniforms.ErosionMixer, instances[o + 25], instances[o + 26], instances[o + 27], instances[o + 28]);
                _meshUniforms.Uniform2(_meshUniforms.UvOffsetMult, instances[o + 29], instances[o + 30]);
                _meshUniforms.Uniform2(_meshUniforms.UvScaleMult, instances[o + 31], instances[o + 32]);
                _meshUniforms.Uniform1(_meshUniforms.UvRotationMult, instances[o + 33]);
                _meshUniforms.Uniform1(_meshUniforms.Frame, instances[o + 10]);

                if (useParticleMeshSkinning && i < es.Particles.Count)
                    UploadMeshBonePalette(es.MeshAnimation.EvaluatePalette(es.Particles[i].Age));

                if (es.MeshIndexCount > 0)
                {
                    if (_drawElements != null)
                    {
                        if (attachedMesh && es.MeshRanges is { Length: > 0 })
                        {
                            bool narrowed = HasAttachedDrawMatch(es.MeshRanges, es.Def.SubmeshesToDraw);
                            foreach (VfxMeshRangeData range in es.MeshRanges)
                            {
                                if (!ShouldDrawAttachedRange(
                                        range.Hash,
                                        narrowed,
                                        es.Def.SubmeshesToDraw,
                                        es.Def.SubmeshesToDrawAlways,
                                        _ownerHiddenSubmeshes))
                                {
                                    continue;
                                }

                                if (native) _gameShaders.DrawBoundPass(_drawElements, range.IndexCount, new IntPtr(range.StartIndex * sizeof(uint)));
                                else _drawElements((uint)PrimitiveType.Triangles, range.IndexCount, (uint)DrawElementsType.UnsignedInt,
                                    new IntPtr(range.StartIndex * sizeof(uint)));
                            }
                        }
                        else
                        {
                            if (native) _gameShaders.DrawBoundPass(_drawElements, es.MeshIndexCount, IntPtr.Zero);
                            else _drawElements((uint)PrimitiveType.Triangles, es.MeshIndexCount, (uint)DrawElementsType.UnsignedInt, IntPtr.Zero);
                        }
                    }
                }
                else if (native) _gameShaders.DrawBoundArrays(PrimitiveType.Triangles, es.MeshVertexCount);
                else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)es.MeshVertexCount);
            }
            _meshUniforms = _stockMeshUniforms;
            _particleUniforms = _stockParticleUniforms;
            _gl.BindVertexArray(_vao);
        }
    }
}
