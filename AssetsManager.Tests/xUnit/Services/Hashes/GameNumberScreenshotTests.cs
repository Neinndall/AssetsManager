using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Hashes
{
    public class GameNumberScreenshotTests
    {
        [Theory]
        [InlineData(12, null)]
        [InlineData(12, 2)]
        [InlineData(1100, null)]
        [InlineData(1100, 2)]
        [InlineData(12, 17)]
        public void CachedNumericEncodingPreservesCandidatesAndCounters(int directoryLength, int? digits)
        {
            string number = digits == 17 ? new string('0', 15) + "09" : "09";
            string seed = "assets/" + new string('é', directoryLength) + "/icon" + number + "_雪.tex";
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[] { seed }));
            string[] expected = guesser.SubstituteNumbers(300, digits).Select(candidate => candidate.Path).ToArray();
            // The numeric formatter deliberately keeps its existing 16-character limit.
            if (digits > 16) expected = System.Array.Empty<string>();
            var engine = new HashGuessEngine(HashGuessDomain.Game,
                expected.Select(path => XxHash64Ext.Hash(path)).Append(42UL).ToHashSet());
            int checkedCount = guesser.SubstituteNumbers(engine, CancellationToken.None, maximum: 300, digits: digits);
            Assert.Equal(expected.Length, checkedCount);
            Assert.Equal(expected.Length, engine.CheckedCandidates);
            Assert.Equal(expected.Order(), engine.Matches.Values.Select(match => match.Path).Order());
            Assert.Contains(42UL, engine.UnknownHashes);
        }

        [Fact]
        public void TwoDigitPassRecoversTheTwelveHistoricalTextureHashes()
        {
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, new[]
            {
                "assets/characters/jade_galio/skins/base/particles/galio_armor_09.tex",
                "assets/characters/petdoughcat/themes/sushi/petdoughcat_sushi_sushi_face01_tx_cm.tex",
                "assets/characters/petstyletwoleblanc/skins/skin2/particles/petstyletwoleblanc_skin2_emote_wtrail01.tex"
            }));
            ulong[] targets =
            {
                0xfa582e6b0397aa3c, 0xfca40ddeb335854c, 0x91c921b59700f33a,
                0x124cd1a6aeb7407c, 0xb4492aff960a1576, 0x3339ab5c0073d644,
                0xfca4f7f8ef50630c, 0xa2f946894b2ffeee, 0x2c3391d99492c9be,
                0xbff15e2661ca6f84, 0x382fb03a5bf110a3, 0x17f00da58c7f25b9
            };
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(targets));
            guesser.SubstituteNumbers(engine, CancellationToken.None, maximum: 200);
            Assert.Empty(engine.Matches);
            guesser.SubstituteNumbers(engine, CancellationToken.None, maximum: 200, digits: 2);
            Assert.Equal(targets.Order(), engine.Matches.Keys.Order());
            Assert.Equal(0, engine.RemainingUnknownCount);
            Assert.All(engine.Matches.Values, match =>
            {
                Assert.Equal(HashGuessStrategy.NumberVariant, match.Strategy);
                Assert.Equal("Generated numeric variant", match.SourceWadPath);
            });
        }
    }
}
