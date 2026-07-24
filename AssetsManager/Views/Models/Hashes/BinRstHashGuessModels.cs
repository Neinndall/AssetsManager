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
        RstXxh64,
        SklJoints
    }

    public enum InternalHashGuessStrategy
    {
        BinContent,
        TextContent,
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
        public string SourceWad { get; init; }
        public string SourceBin { get; init; }
        public bool IsVerified { get; init; }
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
            InternalHashKind.RstXxh64 => "RST XXH64",
            InternalHashKind.SklJoints => "SKL Joints (ELF)",
            _ => "Unknown Domain"
        };
        public string Path => Value;
        public string StrategyText => $"{Strategy} · Verified";
        public string SourceWadPath
        {
            get
            {
                if (!string.IsNullOrEmpty(SourceWad) && !string.IsNullOrEmpty(SourceBin))
                {
                    return $"{System.IO.Path.GetFileName(SourceWad)} -> {SourceBin}";
                }
                if (!string.IsNullOrEmpty(SourceWad)) return SourceWad;
                if (!string.IsNullOrEmpty(SourceBin)) return SourceBin;
                return Source;
            }
        }
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
        public int? RemainingUnknowns { get; init; }
        public long CheckedCandidates { get; init; }
        public long DiscardedCandidates { get; init; }
        public double CandidatesPerSecond { get; init; }
        public TimeSpan Elapsed { get; init; }
        public long ManagedMemoryBytes { get; init; }
        public string CurrentStage { get; init; }
        public IReadOnlyList<InternalHashGuessMatch> NewMatches { get; init; } = Array.Empty<InternalHashGuessMatch>();
    }
}
