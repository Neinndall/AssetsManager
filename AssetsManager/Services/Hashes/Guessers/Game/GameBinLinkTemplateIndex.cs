using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Hashes.Guessers.Game;

// Templates are attested by a resolved link of the same class and field, then checked
// against the exact unresolved link. Pending scopes retain strings, never BIN trees.
internal sealed class GameBinLinkTemplateIndex
{
    private readonly record struct IdentifierKey(uint Owner, uint Field, byte Kind);
    private readonly record struct Identifier(IdentifierKey Key, string Value);
    private readonly record struct Key(uint Class, uint Link, IdentifierKey Text, int Variant);
    private readonly record struct Template(string Prefix, string Suffix);
    private sealed record Pending(ulong Target, string Value, string Wad, ulong Chunk);
    private sealed class Family
    {
        internal HashSet<Template> Templates { get; } = new();
        internal Dictionary<(ulong Target, string Value), Pending> Pending { get; } = new();
    }
    private readonly Dictionary<Key, Family> _families = new();
    private static readonly Regex CamelBoundary = new("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal void Guess(HashGuessEngine engine, BinTree tree, IReadOnlyDictionary<ulong, string> known,
        Func<uint, string> resolveName, Func<ulong, string> resolveWideName,
        string wad, ulong chunk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (engine.RemainingUnknownCount == 0) return;
        foreach (var pair in tree.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new List<Identifier>();
            AddNamed(context, new IdentifierKey(0, 0, 1), resolveName?.Invoke(pair.Key));
            Scope(pair.Value.ClassHash, pair.Value.Properties.Values, context);
        }

        void Scope(uint type, IEnumerable<BinTreeProperty> properties, List<Identifier> inherited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var props = properties as IReadOnlyCollection<BinTreeProperty> ?? properties.ToArray();
            var identifiers = new List<Identifier>(inherited);
            foreach (var p in props)
            {
                if (p is BinTreeString text && IsIdentifier(text.Value))
                    identifiers.Add(new Identifier(new IdentifierKey(type, p.NameHash, 0), text.Value));
                else if (p is BinTreeHash64 name)
                    AddNamed(identifiers, new IdentifierKey(type, p.NameHash, 0), resolveWideName?.Invoke(name.Value));
            }
            foreach (var link in props.OfType<BinTreeWadChunkLink>())
            {
                bool unresolved = engine.UnknownHashes.Contains(link.Value);
                bool resolved = known.TryGetValue(link.Value, out string path);
                if (!unresolved && !resolved) continue;
                foreach (var identifier in identifiers)
                {
                    for (int variant = 0; variant < 5; variant++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string value = Transform(identifier.Value, variant);
                        // Keep the variant key even when another transformation is identical: its
                        // spelling can diverge for a later identifier (camelCase vs snake_case).
                        if (value.Length < 3) continue;
                        var key = new Key(type, link.NameHash, identifier.Key, variant);
                        if (resolved && TryLearn(path, value, out Template template))
                            Learn(key, template);
                        if (unresolved && engine.UnknownHashes.Contains(link.Value))
                            Remember(key, new Pending(link.Value, value, wad, chunk));
                    }
                }
            }
            foreach (var p in props) Visit(p, identifiers);
        }
        void Visit(BinTreeProperty p, List<Identifier> inherited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (p)
            {
                case BinTreeStruct s: Scope(s.ClassHash, s.Properties.Values, inherited); break;
                case BinTreeContainer c: foreach (var child in c.Elements) Visit(child, inherited); break;
                case BinTreeOptional { Value: not null } o: Visit(o.Value, inherited); break;
                case BinTreeMap m:
                    foreach (var pair in m)
                    {
                        var context = inherited;
                        string name = pair.Key switch
                        {
                            BinTreeHash hash => resolveName?.Invoke(hash.Value),
                            BinTreeString text => text.Value,
                            _ => null
                        };
                        if (IsNamedIdentifier(name))
                        {
                            context = new List<Identifier>(inherited);
                            AddNamed(context, new IdentifierKey(0, p.NameHash, 2), name);
                        }
                        Visit(pair.Key, inherited);
                        Visit(pair.Value, context);
                    }
                    break;
            }
        }
        Family GetFamily(Key key)
        {
            if (!_families.TryGetValue(key, out var family)) _families[key] = family = new Family();
            return family;
        }
        void Learn(Key key, Template template)
        {
            var family = GetFamily(key);
            if (!family.Templates.Add(template)) return;
            try
            {
                foreach (var pending in family.Pending.Values) Check(template, pending);
            }
            catch (OperationCanceledException)
            {
                family.Templates.Remove(template);
                throw;
            }
        }
        void Remember(Key key, Pending pending)
        {
            var family = GetFamily(key);
            var pendingKey = (pending.Target, pending.Value);
            if (!family.Pending.TryAdd(pendingKey, pending)) return;
            try
            {
                foreach (var template in family.Templates) Check(template, pending);
            }
            catch (OperationCanceledException)
            {
                family.Pending.Remove(pendingKey);
                throw;
            }
        }
        void Check(Template template, Pending pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!engine.UnknownHashes.Contains(pending.Target)) return;
            engine.CheckNormalizedParts(template.Prefix, pending.Value, template.Suffix,
                HashGuessStrategy.BinLinkSibling, pending.Wad, pending.Chunk, expectedHash: pending.Target);
        }
    }

    private static bool TryLearn(string path, string value, out Template template)
    {
        template = default;
        int start = path.LastIndexOf('/') + 1;
        int at = path.IndexOf(value, start, StringComparison.Ordinal);
        if (at < start || (at > start && char.IsAsciiLetterOrDigit(path[at - 1])) ||
            (at + value.Length < path.Length && char.IsAsciiLetterOrDigit(path[at + value.Length]))) return false;
        template = new Template(path[..at], path[(at + value.Length)..]);
        return true;
    }
    private static void AddNamed(List<Identifier> identifiers, IdentifierKey key, string name)
    {
        if (!IsNamedIdentifier(name)) return;
        name = name[(name.LastIndexOf('/') + 1)..];
        if (IsIdentifier(name)) identifiers.Add(new Identifier(key, name));
    }
    private static bool IsNamedIdentifier(string name) => !string.IsNullOrWhiteSpace(name) &&
        !name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        !(name.Length is 8 or 16 && name.All(char.IsAsciiHexDigit));
    private static bool IsIdentifier(string value) => value.Length is >= 3 and <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static string Transform(string value, int variant) => variant switch
    {
        1 => CamelBoundary.Replace(value, "$1_$2").ToLowerInvariant(),
        2 => value.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant(),
        3 => value.Contains('_') ? value[..value.LastIndexOf('_')].ToLowerInvariant() : value.ToLowerInvariant(),
        4 => string.Join('_', value.Split('_').SkipLast(2)).ToLowerInvariant(),
        _ => value.ToLowerInvariant()
    };
}
