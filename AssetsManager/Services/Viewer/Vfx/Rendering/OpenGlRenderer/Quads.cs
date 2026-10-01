using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    public sealed partial class VfxOpenGlRenderer
    {
        private void RenderQuadEmitter(VfxPlaybackRuntime.EmitterState es, ReadOnlySpan<float> instancesSpan, int renderInstanceCount, float sharedPalettePhase, bool useWireframe, float wireOpacity, Vector3 camPos, Vector3 camRight, Vector3 camUp, int passIndex)
        {
            if (!es.Def.IsVisual) return;
            bool native = UseGameParticle(es, false, passIndex, useWireframe);
            int floats = renderInstanceCount * Stride;
            bool isDistortion = es.Def.DrawsAsDistortion && !useWireframe;
            // The game draws the scene behind a distorting emitter at any strength, so it always needs the frame.
            bool warpsFrame = isDistortion;
            if (warpsFrame && _capture.ColorTexture == 0) return;

            var renderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
            _gl.Uniform2(_particleUniforms.TexDiv, es.Def.TexDiv.X <= 0 ? 1f : es.Def.TexDiv.X, es.Def.TexDiv.Y <= 0 ? 1f : es.Def.TexDiv.Y);
            _gl.Uniform2(_particleUniforms.TexSize, Math.Max(1f, es.TextureWidth), Math.Max(1f, es.TextureHeight));
            Vector2 emitterUvOffset = VfxUvSemantics.Periodic(
                es.Def.EmitterUvScrollRate * es.RenderTime,
                renderState.TextureAddressMode);
            _gl.Uniform2(_particleUniforms.EmitterUvOffset, emitterUvOffset.X, emitterUvOffset.Y);
            Vector2 uvCenter = es.Def.UvTransformCenter;
            _gl.Uniform2(_particleUniforms.UvTransformCenter, uvCenter.X, uvCenter.Y);
            _gl.Uniform1(_particleUniforms.HasTexMult, es.TextureMult != 0 ? 1 : 0);
            var multDiv = es.Def.TextureMultTexDiv;
            _gl.Uniform2(_particleUniforms.TexDivMult, multDiv.X <= 0 ? 1f : multDiv.X, multDiv.Y <= 0 ? 1f : multDiv.Y);
            _gl.Uniform2(
                _particleUniforms.TexSizeMult,
                Math.Max(1f, es.TextureMultWidth),
                Math.Max(1f, es.TextureMultHeight));
            Vector2 emitterUvOffsetMult = VfxUvSemantics.Periodic(
                es.Def.TextureMultEmitterUvScrollRate * es.RenderTime,
                es.Def.TextureMultAddressMode);
            _gl.Uniform2(_particleUniforms.UvScrollRateMult, emitterUvOffsetMult.X, emitterUvOffsetMult.Y);
            Vector2 uvCenterMult = es.Def.TextureMultTransformCenter;
            _gl.Uniform2(_particleUniforms.UvTransformCenterMult, uvCenterMult.X, uvCenterMult.Y);
            _gl.Uniform1(_particleUniforms.FlipUMult, es.Def.TextureMultFlipU ? 1 : 0);
            _gl.Uniform1(_particleUniforms.FlipVMult, es.Def.TextureMultFlipV ? 1 : 0);
            _gl.Uniform1(_particleUniforms.ClampUvMult, es.Def.TextureMultClampUvScroll ? 1 : 0);
            bool directional = ShouldDirectionOrientBillboard(es.Def);
            bool arbitrary = es.Def.IsArbitraryQuad ||
                es.Def.PrimitiveKind == VfxPrimitiveKind.ArbitraryTrail;
            _gl.Uniform1(_particleUniforms.DirectionOriented, directional ? 1 : 0);
            _gl.Uniform1(_particleUniforms.ArbitraryQuad, arbitrary ? 1 : 0);
            _gl.Uniform1(_particleUniforms.LegacyOrientation, es.Def.LegacyOrientation);
            _gl.Uniform1(_particleUniforms.PivotUp, es.Def.LegacyScaleUpFromOrigin ? 1 : 0);
            bool groundLayer = ShouldProjectToGround(es.Def);
            _gl.Uniform1(_particleUniforms.IsGroundLayer, groundLayer ? 1 : 0);
            _gl.Uniform1(_particleUniforms.PrimitiveKind, (int)es.Def.PrimitiveKind);
            bool ribbonPrimitive = es.Def.PrimitiveKind is VfxPrimitiveKind.CameraTrail or
                VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam;
            _gl.Uniform1(_particleUniforms.DepthPushPull, ribbonPrimitive ? 0f : es.Def.DepthPushPull);
            ApplyEmitterDepthState(es.Def, isDistortion);
            if (useWireframe)
            {
                _gl.DepthMask(false);
                _gl.DepthFunc(DepthFunction.Lequal);
            }
            if (useWireframe)
                ApplyWireframeBlend();
            else
                ApplyEmitterBlendState(es.Def, isDistortion);
            if (!useWireframe)
                ApplyParticleCullState(es.Def);
            ModelMaterialDefinition customMaterial = es.Def.HasResolvedCustomMaterial ? es.Def.CustomMaterial : null;
            ApplyCustomMaterialUniforms(
                customMaterial,
                _particleUniforms.UseCustomMaterial,
                _particleUniforms.MaterialTint,
                _particleUniforms.MaterialRepeat,
                _particleUniforms.MaterialAddressU,
                _particleUniforms.MaterialAddressV,
                _particleUniforms.MaterialPremultiplied);
            float alphaCutoff = customMaterial?.AlphaCutoff ?? renderState.AlphaCutoff;
            _gl.Uniform1(_particleUniforms.AlphaCutoff, alphaCutoff);
            _gl.Uniform1(
                _particleUniforms.AlphaTest,
                customMaterial is not null
                    ? (alphaCutoff > 0f ? 1 : 0)
                    : (VfxBlendModes.ShouldAlphaTest(es.Def.BlendMode, renderState.AlphaReference) ? 1 : 0));
            _gl.Uniform1(_particleUniforms.EmissiveStrength, VfxBlendModes.ResolveEmissiveStrength(es.Def.BlendMode));
            bool hasMultLayer = HasTextureMultLayer(es.Def);
            bool useColorRamp = ShouldUseColorRamp(es.Def, es.ColorGradientTexture != 0);
            _gl.Uniform1(_particleUniforms.HasColor, useColorRamp ? 1 : 0);
            _gl.Uniform1(_particleUniforms.RampAtMult, useColorRamp && hasMultLayer ? 1 : 0);
            _gl.Uniform1(_particleUniforms.UvMode, es.Def.UvMode);
            _gl.Uniform1(
                _particleUniforms.ColorRenderFlags,
                VfxBlendModes.ResolveColorRenderFlags(
                    es.Def.ColorRenderFlags,
                    !string.IsNullOrWhiteSpace(es.Def.ParticleColorTexturePath)));
            VfxPaletteDefinition palette = es.Def.PaletteDefinition;
            bool hasPalette = es.PaletteTexture != 0 && palette is { PaletteCount: > 0 };
            _gl.Uniform1(_particleUniforms.HasPalette, hasPalette ? 1 : 0);
            _gl.Uniform1(_particleUniforms.PaletteCount, Math.Max(1, palette?.PaletteCount ?? 1));
            _gl.Uniform1(_particleUniforms.PaletteAddressMode, palette?.AddressMode ?? 0);
            _gl.Uniform1(_particleUniforms.PaletteSelector, PaletteSelectorAtZero(palette));
            Vector4 paletteMask = palette?.PaletteSourceMixColor ?? Vector4.Zero;
            _gl.Uniform4(_particleUniforms.PaletteMixMask, paletteMask.X, paletteMask.Y, paletteMask.Z, paletteMask.W);
            Vector2 paletteScroll = new(
                palette?.ScrollU?.Sample(sharedPalettePhase) ?? 0f,
                palette?.ScrollV?.Sample(sharedPalettePhase) ?? 0f);
            _gl.Uniform2(_particleUniforms.PaletteScroll, paletteScroll.X, paletteScroll.Y);
            _gl.Uniform1(_particleUniforms.ColorLookUpTypeX, es.Def.ColorLookUpTypeX ?? 0);
            _gl.Uniform1(_particleUniforms.ColorLookUpTypeY, es.Def.ColorLookUpTypeY ?? 0);
            Vector2 colorLookUpScales = es.Def.ColorLookUpScales;
            _gl.Uniform2(_particleUniforms.ColorLookUpScales, colorLookUpScales.X, colorLookUpScales.Y);
            _gl.Uniform2(_particleUniforms.ColorLookUpOffsets, es.Def.ColorLookUpOffsets.X, es.Def.ColorLookUpOffsets.Y);
            _gl.Uniform1(_particleUniforms.FlipU, renderState.FlipU ? 1 : 0);
            _gl.Uniform1(_particleUniforms.FlipV, renderState.FlipV ? 1 : 0);
            _gl.Uniform1(_particleUniforms.ClampUv, renderState.ClampUvScroll ? 1 : 0);
            _gl.Uniform1(_particleUniforms.AddressMode, renderState.TextureAddressMode);
            _gl.Uniform1(_particleUniforms.AddressModeMult, es.Def.TextureMultAddressMode);
            _gl.Uniform1(_particleUniforms.IsDistortion, isDistortion ? 1 : 0);
            _gl.Uniform1(_particleUniforms.DistortionStrength, es.Def.Distortion?.Strength ?? 0f);
            VfxAlphaErosionDefinition erosionDefinition = es.Def.AlphaErosion;
            bool erosionEnabled = erosionDefinition is not null && es.Def.UvMode != 2;
            bool hasErosionMap = erosionEnabled && es.ErosionTexture != 0;
            Vector4 erosionDefault = erosionDefinition is not null && string.IsNullOrWhiteSpace(erosionDefinition.TexturePath)
                ? Vector4.One
                : Vector4.Zero;
            _gl.Uniform1(_particleUniforms.HasErosion, erosionEnabled ? 1 : 0);
            _gl.Uniform1(_particleUniforms.HasErosionMap, hasErosionMap ? 1 : 0);
            _gl.Uniform1(_particleUniforms.ErosionAddressMode, erosionDefinition?.AddressMode ?? 0);
            _gl.Uniform4(_particleUniforms.ErosionDefault, erosionDefault.X, erosionDefault.Y, erosionDefault.Z, erosionDefault.W);
            _gl.Uniform1(_particleUniforms.ErosionFeatherIn, erosionDefinition?.FeatherIn ?? 0f);
            _gl.Uniform1(_particleUniforms.ErosionFeatherOut, erosionDefinition?.FeatherOut ?? 0f);
            _gl.Uniform1(_particleUniforms.ErosionSliceWidth, erosionDefinition?.SliceWidth ?? 1.5f);
            bool useSoftParticles = ShouldUseSoftParticles(es.Def, _capture.DepthTexture != 0);
            _gl.Uniform1(_particleUniforms.HasSoftParticle, useSoftParticles ? 1 : 0);
            Vector4 softParams = ResolveSoftParticleParams(es.Def.SoftParticle);
            Vector4 softControl = ResolveSoftParticleControl(es.Def.BlendMode);
            _gl.Uniform4(_particleUniforms.SoftParticleParams, softParams.X, softParams.Y, softParams.Z, softParams.W);
            _gl.Uniform4(_particleUniforms.SoftParticleControl, softControl.X, softControl.Y, softControl.Z, softControl.W);
            _gl.Uniform3(_particleUniforms.PlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
            _gl.Uniform3(_particleUniforms.PlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
            _gl.Uniform3(_particleUniforms.PlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _textures.FallbackTransparentTexture);
            _gl.Uniform1(_particleUniforms.HasTex, ShouldSampleBaseTexture(es.Def, es.Texture) ? 1 : 0);
            ApplyAddressMode(renderState.TextureAddressMode);
            ApplyTextureSampling();
            if (es.TextureMult != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                ApplyAddressMode(es.Def.TextureMultAddressMode);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (_capture.ColorTexture != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture2);
                _gl.BindTexture(TextureTarget.Texture2D, _capture.ColorTexture);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (isDistortion)
            {
                // A missing distortion map contributes zero coverage. The transparent
                // fallback therefore preserves the draw and alpha-test path without warping.
                _gl.ActiveTexture(TextureUnit.Texture3);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    es.DistortionTexture != 0 ? es.DistortionTexture : _textures.FallbackTransparentTexture);
                ApplyAddressMode(2);
                ApplyTextureSampling();
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (es.ErosionTexture != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture4);
                _gl.BindTexture(TextureTarget.Texture2D, es.ErosionTexture);
                ApplyAddressMode(2);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
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
            if (native) BindGameParticle(es, false, passIndex, sharedPalettePhase);
            if (es.Def.PrimitiveKind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail)
            {
                int vertices = _trailGeometry.Build(
                    es,
                    ResolveCameraForward(camRight, camUp),
                    renderInstanceCount);
                if (vertices > 0)
                {
                    _gl.BindVertexArray(_trailVao);
                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
                    UploadTrailVertices(new ReadOnlySpan<float>(
                        _trailGeometry.Vertices,
                        0,
                        vertices * VfxTrailGeometry.VertexStride));
                    if (native) _gameShaders.DrawBoundArrays(PrimitiveType.Triangles, vertices);
                    else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)vertices);
                    _gl.BindVertexArray(_vao);
                }
            }
            else if (es.Def.PrimitiveKind is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam)
            {
                // LTK suppresses a beam ribbon when the same primitive resolves mMesh.
                if (!string.IsNullOrWhiteSpace(es.Def.MeshPath))
                    return;
                int vertices = _beamGeometry.Build(es, camPos, renderInstanceCount);
                if (vertices > 0)
                {
                    _gl.BindVertexArray(_trailVao);
                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
                    UploadTrailVertices(new ReadOnlySpan<float>(
                        _beamGeometry.Vertices,
                        0,
                        vertices * VfxBeamGeometry.VertexStride));
                    if (native) _gameShaders.DrawBoundArrays(PrimitiveType.Triangles, vertices);
                    else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)vertices);
                    _gl.BindVertexArray(_vao);
                }
            }
            else
            {
                _gl.BindVertexArray(_vao);
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instVbo);
                if (floats > _instCapFloats)
                {
                    _gl.BufferData(BufferTargetARB.ArrayBuffer, instancesSpan, BufferUsageARB.DynamicDraw);
                    _instCapFloats = floats;
                }
                else
                {
                    _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, instancesSpan);
                }
                if (native) _gameShaders.DrawBoundArrays(PrimitiveType.TriangleFan, 4, (uint)renderInstanceCount);
                else _gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, (uint)renderInstanceCount);
            }
            _particleUniforms = _stockParticleUniforms;
            _gl.UseProgram(_program);
        }

        private void UploadTrailVertices(ReadOnlySpan<float> vertices)
        {
            if (vertices.Length > _trailCapFloats)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.DynamicDraw);
                _trailCapFloats = vertices.Length;
                return;
            }

            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, vertices);
        }

        private void ResetEmitterDrawScratch()
        {
            _emitterUsed.Clear();
            _particlePassDraws.Clear();
            _firstSourceByEmitter.Clear();
            _sortedQuadGroups.Clear();
            _renderedSortedQuadGroups.Clear();
            foreach (List<VfxPlaybackRuntime.EmitterState> sources in _quadSourceLists)
                sources.Clear();
            _quadSourceListCount = 0;
        }

        private List<VfxPlaybackRuntime.EmitterState> RentQuadSourceList()
        {
            if (_quadSourceListCount == _quadSourceLists.Count)
                _quadSourceLists.Add(new List<VfxPlaybackRuntime.EmitterState>());
            return _quadSourceLists[_quadSourceListCount++];
        }
    }
}
