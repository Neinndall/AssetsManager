using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Loading
{
    internal sealed record ProjectBrowserResult(IReadOnlyList<ProjectBrowserFile> Files, bool IsTruncated);

    internal static class ProjectBrowserService
    {
        internal const int SearchLimit = 1000;

        internal static IReadOnlyList<string> ReadFolders(string root, string folder, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsWithinRoot(root, folder)) throw new ArgumentException("The folder must belong to the project.", nameof(folder));
            var paths = new List<string>();
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (DirectoryInfo directory in new DirectoryInfo(folder).EnumerateDirectories("*", options))
            {
                token.ThrowIfCancellationRequested();
                paths.Add(directory.FullName);
            }
            token.ThrowIfCancellationRequested();
            return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static bool IsWithinRoot(string root, string path)
        {
            string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return canonicalPath.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
                canonicalPath.StartsWith(Path.EndsInDirectorySeparator(canonicalRoot) ? canonicalRoot :
                    canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static ProjectBrowserResult Read(string root, string folder, string query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsWithinRoot(root, folder)) throw new ArgumentException("The folder must belong to the project.", nameof(folder));
            bool searching = !string.IsNullOrWhiteSpace(query);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = searching,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };
            var files = new List<ProjectBrowserFile>();
            bool truncated = false;
            foreach (FileSystemInfo item in new DirectoryInfo(searching ? root : folder).EnumerateFileSystemInfos("*", options))
            {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(root, item.FullName);
                if (searching && !relative.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (searching && files.Count == SearchLimit) { truncated = true; break; }
                files.Add(new ProjectBrowserFile(item.FullName, relative, item is DirectoryInfo));
            }
            token.ThrowIfCancellationRequested();
            return new(files.OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), truncated);
        }
    }
}
