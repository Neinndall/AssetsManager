using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Renderer;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Loading
{
    public class ChromaDebugInvestigationTests
    {
        private readonly ITestOutputHelper _output;

        public ChromaDebugInvestigationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void InvestigateAatroxSkin04()
        {
            string sknPath = @"C:\Users\danielpriego\Desktop\Aatrox.wad.client\assets\characters\aatrox\skins\skin03\aatrox_skin03.skn";
            string skin04Dir = @"C:\Users\danielpriego\Desktop\Aatrox.wad.client\assets\characters\aatrox\skins\skin04";
            string skin4Bin = @"C:\Users\danielpriego\Desktop\Aatrox.wad.client\data\characters\aatrox\skins\skin4.bin";

            if (!File.Exists(sknPath) || !File.Exists(skin4Bin))
            {
                _output.WriteLine("Files do not exist, skipping.");
                return;
            }

            // 1. Check SKN submeshes
            using var skn = SkinnedMesh.ReadFromSimpleSkin(sknPath);
            _output.WriteLine($"SKN Ranges count: {skn.Ranges.Count}");
            foreach (var r in skn.Ranges)
            {
                _output.WriteLine($"  Range Material: '{r.Material}' (Trimmed: '{r.Material.TrimEnd('\0')}')");
            }

            string skin3Bin = @"C:\Users\danielpriego\Desktop\Aatrox.wad.client\data\characters\aatrox\skins\skin3.bin";
            using var stream3 = File.OpenRead(skin3Bin);
            var binTree3 = new BinTree(stream3);
            var metadata3 = SknMaterialTextureResolver.ReadMetadata(new[] { binTree3 }, targetSknPath: sknPath);
            _output.WriteLine($"skin3.bin DefaultTexturePath: {metadata3.DefaultTexturePath}");
            _output.WriteLine($"skin3.bin OverrideMaterials: {string.Join(", ", metadata3.OverrideMaterials.Keys)}");
            _output.WriteLine($"skin3.bin DirectOverrideTexturePaths: {string.Join(", ", metadata3.DirectOverrideTexturePaths.Keys)}");

            using var stream = File.OpenRead(skin4Bin);
            var binTree = new BinTree(stream);

            // 3. Try Resolve Bin Path
            string resolvedBin = SknMaterialTextureResolver.TryResolveBinPath(skin04Dir);
            _output.WriteLine($"Resolved BIN from skin04Dir: {resolvedBin}");

            // 4. Read Metadata
            var metadata = SknMaterialTextureResolver.ReadMetadata(new[] { binTree }, targetSknPath: sknPath);
            _output.WriteLine($"Metadata DefaultTexturePath: {metadata.DefaultTexturePath}");
            _output.WriteLine($"Metadata HasDefaultMaterialLink: {metadata.HasDefaultMaterialLink}");
            _output.WriteLine($"Metadata OverrideMaterials count: {metadata.OverrideMaterials.Count}");
            foreach (var kvp in metadata.OverrideMaterials)
            {
                _output.WriteLine($"  Override Material: {kvp.Key}");
            }
            _output.WriteLine($"Metadata DirectOverrideTexturePaths count: {metadata.DirectOverrideTexturePaths.Count}");
            foreach (var kvp in metadata.DirectOverrideTexturePaths)
            {
                _output.WriteLine($"  Direct Override Texture: {kvp.Key} => {kvp.Value}");
            }

            // 5. Check textures in skin04
            var textureFiles = Directory.GetFiles(skin04Dir, "*.tex");
            var texKeys = textureFiles.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
            _output.WriteLine($"Textures in skin04: {string.Join(", ", texKeys)}");

            // 6. Inspect DefaultMaterial and OverrideMaterials in detail
            _output.WriteLine($"DefaultMaterial is null: {metadata.DefaultMaterial == null}");
            if (metadata.DefaultMaterial != null)
            {
                _output.WriteLine($"DefaultMaterial ShaderHash: {metadata.DefaultMaterial.ShaderHash:x8}");
                _output.WriteLine($"DefaultMaterial Samplers count: {metadata.DefaultMaterial.Samplers.Count}");
                foreach (var s in metadata.DefaultMaterial.Samplers)
                {
                    _output.WriteLine($"  Sampler: Name='{s.TextureName}', Path='{s.TexturePath}'");
                }
            }
            foreach (var kvp in metadata.OverrideMaterials)
            {
                _output.WriteLine($"OverrideMaterial '{kvp.Key}':");
                _output.WriteLine($"  ShaderHash: {kvp.Value.ShaderHash:x8}");
                _output.WriteLine($"  Samplers count: {kvp.Value.Samplers.Count}");
                foreach (var s in kvp.Value.Samplers)
                {
                    _output.WriteLine($"    Sampler: Name='{s.TextureName}', Path='{s.TexturePath}'");
                }
            }

            // 8. Check SKN ranges and what material definition each gets
            var resolution = SknMaterialTextureResolver.Resolve(metadata, texKeys);
            _output.WriteLine($"Default BaseTextureName: {resolution.DefaultMaterialDefinition?.BaseTextureName}");
            foreach (var r in skn.Ranges)
            {
                string matName = r.Material.TrimEnd('\0');
                var def = resolution.ResolveMaterialDefinition(matName);
                _output.WriteLine($"Range '{matName}' => MatDef BaseTextureName: '{def?.BaseTextureName}'");
            }
        }
    }
}
