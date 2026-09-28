using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class GameShaderPermutationLookupTests
    {
        private static readonly string[] Switches = { "A", "B", "C", "D" };

        [Fact]
        public void ExactPermutationIgnoresDefinesTheTocDoesNotDeclare()
        {
            ShaderToc toc = Toc("A=1B=0", "A=1B=1");

            GameShaderPermutationLookup.Match match = GameShaderPermutationLookup.Find(toc, Defines("A=1", "B=1", "DISABLE_FOW=1"));

            Assert.True(match.Exact);
            Assert.Equal(1, match.Index);
            Assert.Equal("A=1B=1", GameShaderPermutationLookup.Key(match.Defines, toc.BaseDefines));
        }

        [Fact]
        public void MissingBaseDefineIsFilledBeforeAnyValueChanges()
        {
            // HKG_MatCap_Diff_Alpha: the material leaves USE_ALBEDO_REMAP out, the game only compiled it with 1.
            ShaderToc toc = Toc("A=0B=1", "A=1B=1C=1");

            GameShaderPermutationLookup.Match match = GameShaderPermutationLookup.Find(toc, Defines("A=1", "B=1"));

            Assert.Equal(1, match.Index);
            Assert.Equal(new[] { "+C=1" }, match.Changes.Select(change => change.ToString()));
        }

        [Fact]
        public void ValueChangeWinsOverDroppingTheDefine()
        {
            ShaderToc toc = Toc("A=1", "A=1B=0");

            GameShaderPermutationLookup.Match match = GameShaderPermutationLookup.Find(toc, Defines("A=1", "B=1"));

            Assert.Equal(1, match.Index);
            GameShaderPermutationLookup.Change change = Assert.Single(match.Changes);
            Assert.Equal(("B", "0", GameShaderPermutationLookup.ChangeKind.Change), (change.Name, change.Value, change.Kind));
        }

        [Fact]
        public void FewestChangesWinAndTheResultIsDeterministic()
        {
            ShaderToc toc = Toc("A=0B=0C=1D=1", "A=0B=0C=0D=1", "A=1B=0C=0D=0");

            GameShaderPermutationLookup.Match match = GameShaderPermutationLookup.Find(toc, Defines("A=1", "B=1", "C=0", "D=1"));

            Assert.Equal(1, match.Index);
            Assert.Equal(new[] { "A->0", "B->0" }, match.Changes.Select(change => change.ToString()));
        }

        [Fact]
        public void ExactOnlyLookupAndOutOfReachSetsReturnNull()
        {
            ShaderToc toc = Toc("A=0B=0C=0D=0");

            Assert.Null(GameShaderPermutationLookup.Find(toc, Defines("A=1", "B=0", "C=0", "D=0"), fallback: false));
            Assert.Null(GameShaderPermutationLookup.Find(toc, Defines("A=1", "B=1", "C=1", "D=1")));
            Assert.NotNull(GameShaderPermutationLookup.Find(toc, Defines("A=1", "B=1", "C=1", "D=1"), maxChanges: 4));
        }

        private static KeyValuePair<string, string>[] Defines(params string[] pairs) =>
            pairs.Select(pair => pair.Split('=')).Select(parts => new KeyValuePair<string, string>(parts[0], parts[1])).ToArray();

        private static ShaderToc Toc(params string[] keys) =>
            new(
                Switches.SelectMany(name => new[] { new ShaderMacroDefinition(name, "0"), new ShaderMacroDefinition(name, "1") }),
                keys.Select(key => XxHash64Ext.Hash(key)),
                keys.Select((_, index) => (uint)index));
    }
}
