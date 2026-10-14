using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    internal sealed partial class GameShaderRuntime
    {
        internal void ResetBindings()
        {
            // A translated pass owns color/depth comparison state. Restore the stock-preview
            // defaults before another material or renderer takes over, just as the reference
            // renderer reapplies material state on every draw.
            _gl.ColorMask(true, true, true, true);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.CullFace);
            for (uint unit = 0; unit < (uint)_maxTextureUnits; unit++)
            {
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                _gl.BindSampler(unit, 0);
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.BindTexture(TextureTarget.Texture2DArray, 0);
                _gl.BindTexture(TextureTarget.Texture3D, 0);
                _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private void ApplyPassState(GameMaterialPassState state, bool meshDoubleSided)
        {
            _doubleSidedTransparent = state.BlendEnabled && (meshDoubleSided || !state.CullEnabled);
            if (state.BlendEnabled)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquation(GLEnum.FuncAdd);
                _gl.BlendFuncSeparate(
                    ToBlend(state.SourceColor),
                    ToBlend(state.DestinationColor),
                    ToBlend(state.SourceAlpha),
                    ToBlend(state.DestinationAlpha));
            }
            else
            {
                _gl.Disable(EnableCap.Blend);
            }

            if (state.DepthEnabled) _gl.Enable(EnableCap.DepthTest);
            else _gl.Disable(EnableCap.DepthTest);
            _gl.DepthFunc(ToDepth(state.DepthCompareFunc));
            _gl.DepthMask((state.WriteMask & WriteDepth) != 0);
            bool colorWrite = ColorWriteEnabled(state.WriteMask);
            _gl.ColorMask(colorWrite, colorWrite, colorWrite, colorWrite);

            if (meshDoubleSided || !state.CullEnabled)
            {
                _gl.Disable(EnableCap.CullFace);
            }
            else
            {
                _gl.Enable(EnableCap.CullFace);
                _gl.CullFace(state.WindingToCull == GameMaterialWinding.CounterClockwise
                    ? TriangleFace.Back
                    : TriangleFace.Front);
            }
        }

        internal static bool ColorWriteEnabled(uint writeMask) => (writeMask & 15u) != 0;

        private static BlendingFactor ToBlend(MapBlendFactor factor) =>
            factor switch
            {
                MapBlendFactor.Zero => BlendingFactor.Zero,
                MapBlendFactor.One => BlendingFactor.One,
                MapBlendFactor.SourceColor => BlendingFactor.SrcColor,
                MapBlendFactor.OneMinusSourceColor => BlendingFactor.OneMinusSrcColor,
                MapBlendFactor.DestinationColor => BlendingFactor.DstColor,
                MapBlendFactor.OneMinusDestinationColor => BlendingFactor.OneMinusDstColor,
                MapBlendFactor.SourceAlpha => BlendingFactor.SrcAlpha,
                MapBlendFactor.OneMinusSourceAlpha => BlendingFactor.OneMinusSrcAlpha,
                _ => BlendingFactor.One
            };

        internal static DepthFunction ToDepth(uint value) =>
            value switch
            {
                // 0 reads as class default (Lequal) rather than Never: VFX materials
                // that write it (such as HKG_Eyes_Blink_Mat) draw in the game.
                0 => DepthFunction.Lequal,
                1 => DepthFunction.Less,
                2 => DepthFunction.Equal,
                3 => DepthFunction.Lequal,
                4 => DepthFunction.Greater,
                5 => DepthFunction.Notequal,
                6 => DepthFunction.Gequal,
                7 => DepthFunction.Always,
                _ => DepthFunction.Lequal
            };

    }
}
