using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes;

// Cross-file hooks retain names, hashes and provenance rather than parsed BIN trees.
internal sealed class BinEntryPathPatternSource
{
    internal static readonly int[] MapIds = { 11, 12, 21, 22, 30, 33, 35, 453 };
    private static readonly HashSet<uint> ScriptTypes = Types("CharScript", "BuffScript", "LolSpellScript", "LevelControlScript");
    private static readonly HashSet<uint> CharacterTypes = Types("CharacterRecord", "TFTCharacterRecord");
    private static readonly HashSet<uint> EntryPathHashTypes = Types(
        "EsportsBannerConfiguration", "GameModeChampionList", "KillCalloutsViewController",
        "OffScreenPOIViewController", "PingRadialViewController", "PlayerReportViewController",
        "PracticeToolViewController", "RewardGroup", "TFTModeData", "TftPlaybook", "UnitFloatingInfoBarData");
    private static readonly uint CharScriptType = Fnv1a.HashLower("CharScript");
    private static readonly uint SpellType = Fnv1a.HashLower("SpellObject");
    private static readonly uint ScriptDataType = Fnv1a.HashLower("ScriptDataObject");
    private static readonly uint ModeType = Fnv1a.HashLower("GameModeMapData");
    private static readonly uint AudioType = Fnv1a.HashLower("MapAudioDataProperties");
    private static readonly uint UnitPropertyType = Fnv1a.HashLower("TftUnitPropertyDefinition");
    private static readonly uint AnvilType = Fnv1a.HashLower("AnvilData");
    private readonly HashResolverService _resolver;
    private readonly HashSet<string> _characters = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, uint> _entryTypes = new();
    private readonly HashSet<NamedEntry> _scripts = new();
    private readonly HashSet<NamedEntry> _spells = new();
    private readonly HashSet<NamedEntry> _scriptData = new();
    private readonly HashSet<HashReference> _entryPathHashes = new();
    private readonly HashSet<ModeLink> _modeLinks = new();
    private readonly HashSet<uint> _modeOwners = new();
    private sealed record NamedEntry(uint Hash, string Name, string Source, string Wad);
    private sealed record HashReference(uint Hash, string Source, string Wad);
    private sealed record ModeLink(uint Owner, uint Target, uint? Field, string Source, string Wad);

    static BinEntryPathPatternSource() => EntryPathHashTypes.Add(0x409a5657);

    internal BinEntryPathPatternSource(HashResolverService resolver = null) => _resolver = resolver;

    private static HashSet<uint> Types(params string[] names) => names.Select(name => Fnv1a.HashLower(name)).ToHashSet();

    internal static IEnumerable<string> ImmediateEntryCandidates(BinTreeObject entry)
    {
        uint type = entry.ClassHash;
        if (type == AudioType)
            foreach (int map in MapIds) yield return $"Maps/Shipping/Map{map}/Audio";
        else if (type == UnitPropertyType && Text(entry, "name") is string unit)
            yield return $"Maps/Shipping/Map22/UnitProperties/{unit}";
        else if (type == AnvilType && Text(entry, "AugmentNameId") is string augment)
            foreach (int map in MapIds) yield return $"Maps/Shipping/Map{map}/Anvils/{augment}";
        else if (ScriptTypes.Contains(type) && Text(entry, "ScriptName") is string script)
        {
            if (type == CharScriptType)
            {
                if (AsciiStartsWith(script, "charscript"))
                    yield return $"Characters/{script[10..]}/Scripts/{script}";
            }
            else
            {
                foreach (int map in MapIds) yield return $"Maps/Shipping/Map{map}/Scripts/{script}";
                yield return $"Maps/Shipping/Common/Scripts/{script}";
            }
        }
        else if (type == ScriptDataType && Text(entry, "mName") is string data)
        {
            foreach (int map in MapIds) yield return $"Maps/Shipping/Map{map}/ScriptData/{data}";
            for (int set = 1; set < 30; set++) yield return $"Maps/Shipping/Map22/Sets/TFTSet{set}/ScriptData/{data}";
        }
    }

