using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Lcu
{
    internal sealed partial class LcuHashGuesser : HashGuesser
    {
        private readonly object _directorySync = new();
        private IReadOnlyList<string> _knownDirectories;
        private long _knownDirectoryRevision = -1;
        private readonly LogService _logService;


        internal LcuHashGuesser(HashFile hashFile, LogService logService)
            : base(hashFile, "*.wad")
        {
            if (hashFile.Domain != HashGuessDomain.Lcu) throw new ArgumentException("LCU guesser requires an LCU hash file.", nameof(hashFile));
            _logService = logService;
        }

        internal LcuHashGuesser(IEnumerable<string> knownPaths, LogService logService)
            : this(new HashFile(HashGuessDomain.Lcu, knownPaths), logService) { }


        internal IReadOnlyList<string> WordlistPaths => Corpus.GetOrCreate(
            "wordlist-paths",
            paths => paths.Where(path => !Regex.IsMatch(path, @"(?:^plugins/rcp-be-lol-game-data/global/default/data/characters/|/[0-9a-f]{32}\.)", RegexOptions.IgnoreCase)).ToList());

        internal override IReadOnlyList<string> BuildWordlist() =>
            Corpus.GetOrCreate("wordlist", _ => HashGuessEngine.BuildWordlist(WordlistPaths));


        internal IReadOnlyList<string> BuildSwordlist() =>
            Corpus.GetOrCreate(
                "swordlist",
                paths => HashGuessEngine.BuildWordlist(
                    paths
                        .Where(path => path.Contains("-fe-lol-", StringComparison.Ordinal)
                            && path.Contains(".json", StringComparison.Ordinal))
                        .Select(Path.GetFileName)));


        internal IReadOnlyList<string> BuildSswordlist() =>
            Corpus.GetOrCreate(
                "sswordlist",
                paths => HashGuessEngine.BuildWordlist(
                    paths
                        .Where(IsRcpFeLolSvgPath)
                        .Select(Path.GetFileName)));


        internal IReadOnlyList<string> BuildPngJpgSwordlist() =>
            Corpus.GetOrCreate(
                "swordlist-png-jpg",
                paths => HashGuessEngine.BuildWordlist(
                    paths
                        .Where(IsRcpFeLolPngJpgPath)
                        .Select(Path.GetFileName)));


        internal IReadOnlyList<string> BuildMediaSwordlist() =>
            Corpus.GetOrCreate(
                "swordlist-media",
                paths => HashGuessEngine.BuildWordlist(
                    paths
                        .Where(IsRcpFeLolMediaPath)
                        .Select(Path.GetFileName)));


        private static bool IsRcpFeLolSvgPath(string path) =>
            path.Contains("-fe-lol-", StringComparison.Ordinal)
            && path.Contains(".svg", StringComparison.Ordinal);


        private static bool IsRcpFeLolPngJpgPath(string path) =>
            path.Contains("-fe-lol-", StringComparison.Ordinal)
            && (path.Contains(".png", StringComparison.Ordinal)
                || path.Contains(".jpg", StringComparison.Ordinal));


        private static bool IsRcpFeLolMediaPath(string path) =>
            path.Contains("-fe-lol-", StringComparison.Ordinal)
            && (path.Contains(".webm", StringComparison.Ordinal)
                || path.Contains(".ogg", StringComparison.Ordinal));


        protected override bool IncludeNumberPath(string path) =>
            !Regex.IsMatch(path, @"(?:^(?:plugins/rcp-be-lol-game-data/[^/]+/[^/]+/v1/champion-|plugins/rcp-be-lol-game-data/global/default/(?:data|assets)/characters/|plugins/rcp-be-lol-game-data/global/default/data/items/icons2d/\d+_|plugins/rcp-be-lol-game-data/[^/]+/[^/]+/v1/champions/-1\.json)|/[0-9a-f]{32}\.)", RegexOptions.IgnoreCase);


        protected override void CheckCandidate(
            HashGuessEngine engine,
            HashGuessCandidate candidate,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            if (candidate.Strategy != HashGuessStrategy.LcuRelativeBasename)
            {
                base.CheckCandidate(engine, candidate, sourceWadPath, sourceChunkHash, cancellationToken);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (string directory in GetKnownDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                engine.CheckCombined(
                    directory,
                    candidate.Path,
                    candidate.Strategy,
                    sourceWadPath,
                    sourceChunkHash);
                if (engine.RemainingUnknownCount == 0) break;
            }
        }


        private IReadOnlyList<string> GetKnownDirectories()
        {
            IReadOnlyList<string> paths = KnownPaths;
            long revision = HashFile.Revision;
            lock (_directorySync)
            {
                if (_knownDirectories == null || _knownDirectoryRevision != revision)
                {
                    _knownDirectories = HashGuessEngine.BuildDirectoryList(paths);
                    _knownDirectoryRevision = revision;
                }
                return _knownDirectories;
            }
        }


        private void LogInvalidJson(string kind, string sourcePath, Exception exception)
        {
            _logService?.LogDebug($"Hash Lab skipped invalid {kind} JSON '{sourcePath}': {exception.Message}");
        }

        internal override void ReleaseMemory()
        {
            base.ReleaseMemory();
            lock (_directorySync)
            {
                _knownDirectories = null;
                _knownDirectoryRevision = -1;
            }
        }
    }
}
