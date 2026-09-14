using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    /// <summary>Normalizes and composes authored BIN colors before they reach the renderer.</summary>
    internal static class VfxColorSemantics
    {
        public static Vector4 Normalize(Vector4 value)
        {
            if (!IsFinite(value)) return Vector4.One;

            return new Vector4(
                MathF.Max(value.X, 0f),
                MathF.Max(value.Y, 0f),
                MathF.Max(value.Z, 0f),
                Math.Clamp(value.W, 0f, 1f));
        }

        public static Vector4 Multiply(Vector4 left, Vector4 right)
            => new(left.X * right.X, left.Y * right.Y, left.Z * right.Z, left.W * right.W);

        public static Vector4 ResolveBirth(VfxCurve4 birthColor, float emitterTime, Random random, float? sharedRoll = null)
            => Normalize(birthColor.SampleBirth(emitterTime, random, sharedRoll));

        public static Vector4 ResolveParticle(Vector4 birthColor, VfxCurve4? colorOverLife, float normalizedAge)
        {
            Vector4 color = colorOverLife is { } curve
                ? Normalize(curve.Sample(normalizedAge))
                : Vector4.One;
            return Multiply(Normalize(birthColor), color);
        }

        private static bool IsFinite(Vector4 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) && float.IsFinite(value.W);
    }

    public enum VfxStencilOperationKind
    {
        Disabled,
        WriteReference,
        TestEqual,
        TestNotEqual
    }

    public readonly record struct VfxStencilDescriptor(
        VfxStencilOperationKind Operation,
        bool WritesStencil,
        bool WritesColor);

    /// <summary>Translates authored particle stencil modes into backend-neutral operations.</summary>
    public static class VfxStencilSemantics
    {
        private static readonly VfxStencilDescriptor Disabled = new(
            VfxStencilOperationKind.Disabled,
            WritesStencil: false,
            WritesColor: true);

        public static bool TryGetDescriptor(byte authoredMode, out VfxStencilDescriptor descriptor)
        {
            descriptor = authoredMode switch
            {
                0 => Disabled,
                1 or 4 => new VfxStencilDescriptor(
                    VfxStencilOperationKind.WriteReference,
                    WritesStencil: true,
                    WritesColor: false),
                2 => new VfxStencilDescriptor(
                    VfxStencilOperationKind.TestEqual,
                    WritesStencil: false,
                    WritesColor: true),
                3 => new VfxStencilDescriptor(
                    VfxStencilOperationKind.TestNotEqual,
                    WritesStencil: false,
                    WritesColor: true),
                _ => default
            };
            return authoredMode <= 4;
        }

        public static byte ResolveReference(
            VfxEmitterRenderState renderState,
            IReadOnlyDictionary<uint, byte> referenceIds)
        {
            ArgumentNullException.ThrowIfNull(renderState);
            ArgumentNullException.ThrowIfNull(referenceIds);
            if (renderState.StencilReference != 0)
                return renderState.StencilReference;
            return renderState.StencilReferenceId != 0 &&
                referenceIds.TryGetValue(renderState.StencilReferenceId, out byte reference)
                    ? reference
                    : (byte)0;
        }
    }

}
