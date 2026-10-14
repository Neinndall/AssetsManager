using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.Core
{
    internal sealed class GlLightGridBindings
    {
        private readonly GL _gl;
        private readonly int _on;
        private readonly int _fullBright;
        private readonly int[] _faces = new int[6];

        internal GlLightGridBindings(GL gl, uint program)
        {
            _gl = gl;
            _on = gl.GetUniformLocation(program, "uLightGridOn");
            _fullBright = gl.GetUniformLocation(program, "uLightGridFullBright");
            for (int face = 0; face < 6; face++)
                _faces[face] = gl.GetUniformLocation(program, $"uLightGridCube[{face}]");
        }

        internal void Apply(MapLightGridData grid, Vector3 position)
        {
            _gl.Uniform1(_on, grid == null ? 0 : 1);
            if (grid == null) return;
            _gl.Uniform1(_fullBright, grid.FullBright);
            Span<Vector3> cube = stackalloc Vector3[6];
            grid.SampleSceneCube(position, cube);
            for (int face = 0; face < 6; face++)
                _gl.Uniform3(_faces[face], cube[face]);
        }
    }
}
