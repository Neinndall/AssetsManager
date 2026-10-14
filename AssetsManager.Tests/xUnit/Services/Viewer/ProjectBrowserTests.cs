using System;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer
{
    public sealed class ProjectBrowserTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AssetsManager-StudioFiles-" + Guid.NewGuid().ToString("N"));
        public ProjectBrowserTests() => Directory.CreateDirectory(_root);

        [Fact]
        public void BrowseIncludesAllFormatsAndOnlyImmediateChildren()
        {
            Directory.CreateDirectory(Path.Combine(_root, "textures"));
            File.WriteAllText(Path.Combine(_root, "skin.bin"), "");
            File.WriteAllText(Path.Combine(_root, "readme.txt"), "");
            File.WriteAllText(Path.Combine(_root, "textures", "albedo.dds"), "");
            var result = ProjectBrowserService.Read(_root, _root, "", CancellationToken.None);
            Assert.Equal(new[] { "textures", "readme.txt", "skin.bin" }, result.Files.Select(file => file.Name));
            Assert.True(result.Files[0].IsDirectory);
            Assert.False(result.IsTruncated);
        }

        [Fact]
        public void SearchFindsNestedResourcesByRelativePath()
        {
            string folder = Path.Combine(_root, "assets", "champion");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Albedo.dds"), "");
            var result = ProjectBrowserService.Read(_root, _root, "CHAMPION", CancellationToken.None);
            Assert.Contains(result.Files, file => file.Name == "Albedo.dds");
        }

        [Fact]
        public void BrowseRejectsSiblingFoldersAndHonorsCancellation()
        {
            Assert.False(ProjectBrowserService.IsWithinRoot(_root, _root + "-other"));
            Assert.Throws<ArgumentException>(() =>
                ProjectBrowserService.Read(_root, _root + "-other", "", CancellationToken.None));
            Assert.True(ProjectBrowserService.IsWithinRoot(_root, Path.Combine(_root, "textures")));
            Assert.Throws<OperationCanceledException>(() =>
                ProjectBrowserService.Read(_root, _root, "", new CancellationToken(true)));
        }

        [Fact]
        public void SearchReportsTruncationInsteadOfSilentlyDroppingMatches()
        {
            for (int i = 0; i <= ProjectBrowserService.SearchLimit; i++)
                File.WriteAllText(Path.Combine(_root, $"match{i}.txt"), "");
            var result = ProjectBrowserService.Read(_root, _root, "match", CancellationToken.None);
            Assert.Equal(ProjectBrowserService.SearchLimit, result.Files.Count);
            Assert.True(result.IsTruncated);
        }

        [Fact]
        public void FolderTreeReadsOnlySortedImmediateDirectories()
        {
            Directory.CreateDirectory(Path.Combine(_root, "zebra", "nested"));
            Directory.CreateDirectory(Path.Combine(_root, "assets"));
            File.WriteAllText(Path.Combine(_root, "skin.bin"), "");
            var folders = ProjectBrowserService.ReadFolders(_root, _root, CancellationToken.None);
            Assert.Equal(new[] { "assets", "zebra" }, folders.Select(Path.GetFileName));
            Assert.Throws<ArgumentException>(() => ProjectBrowserService.ReadFolders(_root, _root + "-other", CancellationToken.None));
            Assert.Throws<OperationCanceledException>(() => ProjectBrowserService.ReadFolders(_root, _root, new CancellationToken(true)));
        }

        [Fact]
        public void RecentProjectsMoveToFrontWithoutDuplicatesAndKeepSixEntries()
        {
            string[] paths = Enumerable.Range(0, 8).Select(i => Path.Combine(_root, $"project{i}")).ToArray();
            var recent = StudioHomeModel.RememberProject(paths, paths[3].ToUpperInvariant());
            Assert.Equal(6, recent.Count);
            Assert.Equal(paths[3].ToUpperInvariant(), recent[0]);
            Assert.Equal(1, recent.Count(path => path.Equals(paths[3], StringComparison.OrdinalIgnoreCase)));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
