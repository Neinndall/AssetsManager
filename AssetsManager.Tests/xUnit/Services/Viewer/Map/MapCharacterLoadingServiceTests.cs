using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapCharacterLoadingServiceTests
    {
        [Fact]
        public void BareSixteenDigitTextureKeyRemainsAHashOnlyAsset()
        {
            MapAssetReference reference = MapCharacterLoadingService.ReferenceFromAuthoredTexture("1234567890abcdef");

            Assert.NotNull(reference);
            Assert.Null(reference.VirtualPath);
            Assert.Equal(0x1234567890abcdeful, reference.PathHash);
        }

        [Fact]
        public void VfxCatalogUsesPrimarySkinResolverForSystemsFoundInLinkedDocuments()
        {
            const uint effectKey = 0x11223344;
            uint systemHash = Fnv1a.HashLower("Effects/Test/Linked");
            uint resolverHash = Fnv1a.HashLower("Characters/Test/Skins/Skin0/Resources");

            var skin = new BinTreeObject(
                "Characters/Test/Skins/Skin0",
                "SkinCharacterDataProperties",
                new BinTreeProperty[]
                {
                    new BinTreeObjectLink(Fnv1a.HashLower("mResourceResolver"), resolverHash)
                });
            var resolver = new BinTreeObject(
                resolverHash,
                Fnv1a.HashLower("ResourceResolver"),
                new BinTreeProperty[]
                {
                    new BinTreeMap(
                        Fnv1a.HashLower("resourceMap"),
                        BinPropertyType.Hash,
                        BinPropertyType.ObjectLink,
                        new[]
                        {
                            new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                                new BinTreeHash(0, effectKey),
                                new BinTreeObjectLink(0, systemHash))
                        })
                });
            var linkedSystem = new BinTreeObject(
                systemHash,
                Fnv1a.HashLower("VfxSystemDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), "Linked"),
                    new BinTreeString(Fnv1a.HashLower("particlePath"), "Effects/Test/Linked")
                });

            MapCharacterVfxCatalog catalog = MapCharacterLoadingService.BuildVfxCatalog(
                new[]
                {
                    new BinTree(new[] { skin, resolver }, Array.Empty<string>()),
                    new BinTree(new[] { linkedSystem }, Array.Empty<string>())
                },
                null,
                null,
                null,
                null);

            Assert.Equal(systemHash, catalog.ResourceMap[effectKey]);
            Assert.True(catalog.Systems.TryGetValue(systemHash, out VfxSystemDefinition system));
            Assert.Equal("Linked", system.Name);
        }

        [Theory]
        [InlineData("assets/characters/test/diffuse.tex")]
        [InlineData("data/characters/test/skins/skin0.bin")]
        public void AuthoredPathRemainsAPathAsset(string path)
        {
            MapAssetReference reference = MapCharacterLoadingService.ReferenceFromAuthoredTexture(path);

            Assert.NotNull(reference);
            Assert.Equal(path, reference.VirtualPath);
            Assert.Equal(0ul, reference.PathHash);
        }
    }
}
