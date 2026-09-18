using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Models
{
    public sealed class ChromaSelectionModelTests
    {
        [Fact]
        public void ReferenceSourceUpdatesSourceStateAndStatus()
        {
            var family = new ChromaFamilyModel { Name = "SKIN01" };
            family.Chromas.Add(new ChromaSkinModel { Name = "SKIN02" });

            var model = new ChromaSelectionModel();
            model.SetScanningState("skins", @"C:\current\skins");
            model.SetFamilies(new[] { family });
            model.SetReferenceSource(@"C:\old\skins");
            model.SetSuccessState();

            Assert.True(model.HasReference);
            Assert.Equal(2, model.SourceCount);
            Assert.Equal("2 sources · 1 skin family · 1 chroma detected", model.StatusText);

            model.ClearReferenceSource();

            Assert.False(model.HasReference);
            Assert.Equal(1, model.SourceCount);
        }

        [Fact]
        public void ChromaSourceKindMarksReferenceEntries()
        {
            var chroma = new ChromaSkinModel
            {
                SourceKind = ChromaSourceKind.Reference,
                SourceRoot = @"C:\old\skins"
            };

            Assert.True(chroma.IsReference);
            Assert.Equal("REFERENCE", chroma.SourceLabel);
            Assert.Equal(@"C:\old\skins", chroma.SourceRoot);
        }
        [Fact]
        public void SelectionMetricsFollowChromasAcrossFamilies()
        {
            var firstChroma = new ChromaSkinModel { Name = "SKIN02" };
            var secondChroma = new ChromaSkinModel { Name = "SKIN03" };
            var firstFamily = new ChromaFamilyModel { Name = "SKIN01" };
            var secondFamily = new ChromaFamilyModel { Name = "SKIN02" };
            firstFamily.Chromas.Add(firstChroma);
            secondFamily.Chromas.Add(secondChroma);

            var model = new ChromaSelectionModel();
            model.SetFamilies(new[] { firstFamily, secondFamily });

            firstChroma.IsSelected = true;
            model.SelectedFamily = secondFamily;
            secondChroma.IsSelected = true;

            Assert.True(firstChroma.IsSelected);
            Assert.Equal(2, model.SelectedCount);
            Assert.True(model.HasSelection);
            Assert.Equal("2 CHROMAS SELECTED", model.SelectionText);
        }
    }
}