    internal void Observe(BinTree tree, InternalHashEvidenceMatcher matcher, string source, string wad,
        CancellationToken cancellationToken = default)
    {
        foreach (var pair in tree.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint hash = pair.Key;
            BinTreeObject entry = pair.Value;
            uint type = entry.ClassHash;
            if (matcher.IsRemaining(InternalHashKind.BinEntries, hash)) _entryTypes[hash] = type;
            if (CharacterTypes.Contains(type) && Text(entry, "mCharacterName") is string character)
                _characters.Add(character);
            if (ScriptTypes.Contains(type) && Text(entry, "ScriptName") is string script)
            {
                if (type == CharScriptType && matcher.IsRemaining(InternalHashKind.BinEntries, hash))
                    _scripts.Add(new(hash, script, source, wad));
                PromoteEntryHash(matcher, hash, source, wad);
                if (matcher.IsRemaining(InternalHashKind.BinHashes, hash)) _entryPathHashes.Add(new(hash, source, wad));
            }
            if (matcher.IsRemaining(InternalHashKind.BinEntries, hash))
            {
                if (type == SpellType && Text(entry, "mScriptName") is string spell) _spells.Add(new(hash, spell, source, wad));
                if (type == ScriptDataType && Text(entry, "mName") is string data) _scriptData.Add(new(hash, data, source, wad));
            }
            if (type == ModeType)
            {
                _modeOwners.Add(hash);
                foreach (var property in entry.Properties.Values)
                {
                    if (property is BinTreeObjectLink link && matcher.IsRemaining(InternalHashKind.BinEntries, link.Value))
                        _modeLinks.Add(new(hash, link.Value, property.NameHash, source, wad));
                    else if (property is BinTreeContainer { ElementType: BinPropertyType.ObjectLink } list)
                        foreach (var child in list.Elements.OfType<BinTreeObjectLink>())
                            if (matcher.IsRemaining(InternalHashKind.BinEntries, child.Value))
                                _modeLinks.Add(new(hash, child.Value, null, source, wad));
                }
            }
            if (EntryPathHashTypes.Contains(type))
                foreach (var property in entry.Properties.Values) CollectHashes(property);
        }

        void CollectHashes(BinTreeProperty property)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (property is BinTreeHash hash && matcher.IsRemaining(InternalHashKind.BinHashes, hash.Value))
                _entryPathHashes.Add(new(hash.Value, source, wad));
            foreach (var child in Children(property)) CollectHashes(child);
        }
    }

    internal void Apply(InternalHashEvidenceMatcher matcher, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var script in _scripts)
            foreach (string character in _characters)
                if (CheckEntry(script, $"Characters/{character}/Scripts/{script.Name}")) break;
        foreach (var spell in _spells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!matcher.IsRemaining(InternalHashKind.BinEntries, spell.Hash)) continue;
            foreach (string character in _characters.Where(character => AsciiStartsWith(spell.Name, character)))
            {
                string directory = $"Characters/{character}/Spells";
                if (CheckEntry(spell, $"{directory}/{spell.Name}") || CheckEntry(spell, $"{directory}/Attacks/{spell.Name}")) break;
                foreach (string ability in CharacterPrefixes(spell.Name))
                    if (CheckEntry(spell, $"{directory}/{ability}Ability/{spell.Name}")) break;
                if (!matcher.IsRemaining(InternalHashKind.BinEntries, spell.Hash)) break;
            }
        }
        foreach (var data in _scriptData)
            foreach (string character in _characters.Where(character => AsciiStartsWith(data.Name, character)))
                if (CheckEntry(data, $"Characters/{character}/ScriptData/{data.Name}")) break;

        var modes = new HashSet<string>(StringComparer.Ordinal);
        var directories = new Dictionary<uint, string>();
        foreach (uint owner in _modeOwners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Resolve(matcher, InternalHashKind.BinEntries, owner);
            int separator = path?.IndexOf("/Modes/", StringComparison.Ordinal) ?? -1;
            if (separator < 0) continue;
            directories[owner] = path[..separator];
            modes.Add(path[(separator + 7)..]);
        }
        foreach (var link in _modeLinks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!matcher.IsRemaining(InternalHashKind.BinEntries, link.Target) || !directories.TryGetValue(link.Owner, out string directory)) continue;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (link.Field is uint field && Resolve(matcher, InternalHashKind.BinFields, field) is string fieldName)
                names.Add(fieldName.Length > 1 && fieldName[0] == 'm' && IsUpper(fieldName[1]) ? fieldName[1..] : fieldName);
            if (_entryTypes.TryGetValue(link.Target, out uint type) && Resolve(matcher, InternalHashKind.BinTypes, type) is string className)
                foreach (string prefix in WordPrefixes(className)) names.Add(prefix);
            foreach (string name in names)
            {
                foreach (string mode in modes)
                    if (CheckLink($"{directory}/GameModeConfigs/{name}_{mode}")) break;
                if (!matcher.IsRemaining(InternalHashKind.BinEntries, link.Target)) break;
                if (CheckLink($"{directory}/GameModeConfigs/{name}") || CheckLink($"{directory}/Configs/{name}") ||
                    CheckLink($"{directory}/{name}") || CheckLink($"Maps/Shipping/Common/{name}") || CheckLink($"UX/HUD/Globals/{name}")) break;
            }
            bool CheckLink(string value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return matcher.CheckContextualCandidate(InternalHashKind.BinEntries, value, link.Source, link.Wad, link.Target);
            }
        }
        foreach (var hash in _entryPathHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PromoteEntryHash(matcher, hash.Hash, hash.Source, hash.Wad);
        }

        bool CheckEntry(NamedEntry entry, string value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return !matcher.IsRemaining(InternalHashKind.BinEntries, entry.Hash) ||
                matcher.CheckContextualCandidate(InternalHashKind.BinEntries, value, entry.Source, entry.Wad, entry.Hash);
        }
    }

    private void PromoteEntryHash(InternalHashEvidenceMatcher matcher, uint hash, string source, string wad)
    {
        if (matcher.IsRemaining(InternalHashKind.BinHashes, hash) && Resolve(matcher, InternalHashKind.BinEntries, hash) is string path)
            matcher.CheckContextualCandidate(InternalHashKind.BinHashes, path, source, wad, hash);
    }

    private string Resolve(InternalHashEvidenceMatcher matcher, InternalHashKind kind, uint hash)
    {
        if (matcher.TryGetVerifiedValue(kind, hash, out string verified)) return verified;
        string value = kind switch
        {
            InternalHashKind.BinEntries => _resolver?.ResolveBinEntry(hash),
            InternalHashKind.BinFields => _resolver?.ResolveBinField(hash),
            InternalHashKind.BinTypes => _resolver?.ResolveBinType(hash),
            _ => null
        };
        return !string.IsNullOrEmpty(value) && Fnv1a.HashLower(value) == hash ? value : null;
    }

    private static string Text(BinTreeObject entry, string field) =>
        entry.Properties.TryGetValue(Fnv1a.HashLower(field), out var property) && property is BinTreeString text ? text.Value : null;

    private static IEnumerable<BinTreeProperty> Children(BinTreeProperty property) => property switch
    {
        BinTreeStruct structure => structure.Properties.Values,
        BinTreeContainer container => container.Elements,
        BinTreeOptional { Value: not null } optional => new[] { optional.Value },
        BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
        _ => Array.Empty<BinTreeProperty>()
    };

    internal static IEnumerable<string> CharacterPrefixes(string name)
    {
        for (int i = 1; i < name.Length; i++)
            if (!char.IsLowSurrogate(name[i])) yield return name[..i];
    }

    internal static IEnumerable<string> WordPrefixes(string name)
    {
        for (int i = 1; i < name.Length; i++)
            if (IsUpper(name[i]) && (name[i - 1] is >= 'a' and <= 'z' || char.IsAsciiDigit(name[i - 1]))) yield return name[..i];
        yield return name;
    }

    private static bool IsUpper(char value) => value is >= 'A' and <= 'Z';
    private static char Lower(char value) => IsUpper(value) ? (char)(value + ('a' - 'A')) : value;
    private static bool AsciiStartsWith(string value, string prefix)
    {
        if (value.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (Lower(value[i]) != Lower(prefix[i])) return false;
        return true;
    }

    internal static IEnumerable<string> ResourceKeyCandidates(string basename)
    {
        foreach (bool skin in new[] { true, false })
        {
            if (ReplaceTokens(basename, skin, "") is not string value) continue;
            if (WithoutVersion(value) is string unversioned) yield return unversioned;
            yield return value;
        }
        if (ReplaceTokens(basename, true, "Base_") is string replaced) yield return replaced;

        string[] tokens = basename.Split('_');
        for (int i = 1; i < tokens.Length; i++) yield return string.Join("_", tokens, i, tokens.Length - i);
        if (tokens.Length > 1)
            for (int i = 0; i < tokens.Length; i++) yield return WithoutTokens(i, 1);
        if (tokens.Length > 2)
            for (int i = 0; i < tokens.Length - 1; i++) yield return WithoutTokens(i, 2);

        string WithoutTokens(int start, int count) => string.Join("_", tokens.Take(start).Concat(tokens.Skip(start + count)));
    }

    private static string ReplaceTokens(string value, bool skin, string replacement)
    {
        var result = new StringBuilder(value.Length);
        bool found = false;
        for (int i = 0; i < value.Length;)
        {
            int length = TokenLength(value, i, skin);
            if (length == 0) result.Append(value[i++]);
            else
            {
                result.Append(replacement);
                i += length;
                found = true;
            }
        }
        return found ? result.ToString() : null;
    }

    private static int TokenLength(string value, int at, bool skin)
    {
        string token = skin ? "skin" : "base_";
        if (value.Length - at < token.Length) return 0;
        for (int i = 0; i < token.Length; i++) if (Lower(value[at + i]) != token[i]) return 0;
        if (!skin) return token.Length;
        int end = at + 4;
        while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
        return end > at + 4 && end < value.Length && value[end] == '_' ? end - at + 1 : 0;
    }

    private static string WithoutVersion(string value)
    {
        int underscore = value.LastIndexOf('_');
        if (underscore < 0) return null;
        int start = underscore + 1;
        if (start < value.Length && value[start] is 'v' or 'V') start++;
        return start < value.Length && value.AsSpan(start).IndexOfAnyExceptInRange('0', '9') < 0 ? value[..underscore] : null;
    }
}
