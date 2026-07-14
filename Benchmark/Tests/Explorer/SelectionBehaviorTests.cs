using System;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using AssetsManager.Views.Dialogs.Controls;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Wad;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Explorer
{
    public class SelectionBehaviorTests
    {
        [Fact]
        public void PrimaryAction_SelectsLeafTreeItem()
        {
            Exception failure = null;
            bool isSelected = false;

            var thread = new Thread(() =>
            {
                try
                {
                    var item = new TreeViewItem();

                    SelectionBehavior.ApplyPrimaryTreeAction(item);

                    isSelected = item.IsSelected;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            Assert.Null(failure);
            Assert.True(isSelected);
        }

        [Fact]
        public void TreeSelection_IsNotOwnedByRecycledContainerBinding()
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string stylePath = Path.Combine(repositoryRoot, "AssetsManager", "Themes", "TreeViewStyles.xaml");
            string xaml = File.ReadAllText(stylePath);

            Assert.DoesNotContain("Property=\"IsSelected\" Value=\"{Binding IsSelected", xaml);
        }

        [Fact]
        public void ComparisonTreeSelection_UpdatesActiveDiffState()
        {
            var previous = new SerializableChunkDiff { IsSelected = true };
            var current = new SerializableChunkDiff();

            WadResultsTreeControl.SetItemSelection(previous, false);
            WadResultsTreeControl.SetItemSelection(current, true);

            Assert.False(previous.IsSelected);
            Assert.True(current.IsSelected);
        }
    }
}
