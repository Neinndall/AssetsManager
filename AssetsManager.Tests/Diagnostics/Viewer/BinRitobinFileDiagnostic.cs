using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>Writes extracted BIN files as Ritobin text with the local hash catalogs.</summary>
    internal static class BinRitobinFileDiagnostic
    {
        internal static async Task Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: bin-ritobin-file <output-dir> <bin-path...>");
                return;
            }

            string output = Directory.CreateDirectory(args[0]).FullName;
            var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            using var resolver = new HashResolverService(new DirectoriesCreator(), log);
            await resolver.LoadAllHashesAsync();
            var serializer = new BinRitobinSerializer(resolver);

            foreach (string bin in args.Skip(1).Where(File.Exists))
            {
                string name = string.Join("_", Path.GetRelativePath(Path.GetPathRoot(bin) ?? string.Empty, bin)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).TakeLast(4));
                string target = Path.Combine(output, name + ".ritobin.txt");
                File.WriteAllText(target, await serializer.WriteBinTreeAsRitobinAsync(File.ReadAllBytes(bin)));
                Console.WriteLine($"[Ritobin] {target}");
            }
        }
    }
}
