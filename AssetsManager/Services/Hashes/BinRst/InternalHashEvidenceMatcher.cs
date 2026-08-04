using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes
{
    internal sealed class InternalHashEvidenceMatcher
    {
        private readonly Dictionary<InternalHashKind, HashSet<ulong>> _targets;
        private readonly Dictionary<InternalHashKind, HashSet<ulong>> _matched;
        private readonly Dictionary<(InternalHashKind Kind, ulong Hash, string Value), InternalHashGuessMatch> _matches = new();
        private readonly List<InternalHashGuessMatch> _pendingMatches = new();

        internal InternalHashEvidenceMatcher(Dictionary<InternalHashKind, HashSet<ulong>> targets)
        {
            foreach (InternalHashKind kind in Enum.GetValues<InternalHashKind>())
                if (!targets.ContainsKey(kind)) targets[kind] = new HashSet<ulong>();
            _targets = targets;
            _matched = Enum.GetValues<InternalHashKind>().ToDictionary(
                kind => kind,
                kind => new HashSet<ulong>());
        }

        internal IReadOnlyCollection<InternalHashGuessMatch> Matches => _matches.Values;
        internal int Remaining => _targets.Sum(pair => Math.Max(0, pair.Value.Count - _matched[pair.Key].Count));
        internal long CheckedCandidates { get; private set; }
        internal long DiscardedCandidates { get; private set; }

        internal int GetRemainingCount(InternalHashKind kind) =>
            _targets.TryGetValue(kind, out HashSet<ulong> values)
                ? Math.Max(0, values.Count - _matched[kind].Count)
                : 0;

        internal IReadOnlyList<InternalHashGuessMatch> TakePendingMatches()
        {
            if (_pendingMatches.Count == 0) return Array.Empty<InternalHashGuessMatch>();
            InternalHashGuessMatch[] matches = _pendingMatches.ToArray();
            _pendingMatches.Clear();
            return matches;
        }

        internal void Check(
            string value,
            InternalHashGuessStrategy strategy,
            string source,
            string sourceWad = null,
            string sourceBin = null,
            IReadOnlyDictionary<InternalHashKind, HashSet<ulong>> localTargets = null,
            bool includeTruncatedRst = true)
        {
            CheckedCandidates++;
            if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || value.Length > 512)
            {
                DiscardedCandidates++;
                return;
            }
            string candidate = NormalizeCandidate(value);
            uint fnv = Fnv1a.HashLower(candidate);
            bool content = strategy is InternalHashGuessStrategy.BinContent or InternalHashGuessStrategy.TextContent;
            bool crossDictionary = strategy == InternalHashGuessStrategy.CrossDictionary;
            bool gamePath = strategy == InternalHashGuessStrategy.GamePath;
            if (content || crossDictionary || gamePath)
            {
                bool isAssetPath = candidate.Contains('.') && !candidate.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                if (!isAssetPath)
                {
                    if (candidate.Contains('/'))
                        Check32(InternalHashKind.BinEntries, fnv, candidate, strategy, source, sourceWad, sourceBin, HasLocalEvidence(fnv, localTargets, InternalHashKind.BinEntries));
                    Check32(InternalHashKind.BinHashes, fnv, candidate, strategy, source, sourceWad, sourceBin, HasLocalEvidence(fnv, localTargets, InternalHashKind.BinHashes));
                }
            }
            if ((content || crossDictionary) && IsIdentifier(candidate))
            {
                Check32(InternalHashKind.BinFields, fnv, candidate, strategy, source, sourceWad, sourceBin, HasLocalEvidence(fnv, localTargets, InternalHashKind.BinFields));
                Check32(InternalHashKind.BinTypes, fnv, candidate, strategy, source, sourceWad, sourceBin, HasLocalEvidence(fnv, localTargets, InternalHashKind.BinTypes));
            }

            if (content || strategy is InternalHashGuessStrategy.CrossDictionary or InternalHashGuessStrategy.CrossVersion or InternalHashGuessStrategy.NumericVariant or InternalHashGuessStrategy.GamePath)
            {
                bool hasXxh3 = includeTruncatedRst && _targets[InternalHashKind.RstXxh3].Count > 0;
                bool hasXxh64 = _targets[InternalHashKind.RstXxh64].Count > 0;
                if (hasXxh3 || hasXxh64)
                {
                    int byteCount = Encoding.UTF8.GetByteCount(candidate);
                    Span<byte> bytes = byteCount <= 1536 ? stackalloc byte[byteCount] : new byte[byteCount];
                    Encoding.UTF8.GetBytes(candidate, bytes);
                    if (hasXxh3)
                        CheckRst(InternalHashKind.RstXxh3, XxHash3.HashToUInt64(bytes), candidate, strategy, source, new[] { 38 }, sourceWad, sourceBin);
                    if (hasXxh64)
                        CheckRst(InternalHashKind.RstXxh64, XxHash64.HashToUInt64(bytes), candidate, strategy, source, includeTruncatedRst ? new[] { 64, 38, 39, 40 } : new[] { 64 }, sourceWad, sourceBin);
                }
            }
        }

        internal bool CheckContextualCandidate(
            InternalHashKind kind,
            string value,
            string source,
            string sourceWad = null,
            uint? observedHash = null,
            InternalHashEvidence evidence = InternalHashEvidence.ObservedHashPair)
        {
            CheckedCandidates++;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            {
                DiscardedCandidates++;
                return false;
            }

            string candidate = NormalizeCandidate(value);
            uint computedHash = Fnv1a.HashLower(candidate);
            if (observedHash.HasValue && computedHash != observedHash.Value)
            {
                DiscardedCandidates++;
                return false;
            }
            if (!observedHash.HasValue)
            {
                return CheckResearchCandidate(
                    kind,
                    candidate,
                    InternalHashGuessStrategy.BinContent,
                    source,
                    InternalHashEvidence.SemanticReference,
                    sourceWad: sourceWad,
                    countCheck: false);
            }

            int before = _matches.Count;
            Check32(
                kind,
                computedHash,
                candidate,
                InternalHashGuessStrategy.BinContent,
                source,
                sourceWad,
                source,
                true,
                evidence);
            return _matches.Count != before;
        }

        internal bool CheckResearchCandidate(
            InternalHashKind kind,
            string value,
            InternalHashGuessStrategy strategy,
            string source,
            InternalHashEvidence evidence,
            int occurrences = 1,
            double expectedRandomMatches = 0,
            string sourceWad = null,
            bool countCheck = true,
            bool verified = false)
        {
            if (countCheck) CheckedCandidates++;
            if (kind is InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64 ||
                string.IsNullOrWhiteSpace(value) ||
                value.Length > 512)
            {
                DiscardedCandidates++;
                return false;
            }

            string candidate = NormalizeCandidate(value);
            uint hash = Fnv1a.HashLower(candidate);
            if (!_targets[kind].Contains(hash))
            {
                DiscardedCandidates++;
                return false;
            }
            _matched[kind].Add(hash);

            var key = (kind, (ulong)hash, candidate);
            if (_matches.ContainsKey(key)) return false;
            if (verified)
            {
                _targets[kind].Remove(hash);
                foreach (var candidateKey in _matches.Keys
                    .Where(item => item.Kind == kind && item.Hash == hash).ToList())
                    _matches.Remove(candidateKey);
            }
            var match = new InternalHashGuessMatch
            {
                Hash = hash,
                LookupHash = hash,
                HashBits = 32,
                Value = candidate,
                Kind = kind,
                Strategy = strategy,
                Source = source,
                SourceWad = sourceWad,
                SourceBin = source,
                IsVerified = verified,
                VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                Confidence = verified ? InternalHashConfidence.Verified : InternalHashConfidence.Candidate,
                Evidence = evidence,
                EvidenceOrigin = GetEvidenceOrigin(evidence),
                EvidenceOccurrences = occurrences,
                ExpectedRandomMatches = expectedRandomMatches
            };
            _matches[key] = match;
            _pendingMatches.Add(match);
            return true;
        }

        internal IReadOnlyCollection<ulong> GetRemaining(InternalHashKind kind) =>
            _targets.TryGetValue(kind, out HashSet<ulong> values)
                ? values
                : Array.Empty<ulong>();

        internal bool IsRemaining(InternalHashKind kind, ulong hash) =>
            _targets.TryGetValue(kind, out HashSet<ulong> values) && values.Contains(hash);

        internal bool CheckSchemaCandidate(
            InternalHashKind kind,
            string value,
            InternalHashGuessStrategy strategy,
            string source,
            InternalHashEvidence evidence = InternalHashEvidence.MetaSchemaWordset)
        {
            CheckedCandidates++;
            if (kind is not (InternalHashKind.BinTypes or InternalHashKind.BinFields) ||
                string.IsNullOrWhiteSpace(value) ||
                value.Length > 128)
            {
                DiscardedCandidates++;
                return false;
            }
            string candidate = value.Trim();
            if (!IsIdentifier(candidate))
            {
                DiscardedCandidates++;
                return false;
            }
            candidate = kind == InternalHashKind.BinTypes
                ? UpperFirst(candidate)
                : char.ToLowerInvariant(candidate[0]) + candidate[1..];
            uint hash = Fnv1a.HashLower(candidate);
            if (!_targets[kind].Contains(hash))
            {
                DiscardedCandidates++;
                return false;
            }
            _matched[kind].Add(hash);
            var key = (kind, (ulong)hash, candidate);
            if (_matches.ContainsKey(key)) return false;
            bool verified = InternalHashGuessMatch.IsPromotableEvidence(evidence);
            if (verified)
            {
                _targets[kind].Remove(hash);
                foreach (var candidateKey in _matches.Keys
                    .Where(item => item.Kind == kind && item.Hash == hash).ToList())
                    _matches.Remove(candidateKey);
            }
            var match = new InternalHashGuessMatch
            {
                Hash = hash,
                LookupHash = hash,
                HashBits = 32,
                Value = candidate,
                Kind = kind,
                Strategy = strategy,
                Source = source,
                IsVerified = verified,
                VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                Confidence = verified ? InternalHashConfidence.Verified : InternalHashConfidence.Candidate,
                Evidence = evidence,
                EvidenceOrigin = GetEvidenceOrigin(evidence)
            };
            _matches[key] = match;
            _pendingMatches.Add(match);
            return true;
        }

        internal static bool IsIdentifier(string value)
        {
            if (value.Length == 0 || value.Length > 128 || !(char.IsLetter(value[0]) || value[0] == '_')) return false;
            for (int index = 1; index < value.Length; index++)
                if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_')) return false;
            return true;
        }

        private static bool HasLocalEvidence(
            uint hash,
            IReadOnlyDictionary<InternalHashKind, HashSet<ulong>> localTargets,
            InternalHashKind kind)
        {
            if (localTargets == null) return false;
            return localTargets.TryGetValue(kind, out var values) && values.Contains(hash);
        }

        private void Check32(
            InternalHashKind kind,
            uint hash,
            string value,
            InternalHashGuessStrategy strategy,
            string source,
            string sourceWad,
            string sourceBin,
            bool hasLocalEvidence,
            InternalHashEvidence evidence = InternalHashEvidence.RuntimeContext)
        {
            if (!hasLocalEvidence || !_targets[kind].Contains(hash)) return;
            if (hasLocalEvidence && evidence == InternalHashEvidence.RuntimeContext)
                evidence = InternalHashEvidence.OwningFileString;
            bool verified = InternalHashGuessMatch.IsPromotableEvidence(evidence);
            if (verified)
            {
                _targets[kind].Remove(hash);
                foreach (var candidateKey in _matches.Keys
                    .Where(key => key.Kind == kind && key.Hash == hash).ToList())
                    _matches.Remove(candidateKey);
            }
            _matched[kind].Add(hash);
            var key = (kind, (ulong)hash, value);
            if (_matches.ContainsKey(key)) return;
            var match = new InternalHashGuessMatch
            {
                Hash = hash,
                LookupHash = hash,
                HashBits = 32,
                Value = value,
                Kind = kind,
                Strategy = strategy,
                Source = source,
                SourceWad = sourceWad,
                SourceBin = sourceBin,
                IsVerified = verified,
                VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                Confidence = verified ? InternalHashConfidence.Verified : InternalHashConfidence.Candidate,
                Evidence = evidence,
                EvidenceOrigin = GetEvidenceOrigin(evidence),
                EvidenceOccurrences = 1
            };
            _matches[key] = match;
            _pendingMatches.Add(match);
        }

        private void CheckRst(
            InternalHashKind kind,
            ulong fullHash,
            string value,
            InternalHashGuessStrategy strategy,
            string source,
            IEnumerable<int> bitOptions,
            string sourceWad = null,
            string sourceBin = null)
        {
            foreach (int bits in bitOptions)
            {
                ulong lookup = bits == 64 ? fullHash : fullHash & ((1UL << bits) - 1);
                if (!_targets[kind].Remove(lookup)) continue;
                var match = new InternalHashGuessMatch
                {
                    Hash = fullHash,
                    LookupHash = lookup,
                    HashBits = bits,
                    Value = value,
                    Kind = kind,
                    Strategy = strategy,
                    Source = source,
                    SourceWad = sourceWad,
                    SourceBin = sourceBin,
                    IsVerified = true,
                    VerificationSchema = InternalHashGuessMatch.CurrentVerificationSchema,
                    Confidence = InternalHashConfidence.Verified,
                    Evidence = InternalHashEvidence.RstHashMatch,
                    EvidenceOrigin = InternalHashEvidenceOrigin.ShippedData
                };
                _matches[(kind, fullHash, value)] = match;
                _pendingMatches.Add(match);
                break;
            }
        }

        private static string UpperFirst(string value) =>
            string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

        internal static string NormalizeCandidate(string value) =>
            value?.Trim().Replace('\\', '/') ?? string.Empty;

        private static InternalHashEvidenceOrigin GetEvidenceOrigin(InternalHashEvidence evidence) => evidence switch
        {
            InternalHashEvidence.ObservedHashPair or
            InternalHashEvidence.OwningEntryString or
            InternalHashEvidence.OwningEntryPrefix or
            InternalHashEvidence.OwningFileString => InternalHashEvidenceOrigin.RuntimeCorrelation,
            InternalHashEvidence.RstHashMatch or
            InternalHashEvidence.GamePathExactMatch => InternalHashEvidenceOrigin.ShippedData,
            InternalHashEvidence.MetaSchemaWordset or
            InternalHashEvidence.MetaSchemaRelation or
            InternalHashEvidence.MetaSchemaUnique => InternalHashEvidenceOrigin.ExternalSchema,
            InternalHashEvidence.SemanticReference => InternalHashEvidenceOrigin.StructuralInference,
            _ => InternalHashEvidenceOrigin.Unknown
        };
    }
}
