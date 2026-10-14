using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.Core;

/// <summary>Elides repeated bindings only while the owning renderer has exclusive draw control.</summary>
internal sealed class GlDrawBindings
{
    private readonly GL _gl;
    private readonly Dictionary<uint, uint> _uniformBuffers = new();
    private bool _active;
    private bool _hasProgram;
    private uint _program;

    internal int ProgramBindCount { get; private set; }
    internal int UniformBufferBindCount { get; private set; }

    internal GlDrawBindings(GL gl) => _gl = gl;

    internal void Begin(bool cache = true)
    {
        Invalidate();
        ProgramBindCount = UniformBufferBindCount = 0;
        _active = cache;
    }

    internal void End()
    {
        _active = false;
        Invalidate();
    }

    internal void Invalidate()
    {
        _hasProgram = false;
        _uniformBuffers.Clear();
    }

    internal void UseProgram(uint program)
    {
        if (_active && _hasProgram && _program == program)
            return;
        _gl.UseProgram(program);
        ProgramBindCount++;
        if (!_active) return;
        _hasProgram = true;
        _program = program;
    }

    internal void BindUniformBuffer(uint binding, uint buffer)
    {
        if (_active && _uniformBuffers.TryGetValue(binding, out uint bound) && bound == buffer)
            return;
        _gl.BindBufferBase(BufferTargetARB.UniformBuffer, binding, buffer);
        UniformBufferBindCount++;
        if (_active) _uniformBuffers[binding] = buffer;
    }
}
