using System.Numerics;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxStencilSceneTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void DefinitionTreeIncludesEligibleUnspawnedChildWritersAndUsesRootVisibility(bool bones, bool owner, bool skinned)
    {
        var writer = Definition(1, 4);
        var child = new VfxSystemDefinition(2, "child", "child", new[] { writer });
        var carrier = Definition(0, 0) with
        {
            TimeBeforeFirstEmission = 10,
            MeshIsSkinned = skinned,
            ChildParticleSet = new VfxChildParticleSetDefinition(new[] { new VfxChildSystemReference("child", 2, 0) },
                false, VfxCurveF.Zero, VfxCurve3.Const(Vector3.Zero), 0,
                bones ? new[] { "joint" } : null)
        };
        var tester = Definition(2, 4);
        var parent = new VfxSystemDefinition(1, "parent", "parent", new[] { carrier, tester });
        var graph = new VfxPlaybackGraphRuntime(parent, Matrix4x4.Identity, 7,
            new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child }, new Dictionary<uint, uint>(),
            (definition, transform, seed) =>
            {
                var runtime = new VfxPlaybackRuntime(seed);
                runtime.SetSystem(definition, transform);
                return runtime;
            });
        Assert.Single(graph.Runtimes);
        if (owner) graph.SetJointTransformProvider(_ => Matrix4x4.Identity);
        var scene = new VfxStencilScene();
        graph.AddStencilClaims(scene, true);
        Assert.Equal(!bones || owner || skinned, scene.TryGetState(tester, out _, out _));
        Assert.True(graph.SetEmitterVisibility(0, false));
        scene = new VfxStencilScene();
        graph.AddStencilClaims(scene, true);
        Assert.False(scene.TryGetState(tester, out _, out _));
        graph.SetAllEmittersVisible(true);
        scene = new VfxStencilScene();
        graph.AddStencilClaims(scene, false);
        Assert.False(scene.TryGetState(tester, out _, out _));
    }

    [Theory]
    [InlineData(2, 4, false, 4, false)]
    [InlineData(2, 4, true, 5, false)]
    [InlineData(2, 4, true, 4, true)]
    [InlineData(2, 0, false, 4, true)]
    [InlineData(3, 0, false, 4, false)]
    [InlineData(3, 0, true, 4, true)]
    [InlineData(3, 4, false, 4, true)]
    [InlineData(4, 4, false, 4, true)]
    public void MissingWriterFallbackOnlyDisablesImpossibleTests(byte mode, byte reference,
        bool writerLive, byte writerReference, bool tested)
    {
        var scene = new VfxStencilScene();
        var tester = Definition(mode, reference);
        scene.Add(Definition(1, writerReference), writerLive);
        scene.Add(tester, true);
        Assert.Equal(tested, scene.TryGetState(tester, out _, out _));
    }

    [Fact]
    public void NamesOverrideNumbersAndShareAcrossOwnersIncludingHiddenClaims()
    {
        var scene = new VfxStencilScene();
        scene.Add(Definition(2, 1, 100), false);
        var writer = Definition(1, 2, 200);
        var tester = Definition(2, 3, 200);
        scene.Add(writer, true);
        scene.Add(tester, true);
        Assert.True(scene.TryGetState(writer, out _, out byte first));
        Assert.True(scene.TryGetState(tester, out _, out byte second));
        Assert.Equal(62, first);
        Assert.Equal(first, second);
        Assert.False(new VfxStencilScene().TryGetState(tester, out _, out _));
    }

    [Fact]
    public void ReferencesWrapAfter64AndNumberedValuesUseOnlySixBits()
    {
        var scene = new VfxStencilScene();
        for (uint id = 1; id <= 65; id++)
        {
            var writer = Definition(1, 7, id);
            scene.Add(writer, true);
            Assert.True(scene.TryGetState(writer, out _, out byte reference));
            Assert.Equal((64 - (int)id) & 63, reference);
        }
        var numbered = Definition(2, 127);
        scene.Add(numbered, true);
        Assert.True(scene.TryGetState(numbered, out _, out byte value));
        Assert.Equal(63, value);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SimpleEmittersAndResolvedMaterialsDoNotSupplyMasks(bool simple, bool custom)
    {
        var scene = new VfxStencilScene();
        var writer = Definition(1, 4) with
        {
            IsSimpleEmitter = simple,
            CustomMaterial = custom ? ModelMaterialDefinition.Default with
                { BindingKind = ModelMaterialBindingKind.Authored } : null
        };
        var tester = Definition(2, 4);
        scene.Add(writer, true);
        scene.Add(tester, true);
        Assert.False(scene.TryGetState(writer, out _, out _));
        Assert.False(scene.TryGetState(tester, out _, out _));
        if (custom)
        {
            scene.Add(writer with { CustomMaterial = writer.CustomMaterial with
                { BindingKind = ModelMaterialBindingKind.Missing } }, true);
            Assert.True(scene.TryGetState(tester, out _, out _));
        }
    }

    [Fact]
    public void NewFrameDropsRemovedWritersAndKeepsSharedVisibleSourcesLive()
    {
        var writer = Definition(1, 4);
        var tester = Definition(2, 4);
        var scene = new VfxStencilScene();
        scene.Add(writer, true);
        scene.Add(writer, false);
        scene.Add(tester, true);
        Assert.True(scene.TryGetState(tester, out _, out _));
        scene = new VfxStencilScene();
        scene.Add(tester, true);
        Assert.False(scene.TryGetState(tester, out _, out _));
    }

    internal static VfxEmitterDefinition Definition(byte mode, byte reference, uint id = 0)
        => new("stencil", VfxCurveF.Const(1), VfxCurveF.Const(1), null, 0, 0, false, false,
            3, VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One), null, null,
            null, null, VfxCurve3.Const(Vector3.Zero), "white.tex", Vector2.One, 1, false, false,
            RenderState: VfxEmitterRenderState.Default with
            {
                StencilMode = mode, StencilReference = reference, StencilReferenceId = id,
                DisableBackfaceCull = true
            });
}
