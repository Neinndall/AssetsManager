using System;
using System.Collections.Generic;

namespace AssetsManager.Views.Models.Hashes
{
    public enum HashGuessDomain { Game, Lcu }
    public enum HashGuessStrategy { EmbeddedPathGrep, BinLengthPath, BinEntry, PreloadReference, ShaderInclude, ShaderVariant, AtlasReference, LcuEmbeddedPath, CharacterTemplate, CharacterSubstitution, SkinNumberVariant, SuffixVariant, ChromaGroupVariant, CrossDomainAsset, CrossDomainGame, PrefixVariant, PluginVariant, LcuPattern, ExtensionVariant, LanguageVariant, NumberVariant, LuaVariant, ImageExtensionVariant, WordlistVariant }

    public sealed class HashGuessMatch
    {
        public ulong Hash { get; set; }
        public string Path { get; set; }
        public HashGuessDomain Domain { get; set; }
        public HashGuessStrategy Strategy { get; set; }
        public string SourceWadPath { get; set; }
        public ulong SourceChunkHash { get; set; }
        public DateTime FoundAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class HashGuessProgress
    {
        public int ProcessedWads { get; init; }
        public int TotalWads { get; init; }
        public int ProcessedChunks { get; init; }
        public int FoundMatches { get; init; }
        public string CurrentWad { get; init; }
    }

    public sealed class HashGuessRunResult
    {
        public HashGuessDomain Domain { get; init; }
        public int UnknownHashesAtStart { get; init; }
        public int ScannedChunks { get; init; }
        public IReadOnlyList<HashGuessMatch> Matches { get; init; } = Array.Empty<HashGuessMatch>();
    }

    public sealed class HashGuessStageResult
    {
        public string Name { get; init; }
        public int Candidates { get; init; }
        public int Matches { get; init; }
    }
}
