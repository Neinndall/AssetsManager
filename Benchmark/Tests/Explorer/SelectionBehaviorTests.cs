using System;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using AssetsManager.Views.Helpers;
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
            bool isModelSelected = false;

            var thread = new Thread(() =>
            {
                try
                {
                    var state = new SelectionState();
                    var item = new TreeViewItem { DataContext = state };
                    SelectionBehavior.SetSingleClickExpand(item, true);

                    SelectionBehavior.ApplyPrimaryTreeAction(item);

                    isSelected = item.IsSelected;
                    isModelSelected = state.IsSelected;
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
            Assert.True(isModelSelected);
        }

        [Fact]
        public void VirtualizedSelection_IsNotOwnedByContainerBindings()
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string treeStyle = File.ReadAllText(Path.Combine(repositoryRoot, "AssetsManager", "Themes", "TreeViewStyles.xaml"));
            string gridStyle = File.ReadAllText(Path.Combine(repositoryRoot, "AssetsManager", "Themes", "GridView.xaml"));
            string resultsTree = File.ReadAllText(Path.Combine(repositoryRoot, "AssetsManager", "Views", "Dialogs", "Controls", "WadResultsTreeControl.xaml"));

            Assert.DoesNotContain("Property=\"IsSelected\" Value=\"{Binding IsSelected", treeStyle);
            Assert.DoesNotContain("Property=\"IsSelected\" Value=\"{Binding IsSelected", gridStyle);
            Assert.DoesNotContain("Property=\"IsSelected\" Value=\"{Binding IsSelected", resultsTree);
        }

        private sealed class SelectionState : ISelectable
        {
            public bool IsSelected { get; set; }
        }
    }
}
