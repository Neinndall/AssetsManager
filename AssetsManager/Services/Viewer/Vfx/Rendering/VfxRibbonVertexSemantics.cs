using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>
    /// Packs the per-vertex ribbon attributes the League/LTK ribbon path computes on the CPU.
    /// Trails and beams do not run their raw (u,v) through the generic quad transform in the shader:
    /// each vertex carries its final base/mult UVs, LOCK_ALPHA UV, atlas cells and color-ramp lookup.
    /// </summary>
    internal static class VfxRibbonVertexSemantics
    {
        // Offsets are the existing VfxTrailGeometry vertex declaration. The first two floats are
        // aCorner, followed by one complete VfxPlaybackRuntime instance record.
        private const int AlphaUvOffset = 11;       // aRotFrame.xy
        private const int CellOffset = 13;          // aAgeVelX.xy
        private const int LookupOffset = 15;        // aAgeVelX.zw
        private const int MultUvOffset = 17;        // aRotationSize.xy
        private const int MultCellOffset = 19;      // aRotationSize.zw

        internal static void Pack(
            VfxPlaybackRuntime.EmitterState state,
            int instanceOffset,
            float[] vertex,
            int vertexOffset,
            float u,
            float v,
            bool transpose,
            int alphaVertexIndex = -1)
            => Pack(
                state,
                state.PrepareInstances(state.InstanceCount),
                instanceOffset,
                vertex,
                vertexOffset,
                u,
                v,
                transpose,
                alphaVertexIndex);

        internal static void Pack(
            VfxPlaybackRuntime.EmitterState state,
            ReadOnlySpan<float> instances,
            int instanceOffset,
            float[] vertex,
            int vertexOffset,
            float u,
            float v,
            bool transpose,
            int alphaVertexIndex = -1)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(vertex);

            VfxEmitterDefinition definition = state.Def;
            VfxEmitterRenderState renderState = definition.RenderState ?? VfxEmitterRenderState.Default;
            Vector2 raw = new(u, v);

            Vector2 baseOffset = new(
                instances[instanceOffset + 19],
                instances[instanceOffset + 20]);
            baseOffset = VfxUvSemantics.Periodic(
                baseOffset + definition.EmitterUvScrollRate * state.RenderTime,
                renderState.TextureAddressMode);
            Vector2 baseScale = new(
                instances[instanceOffset + 21],
                instances[instanceOffset + 22]);
            float baseTurn = instances[instanceOffset + 23];
            Vector2 baseUv = Transform(
                raw,
                definition.UvTransformCenter,
                baseScale,
                baseTurn,
                baseOffset,
                renderState.FlipU,
                renderState.FlipV);
            Vector2 alphaUv = TransformLockedAlpha(
                raw,
                baseScale,
                baseTurn,
                renderState.FlipU,
                renderState.FlipV);

            Vector2 multOffset = new(
                instances[instanceOffset + 29],
                instances[instanceOffset + 30]);
            multOffset = VfxUvSemantics.Periodic(
                multOffset + definition.TextureMultEmitterUvScrollRate * state.RenderTime,
                definition.TextureMultAddressMode);
            Vector2 multScale = new(
                instances[instanceOffset + 31],
                instances[instanceOffset + 32]);
            float multTurn = instances[instanceOffset + 33];
            Vector2 multUv = Transform(
                raw,
                definition.TextureMultTransformCenter,
                multScale,
                multTurn,
                multOffset,
                definition.TextureMultFlipU,
                definition.TextureMultFlipV);

            // Beam authoring applies the layer transform first and then swaps the resulting
            // coordinate pair. Moving the swap before a non-uniform scale/rotation is not equivalent.
            if (transpose)
            {
                baseUv = Swap(baseUv);
                alphaUv = Swap(alphaUv);
                multUv = Swap(multUv);
            }

            if (definition.UvMode == 2)
            {
                // QUAD_VS_FixedAlphaUV addresses the logical indexed vertex, before
                // triangle expansion; alpha ignores the colour layer's UV transform.
                int corner = (alphaVertexIndex >= 0 ? alphaVertexIndex : vertexOffset / VfxTrailGeometry.VertexStride) & 3;
                alphaUv = new Vector2(corner is 1 or 2 ? 1f : 0f, corner >= 2 ? 1f : 0f);
            }

            vertex[vertexOffset] = baseUv.X;
            vertex[vertexOffset + 1] = baseUv.Y;
            vertex[vertexOffset + AlphaUvOffset] = alphaUv.X;
            vertex[vertexOffset + AlphaUvOffset + 1] = alphaUv.Y;

            float logicalFrame = instances[instanceOffset + 10];
            Vector2 baseCell = Cell(logicalFrame, definition.TexDiv);
            Vector2 multCell = Cell(logicalFrame, definition.TextureMultTexDiv);
            vertex[vertexOffset + CellOffset] = baseCell.X;
            vertex[vertexOffset + CellOffset + 1] = baseCell.Y;
            vertex[vertexOffset + MultCellOffset] = multCell.X;
            vertex[vertexOffset + MultCellOffset + 1] = multCell.Y;
            vertex[vertexOffset + MultUvOffset] = multUv.X;
            vertex[vertexOffset + MultUvOffset + 1] = multUv.Y;

            float age01 = instances[instanceOffset + 11];
            float speed = new Vector3(
                instances[instanceOffset + 12],
                instances[instanceOffset + 13],
                instances[instanceOffset + 14]).Length();
            float birthRandom = instances[instanceOffset + 34];
            Vector2 lookup = new(
                LookupAxis(definition.ColorLookUpTypeX ?? VfxAuthoredDefaults.ColorLookUpTypeX,
                    definition.ColorLookUpScales.X, definition.ColorLookUpOffsets.X, age01, speed, birthRandom),
                LookupAxis(definition.ColorLookUpTypeY ?? VfxAuthoredDefaults.ColorLookUpTypeY,
                    definition.ColorLookUpScales.Y, definition.ColorLookUpOffsets.Y, age01, speed, birthRandom));
            vertex[vertexOffset + LookupOffset] = lookup.X;
            vertex[vertexOffset + LookupOffset + 1] = lookup.Y;
        }

        internal static Vector2 Transform(
            Vector2 raw,
            Vector2 center,
            Vector2 scale,
            float radians,
            Vector2 offset,
            bool flipU,
            bool flipV)
            => VfxTextureTransformUtils.Transform(raw, center, scale, radians, offset, flipU, flipV);

        internal static Vector2 TransformLockedAlpha(
            Vector2 raw,
            Vector2 scale,
            float radians,
            bool flipU,
            bool flipV)
            => VfxTextureTransformUtils.TransformLockedAlpha(raw, scale, radians, flipU, flipV);

        internal static Vector2 Cell(float logicalFrame, Vector2 divisions)
            => VfxTextureTransformUtils.ResolveGridCell(logicalFrame, divisions);

        private static float LookupAxis(int kind, float scale, float offset, float age01, float speed, float birthRandom)
            => VfxColorEvaluationUtils.ResolveLookupAxis(kind, scale, offset, age01, speed, birthRandom);

        private static Vector2 Swap(Vector2 value) => new(value.Y, value.X);
    }
}

