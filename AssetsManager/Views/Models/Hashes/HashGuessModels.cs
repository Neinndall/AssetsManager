using System;
using System.Collections.Generic;

namespace AssetsManager.Views.Models.Hashes
{
    public enum HashGuessDomain { Game, Lcu }
    public enum HashGuessStrategy { EmbeddedPathGrep, BinLengthPath, BinEntry, PreloadReference, ShaderInclude, ShaderVariant, AtlasReference, LcuEmbeddedPath, CharacterTemplate, CharacterSubstitution, SkinNumberVariant, SuffixVariant, ChromaGroupVariant, CrossDomainAsset, CrossDomainGame, PrefixVariant, PluginVariant, LcuPattern, ExtensionVariant, LanguageVariant, NumberVariant, LuaVariant, ImageExtensionVariant, WordlistVariant, LcuRelativeBasename, LuaManifest, AnimationBinLink }

    public sealed class HashGuessMatch
    {
        public ulong Hash { get; set; }
        public string Path { get; set; }
        public HashGuessDomain Domain { get; set; }
        public HashGuessStrategy Strategy { get; set; }
        public string SourceWadPath { get; set; }
        public ulong SourceChunkHash { get; set; }
        public DateTime FoundAtUtc { get; set; } = DateTime.UtcNow;
        public string HashText => Hash.ToString("x16");
        public string DomainText => Domain.ToString().ToUpperInvariant();
        public string StrategyText => Strategy.ToString();
    }

    public sealed class HashGuessProgress
    {
        public int ProcessedWads { get; init; }
        public int TotalWads { get; init; }
        public int ProcessedChunks { get; init; }
        public int FoundMatches { get; init; }
        public int RemainingUnknowns { get; init; }
        public string CurrentWad { get; init; }
        public long CheckedCandidates { get; init; }
        public long DiscardedCandidates { get; init; }
        public double CandidatesPerSecond { get; init; }
        public TimeSpan Elapsed { get; init; }
        public long ManagedMemoryBytes { get; init; }
    }

    public sealed class HashGuessRunResult
    {
        public HashGuessDomain Domain { get; init; }
        public int UnknownHashesAtStart { get; init; }
        public int ScannedChunks { get; init; }
        public IReadOnlyList<HashGuessMatch> Matches { get; init; } = Array.Empty<HashGuessMatch>();
    }

    public sealed class HashUnknownRecord
    {
        public ulong Hash { get; set; }
        public HashGuessDomain Domain { get; set; }
        public string FirstSeenPatch { get; set; }
        public string LastSeenPatch { get; set; }
        public string LastObservedPatch { get; set; }
        public int SeenPatchCount { get; set; }
        public int MissedPatchCount { get; set; }
    }

    public sealed class HashUnknownInventory
    {
        public HashSet<ulong> All { get; init; } = new();
        public HashSet<ulong> Current { get; init; } = new();
        public string PatchFingerprint { get; init; }
    }

    public sealed class HashUnknownSummary
    {
        public int Current { get; init; }
        public int Recent { get; init; }
        public int Historical { get; init; }
        public int Total => Current + Recent + Historical;
    }
}
