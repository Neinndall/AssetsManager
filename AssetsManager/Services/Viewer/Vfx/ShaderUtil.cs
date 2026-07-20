using System;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Shared GL shader compilation. Picks a GLSL header compatible with the live context —
    /// desktop GL (#version 330 core) or GL ES / ANGLE (#version 300 es).
    /// </summary>
    public static class ShaderUtil
    {
        public static bool DetectGles(GL gl)
        {
            try
            {
                var version = gl.GetStringS(StringName.Version);
                return version is not null && version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static uint CreateProgram(GL gl, bool gles, string vertexBody, string fragmentBody)
        {
            string vHeader = gles ? "#version 300 es\n" : "#version 330 core\n";
            string fHeader = gles ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";

            uint v = Compile(gl, ShaderType.VertexShader, vHeader + vertexBody);
            uint f = Compile(gl, ShaderType.FragmentShader, fHeader + fragmentBody);

            uint program = gl.CreateProgram();
            gl.AttachShader(program, v);
            gl.AttachShader(program, f);
            gl.LinkProgram(program);
            gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException("Program link failed: " + gl.GetProgramInfoLog(program));

            gl.DetachShader(program, v);
            gl.DetachShader(program, f);
            gl.DeleteShader(v);
            gl.DeleteShader(f);
            return program;
        }

        private static uint Compile(GL gl, ShaderType type, string source)
        {
            uint shader = gl.CreateShader(type);
            gl.ShaderSource(shader, source);
            gl.CompileShader(shader);
            gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"{type} compile failed: " + gl.GetShaderInfoLog(shader));
            return shader;
        }
    }
}
