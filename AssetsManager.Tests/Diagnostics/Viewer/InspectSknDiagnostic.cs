using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Parsing;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class InspectSknDiagnostic
    {
        public static void Run(string sknPath)
        {
            if (string.IsNullOrWhiteSpace(sknPath))
            {
                Console.WriteLine(
                    "Usage: dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- " +
                    "inspect-skn <path-to-skn>");
                return;
            }

            sknPath = Path.GetFullPath(sknPath);
            if (Directory.Exists(sknPath))
            {
                AuditDirectory(sknPath);
                return;
            }
            if (!File.Exists(sknPath))
            {
                Console.WriteLine($"[InspectSkn] File not found: {sknPath}");
                return;
            }

            Console.WriteLine($"[InspectSkn] Inspecting: {sknPath}");
            using var skinnedMesh = SkinnedMesh.ReadFromSimpleSkin(sknPath);

            Console.WriteLine($"SKN Submesh Count (Ranges): {skinnedMesh.Ranges.Count}");

            foreach (var rangeObj in skinnedMesh.Ranges)
            {
                string materialName = rangeObj.Material.TrimEnd('\0');
                var subIndices = skinnedMesh.Indices.Slice(rangeObj.StartIndex, rangeObj.IndexCount);

                int minIndex = int.MaxValue;
                int maxIndex = int.MinValue;
                for (int i = 0; i < rangeObj.IndexCount; i++)
                {
                    int idx = (int)subIndices[i];
                    if (idx < minIndex) minIndex = idx;
                    if (idx > maxIndex) maxIndex = idx;
                }

                bool usesGlobalIndices = true;
                bool usesLocalIndices = rangeObj.StartVertex > 0;
                for (int i = 0; i < rangeObj.IndexCount; i++)
                {
                    int index = (int)subIndices[i];
                    usesGlobalIndices &= index >= rangeObj.StartVertex &&
                                         index < rangeObj.StartVertex + rangeObj.VertexCount;
                    usesLocalIndices &= index >= 0 && index < rangeObj.VertexCount;
                }

                Console.WriteLine($"\n--- Submesh: '{materialName}' ---");
                Console.WriteLine($"    StartVertex: {rangeObj.StartVertex}, VertexCount: {rangeObj.VertexCount}");
                Console.WriteLine($"    StartIndex:  {rangeObj.StartIndex}, IndexCount:  {rangeObj.IndexCount}");
                Console.WriteLine($"    Indices Min Value: {minIndex}, Max Value: {maxIndex}");
                Console.WriteLine($"    Sample First 6 Indices: [{string.Join(", ", subIndices.Take(6))}]");
                Console.WriteLine($"    usesGlobalIndices = {usesGlobalIndices}");
                Console.WriteLine($"    usesLocalIndices  = {usesLocalIndices}");
                Console.WriteLine($"    RESULT: {(usesLocalIndices && !usesGlobalIndices ? "USES RELATIVE/LOCAL INDICES (Requires +StartVertex offset)" : "USES GLOBAL INDICES")}");
            }
        }

        private static void AuditDirectory(string root)
        {
            string[] files = Directory.GetFiles(root, "*.skn", SearchOption.AllDirectories);
            int passed = 0;
            int failed = 0;
            int normalized = 0;
            int directBlend = 0;
            var decoder = new MapCharacterMeshDecoder();

            foreach (string path in files)
            {
                try
                {
                    using var probe = SkinnedMesh.ReadFromSimpleSkin(path);
                    if ((probe.Flags & SkinnedMeshIndexSemantics.NormalizedIndicesFlag) != 0)
                        normalized++;
                    if ((probe.Flags & SkinnedMeshIndexSemantics.DirectBlendIndicesFlag) != 0)
                        directBlend++;

                    using Stream stream = File.OpenRead(path);
                    decoder.Decode(stream);
                    passed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"[InspectSkn] FAIL {path}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Console.WriteLine(
                $"[InspectSkn] Directory audit: total={files.Length}, passed={passed}, failed={failed}, normalized={normalized}, classic={files.Length - normalized}, directBlend={directBlend}.");
        }
    }
}
