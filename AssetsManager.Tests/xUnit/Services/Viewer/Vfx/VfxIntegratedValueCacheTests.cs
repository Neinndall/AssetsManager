using System;
using System.Numerics;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxIntegratedValueCacheTests
{
    [Fact]
    public void EvictedRecentTablesStillResolveBothOrdersFromTheSharedCache()
    {
        var curves = new VfxCurveF[32];
        for (int index = 0; index < curves.Length; index++)
            curves[index] = new VfxCurveF(0, new[] { 0f, 1f }, new[] { (float)index, index + 4f });
        for (int pass = 0; pass < 3; pass++)
        for (int index = 0; index < curves.Length; index++)
        {
            Assert.Equal(index + 2f, VfxIntegratedValue.Sample(curves[index], 1), 4);
            Assert.Equal(index / 2f + 2f / 3f, VfxIntegratedValue.Sample(curves[index], 1, 2), 4);
        }
    }

    [Fact]
    public void SharedValueArraysKeepTimeKeysAndIntegrationOrdersIndependent()
    {
        float[] values = { 0, 4 };
        var first = new VfxCurveF(0, new[] { 0f, 1f }, values);
        var stretched = new VfxCurveF(0, new[] { 0f, 2f }, values);
        for (int repeat = 0; repeat < 100; repeat++)
        {
            Assert.Equal(2f, VfxIntegratedValue.Sample(first, 1), 4);
            Assert.Equal(2f, VfxIntegratedValue.Sample(first, 1), 4);
            Assert.Equal(1f, VfxIntegratedValue.Sample(stretched, 1), 4);
            Assert.Equal(2f / 3f, VfxIntegratedValue.Sample(first, 1, 2), 4);
            Assert.Equal(1f / 3f, VfxIntegratedValue.Sample(stretched, 1, 2), 4);
        }
    }

    [Fact]
    public void ConcurrentScenesKeepTheirOwnTableEvenWithSharedValueArrays()
    {
        Vector3[] values = { Vector3.Zero, new(4, 8, 12) };
        Parallel.For(0, 16, worker =>
        {
            float end = worker % 2 == 0 ? 1 : 2;
            var curve = new VfxCurve3(Vector3.Zero, new[] { 0f, end }, values);
            for (int read = 0; read < 2000; read++)
            {
                int order = (read / 3) % 2 + 1;
                Vector3 expected = new Vector3(2, 4, 6) / (end * (order == 1 ? 1 : 3));
                Assert.InRange(Vector3.Distance(expected, VfxIntegratedValue.Sample(curve, 1, order)), 0, 0.0001f);
            }
        });
    }

    [Fact]
    public void ScalarAndVectorReadersKeepConstantsAndAgeClamping()
    {
        float[] times = { 0, 1 };
        var scalar = new VfxCurveF(0, times, new[] { 6f, 6f });
        var pair = new VfxCurve2(Vector2.Zero, times, new[] { new Vector2(4, 8), new Vector2(4, 8) });
        Assert.Equal(6, VfxIntegratedValue.Sample(scalar, 2));
        Assert.Equal(0, VfxIntegratedValue.Sample(scalar, -1));
        Assert.Equal(new Vector2(2, 4), VfxIntegratedValue.Sample(pair, 0.5f));
        Assert.Equal(6, VfxIntegratedValue.Sample(scalar, 1));
        Assert.Equal(9, VfxIntegratedValue.Sample(VfxCurveF.Const(9), 0.5f, 2));
    }
}
