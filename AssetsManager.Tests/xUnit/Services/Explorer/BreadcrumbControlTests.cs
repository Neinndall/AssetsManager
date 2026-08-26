using System;
using System.IO;
using System.Linq;
using AssetsManager.Views.Controls.Explorer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public class BreadcrumbControlTests
    {
        [Fact]
        public void ShortPathPreservesEveryItem()
        {
            string[] path = { "root", "one", "two", "target" };

            var items = BreadcrumbControl.BuildItems(path, value => value);

            Assert.Equal(path, items.Select(item => item.DisplayName));
            Assert.All(items, item => Assert.True(item.IsEnabled));
        }

        [Fact]
        public void LongPathKeepsEdgesAndCollapsesMiddle()
        {
            string[] path = { "root", "one", "two", "three", "four", "target" };

            var items = BreadcrumbControl.BuildItems(path, value => value);

            Assert.Equal(
                new[] { "root", "one", "...", "four", "target" },
                items.Select(item => item.DisplayName));
            Assert.False(items[2].IsEnabled);
            Assert.Null(items[2].Value);
        }

        [Fact]
        public void TemplateUsesGenericBreadcrumbItems()
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string xaml = File.ReadAllText(Path.Combine(
                repositoryRoot,
                "AssetsManager",
                "Views",
                "Controls",
                "Explorer",
                "BreadcrumbControl.xaml"));

            Assert.Contains("ItemsSource=\"{Binding Items}\"", xaml);
            Assert.Contains("Content=\"{Binding DisplayName}\"", xaml);
            Assert.Contains("FontSize=\"12\"", xaml);
            Assert.DoesNotContain("Binding Nodes", xaml);
        }

        [Fact]
        public void ViewerUsesCompactBreadcrumbText()
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string xaml = File.ReadAllText(Path.Combine(
                repositoryRoot,
                "AssetsManager",
                "Views",
                "Controls",
                "Viewer",
                "ViewerProjectExplorerControl.xaml"));

            Assert.Matches(
                @"<explorer:BreadcrumbControl[^>]*FontSize=""9\.5""",
                xaml.ReplaceLineEndings(" "));
        }
    }
}
