using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Models
{
    public sealed class ChromaSelectionModelTests
    {
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
