using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers.Game;

// Skin metadata and animation graphs can arrive in either order. Keep only their
// identifiers and pending links, isolated by character and skin for this run.
internal sealed class GameAnimationFormIndex
{
    private sealed class Family
    {
        internal HashSet<string> Affixes { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> CheckedAffixes { get; } = new(StringComparer.Ordinal);
        internal Dictionary<ulong, (string Wad, ulong Chunk)> Pending { get; } = new();
        internal string[] Actions;
        internal long Checks;
    }

    private readonly Dictionary<(string Character, string Skin), Family> _families = new();
    private static readonly Regex Context = new(
        @"^data/characters/([^/]+)/(?:skins|animations)/([^/]+)\.bin$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumberedIdentifier = new(
        @"(?:^|[^a-z0-9])([a-z]{2,16}[0-9]{1,3})(?=$|[^a-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly uint AnimationResource = Fnv1a.HashLower("AnimationResourceData");
    private static readonly uint AnimationPath = Fnv1a.HashLower("mAnimationFilePath");
    private static readonly uint MeshProperties = Fnv1a.HashLower("SkinMeshDataProperties");
    private static readonly uint GearData = Fnv1a.HashLower("GearData");
    private static readonly uint Skeleton = Fnv1a.HashLower("skeleton");
    private static readonly uint SimpleSkin = Fnv1a.HashLower("simpleSkin");
    private static readonly uint EquipAnimation = Fnv1a.HashLower("mEquipAnimation");
    private const long CandidateBudget = 100_000;

    internal void Guess(HashGuessEngine engine, BinTree tree, string sourcePath,
        Func<string, IReadOnlyList<string>> animationNames, string wad, ulong chunk,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (engine.RemainingUnknownCount == 0) return;
        Match context = Context.Match(PathUtils.NormalizePath(sourcePath ?? ""));
        if (!context.Success) return;
        string character = context.Groups[1].Value, skin = context.Groups[2].Value;
        var key = (character, skin);
        if (!_families.TryGetValue(key, out Family family)) _families[key] = family = new();
        bool addedLink = false;
        foreach (var obj in tree.Objects.Values) Scope(obj.ClassHash, obj.Properties.Values);
        foreach (var patch in tree.DataOverrides) Visit(patch.Property);
        if (family.Pending.Count == 0 || family.Affixes.Count == 0 || family.Checks >= CandidateBudget) return;
        family.Actions ??= animationNames(character).Select(name => name.EndsWith(".anm", StringComparison.Ordinal) ? name[..^4] : name)
            .SelectMany(name => new[] { name, name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') })
            .Where(name => name.Length is > 0 and <= 96 && !name.Contains('/') && !name.Contains('.'))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string directory = $"assets/characters/{character}/skins/{skin}/animations/";
        foreach (string affix in family.Affixes.Order(StringComparer.Ordinal))
        {
            if (!addedLink && family.CheckedAffixes.Contains(affix)) continue;
            foreach (string action in family.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (family.Checks + 2 > CandidateBudget) return;
                Check(affix + "_" + action);
                Check(action + "_" + affix);
                if (family.Pending.Count == 0) break;
            }
            family.CheckedAffixes.Add(affix);
        }

        void Check(string stem)
        {
            family.Checks++;
            string path = directory + stem + ".anm";
            ulong hash = XxHash64Ext.Hash(path);
            if (!family.Pending.TryGetValue(hash, out var owner)) return;
            if (engine.UnknownHashes.Contains(hash))
                engine.CheckNormalizedParts(directory, stem, ".anm", HashGuessStrategy.AnimationBinLink,
                    owner.Wad, owner.Chunk, expectedHash: hash);
            family.Pending.Remove(hash);
        }
        void Scope(uint type, IEnumerable<BinTreeProperty> properties)
        {
            foreach (var property in properties)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (type == AnimationResource && property.NameHash == AnimationPath &&
                    property is BinTreeWadChunkLink link && engine.UnknownHashes.Contains(link.Value))
                    addedLink |= family.Pending.TryAdd(link.Value, (wad, chunk));
                if (property is BinTreeString text &&
                    (type == MeshProperties && (property.NameHash == Skeleton || property.NameHash == SimpleSkin) ||
                     type == GearData && property.NameHash == EquipAnimation))
                    foreach (Match match in NumberedIdentifier.Matches(text.Value.ToLowerInvariant()))
                    {
                        string value = match.Groups[1].Value;
                        if (value != skin && value != character && family.Affixes.Count < 32) family.Affixes.Add(value);
                    }
                Visit(property);
            }
        }
        void Visit(BinTreeProperty property)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (property)
            {
                case BinTreeStruct structure: Scope(structure.ClassHash, structure.Properties.Values); break;
                case BinTreeContainer container: foreach (var child in container.Elements) Visit(child); break;
                case BinTreeOptional { Value: not null } optional: Visit(optional.Value); break;
                case BinTreeMap map:
                    foreach (var pair in map) { Visit(pair.Key); Visit(pair.Value); }
                    break;
            }
        }
    }
}
