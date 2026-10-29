using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Loading;

public sealed class CharacterOutlineDocumentLoaderTests
{
    private static uint H(string name) => Fnv1a.HashLower(name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjectHeadersMatchTheFullParserIncludingPatchWrappers(bool patch)
    {
        var tree = new BinTree(new[]
        {
            new BinTreeObject("One", "Any", new BinTreeProperty[] { new BinTreeString(H("value"), "nested data must be skipped") }),
            new BinTreeObject("Two", "Any", Array.Empty<BinTreeProperty>())
        }, new[] { "linked.bin" });
        using var body = new MemoryStream(); tree.Write(body);
        using var stream = new MemoryStream();
        if (patch)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("PTCH")); writer.Write(1u); writer.Write(0u);
        }
        body.Position = 0; body.CopyTo(stream); stream.Position = 0;
        if (patch)
        {
            stream.Position = stream.Length;
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            writer.Write(0u);
            stream.Position = 0;
        }
        Assert.Equal(new[] { H("One"), H("Two") }, BinObjectHeaders.Read(stream));
        stream.Position = 0;
        Assert.Equal(tree.Objects.Keys, new BinTree(stream).Objects.Keys);
        stream.SetLength(stream.Length - (patch ? 5 : 1)); stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => BinObjectHeaders.Read(stream));
    }

    [Fact]
    public async Task ProjectCategoryAndMaterialOutsideLinksAreLoadedAndHeaderCacheRefreshesAfterAnEdit()
    {
        string root = Directory.CreateTempSubdirectory("am-inking-").FullName;
        try
        {
            void Save(string file, params BinTreeObject[] objects)
            {
                using var output = File.Create(file);
                new BinTree(objects, Array.Empty<string>()).Write(output);
            }
            BinTreeObject Category(string material) => new("Category", "CharacterOutlineCategory", new BinTreeProperty[]
                { new BinTreeObjectLink(H("Material"), H(material)) });
            BinTreeObject Material(string name) => new(name, "StaticMaterialDef", Array.Empty<BinTreeProperty>());
            var skin = new BinTree(new[] { new BinTreeObject("Skin", "SkinCharacterDataProperties", new BinTreeProperty[]
            {
                new BinTreeStruct(H("skinMeshProperties"), H("SkinMeshDataProperties"), new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(H("OutlineCategorySubmeshes"), BinPropertyType.Embedded, new[]
                    {
                        new BinTreeEmbedded(0, H("CharacterOutlineSubmeshes"), new BinTreeProperty[]
                        { new BinTreeObjectLink(H("category"), H("Category")), new BinTreeString(H("Submeshes"), "Body") })
                    })
                })
            }) }, Array.Empty<string>());
            // Only the category is discoverable by extension; its linked material still has to resolve by content.
            string categoryFile = Path.Combine(root, "category.bin"), materialFile = Path.Combine(root, "material.linked");
            void SaveCategory(string material)
            {
                using var output = File.Create(categoryFile);
                new BinTree(new[] { Category(material) }, new[] { "material.linked" }).Write(output);
            }
            SaveCategory("First");
            Save(materialFile, Material("First"));
            var resolver = new MapAssetResolver(null, null);
            var documents = new List<BinTree> { skin };
            await CharacterOutlineDocumentLoader.ExpandAsync(documents, resolver, root, null, CancellationToken.None);
            Assert.Equal(3, documents.Count);
            Assert.Equal(H("First"), SknMaterialTextureResolver.ReadMetadata(documents).Outlines["Body"].MaterialHash);
            Assert.NotEmpty(resolver.ObjectHeaders);
            SaveCategory("Second");
            Save(materialFile, Material("Second"));
            File.SetLastWriteTimeUtc(materialFile, DateTime.UtcNow.AddSeconds(1));
            documents = new List<BinTree> { skin };
            await CharacterOutlineDocumentLoader.ExpandAsync(documents, resolver, root, null, CancellationToken.None);
            Assert.Equal(H("Second"), SknMaterialTextureResolver.ReadMetadata(documents).Outlines["Body"].MaterialHash);
        }
        finally { Directory.Delete(root, true); }
    }
}
