using AssetsManager.Views.Models.Explorer;
using Material.Icons;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public sealed class ExplorerClientBadgeTests
    {
        [Fact]
        public void SwitchingBetweenClientModesUpdatesBadgeIconEveryTime()
        {
            var toolbar = new ExplorerToolbarModel();

            toolbar.SetClientBadge("PBE", null);
            Assert.Equal("PBE", toolbar.CurrentClientName);
            Assert.Equal(MaterialIconKind.FlaskOutline, toolbar.CurrentClientIcon);

            toolbar.SetClientBadge("LOCAL", null);
            Assert.Equal("LOCAL", toolbar.CurrentClientName);
            Assert.Equal(MaterialIconKind.FolderOpenOutline, toolbar.CurrentClientIcon);

            toolbar.SetClientBadge("LIVE", null);
            Assert.Equal("LIVE", toolbar.CurrentClientName);
            Assert.Equal(MaterialIconKind.SealVariant, toolbar.CurrentClientIcon);

            toolbar.SetClientBadge("RESULT", null);
            Assert.Equal("RESULT", toolbar.CurrentClientName);
            Assert.Equal(MaterialIconKind.FileCompare, toolbar.CurrentClientIcon);
        }
    }
}
