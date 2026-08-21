using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class SubmeshTextureAuditDiagnostic
    {
        public static void Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: submesh-texture-audit <skin.bin> [model.skn]");
                return;
            }

            string binPath = Path.GetFullPath(args[0]);
            string sknPath = args.Length > 1 ? Path.GetFullPath(args[1]) : null;

            if (!File.Exists(binPath))
            {
                Console.WriteLine($"Error: BIN file not found: {binPath}");
                return;
            }

            Console.WriteLine($"================================================================================");
            Console.WriteLine($"SUBMESH & TEXTURE AUDIT DIAGNOSTIC");
            Console.WriteLine($"BIN: {binPath}");
            if (sknPath != null)
            {
                Console.WriteLine($"SKN: {sknPath}");
            }
            Console.WriteLine($"================================================================================");

            using var binStream = File.OpenRead(binPath);
            var binTree = new BinTree(binStream);

            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(binTree);

            Console.WriteLine($"\n--- METADATA READ FROM BIN ---");
            Console.WriteLine($"Default Texture Path: {metadata.DefaultTexturePath ?? "<none>"}");
            Console.WriteLine($"Default Material: {(metadata.DefaultMaterial != null ? $"{metadata.DefaultMaterial.Samplers.Count} samplers" : "<none>")}");
            Console.WriteLine($"Override Textures ({metadata.OverrideTexturePaths.Count} submeshes):");
            foreach ((string submesh, IReadOnlyList<string> paths) in metadata.OverrideTexturePaths)
            {
                Console.WriteLine($"  [{submesh}] -> {string.Join(", ", paths)}");
            }

            Console.WriteLine($"Override Materials ({metadata.OverrideMaterials.Count} submeshes):");
            foreach ((string submesh, SknMaterialDefinition mat) in metadata.OverrideMaterials)
            {
                Console.WriteLine($"  [{submesh}] -> {mat.Samplers.Count} samplers, {mat.Parameters.Count} params");
                foreach (var sampler in mat.Samplers)
                {
                    Console.WriteLine($"    Sampler: {sampler.TextureName} = {sampler.TexturePath}");
                }
            }

            string binDirectory = Path.GetDirectoryName(binPath);
            var availableTextureFiles = Directory.EnumerateFiles(binDirectory, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant(), p => p, StringComparer.OrdinalIgnoreCase);

            Console.WriteLine($"\nDiscovered {availableTextureFiles.Count} texture files in project directory.");

            SknMaterialTextureResolution resolution = SknMaterialTextureResolver.Resolve(metadata, availableTextureFiles.Keys);

            Console.WriteLine($"\n--- RESOLUTION RESULTS ---");
            Console.WriteLine($"Default Texture Key: {resolution.DefaultTextureKey ?? "<none>"}");
            Console.WriteLine($"Default Effect: {resolution.DefaultEffect.Kind}");
            Console.WriteLine($"Resolved Texture Overrides:");
            foreach ((string submesh, string key) in resolution.Overrides)
            {
                Console.WriteLine($"  [{submesh}] -> {key}");
            }
            Console.WriteLine($"Resolved Effects:");
            foreach ((string submesh, ModelMaterialEffectDefinition effect) in resolution.Effects)
            {
                Console.WriteLine($"  [{submesh}] -> Kind={effect.Kind} Texture={effect.TextureName ?? "<none>"} Mask={effect.MaskTextureName ?? "<none>"} Emission={effect.EmissionTextureName ?? "<none>"}");
            }

            if (sknPath != null && File.Exists(sknPath))
            {
                Console.WriteLine($"\n--- AUDITING SKN SUBMESHES ---");
                var skn = SkinnedMesh.ReadFromSimpleSkin(sknPath);

                foreach (var range in skn.Ranges)
                {
                    string submeshName = range.Material.TrimEnd('\0');
                    string normalizedKey = SknMaterialTextureResolver.NormalizeMaterialKey(submeshName);

                    string resolvedTextureKey = resolution.Overrides.TryGetValue(normalizedKey, out string overrideKey)
                        ? overrideKey
                        : resolution.DefaultTextureKey;

                    ModelMaterialEffectDefinition effect = resolution.ResolveEffect(normalizedKey);

                    Console.WriteLine($"\nSubmesh: '{submeshName}' (normalized: '{normalizedKey}')");
                    Console.WriteLine($"  IndexCount={range.IndexCount} StartIndex={range.StartIndex} StartVertex={range.StartVertex} VertexCount={range.VertexCount}");
                    Console.WriteLine($"  Assigned Texture Key: {resolvedTextureKey ?? "<none>"}");
                    Console.WriteLine($"  Assigned Effect Kind: {effect.Kind}");

                    if (resolvedTextureKey != null && availableTextureFiles.TryGetValue(resolvedTextureKey, out string fullTexturePath))
                    {
                        AuditTexturePixels(fullTexturePath);
                    }
                    else
                    {
                        Console.WriteLine($"  WARNING: Resolved texture key '{resolvedTextureKey}' not found on disk!");
                    }
                }
            }

            Console.WriteLine($"\n================================================================================");
            Console.WriteLine($"AUDIT COMPLETE");
            Console.WriteLine($"================================================================================");
        }

        private static void AuditTexturePixels(string texturePath)
        {
            try
            {
                BitmapSource source = TextureUtils.LoadTextureFromFile(texturePath);
                if (source == null)
                {
                    Console.WriteLine($"  Texture File: {Path.GetFileName(texturePath)} -> UNREADABLE");
                    return;
                }

                var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int width = bitmap.PixelWidth;
                int height = bitmap.PixelHeight;
                int stride = width * 4;
                byte[] pixels = new byte[height * stride];
                bitmap.CopyPixels(pixels, stride, 0);

                int transparentPixels = 0;
                int fullyOpaquePixels = 0;
                int semiTransparentPixels = 0;

                for (int i = 3; i < pixels.Length; i += 4)
                {
                    byte alpha = pixels[i];
                    if (alpha == 0) transparentPixels++;
                    else if (alpha == 255) fullyOpaquePixels++;
                    else semiTransparentPixels++;
                }

                int totalPixels = width * height;
                Console.WriteLine($"  Texture File: {Path.GetFileName(texturePath)} ({width}x{height}, {source.Format})");
                Console.WriteLine($"    Alpha distribution: Opaque={fullyOpaquePixels} ({fullyOpaquePixels * 100.0 / totalPixels:F1}%), Transparent={transparentPixels} ({transparentPixels * 100.0 / totalPixels:F1}%), SemiTransparent={semiTransparentPixels} ({semiTransparentPixels * 100.0 / totalPixels:F1}%)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Texture File: {Path.GetFileName(texturePath)} -> ERROR: {ex.Message}");
            }
        }
    }
}
