using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Loading;

/// <summary>Classifies extracted BINs by their object types, including files retaining WAD hash names.</summary>
internal static class VfxFolderCatalog
{
    internal sealed record BrowserCatalog(
        IReadOnlyList<VfxSkinItem> Entries,
        IReadOnlyList<VfxBrowserFolder> Roots,
        IReadOnlyList<MapSceneSource> MapSources,
        IReadOnlyList<MapVariantData> MapVariants);

    private sealed record SpellDiscovery(
        string Character,
        string Name,
        string ObjectPath,
        uint PathHash,
        string BinPath,
        VfxSpellPreview Preview);

    private sealed record MapDeclaration(
        uint ClassHash,
        IReadOnlyList<MapVariantData> Variants);

    private sealed record ScanResult(
        IReadOnlyList<VfxSkinItem> Entries,
        IReadOnlyList<SpellDiscovery> Spells,
        IReadOnlyList<MapVariantData> MapVariants);

    internal static IReadOnlyList<VfxSkinItem> Scan(string root, CancellationToken cancellationToken, LogService log = null)
        => ScanCore(root, cancellationToken, null, log).Entries;

    internal static BrowserCatalog ScanBrowser(
        string root,
        CancellationToken cancellationToken,
        Func<uint, string> resolveBinEntry,
        LogService log = null)
    {
        ScanResult scan = ScanCore(root, cancellationToken, resolveBinEntry, log);
        IReadOnlyList<MapSceneSource> mapSources = DiscoverMapSources(root, cancellationToken);
        IReadOnlyList<VfxBrowserFolder> roots = BuildBrowserTree(root, scan.Entries, scan.Spells, mapSources);
        return new BrowserCatalog(scan.Entries, roots, mapSources, scan.MapVariants);
    }

