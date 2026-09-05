using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class BinDumpDiagnostic
    {
        public static async Task Run(string[] args)
        {
            string defaultWad = @"C:\Riot Games\League of Legends (PBE)\Game\DATA\FINAL\Champions\Tristana.wad.client";
            string wadPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)) ?? defaultWad;
            string filter = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && !a.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)) ?? "skin80";

            if (!File.Exists(wadPath))
            {
                Console.WriteLine($"WAD file not found: {wadPath}");
                return;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine("             BIN TREE OBJECT DUMP & AUDIT         ");
            Console.WriteLine("==================================================");
            Console.WriteLine($"WAD:    {wadPath}");
            Console.WriteLine($"Filter: {filter}");

            var directories = new DirectoriesCreator();
            var serilogLogger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Warning()
                .CreateLogger();
            var logService = new LogService(serilogLogger);
            using var resolver = new HashResolverService(directories, logService);
            Console.WriteLine("Loading hash catalogs (BinaryHashCache)...");
            var sw = Stopwatch.StartNew();
            await resolver.LoadAllHashesAsync();
            Console.WriteLine($"Loaded catalogs in {sw.ElapsedMilliseconds}ms");

            using var wad = new WadFile(wadPath);
            Console.WriteLine($"WAD contains {wad.Chunks.Count} chunks.");

            var binChunks = new List<(ulong Hash, string Path, WadChunk Chunk)>();
            foreach (var (hash, chunk) in wad.Chunks)
            {
                if (chunk.Compression == WadChunkCompression.Satellite) continue;
                string path = resolver.ResolveHash(hash);
                bool isBin = path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

                if (!isBin && path.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    // Inspect header bytes if path is unhashed
                    using var owner = wad.LoadChunkDecompressed(chunk);
                    var seg = owner.DangerousGetArray();
                    if (seg.Count >= 4 && FileTypeDetector.IsPropertyBin(seg.AsSpan()))
                    {
                        isBin = true;
                    }
                }

                if (isBin)
                {
                    if (string.IsNullOrEmpty(filter) || path.Contains(filter, StringComparison.OrdinalIgnoreCase) || hash.ToString("x16").Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        binChunks.Add((hash, path, chunk));
                    }
                }
            }

            Console.WriteLine($"Found {binChunks.Count} matching BIN chunks.");

            var allClassCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var unknownLinks = new List<(string BinPath, string EntryName, string ClassName, string PropName, ulong TargetHash)>();
            var knownLinks = new List<(string BinPath, string EntryName, string ClassName, string PropName, ulong TargetHash, string TargetPath)>();
            var discoveredStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (binHash, binPath, chunk) in binChunks)
            {
                Console.WriteLine("\n--------------------------------------------------------------------------------");
                Console.WriteLine($"BIN: {binPath} (Chunk: {binHash:x16})");
                Console.WriteLine("--------------------------------------------------------------------------------");

                using var owner = wad.LoadChunkDecompressed(chunk);
                var seg = owner.DangerousGetArray();
                if (seg.Count < 4 || !FileTypeDetector.IsPropertyBin(seg.AsSpan()))
                {
                    Console.WriteLine("  Not a valid PropertyBin stream.");
                    continue;
                }

                using var ms = new MemoryStream(seg.Array, seg.Offset, seg.Count, false);
                BinTree tree;
                try
                {
                    tree = new BinTree(ms);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Failed to parse BinTree: {ex.Message}");
                    continue;
                }

                if (tree.Dependencies.Count > 0)
                {
                    Console.WriteLine($"  [Dependencies ({tree.Dependencies.Count})]:");
                    foreach (var dep in tree.Dependencies)
                    {
                        Console.WriteLine($"    -> {dep}");
                    }
                }

                Console.WriteLine($"  [Objects Count: {tree.Objects.Count}]");

                foreach (var (objHash, obj) in tree.Objects)
                {
                    string entryName = resolver.ResolveBinEntry(obj.PathHash);
                    string className = resolver.ResolveBinType(obj.ClassHash);

                    allClassCounts[className] = allClassCounts.GetValueOrDefault(className) + 1;

                    Console.WriteLine($"\n  * Object: [{entryName}] (PathHash: {obj.PathHash:x8}) | Class: [{className}] (ClassHash: {obj.ClassHash:x8})");
                    Console.WriteLine($"    Properties: {obj.Properties.Count}");

                    foreach (var (propHash, prop) in obj.Properties)
                    {
                        string propName = resolver.ResolveBinField(prop.NameHash);
                        DumpProperty(prop, propName, "    ", resolver, binPath, entryName, className, unknownLinks, knownLinks, discoveredStrings);
                    }
                }
            }

            Console.WriteLine("\n==================================================");
            Console.WriteLine("                  TAXONOMY SUMMARY                ");
            Console.WriteLine("==================================================");
            Console.WriteLine("Class Breakdown across scanned BINs:");
            foreach (var (cls, count) in allClassCounts.OrderByDescending(p => p.Value))
            {
                Console.WriteLine($"  {cls,-40} : {count} instances");
            }

            Console.WriteLine("\n==================================================");
            Console.WriteLine($"  CHUNK LINKS AUDIT (Total: {knownLinks.Count + unknownLinks.Count})");
            Console.WriteLine($"  Known:   {knownLinks.Count}");
            Console.WriteLine($"  Unknown: {unknownLinks.Count}");
            Console.WriteLine("==================================================");

            if (unknownLinks.Count > 0)
            {
                Console.WriteLine("\n[UNKNOWN TARGET CHUNKS FOUND IN BIN PROPERTIES]:");
                var grouped = unknownLinks.GroupBy(l => l.TargetHash);
                foreach (var group in grouped)
                {
                    var first = group.First();
                    Console.WriteLine($"  Target: {group.Key:x16} (x{group.Count()} refs)");
                    Console.WriteLine($"    Found in Object Class: [{first.ClassName}] Entry: [{first.EntryName}]");
                    Console.WriteLine($"    Property: [{first.PropName}] in BIN: [{first.BinPath}]");
                }
            }

            var assetStrings = discoveredStrings.Where(s =>
                s.Contains('/', StringComparison.Ordinal) ||
                s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".skn", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".skl", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".wpk", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".preload", StringComparison.OrdinalIgnoreCase)
            ).OrderBy(s => s).ToList();

            Console.WriteLine($"\nDiscovered {assetStrings.Count} asset-like strings in BIN objects.");
            Console.WriteLine("Sample asset strings (up to 30):");
            foreach (var s in assetStrings.Take(30))
            {
                Console.WriteLine($"  - {s}");
            }
        }

        private static void DumpProperty(
            BinTreeProperty prop,
            string propName,
            string indent,
            HashResolverService resolver,
            string binPath,
            string entryName,
            string className,
            List<(string, string, string, string, ulong)> unknownLinks,
            List<(string, string, string, string, ulong, string)> knownLinks,
            HashSet<string> discoveredStrings)
        {
            if (prop == null) return;

            switch (prop)
            {
                case BinTreeWadChunkLink link:
                    ulong target = link.Value;
                    string resolved = resolver.ResolveHash(target);
                    bool isKnown = resolver.IsKnownHash(target);
                    if (isKnown)
                    {
                        knownLinks.Add((binPath, entryName, className, propName, target, resolved));
                        Console.WriteLine($"{indent}  • {propName} (WadChunkLink): {target:x16} -> {resolved}");
                    }
                    else
                    {
                        unknownLinks.Add((binPath, entryName, className, propName, target));
                        Console.WriteLine($"{indent}  • {propName} (WadChunkLink): {target:x16} -> ** UNKNOWN TARGET **");
                    }
                    break;

                case BinTreeString str:
                    discoveredStrings.Add(str.Value);
                    Console.WriteLine($"{indent}  • {propName} (string): \"{str.Value}\"");
                    break;

                case BinTreeObjectLink objLink:
                    string targetEntry = resolver.ResolveBinEntry(objLink.Value);
                    Console.WriteLine($"{indent}  • {propName} (ObjectLink): {objLink.Value:x8} -> \"{targetEntry}\"");
                    break;

                case BinTreeHash h:
                    string genHash = resolver.ResolveBinHashGeneral(h.Value);
                    Console.WriteLine($"{indent}  • {propName} (hash): {h.Value:x8} -> \"{genHash}\"");
                    break;

                case BinTreeStruct st:
                    string stClass = resolver.ResolveBinType(st.ClassHash);
                    Console.WriteLine($"{indent}  • {propName} (struct {stClass} [{st.ClassHash:x8}], {st.Properties.Count} fields):");
                    foreach (var (childHash, childProp) in st.Properties)
                    {
                        string childName = resolver.ResolveBinField(childHash);
                        DumpProperty(childProp, childName, indent + "    ", resolver, binPath, entryName, className, unknownLinks, knownLinks, discoveredStrings);
                    }
                    break;

                case BinTreeContainer cnt:
                    Console.WriteLine($"{indent}  • {propName} (container, {cnt.Elements.Count} items, type: {cnt.ElementType}):");
                    int elIdx = 0;
                    foreach (var el in cnt.Elements)
                    {
                        if (elIdx++ < 10)
                        {
                            DumpProperty(el, $"[{elIdx - 1}]", indent + "    ", resolver, binPath, entryName, className, unknownLinks, knownLinks, discoveredStrings);
                        }
                        else
                        {
                            Console.WriteLine($"{indent}      ... ({cnt.Elements.Count - 10} more items)");
                            break;
                        }
                    }
                    break;

                case BinTreeMap map:
                    Console.WriteLine($"{indent}  • {propName} (map, {map.Count} pairs):");
                    int mIdx = 0;
                    foreach (var pair in map)
                    {
                        if (mIdx++ < 5)
                        {
                            DumpProperty(pair.Key, $"Key[{mIdx - 1}]", indent + "    ", resolver, binPath, entryName, className, unknownLinks, knownLinks, discoveredStrings);
                            DumpProperty(pair.Value, $"Val[{mIdx - 1}]", indent + "    ", resolver, binPath, entryName, className, unknownLinks, knownLinks, discoveredStrings);
                        }
                        else
                        {
                            Console.WriteLine($"{indent}      ... ({map.Count - 5} more pairs)");
                            break;
                        }
                    }
                    break;

                case BinTreeOptional opt:
                    if (opt.Value != null)
                    {
                        Console.WriteLine($"{indent}  • {propName} (optional hasValue):");
                        DumpProperty(opt.Value, "Value", indent + "    ", resolver, binPath, entryName, className, unknownLinks, knownLinks, discoveredStrings);
                    }
                    else
                    {
                        Console.WriteLine($"{indent}  • {propName} (optional null)");
                    }
                    break;

                case BinTreeBool b:
                    Console.WriteLine($"{indent}  • {propName} (bool): {b.Value}");
                    break;

                case BinTreeI8 s8:
                    Console.WriteLine($"{indent}  • {propName} (i8): {s8.Value}");
                    break;
                case BinTreeU8 u8:
                    Console.WriteLine($"{indent}  • {propName} (u8): {u8.Value}");
                    break;
                case BinTreeI16 s16:
                    Console.WriteLine($"{indent}  • {propName} (i16): {s16.Value}");
                    break;
                case BinTreeU16 u16:
                    Console.WriteLine($"{indent}  • {propName} (u16): {u16.Value}");
                    break;
                case BinTreeI32 s32:
                    Console.WriteLine($"{indent}  • {propName} (i32): {s32.Value}");
                    break;
                case BinTreeU32 u32:
                    Console.WriteLine($"{indent}  • {propName} (u32): {u32.Value}");
                    break;
                case BinTreeI64 s64:
                    Console.WriteLine($"{indent}  • {propName} (i64): {s64.Value}");
                    break;
                case BinTreeU64 u64:
                    Console.WriteLine($"{indent}  • {propName} (u64): {u64.Value}");
                    break;
                case BinTreeF32 f:
                    Console.WriteLine($"{indent}  • {propName} (f32): {f.Value}");
                    break;
                case BinTreeVector2 v2:
                    Console.WriteLine($"{indent}  • {propName} (vec2): {v2.Value}");
                    break;
                case BinTreeVector3 v3:
                    Console.WriteLine($"{indent}  • {propName} (vec3): {v3.Value}");
                    break;
                case BinTreeVector4 v4:
                    Console.WriteLine($"{indent}  • {propName} (vec4): {v4.Value}");
                    break;
                case BinTreeMatrix44 m4:
                    Console.WriteLine($"{indent}  • {propName} (mat4)");
                    break;
                case BinTreeColor c:
                    Console.WriteLine($"{indent}  • {propName} (color): {c.Value}");
                    break;

                default:
                    Console.WriteLine($"{indent}  • {propName} ({prop.GetType().Name})");
                    break;
            }
        }
    }
}
