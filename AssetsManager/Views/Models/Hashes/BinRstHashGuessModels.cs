using System;
using System.Collections.Generic;

namespace AssetsManager.Views.Models.Hashes
{
    public enum InternalHashKind
    {
        BinEntries,
        BinFields,
        BinTypes,
        BinHashes,
        RstXxh3,
        RstXxh64
    }

    public enum InternalHashGuessStrategy
    {
        BinContent,
        TextContent,
        RemoteContent,
        GamePath,
        CrossDictionary,
        CrossVersion,
        NumericVariant
    }

    public sealed class InternalHashGuessMatch
    {
        public ulong Hash { get; init; }
        public ulong LookupHash { get; init; }
        public int HashBits { get; init; }
        public string Value { get; init; }
        public InternalHashKind Kind { get; init; }
        public InternalHashGuessStrategy Strategy { get; init; }
        public string Source { get; init; }
        public DateTime FoundAtUtc { get; init; } = DateTime.UtcNow;
        public string HashText => Kind is InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64
            ? Hash.ToString("x16")
            : ((uint)Hash).ToString("x8");
        public string DomainText => Kind switch
        {
            InternalHashKind.BinEntries => "BIN Entries",
            InternalHashKind.BinFields => "BIN Fields",
            InternalHashKind.BinTypes => "BIN Types",
            InternalHashKind.BinHashes => "BIN Hashes",
            InternalHashKind.RstXxh3 => "RST XXH3",
            _ => "RST XXH64"
        };
        public string Path => Value;
        public string StrategyText => Strategy.ToString();
        public string SourceWadPath => Source;
    }

    public sealed class InternalHashInventory
    {
        public Dictionary<InternalHashKind, HashSet<ulong>> Unknowns { get; init; } = new();
        public string PatchFingerprint { get; init; }
        public int ScannedBins { get; init; }
        public int ScannedStringTables { get; init; }
    }

    public sealed class InternalHashSummary
    {
        public int BinEntries { get; init; }
        public int BinFields { get; init; }
        public int BinTypes { get; init; }
        public int BinHashes { get; init; }
        public int RstXxh3 { get; init; }
        public int RstXxh64 { get; init; }
        public int BinTotal => BinEntries + BinFields + BinTypes + BinHashes;
        public int RstTotal => RstXxh3 + RstXxh64;
    }

    public sealed class InternalHashRunResult
    {
        public int UnknownHashesAtStart { get; init; }
        public int ScannedFiles { get; init; }
        public IReadOnlyList<InternalHashGuessMatch> Matches { get; init; } = Array.Empty<InternalHashGuessMatch>();
    }

    public sealed class InternalHashProgress
    {
        public int ProcessedWads { get; init; }
        public int TotalWads { get; init; }
        public int ProcessedFiles { get; init; }
        public int FoundMatches { get; init; }
        public string CurrentStage { get; init; }
    }
}
