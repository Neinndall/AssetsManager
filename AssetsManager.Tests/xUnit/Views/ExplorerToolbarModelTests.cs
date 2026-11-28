using System.Collections.Generic;
using AssetsManager.Views.Models.Explorer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views
{
    public sealed class ExplorerToolbarModelTests
    {
        [Fact]
        public void IsFavoritesToggleVisibleTracksWadAndBackupModeWithNotifications()
        {
            var model = new ExplorerToolbarModel();
            var notified = new List<string>();
            model.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            Assert.True(model.IsFavoritesToggleVisible);

            model.IsWadMode = false;
            Assert.False(model.IsFavoritesToggleVisible);
            Assert.Contains(nameof(ExplorerToolbarModel.IsFavoritesToggleVisible), notified);

            notified.Clear();
            model.IsWadMode = true;
            Assert.True(model.IsFavoritesToggleVisible);
            Assert.Contains(nameof(ExplorerToolbarModel.IsFavoritesToggleVisible), notified);

            notified.Clear();
            model.IsBackupMode = true;
            Assert.False(model.IsFavoritesToggleVisible);
            Assert.Contains(nameof(ExplorerToolbarModel.IsFavoritesToggleVisible), notified);
        }
    }
}
