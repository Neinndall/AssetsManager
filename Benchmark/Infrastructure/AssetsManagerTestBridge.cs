using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using LeagueToolkit.Core.Wad;
using Serilog;

namespace AssetsManager.BenchmarkTests.Infrastructure
{
    internal sealed class AssetsManagerTestBridge : IDisposable
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), $"AssetsManager_Tests_{Guid.NewGuid():N}");
        public LogService LogService { get; } = new(Log.Logger);
        public DirectoriesCreator Directories { get; }

        public AssetsManagerTestBridge()
        {
            Directory.CreateDirectory(RootPath);
            Directories = new DirectoriesCreator(RootPath);
        }

        public string CreateDirectory(string name)
        {
            string path = Path.Combine(RootPath, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string BakeWad(string directory, string fileName, params (string Path, string Content)[] entries)
        {
            string wadPath = Path.Combine(directory, fileName);
            var bakeEntries = new List<WadBakeEntry>(entries.Length);
            foreach (var entry in entries)
            {
                byte[] data = Encoding.UTF8.GetBytes(entry.Content);
                bakeEntries.Add(new WadBakeEntry(entry.Path, () => new MemoryStream(data), WadChunkCompression.None));
            }

            WadBuilder.Bake(bakeEntries, wadPath, new WadBakeSettings());
            return wadPath;
        }

        public WadComparatorService CreateComparator() =>
            new(new HashResolverService(Directories, LogService), LogService);

        public WadPackagingService CreatePackager() =>
            new(LogService, Directories, null);

        public void Dispose()
        {
            if (Directory.Exists(RootPath)) Directory.Delete(RootPath, true);
        }
    }
}
