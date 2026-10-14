using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>Compiles and links shaders for the active desktop GL or OpenGL ES context.</summary>
    public static class GlShaderCompiler
    {
        public static bool UsesEmbeddedProfile(GL gl)
        {
            ArgumentNullException.ThrowIfNull(gl);
            string version = gl.GetStringS(StringName.Version);
            return version?.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase) == true;
        }

        public static uint CreateProgram(GL gl, bool embeddedProfile, string vertexSource, string fragmentSource)
        {
            ArgumentNullException.ThrowIfNull(gl);
            string vertexHeader = embeddedProfile ? "#version 300 es\n" : "#version 330 core\n";
            string fragmentHeader = embeddedProfile
                ? "#version 300 es\nprecision highp float;\n"
                : "#version 330 core\n";
            return CreateRawProgram(gl, vertexHeader + vertexSource, fragmentHeader + fragmentSource);
        }

        public static uint CreateRawProgram(
            GL gl,
            string vertexSource,
            string fragmentSource,
            IReadOnlyDictionary<uint, string> attributes = null)
        {
            ArgumentNullException.ThrowIfNull(gl);
            uint vertexShader = Compile(gl, ShaderType.VertexShader, vertexSource);
            uint fragmentShader = Compile(gl, ShaderType.FragmentShader, fragmentSource);

            try
            {
                uint program = gl.CreateProgram();
                gl.AttachShader(program, vertexShader);
                gl.AttachShader(program, fragmentShader);
                if (attributes != null)
                {
                    foreach ((uint location, string name) in attributes)
                    {
                        if (!string.IsNullOrWhiteSpace(name))
                            gl.BindAttribLocation(program, location, name);
                    }
                }
                gl.LinkProgram(program);
                gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
                if (linked == 0)
                {
                    string log = gl.GetProgramInfoLog(program);
                    gl.DeleteProgram(program);
                    throw new InvalidOperationException("OpenGL program link failed: " + log);
                }

                gl.DetachShader(program, vertexShader);
                gl.DetachShader(program, fragmentShader);
                return program;
            }
            finally
            {
                gl.DeleteShader(vertexShader);
                gl.DeleteShader(fragmentShader);
            }
        }

        private static uint Compile(GL gl, ShaderType type, string source)
        {
            uint shader = gl.CreateShader(type);
            gl.ShaderSource(shader, source);
            gl.CompileShader(shader);
            gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
            if (compiled != 0) return shader;

            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"OpenGL {type} compilation failed: {log}");
        }
    }
}
