using System;
using System.IO;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxStencilParserTests
{
    [Theory]
    [InlineData(false, 2, 0x12345678u, 2, 7, 0x12345678u)]
    [InlineData(false, 2, 0u, 2, 7, 0u)]
    [InlineData(false, 0, 0x12345678u, 0, 0, 0u)]
    [InlineData(false, 99, 0x12345678u, 0, 0, 0u)]
    [InlineData(true, 1, 0x12345678u, 0, 0, 0u)]
    public void ReadsStencilOnlyForEnabledComplexModes(bool simple, byte mode, uint id,
        byte expectedMode, byte expectedReference, uint expectedId)
    {
        var emitter = new BinTreeStruct(0, Fnv1a.HashLower("VfxEmitterDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeU8(Fnv1a.HashLower("stencilMode"), mode),
            new BinTreeU8(Fnv1a.HashLower("stencilRef"), 7),
            new BinTreeHash(Fnv1a.HashLower("StencilReferenceId"), id)
        });
        var system = new BinTreeObject("Effects/Stencil", "VfxSystemDefinitionData", new BinTreeProperty[]
        {
            new BinTreeContainer(Fnv1a.HashLower(simple ? "simpleEmitterDefinitionData" : "complexEmitterDefinitionData"),
                BinPropertyType.Struct, new BinTreeProperty[] { emitter })
        });
        using var stream = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(stream);
        var parsed = Assert.Single(Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);
        Assert.Equal(expectedMode, parsed.RenderState.StencilMode);
        Assert.Equal(expectedReference, parsed.RenderState.StencilReference);
        Assert.Equal(expectedId, parsed.RenderState.StencilReferenceId);
    }
}
