using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes
{
    internal static class BinContentEvidenceSource
    {
        private static readonly Dictionary<string, string> SharedBufferLeaves = new()
        {
            ["CharacterPerDrawVertexCB"] = "CharacterPerDrawVS",
            ["PostEffectPixelCB"] = "PostEffects",
            ["FontVertexCB"] = "FontRendering",
            ["VFXDynamicPerParticleInstanceCBVS"] = "VFXDynamicPerParticleVS",
            ["VFXDynamicPerParticleInstanceCBPS"] = "VFXDynamicPerParticlePS"
        };

        private static void VisitBinStrings(BinTree tree, Action<string> check)
        {
            foreach (string dependency in tree.Dependencies) check(dependency);
            foreach (var item in tree.Objects.Values)
                foreach (var property in item.Properties.Values) Visit(property);
            foreach (var item in tree.DataOverrides)
            {
                check(item.PropertyPath);
                Visit(item.Property);
            }

            void Visit(BinTreeProperty property)
            {
                switch (property)
                {
                    case BinTreeString text: check(text.Value); break;
                    case BinTreeStruct structure:
                        foreach (var child in structure.Properties.Values) Visit(child);
                        break;
                    case BinTreeContainer container:
                        foreach (var child in container.Elements) Visit(child);
                        break;
                    case BinTreeOptional option when option.Value != null: Visit(option.Value); break;
                    case BinTreeMap map:
                        foreach (var child in map) { Visit(child.Key); Visit(child.Value); }
                        break;
                }
            }
        }

        internal static void ScanBinContextualMatches(Stream stream, InternalHashEvidenceMatcher matcher, string path, string wadPath, HashResolverService resolver)
        {
            var tree = new BinTree(stream);
            MatchBinContentEvidence(tree, matcher, path, wadPath, resolver);
        }

        internal static void MatchBinContentEvidence(
            BinTree tree,
            InternalHashEvidenceMatcher matcher,
            string path,
            string wadPath = null,
            HashResolverService resolver = null,
            IReadOnlySet<string> selectedSubMethods = null)
        {
            bool ShouldRun(string id) => selectedSubMethods == null || selectedSubMethods.Contains(id);

            if (ShouldRun("bin-context-owning"))
                MatchOwningEntryStringEvidence(tree, matcher, path, wadPath);
            if (ShouldRun("bin-context-structures"))
                MatchBinContextualEvidence(tree, matcher, path, wadPath, resolver);
            if (ShouldRun("bin-context-pathleaf"))
                MatchResolvedHashPathLeafEvidence(tree, matcher, path, wadPath, resolver);
            if (ShouldRun("bin-context-objectlocal"))
                MatchObjectLocalHashEvidence(tree, matcher, path, wadPath);
            if (matcher.Remaining > 0 && ShouldRun("bin-context-strings"))
            {
                IReadOnlyDictionary<InternalHashKind, HashSet<ulong>> localTargets = CollectLocalTargets(tree);
                VisitBinStrings(tree, value => matcher.Check(value, InternalHashGuessStrategy.BinContent, path, wadPath, path, localTargets));
            }
        }

        private static IReadOnlyDictionary<InternalHashKind, HashSet<ulong>> CollectLocalTargets(BinTree tree)
        {
            var targets = new Dictionary<InternalHashKind, HashSet<ulong>>
            {
                [InternalHashKind.BinEntries] = new(),
                [InternalHashKind.BinFields] = new(),
                [InternalHashKind.BinTypes] = new(),
                [InternalHashKind.BinHashes] = new()
            };
            foreach (var pair in tree.Objects)
            {
                targets[InternalHashKind.BinEntries].Add(pair.Key);
                if (pair.Value.ClassHash != 0) targets[InternalHashKind.BinTypes].Add(pair.Value.ClassHash);
                foreach (BinTreeProperty property in pair.Value.Properties.Values) Visit(property);
            }
            foreach (var item in tree.DataOverrides)
            {
                if (item.ObjectPathHash != 0) targets[InternalHashKind.BinEntries].Add(item.ObjectPathHash);
                Visit(item.Property);
            }

            void Visit(BinTreeProperty property)
            {
                if (property.NameHash != 0) targets[InternalHashKind.BinFields].Add(property.NameHash);
                switch (property)
                {
                    case BinTreeHash hash when hash.Value != 0: targets[InternalHashKind.BinHashes].Add(hash.Value); break;
                    case BinTreeObjectLink link when link.Value != 0: targets[InternalHashKind.BinEntries].Add(link.Value); break;
                    case BinTreeStruct structure:
                        if (structure.ClassHash != 0) targets[InternalHashKind.BinTypes].Add(structure.ClassHash);
                        foreach (BinTreeProperty child in structure.Properties.Values) Visit(child);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements) Visit(child);
                        break;
                    case BinTreeOptional option when option.Value != null: Visit(option.Value); break;
                    case BinTreeMap map:
                        foreach (var child in map) { Visit(child.Key); Visit(child.Value); }
                        break;
                }
            }

            return targets;
        }

        internal static void MatchOwningEntryStringEvidence(
            BinTree tree,
            InternalHashEvidenceMatcher matcher,
            string path,
            string wadPath = null)
        {
            foreach (var pair in tree.Objects)
            {
                uint entryHash = pair.Key;
                if (!matcher.IsRemaining(InternalHashKind.BinEntries, entryHash)) continue;
                var candidates = new Dictionary<string, InternalHashEvidence>(StringComparer.OrdinalIgnoreCase);
                foreach (BinTreeProperty property in pair.Value.Properties.Values)
                    Visit(property);
                if (candidates.Count == 1)
                {
                    var candidate = candidates.First();
                    matcher.CheckContextualCandidate(
                        InternalHashKind.BinEntries,
                        candidate.Key,
                        path,
                        wadPath,
                        entryHash,
                        candidate.Value);
                }

                void Visit(BinTreeProperty property)
                {
                    switch (property)
                    {
                        case BinTreeString text:
                            MatchOwnedString(text.Value);
                            break;
                        case BinTreeStruct structure:
                            foreach (BinTreeProperty child in structure.Properties.Values) Visit(child);
                            break;
                        case BinTreeContainer container:
                            foreach (BinTreeProperty child in container.Elements) Visit(child);
                            break;
                        case BinTreeOptional option when option.Value != null:
                            Visit(option.Value);
                            break;
                        case BinTreeMap map:
                            foreach (var child in map)
                            {
                                Visit(child.Key);
                                Visit(child.Value);
                            }
                            break;
                    }
                }

                void MatchOwnedString(string value)
                {
                    if (string.IsNullOrWhiteSpace(value)) return;
                    string candidate = InternalHashEvidenceMatcher.NormalizeCandidate(value);

                    AddCandidate(candidate, InternalHashEvidence.OwningEntryString);
                    if (!candidate.Contains('/')) return;

                    // A prefix is authoritative only for the entry that owns the string.
                    // This is deliberately not compared against every unknown entry hash.
                    for (int index = candidate.IndexOf('/'); index >= 0; index = candidate.IndexOf('/', index + 1))
                    {
                        if (index < 3) continue;
                        AddCandidate(candidate[..index], InternalHashEvidence.OwningEntryPrefix);
                    }
                }

                void AddCandidate(string candidate, InternalHashEvidence evidence)
                {
                    if (Fnv1a.HashLower(candidate) == entryHash)
                        candidates.TryAdd(candidate, evidence);
                }
            }
        }

        internal static void MatchObjectLocalHashEvidence(
            BinTree tree,
            InternalHashEvidenceMatcher matcher,
            string path,
            string wadPath = null)
        {
            foreach (BinTreeObject item in tree.Objects.Values)
            {
                MatchScope(item.Properties.Values, includeDescendants: false);
                foreach (BinTreeProperty property in item.Properties.Values)
                    VisitScope(property);
            }
            foreach (var item in tree.DataOverrides)
                VisitScope(item.Property);

            void MatchScope(IEnumerable<BinTreeProperty> properties, bool includeDescendants)
            {
                var candidates = new Dictionary<uint, string>();
                var ambiguous = new HashSet<uint>();
                var observedHashes = new HashSet<uint>();
                var pending = new Stack<BinTreeProperty>(properties);
                while (pending.TryPop(out BinTreeProperty property))
                {
                    if (property is BinTreeString text && !string.IsNullOrWhiteSpace(text.Value))
                    {
                        string value = text.Value.Trim();
                        if (InternalHashEvidenceMatcher.IsIdentifier(value))
                        {
                            uint hash = Fnv1a.HashLower(value);
                            if (candidates.TryGetValue(hash, out string existing) &&
                                !string.Equals(existing, value, StringComparison.Ordinal))
                                ambiguous.Add(hash);
                            else
                                candidates.TryAdd(hash, value);
                        }
                    }
                    else if (property is BinTreeHash hash)
                        observedHashes.Add(hash.Value);

                    if (includeDescendants)
                        foreach (BinTreeProperty child in EnumerateChildren(property))
                            pending.Push(child);
                }

                foreach (uint hash in observedHashes)
                    if (!ambiguous.Contains(hash) && candidates.TryGetValue(hash, out string value))
                        matcher.CheckContextualCandidate(InternalHashKind.BinHashes, value, path, wadPath, hash);
            }

            void VisitScope(BinTreeProperty property)
            {
                if (property is BinTreeStruct structure)
                    MatchScope(structure.Properties.Values, includeDescendants: false);
                else if (property is BinTreeMap map)
                    foreach (var pair in map)
                        MatchScope(new[] { pair.Key, pair.Value }, includeDescendants: true);

                foreach (BinTreeProperty child in EnumerateChildren(property))
                    VisitScope(child);
            }

            static IEnumerable<BinTreeProperty> EnumerateChildren(BinTreeProperty property)
            {
                switch (property)
                {
                    case BinTreeStruct structure:
                        foreach (BinTreeProperty child in structure.Properties.Values)
                            yield return child;
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements)
                            yield return child;
                        break;
                    case BinTreeOptional option when option.Value != null:
                        yield return option.Value;
                        break;
                    case BinTreeMap map:
                        foreach (var pair in map)
                        {
                            yield return pair.Key;
                            yield return pair.Value;
                        }
                        break;
                }
            }
        }

        private static void MatchResolvedHashPathLeafEvidence(
            BinTree tree,
            InternalHashEvidenceMatcher matcher,
            string path,
            string wadPath,
            HashResolverService resolver)
        {
            if (resolver == null) return;

            var resolvedHashes = new Dictionary<uint, string>();
            foreach (BinTreeObject item in tree.Objects.Values)
                foreach (BinTreeProperty property in item.Properties.Values)
                    Visit(property);
            foreach (BinTreeDataOverride item in tree.DataOverrides)
                Visit(item.Property);

            void Visit(BinTreeProperty property)
            {
                // A number of UI BIN fields point at a named child node. When the
                // child hash is already known, its final path component is an exact
                // BIN field candidate (e.g. .../TooltipGroup -> TooltipGroup).
                // This remains BIN context: it does not consult Meta Schema names.
                if (property.NameHash != 0 && property is BinTreeHash hash &&
                    TryGetPathLeaf(hash.Value, out string leaf))
                {
                    matcher.CheckContextualCandidate(
                        InternalHashKind.BinFields,
                        leaf,
                        path,
                        wadPath,
                        property.NameHash,
                        InternalHashEvidence.SemanticReference);
                }

                switch (property)
                {
                    case BinTreeStruct structure:
                        foreach (BinTreeProperty child in structure.Properties.Values) Visit(child);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements) Visit(child);
                        break;
                    case BinTreeOptional option when option.Value != null:
                        Visit(option.Value);
                        break;
                    case BinTreeMap map:
                        foreach (var pair in map)
                        {
                            Visit(pair.Key);
                            Visit(pair.Value);
                        }
                        break;
                }
            }

            bool TryGetPathLeaf(uint hash, out string leaf)
            {
                if (!resolvedHashes.TryGetValue(hash, out string resolved))
                {
                    resolved = resolver.ResolveBinHashGeneral(hash);
                    resolvedHashes[hash] = resolved;
                }

                if (string.IsNullOrWhiteSpace(resolved) ||
                    string.Equals(resolved, hash.ToString("x8"), StringComparison.OrdinalIgnoreCase))
                {
                    leaf = null;
                    return false;
                }

                int slash = resolved.LastIndexOf('/');
                leaf = slash >= 0 ? resolved[(slash + 1)..] : resolved;
                return InternalHashEvidenceMatcher.IsIdentifier(leaf);
            }
        }

        internal static void MatchBinContextualEvidence(
            BinTree tree,
            InternalHashEvidenceMatcher matcher,
            string path,
            string wadPath = null,
            HashResolverService resolver = null)
        {
            foreach (var pair in tree.Objects)
            {
                BinTreeObject item = pair.Value;
                MatchEntry(pair.Key, pair.Value);
                foreach (BinTreeProperty property in item.Properties.Values)
                    Visit(property);
            }
            foreach (var item in tree.DataOverrides)
                Visit(item.Property);

            void MatchEntry(uint entryHash, BinTreeObject item)
            {
                if (item.ClassHash is uint classHash)
                {
                    if (classHash == Fnv1a.HashLower("CharacterRecord") || classHash == Fnv1a.HashLower("TFTCharacterRecord"))
                        MatchEntryPattern(entryHash, item, "mCharacterName", value => $"Characters/{value}/CharacterRecords/Root");
                    else if (classHash == Fnv1a.HashLower("GameFontDescription"))
                        MatchEntryPattern(entryHash, item, "name", value => $"UX/Fonts/Descriptions/{value}");
                    else if (classHash == Fnv1a.HashLower("TFTRoundData"))
                        MatchEntryPattern(entryHash, item, "mName", value => $"Maps/Shipping/Map22/Rounds/{value}");
                    else if (classHash == Fnv1a.HashLower("TftItemData"))
                        MatchEntryPattern(entryHash, item, "mName", value => $"Maps/Shipping/Map22/Items/{value}");
                    else if (classHash == Fnv1a.HashLower("TftSetData"))
                        MatchEntryPattern(entryHash, item, "name", value => $"Maps/Shipping/Map22/Sets/{value}");
                    else if (classHash == Fnv1a.HashLower("TooltipFormat"))
                        MatchEntryPattern(entryHash, item, "mObjectName", value => $"UX/Tooltips/{value}");
                    else if (classHash == Fnv1a.HashLower("X3DSharedConstantBufferDef"))
                        MatchSharedBufferDef(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("X3DSharedSamplerDef"))
                        MatchSharedSamplerDef(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("ItemData"))
                        MatchEntryFromU32(entryHash, item, "itemID", value => $"Items/{value}");
                    else if (classHash == Fnv1a.HashLower("SummonerEmote"))
                        MatchEntryFromU32(entryHash, item, "summonerEmoteId", value => $"Loadouts/SummonerEmotes/{value}");
                    else if (classHash == Fnv1a.HashLower("TftMapSkin"))
                    {
                        if (TryGetString(item.Properties, "mapContainer", out string mapContainer))
                            MatchObservedEntry(entryHash, $"Loadouts/TFTMapSkins/{mapContainer[(mapContainer.LastIndexOf('/') + 1)..]}");
                        MatchAnyEntryString(item, "speciesLink");
                    }
                    else if (classHash == Fnv1a.HashLower("SpellObject"))
                        MatchSpellObject(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("ScriptCheat"))
                        MatchEntryFromCandidates(entryHash, item, "mName", new[] { "TFT", "Cherry", "Slime", "Strawberry", "Ultbook" }
                            .Select(mode => (Func<string, string>)(value => $"Cheats/GameModes/{mode}/{value}")));
                    else if (classHash == Fnv1a.HashLower("TftTraitData"))
                        MatchEntryFromCandidates(entryHash, item, "mName", Enumerable.Range(1, 29)
                            .Select(set => (Func<string, string>)(value => $"Maps/Shipping/Map22/Sets/TFTSet{set}/Traits/{value}")));
                    else if (classHash == Fnv1a.HashLower("MapSkin"))
                        MatchEntryFromCandidates(entryHash, item, "name", new[] { 11, 12, 21, 22, 30, 33, 35 }
                            .Select(map => (Func<string, string>)(value => $"Maps/Shipping/Map{map}/MapSkins/{value}")));
                    else if (classHash == Fnv1a.HashLower("AugmentData"))
                        MatchEntryPattern(entryHash, item, "AugmentNameId", value => $"Maps/ModeSpecificData/Augments/{value}");
                    else if (classHash == Fnv1a.HashLower("CompanionData"))
                        MatchAnyEntryString(item, "speciesLink");
                    else if (classHash == Fnv1a.HashLower("ViewControllerSet"))
                        MatchStringsInField(item, "SpecifiedGameModes");
                    else if (classHash == Fnv1a.HashLower("ViewControllerList"))
                        foreach (BinTreeProperty property in item.Properties.Values) VisitStrings(property, MatchAnyEntry);
                    else if (classHash == Fnv1a.HashLower("AtomicClipData") || classHash == Fnv1a.HashLower("SequencerClipData"))
                        MatchAtomicClipData(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("AnimationGraphData") || classHash == Fnv1a.HashLower("AnimationGraphDataContainer"))
                        MatchAnimationGraphData(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("CharacterSkinData") || classHash == Fnv1a.HashLower("SkinData"))
                        MatchSkinData(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("VfxSystemDefinitionData") || classHash == Fnv1a.HashLower("VfxEmitterDefinitionData"))
                        MatchVfxData(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("ResourceResolver") || classHash == Fnv1a.HashLower("GlobalResourceResolver"))
                        MatchHashLinkMap(item, "resourceMap");
                    else if (classHash == Fnv1a.HashLower("MapContainer"))
                        MatchHashLinkMap(item, "chunks");
                }
            }

            void MatchAtomicClipData(uint entryHash, BinTreeObject item)
            {
                string animPath = null;
                VisitForAnimPath(item.Properties.Values);

                if (!string.IsNullOrEmpty(animPath))
                {
                    string fileName = Path.GetFileNameWithoutExtension(animPath);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        MatchObservedEntry(entryHash, fileName);
                        int underscore = fileName.IndexOf('_');
                        if (underscore > 0 && underscore < fileName.Length - 1)
                        {
                            string shortName = fileName[(underscore + 1)..];
                            MatchObservedEntry(entryHash, shortName);
                        }
                    }
                }

                void VisitForAnimPath(IEnumerable<BinTreeProperty> properties)
                {
                    foreach (var prop in properties)
                    {
                        if (animPath != null) return;
                        if (prop is BinTreeString strProp && !string.IsNullOrWhiteSpace(strProp.Value) &&
                            strProp.Value.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                        {
                            animPath = strProp.Value;
                            return;
                        }
                        if (prop is BinTreeStruct strct) VisitForAnimPath(strct.Properties.Values);
                        else if (prop is BinTreeContainer ctr) VisitForAnimPath(ctr.Elements);
                        else if (prop is BinTreeOptional opt && opt.Value != null) VisitForAnimPath(new[] { opt.Value });
                    }
                }
            }

            void MatchAnimationGraphData(uint entryHash, BinTreeObject item)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    string normalizedPath = InternalHashEvidenceMatcher.NormalizeCandidate(path);
                    int charIdx = normalizedPath.IndexOf("characters/", StringComparison.OrdinalIgnoreCase);
                    if (charIdx >= 0)
                    {
                        string sub = normalizedPath[(charIdx + 11)..];
                        int slash = sub.IndexOf('/');
                        if (slash > 0)
                        {
                            string champName = sub[..slash];
                            MatchObservedEntry(entryHash, $"Characters/{champName}/Animations/Base");
                            for (int skin = 0; skin < 50; skin++)
                            {
                                MatchObservedEntry(entryHash, $"Characters/{champName}/Animations/Skin{skin}");
                                MatchObservedEntry(entryHash, $"Characters/{champName}/Animations/Skin{skin:00}");
                            }
                        }
                    }
                }
            }

            void MatchSkinData(uint entryHash, BinTreeObject item)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    string normalizedPath = InternalHashEvidenceMatcher.NormalizeCandidate(path);
                    int charIdx = normalizedPath.IndexOf("characters/", StringComparison.OrdinalIgnoreCase);
                    if (charIdx >= 0)
                    {
                        string sub = normalizedPath[(charIdx + 11)..];
                        int slash = sub.IndexOf('/');
                        if (slash > 0)
                        {
                            string champName = sub[..slash];
                            MatchObservedEntry(entryHash, $"Characters/{champName}/Skins/Base");
                            for (int skin = 0; skin < 50; skin++)
                            {
                                MatchObservedEntry(entryHash, $"Characters/{champName}/Skins/Skin{skin}");
                                MatchObservedEntry(entryHash, $"Characters/{champName}/Skins/Skin{skin:00}");
                            }
                        }
                    }
                }
            }

            void MatchVfxData(uint entryHash, BinTreeObject item)
            {
                if (TryGetString(item.Properties, "particleName", out string vfxName) ||
                    TryGetString(item.Properties, "systemName", out vfxName))
                {
                    var candidates = new List<string> { $"DATA/Effects/Shared/{vfxName}", $"DATA/Shared/VFX/{vfxName}" };
                    if (!string.IsNullOrEmpty(path))
                    {
                        string normalizedPath = InternalHashEvidenceMatcher.NormalizeCandidate(path);
                        int charIdx = normalizedPath.IndexOf("characters/", StringComparison.OrdinalIgnoreCase);
                        if (charIdx >= 0)
                        {
                            string sub = normalizedPath[(charIdx + 11)..];
                            int slash = sub.IndexOf('/');
                            if (slash > 0)
                            {
                                string champName = sub[..slash];
                                candidates.Add($"DATA/Characters/{champName}/VFX/{vfxName}");
                                candidates.Add($"Characters/{champName}/VFX/{vfxName}");
                            }
                        }
                    }
                    foreach (string candidate in candidates)
                        if (MatchObservedEntry(entryHash, candidate)) break;
                }
            }

            void MatchSpellObject(uint entryHash, BinTreeObject item)
            {
                if (TryGetString(item.Properties, "mScriptName", out string name))
                {
                    var candidates = new List<string> { $"Items/Spells/{name}", $"Shared/Spells/{name}" };
                    candidates.AddRange(new[] { 11, 12, 21, 22, 30, 33, 35 }.Select(map => $"Maps/Shipping/Map{map}/Spells/{name}"));
                    int digitCount = name.TakeWhile(char.IsAsciiDigit).Count();
                    if (digitCount > 0) candidates.Add($"Items/{name[..digitCount]}/Spells/{name}");

                    // Extract champion name if path contains characters directory
                    if (!string.IsNullOrEmpty(path))
                    {
                        string normalizedPath = InternalHashEvidenceMatcher.NormalizeCandidate(path);
                        int charIdx = normalizedPath.IndexOf("characters/", StringComparison.OrdinalIgnoreCase);
                        if (charIdx >= 0)
                        {
                            string sub = normalizedPath[(charIdx + 11)..];
                            int slash = sub.IndexOf('/');
                            if (slash > 0)
                            {
                                string champName = sub[..slash];
                                candidates.Add($"Characters/{champName}/Spells/{name}");
                            }
                        }
                    }

                    foreach (string candidate in candidates)
                        if (MatchObservedEntry(entryHash, candidate)) break;
                }

                if (item.Properties.TryGetValue(Fnv1a.HashLower("mSpell"), out BinTreeProperty spellProperty) && spellProperty is BinTreeStruct spell &&
                    spell.Properties.TryGetValue(Fnv1a.HashLower("DataValues"), out BinTreeProperty valuesProperty) && valuesProperty is BinTreeContainer values)
                {
                    foreach (BinTreeProperty value in values.Elements)
                        if (value is BinTreeStruct dataValue && TryGetString(dataValue.Properties, "name", out string dataName))
                            matcher.CheckContextualCandidate(InternalHashKind.BinHashes, dataName, path, wadPath);
                }
            }

            void MatchHashLinkMap(BinTreeObject item, string field)
            {
                if (resolver == null || !item.Properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) || property is not BinTreeMap map) return;
                foreach (var pair in map)
                {
                    if (pair.Key is not BinTreeHash key || pair.Value is not BinTreeObjectLink link) continue;
                    string target = resolver.ResolveBinEntry(link.Value);
                    if (string.Equals(target, link.Value.ToString("x8"), StringComparison.Ordinal)) continue;
                    if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, target, path, wadPath, key.Value)) continue;
                    int slash = target.LastIndexOf('/');
                    if (slash < 0) continue;
                    string basename = target[(slash + 1)..];
                    if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename, path, wadPath, key.Value) ||
                        matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename + "_BV2", path, wadPath, key.Value)) continue;
                    if (basename.Contains("Base_", StringComparison.Ordinal))
                        matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename.Replace("Base_", "", StringComparison.Ordinal), path, wadPath, key.Value);
                    for (int skin = 1; skin < 30; skin++)
                    {
                        string prefix = $"Skin{skin:00}_";
                        if (basename.Contains(prefix, StringComparison.Ordinal))
                        {
                            matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename.Replace(prefix, "", StringComparison.Ordinal), path, wadPath, key.Value);
                            break;
                        }
                    }
                }
            }

            void MatchGenericHashLinkMap(BinTreeMap map)
            {
                if (resolver == null || map.KeyType != BinPropertyType.Hash || map.ValueType != BinPropertyType.ObjectLink) return;
                foreach (var pair in map)
                {
                    if (pair.Key is not BinTreeHash key || pair.Value is not BinTreeObjectLink link) continue;
                    string target = resolver.ResolveBinEntry(link.Value);
                    if (string.Equals(target, link.Value.ToString("x8"), StringComparison.Ordinal)) continue;
                    matcher.CheckContextualCandidate(InternalHashKind.BinHashes, target, path, wadPath, key.Value);
                }
            }

            void MatchEntryPattern(uint entryHash, BinTreeObject item, string field, Func<string, string> format)
            {
                if (TryGetString(item.Properties, field, out string value)) MatchObservedEntry(entryHash, format(value));
            }
            void MatchSharedBufferDef(uint entryHash, BinTreeObject item)
            {
                if (!TryGetString(item.Properties, "name", out string name)) return;
                if (SharedBufferLeaves.TryGetValue(name, out string leaf) &&
                    MatchObservedEntry(entryHash, $"Shaders/SharedData/ConstantBuffers/{leaf}")) return;
                if (MatchObservedEntry(entryHash, $"Shaders/SharedData/ConstantBuffers/{StripSharedBufferSuffix(name)}")) return;
                if (MatchObservedEntry(entryHash, $"Shaders/SharedData/ConstantBuffers/{name}")) return;
                MatchObservedEntry(entryHash, $"Shaders/SharedData/{name}");
            }
            void MatchSharedSamplerDef(uint entryHash, BinTreeObject item)
            {
                if (!TryGetString(item.Properties, "name", out string name)) return;
                if (MatchObservedEntry(entryHash, $"Shaders/SharedData/SharedSamplers/{name}")) return;
                MatchObservedEntry(entryHash, $"Shaders/SharedData/{name}");
            }
            static string StripSharedBufferSuffix(string name)
            {
                if (name.EndsWith("_BUFFER", StringComparison.Ordinal)) return name[..^"_BUFFER".Length];
                if (name.EndsWith("CB", StringComparison.Ordinal) && name.Length > 2) return name[..^2];
                return name;
            }
            void MatchEntryFromU32(uint entryHash, BinTreeObject item, string field, Func<uint, string> format)
            {
                if (item.Properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) && property is BinTreeU32 value)
                    MatchObservedEntry(entryHash, format(value.Value));
            }
            void MatchEntryFromCandidates(uint entryHash, BinTreeObject item, string field, IEnumerable<Func<string, string>> formats)
            {
                if (!TryGetString(item.Properties, field, out string value)) return;
                foreach (Func<string, string> format in formats) if (MatchObservedEntry(entryHash, format(value))) break;
            }
            void MatchAnyEntryString(BinTreeObject item, string field)
            {
                if (TryGetString(item.Properties, field, out string value)) MatchAnyEntry(value);
            }
            void MatchStringsInField(BinTreeObject item, string field)
            {
                if (item.Properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property)) VisitStrings(property, MatchAnyEntry);
            }
            bool MatchObservedEntry(uint hash, string value) => matcher.CheckContextualCandidate(InternalHashKind.BinEntries, value, path, wadPath, hash);
            void MatchAnyEntry(string value) => matcher.CheckContextualCandidate(InternalHashKind.BinEntries, value, path, wadPath);

            void Visit(BinTreeProperty property)
            {
                switch (property)
                {
                    case BinTreeStruct structure:
                        foreach (BinTreeProperty child in structure.Properties.Values) Visit(child);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements) Visit(child);
                        break;
                    case BinTreeOptional option when option.Value != null:
                        Visit(option.Value);
                        break;
                    case BinTreeMap map:
                        MatchGenericHashLinkMap(map);
                        foreach (var child in map) { Visit(child.Key); Visit(child.Value); }
                        break;
                }
            }

            static bool TryGetString(Dictionary<uint, BinTreeProperty> properties, string field, out string value)
            {
                if (properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) && property is BinTreeString text)
                {
                    value = text.Value;
                    return true;
                }
                value = null;
                return false;
            }

            static void VisitStrings(BinTreeProperty property, Action<string> check)
            {
                switch (property)
                {
                    case BinTreeString text: check(text.Value); break;
                    case BinTreeStruct structure:
                        foreach (BinTreeProperty child in structure.Properties.Values) VisitStrings(child, check);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements) VisitStrings(child, check);
                        break;
                    case BinTreeOptional option when option.Value != null: VisitStrings(option.Value, check); break;
                    case BinTreeMap map:
                        foreach (var pair in map)
                        {
                            VisitStrings(pair.Key, check);
                            VisitStrings(pair.Value, check);
                        }
                        break;
                }
            }
        }
    }
}
