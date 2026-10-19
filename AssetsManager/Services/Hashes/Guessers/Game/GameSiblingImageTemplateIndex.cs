using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Hashes.Guessers.Game;

// Two resolved sibling pairs must attest a layout before it is applied to pending image links.
internal sealed class GameSiblingImageTemplateIndex
{
    private readonly record struct Key(uint Owner, uint Source, uint Target);
    private readonly record struct Template(string Root, string Prefix, string Extension);
    private sealed record Pending(ulong Target, string Source, string[] Labels, string Wad, ulong Chunk);
    private sealed record Sample(string Source, string Target, string[] Labels);
    private sealed class Family
    {
        internal Dictionary<Template, HashSet<string>> Evidence { get; } = new();
        internal Dictionary<(ulong, string), Pending> Pending { get; } = new();
        internal Dictionary<(string, string), Sample> Deferred { get; } = new();
        internal bool Active { get; set; }
    }
    private readonly Dictionary<Key, Family> _families = new();
    private static readonly Regex CamelBoundary = new("([a-z0-9])([A-Z])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PlatformSuffix = new(@"(?:[-_][1-9]x)?[-_](?:pc|mobile)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] Separators = { "", "-", "_" };

    internal void Guess(HashGuessEngine engine, uint owner, IReadOnlyCollection<BinTreeProperty> properties,
        IReadOnlyDictionary<ulong, string> known, string wad, ulong chunk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int linkCount = 0;
        foreach (var property in properties)
            if (property is BinTreeWadChunkLink && ++linkCount == 2) break;
        if (linkCount < 2) return;
        var links = properties.OfType<BinTreeWadChunkLink>().ToArray();
        string[] labels = properties.OfType<BinTreeString>().Select(text => text.Value).Where(IsLabel).ToArray();
        foreach (var source in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = Resolve(source.Value);
            if (!IsImage(sourcePath)) continue;
            foreach (var target in links)
            {
                if (source.NameHash == target.NameHash) continue;
                bool pending = engine.UnknownHashes.Contains(target.Value);
                string targetPath = Resolve(target.Value);
                if (!pending && !IsImage(targetPath)) continue;
                var key = new Key(owner, source.NameHash, target.NameHash);
                if (!_families.TryGetValue(key, out var family)) _families[key] = family = new();
                if (IsImage(targetPath))
                {
                    var sample = new Sample(sourcePath, targetPath, labels);
                    // Keep compact evidence; expand names only for families that actually have a missing link.
                    if (!family.Active) family.Deferred.TryAdd((sourcePath, targetPath), sample);
                    else { Activate(family); Learn(family, sample); }
                }
                if (pending)
                {
                    family.Active = true;
                    var item = new Pending(target.Value, sourcePath, labels, wad, chunk);
                    if (!family.Pending.TryAdd((target.Value, sourcePath), item)) { Activate(family); continue; }
                    try
                    {
                        Activate(family);
                        foreach (var pair in family.Evidence)
                            if (pair.Value.Count >= 2) Check(pair.Key, item);
                    }
                    catch (OperationCanceledException) { family.Pending.Remove((target.Value, sourcePath)); throw; }
                }
            }
        }

        string Resolve(ulong hash) => known.TryGetValue(hash, out string path) ? path :
            engine.Matches.TryGetValue(hash, out var match) ? match.Path : null;

        void Activate(Family family)
        {
            foreach (var pair in family.Deferred.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Learn(family, pair.Value);
                family.Deferred.Remove(pair.Key);
            }
        }

        void Learn(Family family, Sample sample)
        {
            var shape = Shape(sample.Source, sample.Labels);
            string directory = PathUtils.NormalizeSeparators(Path.GetDirectoryName(sample.Target));
            int slash = directory.LastIndexOf('/');
            if (slash <= 0 || !shape.Directories.Contains(directory[(slash + 1)..], StringComparer.Ordinal)) return;
            string name = Path.GetFileNameWithoutExtension(sample.Target);
            string variable = shape.Names.Where(value => name.EndsWith(value, StringComparison.Ordinal))
                .OrderByDescending(value => value.Length).FirstOrDefault();
            if (variable == null) return;
            var template = new Template(directory[..(slash + 1)], name[..^variable.Length], Path.GetExtension(sample.Target));
            if (!family.Evidence.TryGetValue(template, out var evidence)) family.Evidence[template] = evidence = new(StringComparer.Ordinal);
            if (evidence.Add(sample.Source) && evidence.Count == 2)
            {
                try { foreach (var item in family.Pending.Values) Check(template, item); }
                catch (OperationCanceledException) { evidence.Remove(sample.Source); throw; }
            }
        }

        void Check(Template template, Pending item)
        {
            if (!engine.UnknownHashes.Contains(item.Target)) return;
            var shape = Shape(item.Source, item.Labels);
            foreach (string directory in shape.Directories)
            {
                string prefix = template.Root + directory + "/" + template.Prefix;
                foreach (string name in shape.Names)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.CheckNormalizedParts(prefix, name, template.Extension, HashGuessStrategy.BinLinkSibling,
                        item.Wad, item.Chunk, expectedHash: item.Target);
                    if (!engine.UnknownHashes.Contains(item.Target)) return;
                }
            }
        }
    }

    private static bool IsImage(string path) => path != null && path.Contains('/') &&
        (path.EndsWith(".tex", StringComparison.Ordinal) || path.EndsWith(".dds", StringComparison.Ordinal) || path.EndsWith(".png", StringComparison.Ordinal));
    private static bool IsLabel(string value) => !string.IsNullOrWhiteSpace(value) && value.Length is >= 3 and <= 96 &&
        !Guid.TryParse(value, out _) && value.Any(char.IsAsciiLetter) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static (string[] Directories, string[] Names) Shape(string path, IEnumerable<string> labels)
    {
        string stem = PlatformSuffix.Replace(Path.GetFileNameWithoutExtension(path), "");
        string[] sourceWords = stem.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var groups = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (int skip = 0; skip < Math.Min(3, sourceWords.Length); skip++) Add(sourceWords[skip..]);
        foreach (string label in labels)
        {
            string[] words = CamelBoundary.Replace(label, "$1_$2").ToLowerInvariant().Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            for (int count = 1; count <= Math.Min(4, words.Length); count++)
            {
                string[] tail = words[^count..];
                Add(tail);
                foreach (string prefix in sourceWords.Take(2)) Add(new[] { prefix }.Concat(tail).ToArray());
            }
        }
        string sourceDirectory = PathUtils.NormalizeSeparators(Path.GetDirectoryName(path));
        var directories = new HashSet<string>(StringComparer.Ordinal) { sourceDirectory[(sourceDirectory.LastIndexOf('/') + 1)..] };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string[] words in groups.Values)
        {
            directories.Add(string.Concat(words));
            Spell(words, 1, words[0]);
        }
        return (directories.Order(StringComparer.Ordinal).ToArray(), names.Order(StringComparer.Ordinal).ToArray());

        void Add(string[] words)
        {
            if (words.Length is < 1 or > 6 || words.Sum(word => word.Length) > 128 ||
                words.Any(word => word.Length == 0 || !word.All(char.IsAsciiLetterOrDigit))) return;
            groups.TryAdd(string.Join('_', words), words);
        }
        void Spell(string[] words, int index, string value)
        {
            if (index == words.Length) { if (value.Length >= 3) names.Add(value); return; }
            foreach (string separator in Separators) Spell(words, index + 1, value + separator + words[index]);
        }
    }
}
