using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Utils
{
    public sealed class SubmeshGeometryAndBindingUtilsTests
    {
        [Fact]
        public void ResolveVertexOffset_ReturnsZero_WhenIndicesAreGlobal()
        {
            // Range with startVertex=100, vertexCount=50, and global indices 100..149
            uint[] indices = { 100, 105, 120, 149 };
            int offset = SubmeshGeometryUtils.ResolveVertexOffset(100, 50, indices, "SubmeshA");
            Assert.Equal(0, offset);
        }

        [Fact]
        public void ResolveVertexOffset_ReturnsStartVertex_WhenIndicesAreLocal()
        {
            // Range with startVertex=100, vertexCount=50, but local indices 0..49
            uint[] indices = { 0, 5, 20, 49 };
            int offset = SubmeshGeometryUtils.ResolveVertexOffset(100, 50, indices, "SubmeshB");
            Assert.Equal(100, offset);
        }

        [Fact]
        public void ResolveVertexOffset_Throws_WhenIndicesOutOfBounds()
        {
            // Range with startVertex=100, vertexCount=50, but index is 300 (neither global nor local)
            uint[] indices = { 0, 5, 300 };
            Assert.Throws<InvalidDataException>(() =>
                SubmeshGeometryUtils.ResolveVertexOffset(100, 50, indices, "SubmeshC"));
        }

        [Fact]
        public void IsSubmeshVisible_MatchesCaseInsensitively()
        {
            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dark", "Gold", "Emblem" };

            Assert.False(SubmeshGeometryUtils.IsSubmeshVisible("dark", hidden));
            Assert.False(SubmeshGeometryUtils.IsSubmeshVisible("GOLD", hidden));
            Assert.True(SubmeshGeometryUtils.IsSubmeshVisible("Base", hidden));
            Assert.True(SubmeshGeometryUtils.IsSubmeshVisible("Persistent", hidden));
            Assert.True(SubmeshGeometryUtils.IsSubmeshVisible("Base", null));
        }

        [Fact]
        public void SelectActiveTexture_FollowsMapOfRules()
        {
            // 1. Missing material -> null
            Assert.Null(SubmeshBindingUtils.SelectActiveTexture("base.tex", "fallback.tex", isMaterialOpaque: true, isMaterialMissing: true));

            // 2. Base texture present -> returns base texture
            Assert.Equal("base.tex", SubmeshBindingUtils.SelectActiveTexture("base.tex", "fallback.tex", isMaterialOpaque: true, isMaterialMissing: false));
            Assert.Equal("base.tex", SubmeshBindingUtils.SelectActiveTexture("base.tex", "fallback.tex", isMaterialOpaque: false, isMaterialMissing: false));

            // 3. No base texture, but opaque -> returns fallback
            Assert.Equal("fallback.tex", SubmeshBindingUtils.SelectActiveTexture(null, "fallback.tex", isMaterialOpaque: true, isMaterialMissing: false));

            // 4. No base texture and non-opaque (translucent/additive) -> returns null
            Assert.Null(SubmeshBindingUtils.SelectActiveTexture(null, "fallback.tex", isMaterialOpaque: false, isMaterialMissing: false));
        }

        [Fact]
        public void ResolveDirectTexture_PrioritizesOverride()
        {
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Dark"] = "sett_skin66_dark_tx_cm",
                ["SnakeBase"] = "sett_skin66_snake_tx_cm"
            };

            Assert.Equal("sett_skin66_dark_tx_cm", SubmeshBindingUtils.ResolveDirectTexture("Dark", overrides, "sett_base_tx_cm"));
            Assert.Equal("sett_base_tx_cm", SubmeshBindingUtils.ResolveDirectTexture("Persistent", overrides, "sett_base_tx_cm"));
        }

        [Fact]
        public void ComputeAnimatedUv_CalculatesScrollingCorrectly()
        {
            var uv = new Vector2(0.5f, 0.5f);
            var repeat = new Vector2(2f, 2f);
            var scroll = new Vector2(0.1f, -0.2f);
            float time = 3f;

            // (0.5 * 2 + 0.1 * 3, 0.5 * 2 + -0.2 * 3) = (1.0 + 0.3, 1.0 - 0.6) = (1.3, 0.4)
            Vector2 result = SubmeshTextureCoordinateUtils.ComputeAnimatedUv(uv, repeat, scroll, time);
            Assert.Equal(1.3f, result.X, 4);
            Assert.Equal(0.4f, result.Y, 4);
        }
    }
}
