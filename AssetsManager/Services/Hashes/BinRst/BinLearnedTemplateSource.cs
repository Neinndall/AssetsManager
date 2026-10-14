using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes
{
    /// <summary>
    /// Learns "prefix + transform(feature) + suffix" naming rules per BIN context (owner class,
    /// field and role) from hashes the catalogs already name, then applies them to unresolved
    /// hashes of the same context. Rules come from real names and every application is checked
    /// against its own target hash, so no broad prefix search can pair a hash with a colliding name.
    /// </summary>
    internal sealed class BinLearnedTemplateSource
    {
        internal const int MinimumSupport = 3;
        // Large contexts (resourceMap keys, clip names) stabilise long before their last sample.
        private const int MaximumLearnedSamplesPerContext = 2000;
        private const int MaximumRulesPerContext = 4096;
        private const int MaximumPrefixLength = 96;
        private const int MaximumSuffixLength = 48;

        private enum Role : byte { Entry, Field, ListItem, Optional, MapKey, MapValue }

        private readonly record struct ContextKey(InternalHashKind Kind, uint Owner, uint Field, Role Role);

        private readonly record struct Feature(string Key, string Value, bool FromFile);

        private sealed class Rule
        {
            public string Feature;
            public int Transform;
            public string Prefix;
            public string Suffix;
            public int Support;
        }

        private sealed record Pending(InternalHashKind Kind, ContextKey Context, uint Hash, Feature[] Features, string Source, string SourceWad);

        private static readonly Func<string, string>[] Transforms =
        {
            value => value,
            Leaf,
            StripExtension,
            value => StripExtension(Leaf(value)),
            value => StripExtension(value.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ? value[5..] : value),
            value => value.Contains('/') ? value[..value.LastIndexOf('/')] : null,
            value => value.Contains('/') ? Leaf(value[..value.LastIndexOf('/')]) : null
        };

        private readonly HashResolverService _resolver;
        private readonly BinPathCasing _casing;
        private readonly Dictionary<ContextKey, Dictionary<string, Rule>> _rules = new();
        // Known map key names per context (event, clip and mask keys are short free-form names).
        private readonly Dictionary<ContextKey, HashSet<string>> _vocabulary = new();
        private const int MaximumVocabularyPerContext = 4000;
        private const int MinimumVocabularyPerContext = 20;
        private readonly Dictionary<ContextKey, int> _learnedSamples = new();
        private readonly List<Pending> _pending = new();
        private readonly HashSet<(ContextKey Context, uint Hash)> _pendingKeys = new();

        internal BinLearnedTemplateSource(HashResolverService resolver, BinPathCasing casing = null)
        {
            _resolver = resolver;
            _casing = casing ?? BinPathCasing.Empty;
        }

        internal int PendingCount => _pending.Count;

        internal void Observe(BinTree tree, InternalHashEvidenceMatcher matcher, string path, string wadPath)
        {
            string file = string.IsNullOrWhiteSpace(path) || path.StartsWith('[') ? null : InternalHashEvidenceMatcher.NormalizeCandidate(path);
            foreach (var (entryHash, item) in tree.Objects)
            {
                string entryPath = KnownName(matcher, InternalHashKind.BinEntries, entryHash);
                Record(matcher, new ContextKey(InternalHashKind.BinEntries, item.ClassHash, 0, Role.Entry), entryHash,
                    item.Properties.Values, file, null, path, wadPath);
                foreach (BinTreeProperty property in item.Properties.Values)
                    Visit(matcher, property, item.ClassHash, item.Properties.Values, Role.Field, property.NameHash, file, entryPath, path, wadPath);
            }

            // Patch BINs carry values in overrides of entries defined elsewhere; their owner class is unknown here.
            foreach (BinTreeDataOverride data in tree.DataOverrides)
            {
                string entryPath = KnownName(matcher, InternalHashKind.BinEntries, data.ObjectPathHash);
                Visit(matcher, data.Property, 0, new[] { data.Property }, Role.Field, data.Property.NameHash, file, entryPath, path, wadPath);
            }
        }

        private void Visit(
            InternalHashEvidenceMatcher matcher,
            BinTreeProperty property,
            uint owner,
            IEnumerable<BinTreeProperty> siblings,
            Role role,
            uint field,
            string file,
            string entryPath,
            string path,
            string wadPath)
        {
            switch (property)
            {
                case BinTreeHash hash when hash.Value != 0:
                    Record(matcher, new ContextKey(InternalHashKind.BinHashes, owner, field, role), hash.Value, siblings, file, entryPath, path, wadPath);
                    break;
                case BinTreeObjectLink link when link.Value != 0:
                    Record(matcher, new ContextKey(InternalHashKind.BinEntries, owner, field, role), link.Value, siblings, file, entryPath, path, wadPath);
                    break;
                case BinTreeStruct structure:
                    foreach (BinTreeProperty child in structure.Properties.Values)
                        Visit(matcher, child, structure.ClassHash, structure.Properties.Values, Role.Field, child.NameHash, file, entryPath, path, wadPath);
                    break;
                case BinTreeContainer container:
                    foreach (BinTreeProperty child in container.Elements)
                        Visit(matcher, child, owner, siblings, Role.ListItem, field, file, entryPath, path, wadPath);
                    break;
                case BinTreeOptional option when option.Value != null:
                    Visit(matcher, option.Value, owner, siblings, Role.Optional, field, file, entryPath, path, wadPath);
                    break;
                case BinTreeMap map:
                    foreach (var pair in map)
                    {
                        // A map key is usually named after its value: expose the value's own fields.
                        IEnumerable<BinTreeProperty> valueFields = pair.Value is BinTreeStruct value ? value.Properties.Values : new[] { pair.Value };
                        Visit(matcher, pair.Key, owner, valueFields, Role.MapKey, field, file, entryPath, path, wadPath);
                        Visit(matcher, pair.Value, owner, siblings, Role.MapValue, field, file, entryPath, path, wadPath);
                    }
                    break;
            }
        }

        private void Record(
            InternalHashEvidenceMatcher matcher,
            ContextKey context,
            uint hash,
            IEnumerable<BinTreeProperty> siblings,
            string file,
            string entryPath,
            string path,
            string wadPath)
        {
            if (matcher.IsRemaining(context.Kind, hash))
            {
                if (_pendingKeys.Add((context, hash)))
                    _pending.Add(new Pending(context.Kind, context, hash, Features(matcher, siblings, file, entryPath).ToArray(), path, wadPath));
                return;
            }

            string name = KnownName(matcher, context.Kind, hash);
            if (name == null) return;
            if (context.Role == Role.MapKey && context.Kind == InternalHashKind.BinHashes && !name.Contains('/'))
            {
                if (!_vocabulary.TryGetValue(context, out var words)) _vocabulary[context] = words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (words.Count < MaximumVocabularyPerContext) words.Add(name);
            }
            if (_learnedSamples.GetValueOrDefault(context) >= MaximumLearnedSamplesPerContext) return;
            _learnedSamples[context] = _learnedSamples.GetValueOrDefault(context) + 1;
            if (!_rules.TryGetValue(context, out var rules)) _rules[context] = rules = new Dictionary<string, Rule>(StringComparer.Ordinal);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Feature feature in Features(matcher, siblings, file, entryPath))
                for (int transform = 0; transform < Transforms.Length; transform++)
                {
                    string value = Transforms[transform](feature.Value);
                    if (string.IsNullOrEmpty(value) || value.Length < 3) continue;
                    int index = name.IndexOf(value, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;
                    string prefix = name[..index];
                    string suffix = name[(index + value.Length)..];
                    if (prefix.Length > MaximumPrefixLength || suffix.Length > MaximumSuffixLength) continue;
                    string key = $"{feature.Key}|{transform}|{prefix.ToLowerInvariant()}|{suffix.ToLowerInvariant()}";
                    if (!seen.Add(key)) continue;
                    if (!rules.TryGetValue(key, out Rule rule))
                    {
                        if (rules.Count >= MaximumRulesPerContext) continue;
                        rules[key] = rule = new Rule { Feature = feature.Key, Transform = transform, Prefix = prefix, Suffix = suffix };
                    }
                    rule.Support++;
                }
        }

        /// <summary>Applies learned rules to every unresolved hash seen, returning the number resolved.</summary>
        internal int Apply(InternalHashEvidenceMatcher matcher)
        {
            int resolved = 0;
            foreach (Pending pending in _pending)
            {
                if (!matcher.IsRemaining(pending.Kind, pending.Hash) || !_rules.TryGetValue(pending.Context, out var rules)) continue;
                foreach (Rule rule in rules.Values.Where(r => r.Support >= MinimumSupport).OrderByDescending(r => r.Support))
                {
                    bool matched = false;
                    foreach (Feature feature in pending.Features)
                    {
                        if (feature.Key != rule.Feature) continue;
                        string value = Transforms[rule.Transform](feature.Value);
                        if (string.IsNullOrEmpty(value)) continue;
                        string candidate = rule.Prefix + value + rule.Suffix;
                        if (Fnv1a.HashLower(candidate) != pending.Hash) continue;
                        // Values often come from lowercase asset paths; the object's own strings spell them.
                        candidate = _casing.Recase(candidate, pending.Features.Where(f => !f.FromFile).Select(f => f.Value).ToList());
                        if (matcher.CheckContextualCandidate(pending.Kind, candidate, pending.Source, pending.SourceWad, pending.Hash, InternalHashEvidence.SemanticReference))
                            resolved++;
                        matched = true;
                        break;
                    }
                    if (matched) break;
                }
            }
            resolved += ApplyVocabulary(matcher);
            _pending.Clear();
            _pendingKeys.Clear();
            return resolved;
        }

        internal InternalHashEvidenceMatcher.NoiseGateResult? LastVocabularyGate { get; private set; }

        /// <summary>
        /// Map keys such as animation events and clips are short designer names reused across
        /// characters (Trail_01, Wings, Idle2). Each context's known keys, with numbered variants,
        /// are hashed once and looked up for its unresolved keys under a noise gate, since every
        /// candidate competes with every unresolved key of that context.
        /// </summary>
        private int ApplyVocabulary(InternalHashEvidenceMatcher matcher)
        {
            var pendingByContext = _pending
                .Where(p => p.Context.Role == Role.MapKey && p.Kind == InternalHashKind.BinHashes && matcher.IsRemaining(p.Kind, p.Hash))
                .GroupBy(p => p.Context)
                .Where(g => _vocabulary.TryGetValue(g.Key, out var words) && words.Count >= MinimumVocabularyPerContext)
                .ToList();
            if (pendingByContext.Count == 0) return 0;

            int resolved = 0;
            matcher.BeginGate("Map key vocabulary");
            try
            {
                foreach (var group in pendingByContext)
                {
                    var candidates = new Dictionary<uint, string>();
                    var ambiguous = new HashSet<uint>();
                    foreach (string word in _vocabulary[group.Key])
                        foreach (string candidate in NumberedVariants(word))
                        {
                            uint hash = Fnv1a.HashLower(candidate);
                            if (candidates.TryGetValue(hash, out string existing))
                            {
                                if (!string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)) ambiguous.Add(hash);
                            }
                            else candidates[hash] = candidate;
                        }

                    var targets = group.Select(p => p).DistinctBy(p => p.Hash).ToList();
                    foreach (Pending pending in targets)
                    {
                        if (ambiguous.Contains(pending.Hash) || !candidates.TryGetValue(pending.Hash, out string name)) continue;
                        if (matcher.CheckVocabularyCandidate(pending.Kind, name, pending.Source, pending.SourceWad, pending.Hash))
                            resolved++;
                    }
                }
            }
            finally
            {
                LastVocabularyGate = matcher.EndGate();
            }
            return resolved;
        }

        // "Trail_01" -> Trail, Trail1..30, Trail_1..30, Trail_01..09 (and the word itself).
        private static IEnumerable<string> NumberedVariants(string word)
        {
            yield return word;
            string stem = word.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd('_');
            if (stem.Length < 2) yield break;
            if (!string.Equals(stem, word, StringComparison.Ordinal)) yield return stem;
            for (int number = 0; number <= 30; number++)
            {
                yield return stem + number;
                yield return stem + "_" + number;
                if (number < 10) yield return stem + "_0" + number;
            }
        }

        private IEnumerable<Feature> Features(InternalHashEvidenceMatcher matcher, IEnumerable<BinTreeProperty> properties, string file, string entryPath)
        {
            foreach (BinTreeProperty property in properties.Take(24))
            {
                string key = property.NameHash.ToString("x8");
                switch (property)
                {
                    case BinTreeString text when !string.IsNullOrWhiteSpace(text.Value) && text.Value.Length <= 256:
                        yield return new Feature("s." + key, text.Value, false);
                        break;
                    case BinTreeOptional { Value: BinTreeString optional } when !string.IsNullOrWhiteSpace(optional.Value) && optional.Value.Length <= 256:
                        yield return new Feature("s." + key, optional.Value, false);
                        break;
                    case BinTreeHash hash when KnownName(matcher, InternalHashKind.BinHashes, hash.Value) is string hashName:
                        yield return new Feature("h." + key, hashName, false);
                        break;
                    case BinTreeObjectLink link when KnownName(matcher, InternalHashKind.BinEntries, link.Value) is string linkName:
                        yield return new Feature("l." + key, linkName, false);
                        break;
                }
            }
            if (file != null) yield return new Feature("file", file, true);
            if (entryPath != null) yield return new Feature("entry", entryPath, false);
        }

        private string KnownName(InternalHashEvidenceMatcher matcher, InternalHashKind kind, uint hash)
        {
            if (hash == 0) return null;
            if (matcher.TryGetVerifiedValue(kind, hash, out string verified)) return verified;
            if (_resolver == null) return null;
            string name = kind == InternalHashKind.BinEntries ? _resolver.ResolveBinEntry(hash) : _resolver.ResolveBinHash(hash);
            return string.IsNullOrEmpty(name) || string.Equals(name, hash.ToString("x8"), StringComparison.OrdinalIgnoreCase) ? null : name;
        }

        private static string Leaf(string value)
        {
            int slash = value.LastIndexOf('/');
            return slash >= 0 ? value[(slash + 1)..] : value;
        }

        private static string StripExtension(string value)
        {
            int dot = value.LastIndexOf('.');
            int slash = value.LastIndexOf('/');
            return dot > slash + 1 ? value[..dot] : value;
        }
    }
}
