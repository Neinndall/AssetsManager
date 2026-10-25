using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers.Game;

// Sampler roles share vocabulary, but candidates must match the exact owning WAD link.
internal sealed class GameMaterialTextureIndex
{
    private static readonly string[] Roles = { "matcap", "flow", "mask", "noise", "scroll", "distort", "iridescen" };
    private static readonly uint MaterialClass = Fnv1a.HashLower("StaticMaterialDef");
    private static readonly uint Samplers = Fnv1a.HashLower("samplerValues");
    private static readonly uint TextureName = Fnv1a.HashLower("TextureName");
    private static readonly uint TexturePath = Fnv1a.HashLower("texturePath");
    private static readonly uint DynamicMaterial = Fnv1a.HashLower("dynamicMaterial");
    private static readonly uint DynamicTextures = Fnv1a.HashLower("textures");
    private static readonly uint DynamicOptions = Fnv1a.HashLower("options");
    private static readonly Regex SkinBin = new(@"^(?:data|assets)/characters/([^/]+)/(skins|themes)/([^/]+)\.bin$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CharacterTexture = new(@"^assets/characters/([^/]+)/(?:skins|themes)/([^/]+)/", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly Dictionary<string, string[]> _names;
    private readonly string[] _sharedParticleNames;
    private readonly string[] _rootSkinTextureNames;
    private readonly ConditionalWeakTable<HashGuessEngine, ConcurrentDictionary<Scope, byte>> _scanned = new();
    private readonly record struct Scope(ulong Target, string Directory, string Role);

    internal GameMaterialTextureIndex(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var namesByRole = Roles.ToDictionary(role => role, _ => new HashSet<string>(StringComparer.Ordinal));
        var sharedParticleNames = new HashSet<string>(StringComparer.Ordinal);
        var rootSkinTextureNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = PathUtils.NormalizePath(value);
            if (!path.EndsWith(".tex", StringComparison.Ordinal) && !path.EndsWith(".dds", StringComparison.Ordinal)) continue;
            string name = path[(path.LastIndexOf('/') + 1)..];
            if ((path.StartsWith("assets/shared/materials/", StringComparison.Ordinal) ||
                 path.StartsWith("assets/maps/particles/", StringComparison.Ordinal)) && !name[..^4].Contains('.'))
                sharedParticleNames.Add(name);
            string[] roles = Roles.Where(role => name.Contains(role, StringComparison.Ordinal)).ToArray();
            Match character = CharacterTexture.Match(path);
            if (character.Success && path.LastIndexOf('/') == character.Length - 1)
                rootSkinTextureNames.Add(name);
            if (roles.Length == 0) continue;

            string relative = name;
            if (character.Success)
            {
                string prefix = character.Groups[1].Value + "_", container = character.Groups[2].Value + "_";
                if (relative.StartsWith(prefix, StringComparison.Ordinal)) relative = relative[prefix.Length..];
                if (relative.StartsWith(container, StringComparison.Ordinal)) relative = relative[container.Length..];
            }
            foreach (string role in roles)
            {
                namesByRole[role].Add(name);
                if (relative.Length > 4) namesByRole[role].Add(relative);
            }
        }
        _names = namesByRole.ToDictionary(pair => pair.Key,
            pair => pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        _sharedParticleNames = sharedParticleNames.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        _rootSkinTextureNames = rootSkinTextureNames.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    internal static bool HasUnresolvedContext(HashGuessEngine engine, BinTree tree, string sourcePath, CancellationToken cancellationToken)
    {
        if (tree == null || engine.RemainingUnknownCount == 0 || !IsSkinBin(sourcePath)) return false;
        return Targets(engine, tree, cancellationToken).Any();
    }

    internal static bool IsSkinBin(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) &&
        path.Contains("characters", StringComparison.OrdinalIgnoreCase) && SkinBin.IsMatch(PathUtils.NormalizePath(path));

    internal static bool HasUnresolvedSamplerContext(HashGuessEngine engine, BinTree tree, string sourcePath,
        CancellationToken cancellationToken) => tree != null && IsSkinBin(sourcePath) &&
        SamplerTargets(engine, tree, cancellationToken).Any();

    internal static bool HasUnresolvedDynamicMaterialContext(HashGuessEngine engine, BinTree tree, string sourcePath,
        CancellationToken cancellationToken) => tree != null && engine.RemainingUnknownCount > 0 &&
        IsSkinBin(sourcePath) && DynamicMaterialTargets(engine, tree, cancellationToken).Any();

    internal long GuessDynamicMaterialOptions(HashGuessEngine engine, BinTree tree, string sourcePath, string wad, ulong chunk,
        CancellationToken cancellationToken, long candidateBudget = long.MaxValue)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tree == null || candidateBudget <= 0 || engine.RemainingUnknownCount == 0) return 0;
        Match skin = SkinBin.Match(PathUtils.NormalizePath(sourcePath));
        if (!skin.Success) return 0;
        string directory = $"assets/characters/{skin.Groups[1].Value}/{skin.Groups[2].Value}/{skin.Groups[3].Value}/";
        var scanned = _scanned.GetOrCreateValue(engine);
        var targets = DynamicMaterialTargets(engine, tree, cancellationToken).ToHashSet();
        targets.RemoveWhere(target => !scanned.TryAdd(new Scope(target, directory, "dynamic-material-options"), 0));
        if (targets.Count == 0) return 0;
        long checkedCandidates = 0;
        bool complete = false;
        try
        {
            // Hash each basename once for this directory, retaining exact ownership of every link.
            foreach (string name in _rootSkinTextureNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (targets.Count == 0) break;
                if (checkedCandidates >= candidateBudget) return checkedCandidates;
                checkedCandidates++;
                if (engine.CheckNormalizedParts(directory, name, ReadOnlySpan<char>.Empty,
                    HashGuessStrategy.BinLinkSibling, wad, chunk, expectedHashes: targets))
                    targets.RemoveWhere(target => !engine.UnknownHashes.Contains(target));
            }
            complete = true;
        }
        finally
        {
            if (!complete)
                foreach (ulong target in targets)
                    scanned.TryRemove(new Scope(target, directory, "dynamic-material-options"), out _);
        }
        return checkedCandidates;
    }

