using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
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
            bool transpose)
            => Pack(
                state,
                state.PrepareInstances(state.InstanceCount),
                instanceOffset,
                vertex,
                vertexOffset,
                u,
                v,
                transpose);

        internal static void Pack(
            VfxPlaybackRuntime.EmitterState state,
            ReadOnlySpan<float> instances,
            int instanceOffset,
            float[] vertex,
            int vertexOffset,
            float u,
            float v,
            bool transpose)
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
        {
            Vector2 placed = (raw - center) * scale;
            float c = MathF.Cos(radians);
            float s = MathF.Sin(radians);
            Vector2 result = new(
                placed.X * c - placed.Y * s,
                placed.X * s + placed.Y * c);
            result += center + offset;
            if (flipU) result.X = 1f - result.X;
            if (flipV) result.Y = 1f - result.Y;
            return result;
        }

        internal static Vector2 TransformLockedAlpha(
            Vector2 raw,
            Vector2 scale,
            float radians,
            bool flipU,
            bool flipV)
        {
            Vector2 placed = raw * scale;
            float c = MathF.Cos(radians);
            float s = MathF.Sin(radians);
            Vector2 result = new(
                placed.X * c - placed.Y * s,
                placed.X * s + placed.Y * c);
            if (flipU) result.X = 1f - result.X;
            if (flipV) result.Y = 1f - result.Y;
            return result;
        }

        internal static Vector2 Cell(float logicalFrame, Vector2 divisions)
        {
            int columns = Math.Max(1, (int)MathF.Round(MathF.Max(1f, divisions.X)));
            int rows = Math.Max(1, (int)MathF.Round(MathF.Max(1f, divisions.Y)));
            int cells = checked(columns * rows);
            int frame = (int)MathF.Floor(logicalFrame + 0.0001f);
            int cell = ((frame % cells) + cells) % cells;
            return new Vector2(cell % columns, cell / columns);
        }

        private static float LookupAxis(int kind, float scale, float offset, float age01, float speed, float birthRandom)
            => kind switch
            {
                1 => scale * age01 + offset,
                2 => scale * speed + offset,
                3 => scale * birthRandom + offset,
                _ => scale
            };

        private static Vector2 Swap(Vector2 value) => new(value.Y, value.X);
    }
}

