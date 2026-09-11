using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public sealed class SkinRoleTextureTests
    {
        [Fact]
        public void GuessSkinRoleTextures_ResolvesRoleTextureFromMaterialOverride()
        {
            const string target = "assets/characters/urgot/skins/skin42/urgot_skin42_lower_tx_cm.tex";
            ulong targetHash = XxHash64Ext.Hash(target);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()), null, _ => string.Empty);
            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { targetHash }, m => matches.Add(m));

            guesser.GuessSkinRoleTextures(
                engine,
                new ArraySegment<byte>(new byte[4]),
                "data/characters/urgot/skins/skin42.bin",
                "Urgot.wad.client",
                1UL,
                System.Threading.CancellationToken.None,
                () => CreateSkinTree("Characters/Urgot/Skins/Skin42/Materials/Urgot_Skin42_Mat", "lower", targetHash));

            Assert.Single(matches);
            Assert.Equal(target, matches[0].Path);
            Assert.Equal(0, engine.RemainingUnknownCount);
        }

        [Fact]
        public void GuessSkinRoleTextures_StripsKnownRolesWhenLearningStems()
        {
            const string target = "assets/characters/janna/skins/skin70/janna_skin70_recall_tx_cm.tex";
            ulong targetHash = XxHash64Ext.Hash(target);
            var hashFile = new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/janna/skins/skin70/janna_skin70_body_tx_cm.tex",
                "assets/characters/janna/skins/skin70/janna_skin70_weapon_tx_cm.tex"
            });
            var guesser = new GameHashGuesser(hashFile, null, _ => string.Empty);
            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { targetHash }, m => matches.Add(m));

            guesser.GuessSkinRoleTextures(
                engine,
                new ArraySegment<byte>(new byte[4]),
                "data/characters/janna/skins/skin70.bin",
                "Janna.wad.client",
                2UL,
                System.Threading.CancellationToken.None,
                () => CreateSkinTree("Characters/Janna/Skins/Skin70/Materials/Janna_Skin70_Mat", "Recall", targetHash));

            Assert.Single(matches);
            Assert.Equal(target, matches[0].Path);
        }

        [Fact]
        public void GuessSkinRoleTextures_ResolvesGlossMapTwin()
        {
            const string target = "assets/characters/urgot/skins/skin42/urgot_skin42_lower_tx_gm.tex";
            ulong targetHash = XxHash64Ext.Hash(target);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()), null, _ => string.Empty);
            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { targetHash }, m => matches.Add(m));

            guesser.GuessSkinRoleTextures(
                engine,
                new ArraySegment<byte>(new byte[4]),
                "data/characters/urgot/skins/skin42.bin",
                "Urgot.wad.client",
                4UL,
                System.Threading.CancellationToken.None,
                () => CreateSkinTree("Characters/Urgot/Skins/Skin42/Materials/Urgot_Skin42_Mat", "lower", targetHash));

            Assert.Single(matches);
            Assert.Equal(target, matches[0].Path);
            Assert.Equal(0, engine.RemainingUnknownCount);
        }

        [Fact]
        public void GuessSkinRoleTextures_ResolvesBareRoleDds()
        {
            const string target = "assets/characters/jade_nami/skins/skin02/jade_nami_skin02_cubemap.dds";
            ulong targetHash = XxHash64Ext.Hash(target);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()), null, _ => string.Empty);
            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { targetHash }, m => matches.Add(m));

            guesser.GuessSkinRoleTextures(
                engine,
                new ArraySegment<byte>(new byte[4]),
                "data/characters/jade_nami/skins/skin02.bin",
                "Nami.wad.client",
                5UL,
                System.Threading.CancellationToken.None,
                () => CreateSkinTree("Characters/Jade_Nami/Skins/Skin02/Materials/Jade_Nami_Skin02_Mat", "cubemap", targetHash));

            Assert.Single(matches);
            Assert.Equal(target, matches[0].Path);
            Assert.Equal(0, engine.RemainingUnknownCount);
        }

        [Theory]
        [InlineData("skin58", "weapon_light", "assets/characters/jade_nami/skins/skin58/jade_nami_skin58_weapon_light_tx_cm.tex")]
        [InlineData("skin24", "mask", "assets/characters/jade_nami/skins/skin24/jade_nami_skin24_mask_tx.tex")]
        public void GuessSkinRoleTextures_KeepsUnderscoresAndShortEndings(string skin, string submesh, string target)
        {
            ulong targetHash = XxHash64Ext.Hash(target);
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()), null, _ => string.Empty);
            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { targetHash }, m => matches.Add(m));

            guesser.GuessSkinRoleTextures(
                engine,
                new ArraySegment<byte>(new byte[4]),
                $"data/characters/jade_nami/skins/{skin}.bin",
                "Nami.wad.client",
                6UL,
                System.Threading.CancellationToken.None,
                () => CreateSkinTree($"Characters/Jade_Nami/Skins/{skin}/Materials/Jade_Nami_Mat", submesh, targetHash));

            Assert.Single(matches);
            Assert.Equal(target, matches[0].Path);
            Assert.Equal(0, engine.RemainingUnknownCount);
        }

        [Fact]
        public void GuessSkinRoleTextures_IgnoresMaterialsWithoutUnknownTargets()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Array.Empty<string>()), null, _ => string.Empty);
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong> { 99999UL });

            guesser.GuessSkinRoleTextures(
                engine,
                new ArraySegment<byte>(new byte[4]),
                "data/characters/urgot/skins/skin42.bin",
                "Urgot.wad.client",
                3UL,
                System.Threading.CancellationToken.None,
                () => CreateSkinTree("Characters/Urgot/Skins/Skin42/Materials/Urgot_Skin42_Mat", "lower", 12345UL));

            Assert.Empty(engine.Matches);
            Assert.Equal(1, engine.RemainingUnknownCount);
        }

        private static BinTree CreateSkinTree(string materialPath, string submesh, ulong textureHash)
        {
            var sampler = new BinTreeEmbedded(
                0,
                Fnv1a.HashLower("StaticMaterialShaderSamplerDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("textureName"), "Diffuse_Texture"),
                    new BinTreeWadChunkLink(Fnv1a.HashLower("texturePath"), textureHash)
                });
            var material = new BinTreeObject(
                materialPath,
                "StaticMaterialDef",
                new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("samplerValues"),
                        BinPropertyType.Embedded,
                        new[] { sampler })
                });
            var skinOverride = new BinTreeEmbedded(
                0,
                Fnv1a.HashLower("SkinMeshDataProperties_MaterialOverride"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("submesh"), submesh),
                    new BinTreeObjectLink(Fnv1a.HashLower("Material"), Fnv1a.HashLower(materialPath))
                });
            var mesh = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(
                        Fnv1a.HashLower("materialOverride"),
                        BinPropertyType.Embedded,
                        new[] { skinOverride })
                });
            var skin = new BinTreeObject(
                "Characters/Test/Skins/Skin",
                "SkinCharacterDataProperties",
                new BinTreeProperty[] { mesh });
            return new BinTree(new[] { skin, material }, Array.Empty<string>());
        }
    }
}