    private static IEnumerable<ulong> DynamicMaterialTargets(HashGuessEngine engine, BinTree tree, CancellationToken cancellationToken)
    {
        foreach (var obj in tree.Objects.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj.ClassHash != MaterialClass || !obj.Properties.TryGetValue(DynamicMaterial, out var dynProp) ||
                dynProp is not BinTreeStruct dynMat || !dynMat.Properties.TryGetValue(DynamicTextures, out var texProp) ||
                texProp is not BinTreeContainer swapDefs) continue;
            foreach (var swapDef in swapDefs.Elements.OfType<BinTreeStruct>())
            {
                if (!swapDef.Properties.TryGetValue(DynamicOptions, out var optProp) || optProp is not BinTreeContainer options) continue;
                foreach (var option in options.Elements.OfType<BinTreeStruct>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (option.Properties.TryGetValue(TextureName, out var linkProp) && linkProp is BinTreeWadChunkLink link &&
                        engine.UnknownHashes.Contains(link.Value)) yield return link.Value;
                }
            }
        }
    }

    internal long GuessSharedParticles(HashGuessEngine engine, BinTree tree, string sourcePath, string wad, ulong chunk,
        CancellationToken cancellationToken, long candidateBudget = long.MaxValue)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tree == null || candidateBudget <= 0 || engine.RemainingUnknownCount == 0) return 0;
        Match skin = SkinBin.Match(PathUtils.NormalizePath(sourcePath));
        if (!skin.Success) return 0;
        string directory = $"assets/characters/{skin.Groups[1].Value}/{skin.Groups[2].Value}/{skin.Groups[3].Value}/particles/";
        var scanned = _scanned.GetOrCreateValue(engine);
        long checkedCandidates = 0;
        foreach (ulong target in SamplerTargets(engine, tree, cancellationToken).Distinct())
        {
            var scope = new Scope(target, directory, "shared-particles");
            if (!scanned.TryAdd(scope, 0)) continue;
            bool complete = false;
            try
            {
                foreach (string name in _sharedParticleNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!engine.UnknownHashes.Contains(target)) break;
                    if (checkedCandidates >= candidateBudget) return checkedCandidates;
                    checkedCandidates++;
                    engine.CheckNormalizedParts(directory, name, ReadOnlySpan<char>.Empty,
                        HashGuessStrategy.BinLinkSibling, wad, chunk, expectedHash: target);
                }
                complete = true;
            }
            finally { if (!complete) scanned.TryRemove(scope, out _); }
        }
        return checkedCandidates;
    }

    private static IEnumerable<ulong> SamplerTargets(HashGuessEngine engine, BinTree tree, CancellationToken cancellationToken)
    {
        foreach (var obj in tree.Objects.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj.ClassHash != MaterialClass || !obj.Properties.TryGetValue(Samplers, out var property) ||
                property is not BinTreeContainer samplers) continue;
            foreach (var sampler in samplers.Elements.OfType<BinTreeStruct>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sampler.Properties.TryGetValue(TexturePath, out var path) && path is BinTreeWadChunkLink link &&
                    engine.UnknownHashes.Contains(link.Value)) yield return link.Value;
            }
        }
    }

    internal long Guess(HashGuessEngine engine, BinTree tree, string sourcePath, string wad, ulong chunk,
        CancellationToken cancellationToken, long candidateBudget = long.MaxValue)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tree == null || candidateBudget <= 0 || engine.RemainingUnknownCount == 0) return 0;
        Match skin = SkinBin.Match(PathUtils.NormalizePath(sourcePath));
        if (!skin.Success) return 0;
        string character = skin.Groups[1].Value, container = skin.Groups[3].Value;
        string directory = $"assets/characters/{character}/{skin.Groups[2].Value}/{container}/";
        string[] prefixes = { directory, directory + character + "_", directory + character + "_" + container + "_", directory + "particles/" };
        var scanned = _scanned.GetOrCreateValue(engine);
        long checkedCandidates = 0;
        foreach (var (target, role) in Targets(engine, tree, cancellationToken))
        {
            var scope = new Scope(target, directory, role);
            if (!scanned.TryAdd(scope, 0)) continue;
            bool complete = false;
            try
            {
                foreach (string file in _names[role])
                {
                    if (!engine.UnknownHashes.Contains(target)) break;
                    foreach (string prefix in prefixes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (checkedCandidates >= candidateBudget) return checkedCandidates;
                        checkedCandidates++;
                        engine.CheckNormalizedParts(prefix, file, ReadOnlySpan<char>.Empty,
                            HashGuessStrategy.BinLinkSibling, wad, chunk, expectedHash: target);
                        if (!engine.UnknownHashes.Contains(target)) break;
                    }
                }
                complete = true;
            }
            finally
            {
                // Interrupted scopes must remain eligible when the same engine resumes.
                if (!complete) scanned.TryRemove(scope, out _);
            }
        }
        return checkedCandidates;
    }

    private static IEnumerable<(ulong Target, string Role)> Targets(HashGuessEngine engine, BinTree tree, CancellationToken cancellationToken)
    {
        foreach (var obj in tree.Objects.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj.ClassHash != MaterialClass || !obj.Properties.TryGetValue(Samplers, out var property) || property is not BinTreeContainer samplers) continue;
            foreach (var sampler in samplers.Elements.OfType<BinTreeStruct>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sampler.Properties.TryGetValue(TextureName, out var nameProperty) || nameProperty is not BinTreeString name ||
                    !sampler.Properties.TryGetValue(TexturePath, out var linkProperty) || linkProperty is not BinTreeWadChunkLink link ||
                    !engine.UnknownHashes.Contains(link.Value)) continue;
                foreach (string role in Roles)
                    if (name.Value.Contains(role, StringComparison.OrdinalIgnoreCase)) yield return (link.Value, role);
            }
        }
    }
}
