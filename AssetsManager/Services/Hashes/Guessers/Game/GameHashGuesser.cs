using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Game
{
    internal sealed partial class GameHashGuesser : HashGuesser
    {
        private static readonly string[] ShaderExtensions = { ".ps_2_0", ".ps_3_0", ".vs_2_0", ".vs_3_0", ".ps", ".vs", ".cs" };
        private static readonly string[] ShaderVariants = { ".dx11", ".dx9", ".dx9sm3", ".glsl", ".metal", "-dx11", "-metal" };
        // Compiled permutations are numbered 0..31 and in hundreds (up to 12700 in current data).
        // GrepWad keeps to 0..31 for speed; Basic Suite expands every known shader with the full set.
        private const int ShaderBasePermutationCount = 32;
        private static readonly int[] ShaderPermutationIndices = Enumerable.Range(0, ShaderBasePermutationCount)
            .Concat(Enumerable.Range(1, 200).Select(index => index * 100))
            .ToArray();
        private readonly LogService _logService;
        private readonly Func<uint, string> _resolveBinHash;
        private readonly Func<ulong, string> _resolveBinHash64;

        internal GameHashGuesser(HashFile hashFile, LogService logService = null, Func<uint, string> resolveBinHash = null,
            Func<ulong, string> resolveBinHash64 = null)
            : base(hashFile, "*.wad.client")
        {
            if (hashFile.Domain != HashGuessDomain.Game) throw new ArgumentException("GAME guesser requires a GAME hash file.", nameof(hashFile));
            _logService = logService;
            _resolveBinHash = resolveBinHash;
            _resolveBinHash64 = resolveBinHash64;
        }

        internal GameHashGuesser() : this(new HashFile(HashGuessDomain.Game, Array.Empty<string>())) { }

        internal override IReadOnlyList<string> BuildWordlist() =>
            Corpus.GetOrCreate("wordlist", HashGuessEngine.BuildWordlist);


        internal IEnumerable<HashGuessCandidate> GuessFromBinEntryBasenames(IEnumerable<string> binEntryPaths)
        {
            var basenames = binEntryPaths.Select(Path.GetFileName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var extensions = KnownPaths.Select(Path.GetExtension)
                .Where(extension => extension.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(extension => !Regex.IsMatch(extension, @"(?:glsl|dx9|dx9sm3|dx11|metal)_", RegexOptions.IgnoreCase))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();

            foreach (string extension in extensions)
            foreach (string basename in basenames)
                yield return new HashGuessCandidate(basename + extension, HashGuessStrategy.CrossDomainGame);
        }

        private static bool IsShaderReference(StringBuilder value)
        {
            foreach (string extension in ShaderExtensions)
                if (EndsWith(value, extension)) return true;
            return false;
        }

        private static bool EndsWith(StringBuilder value, string suffix)
        {
            if (value.Length < suffix.Length) return false;
            int offset = value.Length - suffix.Length;
            for (int index = 0; index < suffix.Length; index++)
                if (char.ToLowerInvariant(value[offset + index]) != suffix[index]) return false;
            return true;
        }

    }
}
