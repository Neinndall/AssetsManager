using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AssetsManager.Shaders;
using Xunit;

namespace AssetsManager.Tests.xUnit.Shaders;

public sealed class GameShaderDrawBaseTests
{
    [Theory]
    [InlineData(true, false, "main")]
    [InlineData(false, true, "draw")]
    [InlineData(true, true, "draw_with_instances")]
    public void DrawBasesBecomeZeroWithoutChangingVertexAndInstanceIndices(bool instance, bool vertex, string entry)
    {
        uint[] words = Patch(Module(instance, vertex, entry));
        var instructions = Instructions(words);
        Assert.DoesNotContain(instructions, instruction => instruction.Op == 17 && instruction.Args[0] == 4427);
        Assert.DoesNotContain(instructions, instruction => instruction.Op == 10);
        Assert.DoesNotContain(instructions, instruction => instruction.Op is 5 or 71 && instruction.Args[0] is 21 or 23);
        Assert.DoesNotContain(instructions, instruction => instruction.Op == 59 && instruction.Args[1] is 21 or 23);
        Assert.DoesNotContain(instructions, instruction => instruction.Op == 61 && instruction.Args[2] is 21 or 23);
        uint[] expectedInterface = (instance ? new uint[] { 20 } : Array.Empty<uint>())
            .Concat(vertex ? new uint[] { 22 } : Array.Empty<uint>()).Append(24u).ToArray();
        Assert.Equal(expectedInterface, Assert.Single(instructions.Where(instruction => instruction.Op == 15)).Args
            .Skip(2 + GameShaderTranslator.SpirvString(entry).Length));

        foreach ((bool present, uint index, uint load, uint baseLoad, uint builtin) in new[]
                 { (instance, 20u, 30u, 31u, 43u), (vertex, 22u, 33u, 34u, 42u) })
        {
            if (!present) continue;
            Assert.Contains(instructions, instruction => instruction.Op == 71 && instruction.Args.SequenceEqual(new[] { index, 11u, builtin }));
            Assert.Contains(instructions, instruction => instruction.Op == 61 && instruction.Args.SequenceEqual(new[] { 10u, load, index }));
            var copied = Assert.Single(instructions.Where(instruction => instruction.Op == 83 && instruction.Args[1] == baseLoad));
            Assert.Contains(instructions, instruction => instruction.Op == 43 &&
                instruction.Args.SequenceEqual(new[] { 10u, copied.Args[2], 0u }));
        }
        int firstFunction = instructions.FindIndex(instruction => instruction.Op == 54);
        Assert.All(instructions.Select((instruction, at) => (instruction, at)).Where(pair => pair.instruction.Op == 43),
            pair => Assert.True(pair.at < firstFunction));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void GlslKeepsDrawIndicesWithoutBaseUniformsOrExtensions(bool instance, bool vertex)
    {
        string glsl = Cross(Patch(Module(instance, vertex, "main")));
        Assert.Contains("#version 300 es", glsl);
        Assert.Equal(instance, glsl.Contains("gl_InstanceID", StringComparison.Ordinal));
        Assert.Equal(vertex, glsl.Contains("gl_VertexID", StringComparison.Ordinal));
        Assert.DoesNotContain("BaseInstance", glsl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BaseVertex", glsl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uniform ", glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("draw_parameters", glsl, StringComparison.OrdinalIgnoreCase);
    }

    private static object Invoke(string method, params object[] args)
        => typeof(GameShaderTranslator).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    private static uint[] Patch(uint[] words)
    {
        var reflection = new ShaderReflectionData(5, 0, Array.Empty<ShaderResourceData>(),
            Array.Empty<ShaderConstantBufferData>(), Array.Empty<ShaderSignatureData>(), Array.Empty<ShaderSignatureData>());
        object patched = Invoke("PatchSpirv", words, reflection, GameShaderTranslator.Stage.Vertex);
        return (uint[])patched.GetType().GetProperty("Words")!.GetValue(patched)!;
    }

    private static string Cross(uint[] words)
    {
        object crossed = Invoke("CrossCompile", words, GameShaderTranslator.Stage.Vertex);
        return (string)crossed.GetType().GetProperty("Glsl")!.GetValue(crossed)!;
    }

    private static List<(uint Op, uint[] Args)> Instructions(uint[] words)
    {
        var instructions = new List<(uint, uint[])>();
        for (int at = 5; at < words.Length;)
        {
            int count = (int)(words[at] >> 16);
            Assert.InRange(count, 1, words.Length - at);
            instructions.Add((words[at] & 0xffff, words.Skip(at + 1).Take(count - 1).ToArray()));
            at += count;
        }
        return instructions;
    }

    // A valid vertex module with the index-minus-base form produced by dxbc-spirv.
    private static uint[] Module(bool instance, bool vertex, string entry)
    {
        var words = new List<uint> { 0x07230203, 0x00010500, 0, 40, 0 };
        void Emit(uint op, params uint[] args)
        {
            words.Add(((uint)(args.Length + 1) << 16) | op);
            words.AddRange(args);
        }
        Emit(17, 1);
        Emit(17, 4427);
        Emit(10, GameShaderTranslator.SpirvString("SPV_KHR_shader_draw_parameters"));
        Emit(14, 0, 1);
        var interfaces = (instance ? new uint[] { 20, 21 } : Array.Empty<uint>())
            .Concat(vertex ? new uint[] { 22, 23 } : Array.Empty<uint>()).Append(24u);
        Emit(15, new uint[] { 0, 1 }.Concat(GameShaderTranslator.SpirvString(entry)).Concat(interfaces).ToArray());
        foreach ((bool present, uint index, uint drawBase, uint builtin, uint baseBuiltin, string name) in new[]
                 { (instance, 20u, 21u, 43u, 4425u, "Instance"), (vertex, 22u, 23u, 42u, 4424u, "Vertex") })
        {
            if (!present) continue;
            Emit(5, new[] { index }.Concat(GameShaderTranslator.SpirvString(name + "Index")).ToArray());
            Emit(5, new[] { drawBase }.Concat(GameShaderTranslator.SpirvString("Base" + name)).ToArray());
            Emit(71, index, 11, builtin);
            Emit(71, drawBase, 11, baseBuiltin);
        }
        Emit(71, 24, 11, 0);
        Emit(19, 2);
        Emit(33, 3, 2);
        Emit(21, 10, 32, 1);
        Emit(32, 11, 1, 10);
        Emit(22, 12, 32);
        Emit(23, 13, 12, 4);
        Emit(32, 14, 3, 13);
        Emit(43, 12, 15, 0);
        Emit(43, 12, 16, 0x3f800000);
        if (instance) { Emit(59, 11, 20, 1); Emit(59, 11, 21, 1); }
        if (vertex) { Emit(59, 11, 22, 1); Emit(59, 11, 23, 1); }
        Emit(59, 14, 24, 3);
        Emit(54, 2, 1, 0, 3);
        Emit(248, 25);
        if (instance)
        {
            Emit(61, 10, 30, 20);
            Emit(61, 10, 31, 21);
            Emit(130, 10, 32, 30, 31);
            Emit(111, 12, 36, 32);
        }
        if (vertex)
        {
            Emit(61, 10, 33, 22);
            Emit(61, 10, 34, 23);
            Emit(130, 10, 35, 33, 34);
            Emit(111, 12, 37, 35);
        }
        Emit(80, 13, 38, vertex ? 37u : 15u, instance ? 36u : 15u, 15, 16);
        Emit(62, 24, 38);
        Emit(253);
        Emit(56);
        return words.ToArray();
    }
}