    private static ScanResult ScanCore(
        string root,
        CancellationToken cancellationToken,
        Func<uint, string> resolveBinEntry,
        LogService log)
    {
        var skins = new List<VfxSkinItem>();
        var effects = new List<VfxSkinItem>();
        var spells = new List<SpellDiscovery>();
        var mapDeclarations = new List<MapDeclaration>();
        var mapVariantParser = new MapVariantParser();
        uint skinClass = Fnv1a.HashLower("SkinCharacterDataProperties");
        uint systemClass = Fnv1a.HashLower("VfxSystemDefinitionData");
        uint spellClass = Fnv1a.HashLower("SpellObject");

        foreach (string path in Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = File.OpenRead(path);
                var tree = new BinTree(stream);
                bool skin = tree.Objects.Values.Any(item =>
                    item.ClassHash == skinClass && HasPreviewableSkin(item));
                bool system = tree.Objects.Values.Any(item => item.ClassHash == systemClass);

                foreach (BinTreeObject mapObject in tree.Objects.Values.Where(item =>
                             item.ClassHash == MapVariantParser.MapClass ||
                             item.ClassHash == MapVariantParser.MapSkinClass ||
                             item.ClassHash == MapVariantParser.MapContainerClass))
                {
                    IReadOnlyList<MapVariantData> variants = mapVariantParser.Parse(tree, mapObject.PathHash);
                    if (variants.Count > 0)
                        mapDeclarations.Add(new MapDeclaration(mapObject.ClassHash, variants));
                }

                if (resolveBinEntry != null)
                {
                    foreach (BinTreeObject spell in tree.Objects.Values.Where(item => item.ClassHash == spellClass))
                    {
                        string objectPath = resolveBinEntry(spell.PathHash);
                        if (!TryParseSpellPath(objectPath, out string character, out string spellName)) continue;
                        spells.Add(new SpellDiscovery(
                            character,
                            spellName,
                            objectPath,
                            spell.PathHash,
                            Path.GetFullPath(path),
                            VfxSpellPreviewReader.Read(spell)));
                    }
                }

                if (!skin && !system) continue;
                string stem = Path.GetFileNameWithoutExtension(path);
                int index = stem.StartsWith("skin", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(stem.AsSpan(4), out int id) ? id : int.MaxValue;
                var entry = new VfxSkinItem
                {
                    DisplayName = Path.GetRelativePath(root, path),
                    BinPath = Path.GetFullPath(path),
                    SkinIndex = index
                };
                (skin ? skins : effects).Add(entry);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                log?.LogWarning($"Unable to classify VFX BIN '{path}': {ex.Message}");
            }
        }

        string rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string rootStem = rootName;
        if (rootStem.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
            rootStem = rootStem[..^11];
        else if (rootStem.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
            rootStem = rootStem[..^4];
        rootStem = rootStem.Trim().ToLowerInvariant();

        int CalculateScore(VfxSkinItem item)
        {
            string normalized = item.DisplayName.Replace('\\', '/').ToLowerInvariant();
            int score = 100;

            if (!string.IsNullOrEmpty(rootStem))
            {
                if (normalized.Contains($"/characters/{rootStem}/skins/") ||
                    normalized.StartsWith($"data/characters/{rootStem}/skins/") ||
                    normalized.StartsWith($"assets/characters/{rootStem}/skins/"))
                {
                    score = 0;
                }
                else if (normalized.Contains($"/{rootStem}/skins/"))
                {
                    score = 10;
                }
            }

            if (normalized.Contains("faerie") || normalized.Contains("critter") ||
                normalized.Contains("pet") || normalized.Contains("turret") ||
                normalized.Contains("trap") || normalized.Contains("dummy") ||
                normalized.Contains("clone") || normalized.Contains("projectile") ||
                normalized.Contains("minion") || normalized.Contains("companion"))
            {
                score += 500;
            }

            return score;
        }

        VfxSkinItem[] entries = (skins.Count > 0 ? skins : effects)
            .OrderBy(CalculateScore)
            .ThenBy(item => item.SkinIndex)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int MapDeclarationScore(MapDeclaration declaration)
        {
            int score = declaration.ClassHash == MapVariantParser.MapClass
                ? 0
                : declaration.ClassHash == MapVariantParser.MapSkinClass ? 100 : 200;
            MapVariantData opening = MapVariantData.Opening(declaration.Variants);
            string logical = opening?.Map?.Value?.Replace('\\', '/').ToLowerInvariant() ?? string.Empty;
            if (!string.IsNullOrEmpty(rootStem) &&
                logical.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment.Equals(rootStem, StringComparison.OrdinalIgnoreCase)))
            {
                score -= 50;
            }
            return score;
        }

        IReadOnlyList<MapVariantData> selectedVariants = mapDeclarations
            .OrderBy(MapDeclarationScore)
            .ThenBy(declaration => MapVariantData.Opening(declaration.Variants)?.Map?.Value, StringComparer.OrdinalIgnoreCase)
            .Select(declaration => declaration.Variants)
            .FirstOrDefault() ?? Array.Empty<MapVariantData>();

        return new ScanResult(entries, spells.ToArray(), selectedVariants);
    }

    private static IReadOnlyList<MapSceneSource> DiscoverMapSources(
        string root,
        CancellationToken cancellationToken)
    {
        var sources = new Dictionary<string, MapSceneSource>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(root, "*.mapgeo", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MapPath.TryFromGeometryFile(path, out MapPath map))
                continue;

            sources[map.Value] = new MapSceneSource(
                map,
                Path.GetFullPath(path),
                Path.GetFullPath(root));
        }

        return sources.Values
            .OrderBy(source => source.Map.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasPreviewableSkin(BinTreeObject skin)
    {
        uint meshPropertiesHash = Fnv1a.HashLower("skinMeshProperties");
        uint simpleSkinHash = Fnv1a.HashLower("simpleSkin");
        if (!skin.Properties.TryGetValue(meshPropertiesHash, out BinTreeProperty meshProperty) ||
            meshProperty is not BinTreeStruct meshProperties ||
            !meshProperties.Properties.TryGetValue(simpleSkinHash, out BinTreeProperty asset))
        {
            return false;
        }

        if (asset is BinTreeOptional optional)
            asset = optional.Value;

        // LTK's SkinViewport refuses a preview when no mesh asset is authored. Keep those
        // root/base declarations out of VFX Studio's selectable Skins instead of loading an empty skin.
        return asset switch
        {
            BinTreeString text => !string.IsNullOrWhiteSpace(text.Value),
            BinTreeWadChunkLink link => link.Value != 0,
            BinTreeU64 value => value.Value != 0,
            BinTreeHash hash => hash.Value != 0,
            BinTreeObjectLink link => link.Value != 0,
            BinTreeU32 value => value.Value != 0,
            _ => false
        };
    }

    private static IReadOnlyList<VfxBrowserFolder> BuildBrowserTree(
        string root,
        IReadOnlyList<VfxSkinItem> entries,
        IReadOnlyList<SpellDiscovery> spellDiscoveries,
        IReadOnlyList<MapSceneSource> mapSources)
    {
        string rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var mapRoot = new VfxBrowserFolder("MapGeometry", VfxBrowserFolderKind.Root)
        {
            IsExpanded = true
        };
        foreach (MapSceneSource source in mapSources ?? Array.Empty<MapSceneSource>())
        {
            string title = Path.GetFileName(source.SelectedGeometryPath);
            mapRoot.Children.Add(new MapBrowserNode(
                string.IsNullOrWhiteSpace(title) ? source.Map.Value : title,
                MapBrowserNodeKind.MapFile,
                source.Map.Value,
                source));
        }

        var charactersRoot = new VfxBrowserFolder("Characters", VfxBrowserFolderKind.Root)
        {
            IsExpanded = true
        };
        var characters = new Dictionary<string, VfxBrowserFolder>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<(string Character, string Group), VfxBrowserFolder>();

        VfxBrowserFolder Character(string rawName)
        {
            string key = string.IsNullOrWhiteSpace(rawName) ? "Other" : rawName;
            if (characters.TryGetValue(key, out VfxBrowserFolder held)) return held;
            string kind = IsCompanion(key, rootName) ? "Companion" : "Character";
            held = new VfxBrowserFolder(PrettyCharacterName(key), VfxBrowserFolderKind.Character, kind);
            characters[key] = held;
            return held;
        }

        VfxBrowserFolder Group(string character, string title)
        {
            var key = (character.ToLowerInvariant(), title);
            if (groups.TryGetValue(key, out VfxBrowserFolder held)) return held;
            held = new VfxBrowserFolder(title, VfxBrowserFolderKind.Group);
            groups[key] = held;
            Character(character).Children.Add(held);
            return held;
        }

        foreach (VfxSkinItem entry in entries)
        {
            string[] segments = Split(entry.DisplayName);
            int characterAt = IndexOf(segments, "characters");
            string character = characterAt >= 0 && characterAt + 1 < segments.Length
                ? segments[characterAt + 1]
                : "Other";
            int areaAt = characterAt >= 0 ? characterAt + 2 : -1;
            string area = areaAt >= 0 && areaAt < segments.Length
                ? segments[areaAt].ToLowerInvariant()
                : string.Empty;

            switch (area)
            {
                case "skins":
                    entry.BrowserTitle = SkinTitle(entry);
                    Group(character, "Skins").Children.Add(entry);
                    break;
                case "themes":
                    // Theme BINs are support/resource layers for the authored skins, not separate
                    // preview choices. Keep them in the catalog so skin resolution can consume
                    // their data, but do not duplicate the same model under a visible Themes tree.
                    break;
                case "animations":
                    entry.BrowserTitle = PrettySegment(Path.GetFileNameWithoutExtension(entry.BinPath));
                    Group(character, "Animation Data").Children.Add(entry);
                    break;
                case "spells":
                    entry.BrowserTitle = RelativeTitle(segments, areaAt + 1, entry.BinPath);
                    Group(character, "Spell Data").Children.Add(entry);
                    break;
                default:
                    entry.BrowserTitle = RelativeTitle(segments, Math.Max(0, areaAt), entry.BinPath);
                    Group(character, "VFX Data").Children.Add(entry);
                    break;
            }
        }

        foreach (IGrouping<string, SpellDiscovery> owner in spellDiscoveries
                     .GroupBy(spell => spell.Character, StringComparer.OrdinalIgnoreCase))
        {
            var skinsKey = (owner.Key.ToLowerInvariant(), "Skins");
            if (!groups.TryGetValue(skinsKey, out VfxBrowserFolder skinsFolder)) continue;

            var discovered = owner
                .GroupBy(spell => spell.PathHash)
                .Select(group =>
                {
                    SpellDiscovery[] declarations = group.ToArray();
                    SpellDiscovery first = declarations[0];
                    VfxSpellAvailability availability = declarations.Length == 1
                        ? VfxSpellPreviewReader.AvailabilityOf(first.Preview, includeImpact: true)
                        : VfxSpellAvailability.Ambiguous;
                    return (First: first, Count: declarations.Length, Availability: availability);
                })
                .OrderBy(item => item.First.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (VfxSkinItem skin in skinsFolder.Children.OfType<VfxSkinItem>())
            {
                var nestedGroups = new Dictionary<string, VfxBrowserFolder>(StringComparer.OrdinalIgnoreCase);
                foreach (var found in discovered)
                {
                    SpellDiscovery spell = found.First;
                    string displayName = spell.Name;
                    ICollection<object> target = skin.SpellItems;
                    int slash = spell.Name.IndexOf('/');
                    if (slash > 0 && slash + 1 < spell.Name.Length)
                    {
                        string groupName = spell.Name[..slash];
                        if (!nestedGroups.TryGetValue(groupName, out VfxBrowserFolder groupFolder))
                        {
                            groupFolder = new VfxBrowserFolder(groupName, VfxBrowserFolderKind.Group);
                            nestedGroups[groupName] = groupFolder;
                            skin.SpellItems.Add(groupFolder);
                        }
                        target = groupFolder.Children;
                        displayName = spell.Name[(slash + 1)..];
                    }

                    target.Add(new VfxSpellBrowserItem
                    {
                        Owner = skin,
                        Name = displayName,
                        ObjectPath = spell.ObjectPath,
                        PathHash = spell.PathHash,
                        BinPath = spell.BinPath,
                        DeclarationCount = found.Count,
                        Preview = spell.Preview,
                        Availability = found.Availability
                    });
                }

                if (skin.SpellItems.Count > 0 &&
                    !skin.Sections.Any(section => section.Kind == VfxBrowserSectionKind.Spells))
                {
                    skin.Sections.Add(new VfxBrowserSection(skin, "Spells", VfxBrowserSectionKind.Spells));
                }
            }
        }

        foreach (VfxBrowserFolder character in characters.Values.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase))
        {
            SortCharacterGroups(character);
            charactersRoot.Children.Add(character);
        }

        var roots = new List<VfxBrowserFolder>(2);
        if (mapRoot.Children.Count > 0)
            roots.Add(mapRoot);
        if (charactersRoot.Children.Count > 0)
            roots.Add(charactersRoot);
        return roots;
    }

    private static void SortCharacterGroups(VfxBrowserFolder character)
    {
        static int Rank(string title) => title switch
        {
            "Skins" => 0,
            "Animation Data" => 1,
            "Spell Data" => 2,
            _ => 3
        };

        object[] ordered = character.Children
            .OrderBy(item => item is VfxBrowserFolder folder ? Rank(folder.Title) : int.MaxValue)
            .ThenBy(item => item is VfxBrowserFolder folder ? folder.Title : string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        character.Children.Clear();
        foreach (object item in ordered) character.Children.Add(item);
    }

    private static bool TryParseSpellPath(string objectPath, out string character, out string spellName)
    {
        character = null;
        spellName = null;
        if (string.IsNullOrWhiteSpace(objectPath)) return false;
        string[] segments = Split(objectPath);
        if (segments.Length < 4 ||
            !segments[0].Equals("Characters", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("Spells", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        character = segments[1];
        spellName = string.Join('/', segments.Skip(3));
        return !string.IsNullOrWhiteSpace(character) && !string.IsNullOrWhiteSpace(spellName);
    }

    private static string[] Split(string path)
        => (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static int IndexOf(IReadOnlyList<string> segments, string wanted)
    {
        for (int index = 0; index < segments.Count; index++)
            if (segments[index].Equals(wanted, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private static string SkinTitle(VfxSkinItem entry)
        => entry.SkinIndex != int.MaxValue
            ? $"Skin {entry.SkinIndex}"
            : PrettySegment(Path.GetFileNameWithoutExtension(entry.BinPath));

    private static string RelativeTitle(string[] segments, int start, string fallbackPath)
    {
        if (start >= 0 && start < segments.Length)
        {
            string[] held = segments.Skip(start).ToArray();
            held[^1] = Path.GetFileNameWithoutExtension(held[^1]);
            return string.Join(" / ", held.Select(PrettySegment));
        }
        return PrettySegment(Path.GetFileNameWithoutExtension(fallbackPath));
    }

    private static bool IsCompanion(string character, string rootName)
        => character.StartsWith("PetChibi", StringComparison.OrdinalIgnoreCase) ||
           rootName.Contains("companion", StringComparison.OrdinalIgnoreCase);

    private static string PrettyCharacterName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Other";
        if (value.StartsWith("petchibi", StringComparison.OrdinalIgnoreCase) && value.Length > 8)
            return "PetChibi" + UpperFirst(value[8..]);
        return UpperFirst(value);
    }

    private static string PrettySegment(string value)
        => string.IsNullOrWhiteSpace(value) ? "Other" : UpperFirst(value.Replace('_', ' '));

    private static string UpperFirst(string value)
        => string.IsNullOrEmpty(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];
}
