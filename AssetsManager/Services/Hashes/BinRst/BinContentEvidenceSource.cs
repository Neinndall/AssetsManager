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
        private static readonly HashSet<uint> NamedEntryTypes = new()
        {
            Fnv1a.HashLower("StaticMaterialDef"),
            Fnv1a.HashLower("UISceneData"),
            Fnv1a.HashLower("UiElementEffectAmmoData"),
            Fnv1a.HashLower("UiElementEffectAnimatedRotatingIconData"),
            Fnv1a.HashLower("UiElementEffectAnimationData"),
            Fnv1a.HashLower("UiElementEffectArcFillData"),
            Fnv1a.HashLower("UiElementEffectCircleMaskCooldownData"),
            Fnv1a.HashLower("UiElementEffectCircleMaskDesaturateData"),
            Fnv1a.HashLower("UiElementEffectCooldownData"),
            Fnv1a.HashLower("UiElementEffectCooldownRadialData"),
            Fnv1a.HashLower("UiElementEffectCustomMaterialData"),
            Fnv1a.HashLower("UiElementEffectData"),
            Fnv1a.HashLower("UiElementEffectDesaturateData"),
            Fnv1a.HashLower("UiElementEffectFillPercentageData"),
            Fnv1a.HashLower("UiElementEffectGlowConstantData"),
            Fnv1a.HashLower("UiElementEffectGlowData"),
            Fnv1a.HashLower("UiElementEffectGlowingRotatingIconData"),
            Fnv1a.HashLower("UiElementEffectInstancedData"),
            Fnv1a.HashLower("UiElementEffectLineData"),
            Fnv1a.HashLower("UiElementEffectRotatingIconData"),
            Fnv1a.HashLower("UiElementGroupButtonData"),
            Fnv1a.HashLower("UiElementGroupData"),
            Fnv1a.HashLower("UiElementGroupFramedData"),
            Fnv1a.HashLower("UiElementGroupManagedLayoutData"),
            Fnv1a.HashLower("UiElementGroupMeterData"),
            Fnv1a.HashLower("UiElementGroupSliderData"),
            Fnv1a.HashLower("UiElementIconData"),
            Fnv1a.HashLower("UiElementParticleSystemData"),
            Fnv1a.HashLower("UiElementRegionData"),
            Fnv1a.HashLower("UiElementScissorRegionData"),
            Fnv1a.HashLower("UiElementSpineAnimationData"),
            Fnv1a.HashLower("UiElementTextData"),
            Fnv1a.HashLower("UiSceneViewPaneData"),
            Fnv1a.HashLower("UiComponent"),
            0x857c08ad
        };

        private static readonly HashSet<uint> ObjectPathTypes = new()
        {
            Fnv1a.HashLower("VfxSystemDefinitionData"),
            Fnv1a.HashLower("SpellObject"),
            Fnv1a.HashLower("SkinCharacterDataProperties"),
            Fnv1a.HashLower("TftSkinCharacterDataProperties"),
            Fnv1a.HashLower("AnimationGraphData")
        };

        private static readonly HashSet<uint> SkinCharacterDataPropertiesTypes = new()
        {
            Fnv1a.HashLower("SkinCharacterDataProperties"),
            Fnv1a.HashLower("TftSkinCharacterDataProperties")
        };

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

        internal static void MatchBinContentEvidence(
            BinTree tree,
            InternalHashEvidenceMatcher matcher,
            string path,
            string wadPath = null,
            HashResolverService resolver = null,
            IReadOnlySet<string> selectedSubMethods = null,
            BinPathCasing casing = null)
        {
            bool ShouldRun(string id) => selectedSubMethods == null || selectedSubMethods.Contains(id);
            IReadOnlyDictionary<InternalHashKind, HashSet<ulong>> localTargets = CollectLocalTargets(tree);

            // File paths run first: the entries they name feed objectPath and link hooks below.
            if (ShouldRun("bin-context-filepath"))
                MatchFilePathEvidence(tree, matcher, path, wadPath, casing ?? BinPathCasing.Empty);
            if (ShouldRun("bin-context-owning"))
                MatchOwningEntryStringEvidence(tree, matcher, path, wadPath);
            if (ShouldRun("bin-context-structures"))
                MatchBinContextualEvidence(tree, matcher, path, wadPath, resolver, localTargets, casing);
            if (ShouldRun("bin-context-pathleaf"))
                MatchResolvedHashPathLeafEvidence(tree, matcher, path, wadPath, resolver);
            if (ShouldRun("bin-context-objectlocal"))
                MatchObjectLocalHashEvidence(tree, matcher, path, wadPath);
            if (matcher.Remaining > 0 &&
                (ShouldRun("bin-context-strings") || ShouldRun("rst-content-binstrings")))
            {
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

        /// <summary>
        /// Root objects are named after their BIN file (data/characters/x/skins/skin3.bin ->
        /// Characters/X/Skins/Skin3), resolvers append /Resources and loadout-style objects nest
        /// their own name under the file path. Every candidate is checked against its own object.
        /// </summary>
        internal static void MatchFilePathEvidence(
            BinTree tree,
            InternalHashEvidenceMatcher matcher,
            string path,
            string wadPath,
            BinPathCasing casing)
        {
            string stem = GetBinPathStem(path);
            if (stem == null) return;
            int slash = stem.LastIndexOf('/');
            string parent = slash > 0 ? stem[..slash] : null;
            var namesByObject = tree.Objects.ToDictionary(pair => pair.Key, pair => ObjectNames(pair.Value));
            // Any name in the file may spell a folder of its path (a resolver has no strings of its own).
            var fileHints = namesByObject.Values.SelectMany(names => names).Distinct(StringComparer.Ordinal).Take(256).ToList();

            foreach (var (entryHash, names) in namesByObject)
            {
                if (!matcher.IsRemaining(InternalHashKind.BinEntries, entryHash)) continue;
                if (Check(stem) || Check(stem + "/Resources")) continue;
                foreach (string name in names)
                    if (Check($"{stem}/{name}") || (parent != null && Check($"{parent}/{name}"))) break;

                bool Check(string candidate) => matcher.CheckContextualCandidate(
                    InternalHashKind.BinEntries,
                    casing.Recase(candidate, names.Count > 0 ? names.Concat(fileHints).ToList() : fileHints),
                    path,
                    wadPath,
                    entryHash,
                    InternalHashEvidence.SemanticReference);
            }

            static List<string> ObjectNames(BinTreeObject item) => item.Properties.Values
                .Select(property => property is BinTreeOptional { Value: BinTreeString optional } ? optional : property)
                .OfType<BinTreeString>()
                .Select(text => text.Value?.Trim())
                .Where(value => !string.IsNullOrEmpty(value) && InternalHashEvidenceMatcher.IsIdentifier(value))
                .ToList();
        }

        private static string GetBinPathStem(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('[')) return null;
            string stem = InternalHashEvidenceMatcher.NormalizeCandidate(path);
            if (stem.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) stem = stem[5..];
            if (stem.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            return stem.Length > 0 && !stem.Contains(':') ? stem : null;
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

        private static void MatchResolvedEntryLink(
            uint linkHash,
            InternalHashEvidenceMatcher matcher,
            HashResolverService resolver,
            string path,
            string wadPath)
        {
            string candidate = resolver.ResolveBinEntry(linkHash);
            if (string.IsNullOrWhiteSpace(candidate) ||
                string.Equals(candidate, linkHash.ToString("x8"), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            matcher.CheckContextualCandidate(
                InternalHashKind.BinEntries,
                candidate,
                path,
                wadPath,
                linkHash,
                InternalHashEvidence.SemanticReference);
        }

        private static readonly string[] CommonGearNames =
        {
            "Default", "Ult", "Ultimate", "Base", "Melee", "Ranged", "Shadow",
            "Form1", "Form2", "Form3", "Form4", "Level6", "level11", "Level11", "Level16", "level16",
            "Normal", "Spell1", "Spell2", "Spell3", "Spell4", "Empowered", "Frenzy", "Mounted", "Biped",
            "Transformation", "WraithForm", "Demon", "Wings", "Crown_1", "Crown_2", "Bound", "Unbound",
            "UltForm", "Rage", "Tier1", "Tier2", "Tier3", "SlotMachine1", "SlotMachine2", "SlotMachine3",
            "Weapon0", "Weapon1", "Weapon2", "Weapon3", "Level1", "Level2", "Level3", "Level4"
        };

        internal static readonly string[] CommonAnimationClipNames =
        {
            // Core Movement & Transitions
            "Idle1", "Idle2", "Idle3", "Idle4", "Idle_Base", "Idle_In", "Idle_Out", "Idle_Trans", "Idle_Turn", "Idle_Turn_Left", "Idle_Turn_Right",
            "Run", "Run_Base", "Run_Fast", "Run_Hurt", "Run_In", "Run_Out", "Run_Trans", "Run_To_Idle", "Run_ToRun", "Run_Idle_Trans", "RunIn", "RunOut",
            "Run_Homeguard", "Run_Homeguard_IN", "Run_Homeguard_Into", "Run_Homeguard_Out", "Run_HomeguardOut", "Run_Homeguard_OUT", "Run_Homeguard_RunIn",
            "Run_Homeguard_Slayer", "Run_Homeguard_To_Idle", "Run_Homeguard_To_Idle_Homeguard", "Run_Homeguard_toRun", "Run_Homeguard_To_Run",
            "Run_Homeguard_Trans", "Run_Homeguard_Trans_to_Run", "Run_Homeguard_Trans_to_Run_Fast", "Run_Homeguard_var01", "Run_HomeguardVarient",
            "RunHUnt_To_Idle", "Run_ICE", "Run_Sprint", "Run_Combat", "Run_Aggro", "Run_Slow", "Run_SuperSlayer", "Run_Fly", "Run_Glide", "Run_Swim",
            "Walk", "Walk_Base", "Walk_In", "Walk_Out", "Walk_Trans", "Walk_To_Idle", "Walk_Combat",

            // Attacks & Crits
            "Attack1", "Attack2", "Attack3", "Attack4", "Attack5", "Attack1_Crit", "Attack2_Crit", "Attack3_Crit", "Attack4_Crit", "Attack_Crit",
            "Attack1_To_Idle", "Attack2_To_Idle", "Attack3_To_Idle", "Attack4_To_Idle", "Attack1_ToIdle", "Attack2_ToIdle", "Attack1ToIdle", "Attack2ToIdle",
            "Attack1_Dash", "Attack2_Dash", "Attack1_BASE", "Attack2_BASE", "Attack1_Fast", "Attack2_Fast", "Attack1_Spell", "Attack2_Spell",
            "Crit", "Crit1", "Crit2", "Crit_Attack",

            // Spells (Q, W, E, R / Spell 1 to 4)
            "Spell1", "Spell2", "Spell3", "Spell4", "Spell1_Cast", "Spell2_Cast", "Spell3_Cast", "Spell4_Cast",
            "Spell1_Windup", "Spell2_Windup", "Spell3_Windup", "Spell4_Windup",
            "Spell1_Channel", "Spell2_Channel", "Spell3_Channel", "Spell4_Channel",
            "Spell1_Loop", "Spell2_Loop", "Spell3_Loop", "Spell4_Loop",
            "Spell1_Mis", "Spell2_Mis", "Spell3_Mis", "Spell4_Mis",
            "Spell1_In", "Spell2_In", "Spell3_In", "Spell4_In",
            "Spell1_Out", "Spell2_Out", "Spell3_Out", "Spell4_Out",
            "Spell1_End", "Spell2_End", "Spell3_End", "Spell4_End",
            "Spell1_Trans", "Spell2_Trans", "Spell3_Trans", "Spell4_Trans",
            "Spell1_To_Idle", "Spell2_To_Idle", "Spell3_To_Idle", "Spell4_To_Idle",
            "Spell1_ToRun", "Spell2_ToRun", "Spell3_ToRun", "Spell4_ToRun",
            "Spell1_Start", "Spell2_Start", "Spell3_Start", "Spell4_Start",
            "Spell1_Recast", "Spell2_Recast", "Spell3_Recast", "Spell4_Recast",
            "Spell1_Hold", "Spell2_Hold", "Spell3_Hold", "Spell4_Hold",

            // Recall, Death & Respawn
            "Recall", "Recall_Base", "Recall_In", "Recall_Out", "Recall_Loop", "Recall_Windup", "Recall_LeadIn", "Recall_End", "Recall_Lead_In",
            "Death", "Death_Base", "Death_In", "Death_Out", "Death1", "Death2", "Death3", "Death_Idle", "Death_Resurrect",
            "Respawn", "Respawn_Base", "Respawn_In", "Respawn_Out",

            // Emotes & Socials
            "Dance", "Dance_In", "Dance_Out", "Dance_Loop", "Dance1", "Dance2",
            "Taunt", "Taunt_In", "Taunt_Out", "Taunt_Loop", "Taunt1", "Taunt2",
            "Joke", "Joke_In", "Joke_Out", "Joke_Loop", "Joke1", "Joke2",
            "Laugh", "Laugh_In", "Laugh_Out", "Laugh_Loop", "Laugh1", "Laugh2",
            "Cheer", "Cheer1", "Cheer2",

            // Crowd Control, Turns & Game Events
            "Channel", "Channel_In", "Channel_Out", "Channel_Loop", "Channel_W", "Channel_R",
            "Turn_Left", "Turn_Right", "Turn_180", "Turn_90", "Turn_L", "Turn_R",
            "Stun", "Stun_In", "Stun_Out", "Stun_Loop", "Stunned",
            "Knockup", "Knockup_In", "Knockup_Out", "Knockup_Loop",
            "Fear", "Fear_In", "Fear_Out", "Fear_Loop",
            "Charm", "Charm_In", "Charm_Out", "Charm_Loop",
            "Sleep", "Sleep_In", "Sleep_Out", "Sleep_Loop",
            "Spawn", "Spawn_Base", "Spawn_In", "Spawn_Out",
            "Victory", "Victory_Base", "Victory_Loop",
            "Lose", "Lose_Base", "Lose_Loop"
        };

        internal static readonly string[] CommonAnimationTracks =
        {
            "Default", "Override", "Additive", "UpperBody", "LowerBody", "Head", "Torso", "Pelvis", "Arms",
            "Movement", "Action", "Face", "Weapon", "Wings", "Tail", "Base", "FullBody"
        };

        internal static readonly string[] CommonAnimationMasks =
        {
            "Base", "UpperBody", "LowerBody", "Head", "Torso", "Pelvis", "Arms", "Weapon", "Wings", "Tail",
            "Additive", "FullBody", "NoRoot", "Root", "Default"
        };

        internal static readonly string[] CommonAnimationSyncGroups =
        {
            "Default", "Run", "Walk", "Idle", "Movement", "Combat", "Action"
        };

        private static bool TryMatchAnimationFileCandidates(
            uint targetHash,
            string animFilePath,
            string champName,
            string skinNum,
            string path,
            string wadPath,
            InternalHashEvidenceMatcher matcher)
        {
            if (string.IsNullOrWhiteSpace(animFilePath)) return false;
            string norm = animFilePath.Replace('\\', '/');
            if (norm.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                norm = norm[..^4];

            int lastSlash = norm.LastIndexOf('/');
            string leaf = lastSlash >= 0 ? norm[(lastSlash + 1)..] : norm;
            if (string.IsNullOrWhiteSpace(leaf)) return false;

            // Check before dot if present (e.g. recall.skins_ahri_skin14)
            int dotIdx = leaf.IndexOf('.');
            if (dotIdx > 0)
            {
                string beforeDot = leaf[..dotIdx];
                if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, beforeDot, path, wadPath, targetHash))
                    return true;
                if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, char.ToUpperInvariant(beforeDot[0]) + beforeDot[1..], path, wadPath, targetHash))
                    return true;
            }

            var stems = new List<string>(8) { leaf };

            if (!string.IsNullOrEmpty(champName))
            {
                string cPrefix = champName + "_";
                if (leaf.StartsWith(cPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string rem = leaf[cPrefix.Length..];
                    stems.Add(rem);
                    if (rem.StartsWith("skin", StringComparison.OrdinalIgnoreCase))
                    {
                        int nextU = rem.IndexOf('_');
                        if (nextU > 0 && nextU < rem.Length - 1)
                            stems.Add(rem[(nextU + 1)..]);
                    }
                    else if (rem.StartsWith("base_", StringComparison.OrdinalIgnoreCase))
                    {
                        stems.Add(rem[5..]);
                    }
                }
            }

            int firstUnderscore = leaf.IndexOf('_');
            if (firstUnderscore > 0 && firstUnderscore < leaf.Length - 1)
            {
                stems.Add(leaf[(firstUnderscore + 1)..]);
            }

            for (int u = leaf.IndexOf('_'); u >= 0 && u < leaf.Length - 1; u = leaf.IndexOf('_', u + 1))
            {
                stems.Add(leaf[(u + 1)..]);
            }

            foreach (string stem in stems)
            {
                if (string.IsNullOrWhiteSpace(stem)) continue;

                // 1. Raw stem
                if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, stem, path, wadPath, targetHash))
                    return true;

                string[] tokens = stem.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                // 2. PascalCase with underscores (e.g. Run_Homeguard_To_Run)
                string[] casedTokens = tokens.Select(t => char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant()).ToArray();
                string pascalUnderscore = string.Join("_", casedTokens);
                if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, pascalUnderscore, path, wadPath, targetHash))
                    return true;

                // 3. PascalCase without underscores (e.g. RunHomeguardToRun, RunIn)
                string pascalConcat = string.Concat(casedTokens);
                if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, pascalConcat, path, wadPath, targetHash))
                    return true;

                // 4. Uppercase acronyms on last token (e.g. Run_Homeguard_IN, Run_Homeguard_OUT, Run_ICE)
                if (tokens.Length > 1)
                {
                    string lastLower = tokens[^1].ToLowerInvariant();
                    if (lastLower is "in" or "out" or "ice" or "fast" or "a" or "b" or "c" or "d")
                    {
                        string[] acroTokens = (string[])casedTokens.Clone();
                        acroTokens[^1] = acroTokens[^1].ToUpperInvariant();
                        if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, string.Join("_", acroTokens), path, wadPath, targetHash))
                            return true;
                    }
                }

                // 5. Transition casing ("_to_" lowercase e.g. Run_Homeguard_to_Run, Run_Homeguard_toRun)
                bool hasTo = tokens.Any(t => t.Equals("to", StringComparison.OrdinalIgnoreCase));
                if (hasTo)
                {
                    string[] toTokens = tokens.Select(t => t.Equals("to", StringComparison.OrdinalIgnoreCase) ? "to" : (char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant())).ToArray();
                    if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, string.Join("_", toTokens), path, wadPath, targetHash))
                        return true;
                }
            }

            return false;
        }

        private static bool TryGetNumericOrStringId(
            Dictionary<uint, BinTreeProperty> properties,
            out string id)
        {
            foreach (string field in new[] { "mId", "id", "mID", "mItemId", "mItemID", "itemID", "itemId" })
            {
                uint hash = Fnv1a.HashLower(field);
                if (properties.TryGetValue(hash, out BinTreeProperty prop))
                {
                    switch (prop)
                    {
                        case BinTreeU32 u32: id = u32.Value.ToString(); return true;
                        case BinTreeI32 i32: id = i32.Value.ToString(); return true;
                        case BinTreeU64 u64: id = u64.Value.ToString(); return true;
                        case BinTreeI64 i64: id = i64.Value.ToString(); return true;
                        case BinTreeString str when !string.IsNullOrWhiteSpace(str.Value):
                            id = str.Value.Trim(); return true;
                    }
                }
            }
            id = null;
            return false;
        }

        private static bool TryGetLeafFromPath(string filePath, string folderSegment, out string leaf)
        {
            leaf = null;
            if (string.IsNullOrEmpty(filePath)) return false;
            string norm = InternalHashEvidenceMatcher.NormalizeCandidate(filePath);
            int idx = norm.IndexOf(folderSegment, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            string sub = norm[(idx + folderSegment.Length)..].TrimStart('/');
            int slash = sub.IndexOf('/');
            leaf = slash > 0 ? sub[..slash] : sub;
            if (leaf.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^4];
            return !string.IsNullOrWhiteSpace(leaf);
        }

        // TFT content is prefixed by its set: "TFT14_..." -> 14.
        private static bool TryGetTftSet(string name, out int set)
        {
            set = 0;
            if (name == null || !name.StartsWith("TFT", StringComparison.OrdinalIgnoreCase)) return false;
            int end = 3;
            while (end < name.Length && char.IsAsciiDigit(name[end])) end++;
            return end > 3 && end < name.Length && name[end] == '_' &&
                int.TryParse(name.AsSpan(3, end - 3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out set);
        }

        private static bool TryGetObjectLink(
            Dictionary<uint, BinTreeProperty> properties,
            string field,
            out BinTreeObjectLink link)
        {
            if (properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) &&
                property is BinTreeObjectLink objectLink)
            {
                link = objectLink;
                return true;
            }

            link = null;
            return false;
        }

        private static bool TryGetObjectPathHash(
            Dictionary<uint, BinTreeProperty> properties,
            out uint hashValue)
        {
            if (properties.TryGetValue(Fnv1a.HashLower("objectPath"), out BinTreeProperty property))
            {
                if (property is BinTreeHash hash && hash.Value != 0)
                {
                    hashValue = hash.Value;
                    return true;
                }
                if (property is BinTreeObjectLink link && link.Value != 0)
                {
                    hashValue = (uint)link.Value;
                    return true;
                }
            }

            hashValue = 0;
            return false;
        }

        private static bool TryGetString(
            Dictionary<uint, BinTreeProperty> properties,
            string field,
            out string value)
        {
            if (properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) &&
                property is BinTreeString text)
            {
                value = text.Value;
                return true;
            }

            value = null;
            return false;
        }

        private static bool TryGetStringByHash(
            Dictionary<uint, BinTreeProperty> properties,
            uint fieldHash,
            out string value)
        {
            if (properties.TryGetValue(fieldHash, out BinTreeProperty property) &&
                property is BinTreeString text)
            {
                value = text.Value;
                return true;
            }

            value = null;
            return false;
        }

        private static bool TryGetObjectLinkByHash(
            Dictionary<uint, BinTreeProperty> properties,
            uint fieldHash,
            out BinTreeObjectLink link)
        {
            if (properties.TryGetValue(fieldHash, out BinTreeProperty property) &&
                property is BinTreeObjectLink objectLink)
            {
                link = objectLink;
                return true;
            }

            link = null;
            return false;
        }

        internal static void MatchBinContextualEvidence(
            BinTree tree,
            InternalHashEvidenceMatcher matcher,
            string path,
            string wadPath = null,
            HashResolverService resolver = null,
            IReadOnlyDictionary<InternalHashKind, HashSet<ulong>> localTargets = null,
            BinPathCasing casing = null)
        {
            localTargets ??= CollectLocalTargets(tree);
            casing ??= BinPathCasing.Empty;
            foreach (var pair in tree.Objects)
            {
                BinTreeObject item = pair.Value;
                MatchEntry(pair.Key, pair.Value);
                if (ObjectPathTypes.Contains(item.ClassHash))
                    MatchObjectPathFromEntry(pair.Key, item);

                foreach (BinTreeProperty property in item.Properties.Values)
                    Visit(property, item.ClassHash);
            }
            foreach (var item in tree.DataOverrides)
                Visit(item.Property, 0);

            MatchCollectedItemHashes();

            string ResolveEntryPath(uint hash)
            {
                if (matcher.TryGetVerifiedValue(InternalHashKind.BinEntries, hash, out string matched))
                    return matched;
                if (resolver == null) return null;
                string resolved = resolver.ResolveBinEntry(hash);
                return string.Equals(resolved, hash.ToString("x8"), StringComparison.OrdinalIgnoreCase) ? null : resolved;
            }

            string ResolveHashValue(uint hash)
            {
                if (matcher.TryGetVerifiedValue(InternalHashKind.BinHashes, hash, out string matched))
                    return matched;
                if (resolver == null) return null;
                string resolved = resolver.ResolveBinHashGeneral(hash);
                return string.Equals(resolved, hash.ToString("x8"), StringComparison.OrdinalIgnoreCase) ? null : resolved;
            }

            void MatchObjectPathFromEntry(uint entryHash, BinTreeObject item)
            {
                if (!TryGetObjectPathHash(item.Properties, out uint objectPathHash))
                    return;

                string entryPath = ResolveEntryPath(entryHash);
                if (string.IsNullOrWhiteSpace(entryPath)) return;

                matcher.CheckContextualCandidate(InternalHashKind.BinHashes, entryPath, path, wadPath, objectPathHash);
            }

            void MatchEntry(uint entryHash, BinTreeObject item)
            {
                if (item.ClassHash is uint classHash)
                {
                    if (NamedEntryTypes.Contains(classHash))
                        MatchEntryDirect(entryHash, item, "name");
                    else if (classHash == Fnv1a.HashLower("ContextualActionData"))
                        MatchEntryDirect(entryHash, item, "mObjectPath");
                    else if (classHash == Fnv1a.HashLower("CustomShaderDef"))
                        MatchEntryDirect(entryHash, item, "objectPath");
                    else if (classHash == Fnv1a.HashLower("RewardGroup"))
                        MatchEntryDirect(entryHash, item, "internalName");
                    else if (classHash == Fnv1a.HashLower("Sequence"))
                        MatchEntryDirect(entryHash, item, "path");
                    else if (classHash == 0x8d31b69b)
                    {
                        if (TryGetString(item.Properties, "QuestName", out string qName))
                            MatchObservedEntry(entryHash, $"Maps/ModeSpecificData/ModesQuests/{qName}");
                    }
                    else if (classHash == Fnv1a.HashLower("TftShopData"))
                    {
                        if (TryGetString(item.Properties, "mName", out string shopName))
                        {
                            for (int set = 1; set < 30; set++)
                                if (MatchObservedEntry(entryHash, $"Maps/Shipping/Map22/Sets/TFTSet{set}/Shop/{shopName}"))
                                    break;
                            MatchObservedEntry(entryHash, $"Maps/Shipping/Map22/Shop/{shopName}");
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("MapPlaceableContainer"))
                    {
                        if (item.Properties.TryGetValue(Fnv1a.HashLower("items"), out BinTreeProperty itemsProp) &&
                            itemsProp is BinTreeMap itemsMap)
                        {
                            foreach (var pair in itemsMap)
                            {
                                if (pair.Value is not BinTreeStruct placeableStruct ||
                                    placeableStruct.ClassHash != Fnv1a.HashLower("GdsMapObject"))
                                    continue;
                                if (TryGetStringByHash(placeableStruct.Properties, 0xad304db5, out string gdsPath))
                                    MatchAnyEntry(gdsPath);
                                else if (TryGetObjectLinkByHash(placeableStruct.Properties, 0xad304db5, out BinTreeObjectLink gdsLink))
                                    MatchLinkedEntry(gdsLink.Value);
                            }
                        }
                        MatchMapPlaceableContainerLattice(entryHash, item, path);
                    }
                    else if (classHash == Fnv1a.HashLower("ChallengeConfigData") || classHash == 0xb36600f0)
                    {
                        if (TryGetNumericOrStringId(item.Properties, out string challengeId))
                        {
                            MatchObservedEntry(entryHash, $"LCU/Challenges/Config/{challengeId}/Config");
                            MatchObservedEntry(entryHash, $"LCU/Challenges/Config/{challengeId}");
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("CollectiblesEsportsTeamData") || classHash == 0xe2fd6db7)
                    {
                        if (TryGetNumericOrStringId(item.Properties, out string teamId) &&
                            (TryGetString(item.Properties, "mName", out string teamName) ||
                             TryGetString(item.Properties, "mTeamName", out teamName) ||
                             TryGetString(item.Properties, "name", out teamName)))
                        {
                            MatchObservedEntry(entryHash, $"Lcu/Collectibles/EsportsTeams/{teamId}_{teamName}");
                            MatchObservedEntry(entryHash, $"LCU/Collectibles/EsportsTeams/{teamId}_{teamName}");
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("TftDamageSkinData") || classHash == 0x967b16cd ||
                             classHash == Fnv1a.HashLower("DamageSkin") ||
                             (!string.IsNullOrEmpty(path) && path.Contains("TFTDamageSkins", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (TryGetString(item.Properties, "mDamageSkinName", out string damageName) ||
                            TryGetString(item.Properties, "mName", out damageName) ||
                            TryGetString(item.Properties, "name", out damageName) ||
                            TryGetLeafFromPath(path, "TFTDamageSkins", out damageName))
                        {
                            for (int tier = 1; tier <= 3; tier++)
                            {
                                string tierEntry = $"Loadouts/TFTDamageSkins/{damageName}/{damageName}_Tier{tier}";
                                MatchObservedEntry(entryHash, tierEntry);
                                MatchObservedEntry(entryHash, $"{tierEntry}/ResourceBin/Resources");
                                if (TryGetObjectLink(item.Properties, "mResourceResolver", out BinTreeObjectLink dmgResLink) && dmgResLink.Value != 0)
                                {
                                    MatchObservedEntry((uint)dmgResLink.Value, $"{tierEntry}/ResourceBin/Resources");
                                }
                            }
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("AramBoonData") || classHash == 0x78b217f2 ||
                             classHash == Fnv1a.HashLower("AramBoon") ||
                             (!string.IsNullOrEmpty(path) && path.Contains("AramBoons", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (TryGetString(item.Properties, "mName", out string boonName) ||
                            TryGetString(item.Properties, "name", out boonName) ||
                            TryGetLeafFromPath(path, "AramBoons", out boonName))
                        {
                            MatchObservedEntry(entryHash, $"Loadouts/AramBoons/{boonName}/{boonName}");
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("SummonerBannerData") ||
                             classHash == Fnv1a.HashLower("RegaliaBanner") ||
                             (!string.IsNullOrEmpty(path) && (path.Contains("SummonerBanners", StringComparison.OrdinalIgnoreCase) || path.Contains("Regalia", StringComparison.OrdinalIgnoreCase))))
                    {
                        if (TryGetString(item.Properties, "mName", out string bannerName) ||
                            TryGetString(item.Properties, "name", out bannerName))
                        {
                            for (int tier = 1; tier <= 5; tier++)
                            {
                                MatchObservedEntry(entryHash, $"Loadouts/SummonerBanners/Flags/{bannerName}/Flag_{tier}");
                            }
                            MatchObservedEntry(entryHash, $"Loadouts/Regalia/Banners/Other/{bannerName}");
                            MatchObservedEntry(entryHash, $"Loadouts/Regalia/Banners/Ranked/{bannerName}");
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("TftPassItem") || classHash == Fnv1a.HashLower("TftBattlePassData") ||
                             (!string.IsNullOrEmpty(path) && path.Contains("Passes/Tft", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (TryGetString(item.Properties, "mName", out string passAssetName) ||
                            TryGetString(item.Properties, "mAssetName", out passAssetName) ||
                            TryGetString(item.Properties, "name", out passAssetName))
                        {
                            MatchObservedEntry(entryHash, $"Passes/Tft/Assets/{passAssetName}");
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("CharacterRecord") || classHash == Fnv1a.HashLower("TFTCharacterRecord"))
                        MatchCharacterRecord(entryHash, item);
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
                    else if (classHash == Fnv1a.HashLower("Character"))
                        MatchEntryPattern(entryHash, item, "name", value => $"Characters/{value}");
                    else if (classHash == Fnv1a.HashLower("CheatSet"))
                        MatchEntryPattern(entryHash, item, "mName", value => $"Cheats/CheatSets/{value}");
                    else if (classHash == Fnv1a.HashLower("X3DSharedConstantBufferDef"))
                        MatchSharedBufferDef(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("X3DSharedSamplerDef"))
                        MatchSharedSamplerDef(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("ItemData"))
                    {
                        MatchEntryFromU32(entryHash, item, "itemID", value => $"Items/{value}");
                        matcher.ObserveItemDataEntry(entryHash);
                        if (item.Properties.TryGetValue(Fnv1a.HashLower("mVFXResourceResolver"), out BinTreeProperty resolverProperty) &&
                            resolverProperty is BinTreeStruct vfxResolver)
                        {
                            MatchHashLinkMapProperties(vfxResolver.Properties, "resourceMap");
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("SummonerEmote"))
                        MatchEntryFromU32(entryHash, item, "summonerEmoteId", value => $"Loadouts/SummonerEmotes/{value}");
                    else if (classHash == Fnv1a.HashLower("TftMapSkin"))
                    {
                        if (TryGetString(item.Properties, "mapContainer", out string mapContainer))
                        {
                            int lastSlash = mapContainer.LastIndexOf('/');
                            string containerName = lastSlash >= 0 ? mapContainer[(lastSlash + 1)..] : mapContainer;
                            MatchObservedEntry(entryHash, $"Loadouts/TFTMapSkins/{containerName}");
                        }
                        if (TryGetString(item.Properties, "GroupLink", out string groupLink))
                            MatchAnyEntry(groupLink);
                        else if (TryGetObjectLink(item.Properties, "GroupLink", out BinTreeObjectLink groupEntry))
                            MatchLinkedEntry(groupEntry.Value);
                        MatchAnyEntryString(item, "speciesLink");
                    }
                    else if (classHash == Fnv1a.HashLower("SpellObject"))
                        MatchSpellObject(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("ScriptCheat"))
                    {
                        MatchEntryFromCandidates(entryHash, item, "mName", new[] { "TFT", "Cherry", "Slime", "Strawberry", "Ultbook" }
                            .Select(mode => (Func<string, string>)(value => $"Cheats/GameModes/{mode}/{value}")));
                        // TFT cheats live under their set folder, named by the cheat's TFT{N}_ prefix.
                        if (TryGetString(item.Properties, "mName", out string cheatName))
                        {
                            if (TryGetTftSet(cheatName, out int cheatSet))
                                MatchObservedEntry(entryHash, $"Cheats/GameModes/TFT/TFT{cheatSet}/{cheatName}");

                            if (TryGetString(item.Properties, "mChampionName", out string champName))
                            {
                                MatchObservedEntry(entryHash, $"Cheats/ChampSpecific/{champName}/{cheatName}");
                            }
                            else if (!string.IsNullOrEmpty(path) && TryGetLeafFromPath(path, "characters", out string champFromPath))
                            {
                                MatchObservedEntry(entryHash, $"Cheats/ChampSpecific/{champFromPath}/{cheatName}");
                            }
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("TrophyData"))
                    {
                        // The cup folder is the fifth segment of ASSETS/Loadouts/SummonerTrophies/Trophies/{cup}/...
                        if (item.Properties.TryGetValue(Fnv1a.HashLower("skinMeshProperties"), out BinTreeProperty meshProperty) &&
                            meshProperty is BinTreeStruct mesh &&
                            TryGetString(mesh.Properties, "skeleton", out string skeleton))
                        {
                            string[] segments = InternalHashEvidenceMatcher.NormalizeCandidate(skeleton).Split('/');
                            if (segments.Length > 4)
                                foreach (int gems in new[] { 4, 8, 16 })
                                    if (MatchObservedEntry(entryHash, $"Loadouts/SummonerTrophies/Trophies/{segments[4]}/Trophy_{gems}")) break;
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("TftPlaybook"))
                    {
                        if (TryGetString(item.Properties, "name", out string playbook))
                        {
                            MatchObservedEntry(entryHash, $"Loadouts/TFTPlaybooks/{playbook}");
                            if (item.Properties.TryGetValue(Fnv1a.HashLower("VfxResourceResolver"), out BinTreeProperty playbookResolver) &&
                                playbookResolver is BinTreeHash playbookResolverHash)
                                matcher.CheckContextualCandidate(InternalHashKind.BinHashes, $"Loadouts/TFTPlaybooks/{playbook}/Resources", path, wadPath, playbookResolverHash.Value);
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("TftTraitData"))
                        MatchEntryFromCandidates(entryHash, item, "mName", Enumerable.Range(1, 29)
                            .Select(set => (Func<string, string>)(value => $"Maps/Shipping/Map22/Sets/TFTSet{set}/Traits/{value}")));
                    else if (classHash == Fnv1a.HashLower("MapSkin"))
                        MatchEntryFromCandidates(entryHash, item, "name", new[] { 11, 12, 21, 22, 30, 33, 35 }
                            .Select(map => (Func<string, string>)(value => $"Maps/Shipping/Map{map}/MapSkins/{value}")));
                    else if (classHash == Fnv1a.HashLower("AugmentData") || classHash == Fnv1a.HashLower("TftAugmentData"))
                    {
                        if (TryGetString(item.Properties, "AugmentNameId", out string augName) ||
                            TryGetString(item.Properties, "mName", out augName) ||
                            TryGetString(item.Properties, "name", out augName))
                        {
                            MatchObservedEntry(entryHash, $"Maps/ModeSpecificData/Augments/{augName}");
                            MatchObservedEntry(entryHash, $"Maps/ModeSpecificData/Augments/{augName}/{augName}");
                            MatchObservedEntry(entryHash, $"Maps/ModeSpecificData/Augments/{augName}/Resources");
                            MatchObservedEntry(entryHash, $"Maps/ModeSpecificData/RUBY/{augName}/{augName}");
                            MatchObservedEntry(entryHash, $"Maps/ModeSpecificData/ULTBOOK/{augName}/{augName}");

                            if (TryGetTftSet(augName, out int augSet))
                            {
                                MatchObservedEntry(entryHash, $"Maps/Shipping/Map22/Augments/Set{augSet}/{augName}");
                            }
                            for (int s = 10; s <= 20; s++)
                            {
                                if (MatchObservedEntry(entryHash, $"Maps/Shipping/Map22/Augments/Set{s}/{augName}"))
                                    break;
                            }

                            if (TryGetObjectLink(item.Properties, "RootSpell", out BinTreeObjectLink rootSpellLink) && rootSpellLink.Value != 0)
                            {
                                MatchObservedEntry((uint)rootSpellLink.Value, $"Maps/ModeSpecificData/Augments/{augName}/Augment_{augName}");
                            }
                            if (TryGetObjectLink(item.Properties, "mResourceResolver", out BinTreeObjectLink augResLink) && augResLink.Value != 0)
                            {
                                MatchObservedEntry((uint)augResLink.Value, $"Maps/ModeSpecificData/Augments/{augName}/Resources");
                            }
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("ItemGroup") || classHash == 0x160b6ce9 ||
                             classHash == Fnv1a.HashLower("ItemGroupData"))
                    {
                        if (item.Properties.TryGetValue(Fnv1a.HashLower("mItemGroupID"), out BinTreeProperty idProp) &&
                            idProp is BinTreeHash idHash && idHash.Value != 0)
                        {
                            string idStr = ResolveHashValue(idHash.Value);
                            if (!string.IsNullOrWhiteSpace(idStr))
                            {
                                MatchObservedEntry(entryHash, $"Items/ItemGroup/{idStr}");
                                MatchObservedEntry(entryHash, $"Items/ItemGroups/{idStr}");
                                MatchObservedEntry(entryHash, $"Items/ItemGroups/Unique/{idStr}");
                            }
                        }
                        if (TryGetString(item.Properties, "mName", out string grpName) ||
                            TryGetString(item.Properties, "name", out grpName))
                        {
                            MatchObservedEntry(entryHash, $"Items/ItemGroups/{grpName}");
                            MatchObservedEntry(entryHash, $"Items/ItemGroups/Unique/{grpName}");
                            MatchObservedEntry(entryHash, $"Items/ItemModifiers/{grpName}");
                        }
                        if (TryGetNumericOrStringId(item.Properties, out string gNumId))
                        {
                            MatchObservedEntry(entryHash, $"Items/ItemGroups/Unique/{gNumId}");
                        }
                    }
                    else if (classHash == Fnv1a.HashLower("ItemShopGameModeData"))
                    {
                        MatchItemShopGameModeData(item);
                    }
                    else if (classHash == Fnv1a.HashLower("GameModeItemList"))
                    {
                        MatchGameModeItemList(item);
                    }
                    else if (classHash == Fnv1a.HashLower("CompanionData"))
                        MatchAnyEntryString(item, "speciesLink");
                    else if (classHash == Fnv1a.HashLower("ViewControllerSet"))
                        MatchStringsInField(item, "SpecifiedGameModes");
                    else if (classHash == Fnv1a.HashLower("ViewControllerList"))
                        foreach (BinTreeProperty property in item.Properties.Values) VisitStrings(property, MatchAnyEntry);
                    else if (classHash == Fnv1a.HashLower("AnimationGraphData"))
                        MatchAnimationGraphData(entryHash, item);
                    else if (SkinCharacterDataPropertiesTypes.Contains(classHash))
                        MatchSkinCharacterData(entryHash, item);
                    else if (classHash == Fnv1a.HashLower("VfxSystemDefinitionData") || classHash == Fnv1a.HashLower("VfxEmitterDefinitionData"))
                    {
                        MatchEntryDirect(entryHash, item, "particlePath");
                        MatchVfxData(entryHash, item);
                    }
                    else if (classHash == Fnv1a.HashLower("ResourceResolver") || classHash == Fnv1a.HashLower("GlobalResourceResolver"))
                        MatchHashLinkMap(item, "resourceMap");
                    else if (classHash == Fnv1a.HashLower("MapContainer"))
                    {
                        MatchEntryDirect(entryHash, item, "mapPath");
                        MatchHashLinkMap(item, "chunks");
                    }
                }
            }

            void MatchMapPlaceableContainerLattice(uint entryHash, BinTreeObject item, string filePath)
            {
                if (string.IsNullOrEmpty(filePath)) return;
                string norm = InternalHashEvidenceMatcher.NormalizeCandidate(filePath);
                if (!norm.Contains("mapgeometry", StringComparison.OrdinalIgnoreCase)) return;

                int mapIdx = norm.IndexOf("mapgeometry/map", StringComparison.OrdinalIgnoreCase);
                if (mapIdx < 0) return;
                string afterMap = norm[(mapIdx + "mapgeometry/map".Length)..];
                int slash = afterMap.IndexOf('/');
                if (slash <= 0) return;
                string mapNum = afterMap[..slash];

                string theme = null;
                int chunksIdx = norm.IndexOf("/chunks/", StringComparison.OrdinalIgnoreCase);
                if (chunksIdx >= 0)
                {
                    string afterChunks = norm[(chunksIdx + "/chunks/".Length)..];
                    int nextSlash = afterChunks.IndexOf('/');
                    theme = nextSlash > 0 ? afterChunks[..nextSlash] : afterChunks;
                    if (theme.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) theme = theme[..^4];
                }

                if (!string.IsNullOrEmpty(theme))
                {
                    string[] subChunks =
                    {
                        "Art", "Audio", "VFX", "Lighting", "Props", "Skybox", "Ground", "Structures", "Geometry",
                        "Esports_Banners", "Hall_Of_Legends", "SRS_Chemtech", "SRS_Cloud", "SRS_Earth", "SRS_Fire",
                        "SRS_Geometry", "SRS_Ground", "SRS_Hextech", "SRS_Ocean", "SRS_Shop", "SRS_Structures",
                        "SRX_Particles", "SRX_Chemtech_VFX", "Intor_BattleAcademia"
                    };

                    MatchObservedEntry(entryHash, $"Maps/MapGeometry/Map{mapNum}/Chunks/{theme}");
                    MatchObservedEntry(entryHash, $"Maps/MapGeometry/Map{mapNum}/Chunks/{theme}_Audio");

                    foreach (string sc in subChunks)
                    {
                        if (MatchObservedEntry(entryHash, $"Maps/MapGeometry/Map{mapNum}/Chunks/{theme}/{sc}")) break;
                        if (MatchObservedEntry(entryHash, $"Maps/MapGeometry/Map{mapNum}/Chunks/{theme}/{sc}_{theme}")) break;
                    }
                }
            }

            void MatchCharacterRecord(uint entryHash, BinTreeObject item)
            {
                if (!TryGetString(item.Properties, "mCharacterName", out string cname) || string.IsNullOrWhiteSpace(cname))
                    return;

                string prefix = $"Characters/{cname}";

                MatchObservedEntry(entryHash, $"{prefix}/CharacterRecords/Root");
                MatchObservedEntry(entryHash, prefix);

                MatchAnyEntry(prefix);
                MatchAnyEntry($"{prefix}/CharacterRecords/Root");
                MatchAnyEntry($"{prefix}/CharacterRecords/SLIME");
                MatchAnyEntry($"{prefix}/CharacterRecords/URF");
                MatchAnyEntry($"{prefix}/Skins/Meta");
                MatchAnyEntry($"{prefix}/Skins/Root");

                if (item.Properties.TryGetValue(Fnv1a.HashLower("basicAttack"), out BinTreeProperty basicProp) &&
                    basicProp is BinTreeStruct basicStruct &&
                    TryGetStringOrOptional(basicStruct.Properties, "mAttackName", out string basicName))
                {
                    MatchAnyEntry($"{prefix}/Spells/{basicName}");
                }

                CheckAttackList("extraAttacks");
                CheckAttackList("critAttacks");

                void CheckAttackList(string field)
                {
                    if (item.Properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty listProp) &&
                        listProp is BinTreeContainer container)
                    {
                        foreach (BinTreeProperty elem in container.Elements)
                        {
                            if (elem is BinTreeStruct attackStruct &&
                                TryGetStringOrOptional(attackStruct.Properties, "mAttackName", out string attackName))
                            {
                                MatchAnyEntry($"{prefix}/Spells/{attackName}");
                            }
                        }
                    }
                }

                if (item.Properties.TryGetValue(Fnv1a.HashLower("spellNames"), out BinTreeProperty spellNamesProp) &&
                    spellNamesProp is BinTreeContainer spellContainer)
                {
                    foreach (BinTreeProperty elem in spellContainer.Elements)
                    {
                        if (elem is BinTreeString spellNameStr && !string.IsNullOrWhiteSpace(spellNameStr.Value))
                        {
                            string spellName = spellNameStr.Value;
                            MatchAnyEntry($"{prefix}/Spells/{spellName}");
                            int slash = spellName.IndexOf('/');
                            if (slash > 0)
                            {
                                string parent = spellName[..slash];
                                MatchAnyEntry($"{prefix}/Spells/{parent}");
                            }
                        }
                    }
                }

                if (item.Properties.TryGetValue(Fnv1a.HashLower("extraSpells"), out BinTreeProperty extraSpellsProp) &&
                    extraSpellsProp is BinTreeContainer extraContainer)
                {
                    foreach (BinTreeProperty elem in extraContainer.Elements)
                    {
                        if (elem is BinTreeString extraSpellStr &&
                            !string.IsNullOrWhiteSpace(extraSpellStr.Value) &&
                            !extraSpellStr.Value.Equals("BaseSpell", StringComparison.OrdinalIgnoreCase))
                        {
                            MatchAnyEntry($"{prefix}/Spells/{extraSpellStr.Value}");
                        }
                    }
                }
            }

            void MatchAnimationGraphData(uint entryHash, BinTreeObject item)
            {
                string champName = null;
                string skinNum = null;
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
                            champName = sub[..slash];
                            int skinIdx = sub.IndexOf("skin", StringComparison.OrdinalIgnoreCase);
                            if (skinIdx >= 0)
                            {
                                int end = skinIdx + 4;
                                while (end < sub.Length && char.IsDigit(sub[end])) end++;
                                if (end > skinIdx + 4)
                                    skinNum = sub[(skinIdx + 4)..end];
                            }

                            MatchObservedEntry(entryHash, $"Characters/{champName}/Animations/Base");
                            for (int skin = 0; skin < 200; skin++)
                            {
                                MatchObservedEntry(entryHash, $"Characters/{champName}/Animations/Skin{skin}");
                                MatchObservedEntry(entryHash, $"Characters/{champName}/Animations/Skin{skin:00}");
                            }
                        }
                    }
                }

                // 1. Clips Map (mClipDataMap)
                if (item.Properties.TryGetValue(Fnv1a.HashLower("mClipDataMap"), out BinTreeProperty clipMapProp) &&
                    clipMapProp is BinTreeMap clipMap)
                {
                    foreach (var pair in clipMap)
                    {
                        if (pair.Key is not BinTreeHash clipHash || clipHash.Value == 0) continue;
                        uint targetHash = clipHash.Value;

                        bool matched = false;
                        if (pair.Value is BinTreeStruct clipStruct)
                        {
                            // A. Direct string clip names if preserved in struct
                            if (TryGetString(clipStruct.Properties, "mClipName", out string cName) && !string.IsNullOrWhiteSpace(cName))
                                matched = matcher.CheckContextualCandidate(InternalHashKind.BinHashes, cName, path, wadPath, targetHash);
                            if (!matched && TryGetString(clipStruct.Properties, "mAnimationName", out string aName) && !string.IsNullOrWhiteSpace(aName))
                                matched = matcher.CheckContextualCandidate(InternalHashKind.BinHashes, aName, path, wadPath, targetHash);

                            // B. Resolve animation file path (from string or WadChunkLink / U64)
                            string animFilePath = null;
                            if (!matched && clipStruct.Properties.TryGetValue(Fnv1a.HashLower("mAnimationResourceData"), out BinTreeProperty resProp) &&
                                resProp is BinTreeStruct resStruct)
                            {
                                if (TryGetString(resStruct.Properties, "mAnimationFilePath", out string s))
                                    animFilePath = s;
                                else if (resStruct.Properties.TryGetValue(Fnv1a.HashLower("mAnimationFilePath"), out BinTreeProperty linkProp))
                                {
                                    ulong linkVal = linkProp switch
                                    {
                                        BinTreeWadChunkLink link => link.Value,
                                        BinTreeU64 u64 => u64.Value,
                                        _ => 0
                                    };
                                    if (linkVal != 0 && resolver != null)
                                    {
                                        string resolved = resolver.ResolveHash(linkVal);
                                        if (!string.IsNullOrEmpty(resolved) && resolved.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                                            animFilePath = resolved;
                                    }
                                }

                                if (string.IsNullOrEmpty(animFilePath) && TryGetString(resStruct.Properties, "mAnimationName", out string resAnimName))
                                {
                                    matched = matcher.CheckContextualCandidate(InternalHashKind.BinHashes, resAnimName, path, wadPath, targetHash);
                                }
                            }

                            if (!matched && string.IsNullOrEmpty(animFilePath))
                            {
                                if (TryGetString(clipStruct.Properties, "mAnimationFilePath", out string s2))
                                    animFilePath = s2;
                                else if (clipStruct.Properties.TryGetValue(Fnv1a.HashLower("mAnimationFilePath"), out BinTreeProperty linkProp2))
                                {
                                    ulong linkVal2 = linkProp2 switch
                                    {
                                        BinTreeWadChunkLink link2 => link2.Value,
                                        BinTreeU64 u64_2 => u64_2.Value,
                                        _ => 0
                                    };
                                    if (linkVal2 != 0 && resolver != null)
                                    {
                                        string resolved2 = resolver.ResolveHash(linkVal2);
                                        if (!string.IsNullOrEmpty(resolved2) && resolved2.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                                            animFilePath = resolved2;
                                    }
                                }
                            }

                            // C. Generate all candidate forms from animFilePath
                            if (!matched && !string.IsNullOrWhiteSpace(animFilePath))
                            {
                                matched = TryMatchAnimationFileCandidates(targetHash, animFilePath, champName, skinNum, path, wadPath, matcher);
                            }
                        }

                        // D. Fallback fast-path: Universal high-frequency animation clip names
                        if (!matched)
                        {
                            foreach (string commonClip in CommonAnimationClipNames)
                            {
                                if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, commonClip, path, wadPath, targetHash))
                                    break;
                            }
                        }
                    }
                }

                // 2. Track Data Map (mTrackDataMap)
                if (item.Properties.TryGetValue(Fnv1a.HashLower("mTrackDataMap"), out BinTreeProperty trackMapProp) &&
                    trackMapProp is BinTreeMap trackMap)
                {
                    foreach (var pair in trackMap)
                    {
                        if (pair.Key is BinTreeHash trackHash && trackHash.Value != 0)
                        {
                            foreach (string track in CommonAnimationTracks)
                            {
                                if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, track, path, wadPath, trackHash.Value))
                                    break;
                            }
                        }
                    }
                }

                // 3. Mask Data Map (mMaskDataMap)
                if (item.Properties.TryGetValue(Fnv1a.HashLower("mMaskDataMap"), out BinTreeProperty maskMapProp) &&
                    maskMapProp is BinTreeMap maskMap)
                {
                    foreach (var pair in maskMap)
                    {
                        if (pair.Key is BinTreeHash maskHash && maskHash.Value != 0)
                        {
                            foreach (string mask in CommonAnimationMasks)
                            {
                                if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, mask, path, wadPath, maskHash.Value))
                                    break;
                            }
                        }
                    }
                }

                // 4. Sync Group Data Map (mSyncGroupDataMap)
                if (item.Properties.TryGetValue(Fnv1a.HashLower("mSyncGroupDataMap"), out BinTreeProperty syncMapProp) &&
                    syncMapProp is BinTreeMap syncMap)
                {
                    foreach (var pair in syncMap)
                    {
                        if (pair.Key is BinTreeHash syncHash && syncHash.Value != 0)
                        {
                            foreach (string sync in CommonAnimationSyncGroups)
                            {
                                if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, sync, path, wadPath, syncHash.Value))
                                    break;
                            }
                        }
                    }
                }

                // 5. Event Data Map (mEventDataMap)
                if (item.Properties.TryGetValue(Fnv1a.HashLower("mEventDataMap"), out BinTreeProperty eventMapProp) &&
                    eventMapProp is BinTreeMap eventMap)
                {
                    foreach (var pair in eventMap)
                    {
                        if (pair.Key is BinTreeHash eventHash && eventHash.Value != 0 && pair.Value is BinTreeStruct eventStruct)
                        {
                            if (TryGetString(eventStruct.Properties, "mName", out string evName) && !string.IsNullOrWhiteSpace(evName))
                                matcher.CheckContextualCandidate(InternalHashKind.BinHashes, evName, path, wadPath, eventHash.Value);
                            if (TryGetString(eventStruct.Properties, "mEffectName", out string effName) && !string.IsNullOrWhiteSpace(effName))
                                matcher.CheckContextualCandidate(InternalHashKind.BinHashes, effName, path, wadPath, eventHash.Value);
                            if (TryGetString(eventStruct.Properties, "mEffectKey", out string effKey) && !string.IsNullOrWhiteSpace(effKey))
                                matcher.CheckContextualCandidate(InternalHashKind.BinHashes, effKey, path, wadPath, eventHash.Value);
                        }
                    }
                }
            }

            void MatchSkinCharacterData(uint entryHash, BinTreeObject item)
            {
                string skinPath = ResolveEntryPath(entryHash);

                if (skinPath == null && TryGetString(item.Properties, "championSkinName", out string champSkinName))
                {
                    int skinIdx = champSkinName.IndexOf("Skin", StringComparison.OrdinalIgnoreCase);
                    if (skinIdx > 0 && skinIdx < champSkinName.Length - 4)
                    {
                        string champ = champSkinName[..skinIdx];
                        string skinNum = champSkinName[(skinIdx + 4)..].TrimStart('0');
                        if (string.IsNullOrEmpty(skinNum)) skinNum = "0";
                        string candidate = $"Characters/{champ}/Skins/Skin{skinNum}";
                        if (MatchObservedEntry(entryHash, candidate))
                            skinPath = candidate;
                    }
                }

                if (skinPath == null && TryGetStringOrOptional(item.Properties, "iconSquare", out string iconSquare))
                {
                    string normIcon = InternalHashEvidenceMatcher.NormalizeCandidate(iconSquare);
                    if (normIcon.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase))
                    {
                        string sub = normIcon["assets/characters/".Length..];
                        int slash = sub.IndexOf('/');
                        if (slash > 0)
                        {
                            string champ = sub[..slash];
                            MatchObservedEntry(entryHash, $"Characters/{champ}/Skins/Base");
                            for (int i = 0; i < 200; i++)
                            {
                                string candidate = $"Characters/{champ}/Skins/Skin{i}";
                                if (MatchObservedEntry(entryHash, candidate))
                                {
                                    skinPath = candidate;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (skinPath == null && !string.IsNullOrEmpty(path))
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
                            for (int skin = 0; skin < 200; skin++)
                            {
                                string candidate = $"Characters/{champName}/Skins/Skin{skin}";
                                if (MatchObservedEntry(entryHash, candidate))
                                {
                                    skinPath = candidate;
                                    break;
                                }
                                MatchObservedEntry(entryHash, $"Characters/{champName}/Skins/Skin{skin:00}");
                            }
                        }
                    }
                }

                if (skinPath != null)
                {
                    if (TryGetObjectLink(item.Properties, "mResourceResolver", out BinTreeObjectLink resResolverLink) && resResolverLink.Value != 0)
                    {
                        MatchObservedEntry((uint)resResolverLink.Value, $"{skinPath}/Resources");
                    }

                    if (item.Properties.TryGetValue(Fnv1a.HashLower("skinAnimationProperties"), out BinTreeProperty animProp) &&
                        animProp is BinTreeStruct animStruct &&
                        TryGetObjectLink(animStruct.Properties, "animationGraphData", out BinTreeObjectLink animGraphLink) &&
                        animGraphLink.Value != 0)
                    {
                        string[] parts = skinPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4 && parts[0].Equals("Characters", StringComparison.OrdinalIgnoreCase) &&
                            parts[2].Equals("Skins", StringComparison.OrdinalIgnoreCase))
                        {
                            MatchObservedEntry((uint)animGraphLink.Value, $"Characters/{parts[1]}/Animations/{parts[3]}");
                        }
                    }

                    // PR #49: Match Gear entries (Characters/{Champ}/Skins/Skin{N}/Gear/{GearName})
                    string[] skinParts = skinPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    string skinSubFolder = skinParts.Length >= 4 ? skinParts[3] : null;

                    // 1. Inspect skinUpgradeData -> gearSkinUpgrades / mGearSkinUpgrades or mGearList
                    BinTreeContainer gearContainer = null;
                    if (item.Properties.TryGetValue(Fnv1a.HashLower("skinUpgradeData"), out BinTreeProperty upProp) &&
                        upProp is BinTreeEmbedded upEmbedded &&
                        (upEmbedded.Properties.TryGetValue(0xcb522723, out BinTreeProperty upgradesProp) ||
                         upEmbedded.Properties.TryGetValue(Fnv1a.HashLower("gearSkinUpgrades"), out upgradesProp) ||
                         upEmbedded.Properties.TryGetValue(Fnv1a.HashLower("mGearSkinUpgrades"), out upgradesProp)) &&
                        upgradesProp is BinTreeContainer upCont)
                    {
                        gearContainer = upCont;
                    }
                    else if (item.Properties.TryGetValue(Fnv1a.HashLower("mGearList"), out BinTreeProperty gearListProp) &&
                             gearListProp is BinTreeContainer glCont)
                    {
                        gearContainer = glCont;
                    }

                    if (gearContainer != null)
                    {
                        foreach (BinTreeProperty elem in gearContainer.Elements)
                        {
                            if (elem is BinTreeObjectLink gearLink && gearLink.Value != 0)
                            {
                                uint gearHash = (uint)gearLink.Value;
                                if (tree.Objects.TryGetValue(gearHash, out BinTreeObject gearObj))
                                {
                                    if (TryGetString(gearObj.Properties, "mGearName", out string gName) ||
                                        TryGetString(gearObj.Properties, "mName", out gName) ||
                                        TryGetString(gearObj.Properties, "name", out gName))
                                    {
                                        MatchObservedEntry(gearHash, $"{skinPath}/Gear/{gName}");
                                    }
                                }

                                foreach (string cg in CommonGearNames)
                                {
                                    if (MatchObservedEntry(gearHash, $"{skinPath}/Gear/{cg}"))
                                        break;
                                }

                                if (!string.IsNullOrEmpty(skinSubFolder))
                                    MatchObservedEntry(gearHash, $"{skinPath}/Gear/{skinSubFolder}");
                            }
                        }
                    }

                    // 2. Also check any standalone GearSkinUpgrade (0x27dd6361) objects declared in this skin's tree
                    foreach (var pair in tree.Objects)
                    {
                        if (pair.Value.ClassHash == 0x27dd6361)
                        {
                            uint gHash = pair.Key;
                            if (TryGetString(pair.Value.Properties, "mGearName", out string gName) ||
                                TryGetString(pair.Value.Properties, "mName", out gName) ||
                                TryGetString(pair.Value.Properties, "name", out gName))
                            {
                                MatchObservedEntry(gHash, $"{skinPath}/Gear/{gName}");
                            }
                            foreach (string cg in CommonGearNames)
                            {
                                if (MatchObservedEntry(gHash, $"{skinPath}/Gear/{cg}"))
                                    break;
                            }
                            if (!string.IsNullOrEmpty(skinSubFolder))
                                MatchObservedEntry(gHash, $"{skinPath}/Gear/{skinSubFolder}");
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
                    if (TryGetTftSet(name, out int spellSet)) candidates.Add($"Maps/Shipping/Map22/Sets/TFTSet{spellSet}/Spells/{name}");

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
                            matcher.CheckContextualCandidate(InternalHashKind.BinHashes, dataName, path, wadPath, localTargets: localTargets);
                }
            }

            void MatchHashLinkMap(BinTreeObject item, string field) => MatchHashLinkMapProperties(item.Properties, field);

            void MatchHashLinkMapProperties(Dictionary<uint, BinTreeProperty> properties, string field)
            {
                if (!properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) || property is not BinTreeMap map) return;
                foreach (var pair in map)
                {
                    if (pair.Key is not BinTreeHash key || pair.Value is not BinTreeObjectLink link) continue;
                    string target = ResolveEntryPath(link.Value);
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, target, path, wadPath, key.Value)) continue;
                    int slash = target.LastIndexOf('/');
                    if (slash < 0) continue;
                    string basename = target[(slash + 1)..];
                    if (matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename, path, wadPath, key.Value) ||
                        matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename + "_BV2", path, wadPath, key.Value)) continue;
                    if (basename.Contains("Base_", StringComparison.Ordinal))
                        matcher.CheckContextualCandidate(InternalHashKind.BinHashes, basename.Replace("Base_", "", StringComparison.Ordinal), path, wadPath, key.Value);
                    for (int skin = 1; skin < 90; skin++)
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
                if (map.KeyType != BinPropertyType.Hash || map.ValueType != BinPropertyType.ObjectLink) return;
                foreach (var pair in map)
                {
                    if (pair.Key is not BinTreeHash key || pair.Value is not BinTreeObjectLink link) continue;
                    string target = ResolveEntryPath(link.Value);
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    matcher.CheckContextualCandidate(InternalHashKind.BinHashes, target, path, wadPath, key.Value);
                }
            }

            void MatchItemShopGameModeData(BinTreeObject item)
            {
                CheckItemListByHash(item, Fnv1a.HashLower("CompletedItems"));
                CheckItemListByHash(item, 0xc561f8e9);
                CheckItemListByHash(item, 0x37792a41);
                if (item.Properties.TryGetValue(0x891a5676, out BinTreeProperty structProp) &&
                    structProp is BinTreeStruct strct &&
                    strct.Properties.TryGetValue(Fnv1a.HashLower("items"), out BinTreeProperty itemsProp) &&
                    itemsProp is BinTreeContainer container)
                {
                    CheckListContainer(container);
                }
            }

            void MatchGameModeItemList(BinTreeObject item)
            {
                CheckItemListByHash(item, Fnv1a.HashLower("mItems"));
            }

            void CheckItemListByHash(BinTreeObject item, uint fieldHash)
            {
                if (item.Properties.TryGetValue(fieldHash, out BinTreeProperty prop) &&
                    prop is BinTreeContainer container)
                {
                    CheckListContainer(container);
                }
            }

            void CheckListContainer(BinTreeContainer container)
            {
                foreach (BinTreeProperty elem in container.Elements)
                {
                    uint hashVal = 0;
                    bool isHashValue = false;
                    if (elem is BinTreeHash h)
                    {
                        hashVal = h.Value;
                        isHashValue = true;
                    }
                    else if (elem is BinTreeObjectLink l)
                    {
                        hashVal = (uint)l.Value;
                    }

                    if (hashVal == 0) continue;
                    if (isHashValue) matcher.ObserveItemListHash(hashVal);

                    string candidate = ResolveEntryPath(hashVal);
                    if (!string.IsNullOrWhiteSpace(candidate))
                        matcher.CheckContextualCandidate(InternalHashKind.BinHashes, candidate, path, wadPath, hashVal);
                }
            }

            void MatchCollectedItemHashes()
            {
                foreach (uint hash in matcher.GetCollectedItemHashCandidates())
                {
                    if (!matcher.IsRemaining(InternalHashKind.BinHashes, hash)) continue;
                    string candidate = ResolveEntryPath(hash);
                    if (!string.IsNullOrWhiteSpace(candidate))
                        matcher.CheckContextualCandidate(InternalHashKind.BinHashes, candidate, path, wadPath, hash);
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
            void MatchEntryDirect(uint entryHash, BinTreeObject item, string field)
            {
                if (TryGetString(item.Properties, field, out string val) && !string.IsNullOrWhiteSpace(val))
                    MatchObservedEntry(entryHash, val);
            }
            // Character folders often come from the lowercase BIN path; restore Riot casing.
            bool MatchObservedEntry(uint hash, string value) =>
                matcher.CheckContextualCandidate(InternalHashKind.BinEntries, casing.Recase(value, new[] { value }), path, wadPath, hash);
            void MatchLinkedEntry(uint linkHash)
            {
                if (resolver != null) MatchResolvedEntryLink(linkHash, matcher, resolver, path, wadPath);
            }
            void MatchAnyEntry(string value) => matcher.CheckContextualCandidate(InternalHashKind.BinEntries, value, path, wadPath, localTargets: localTargets);

            void Visit(BinTreeProperty property, uint parentClassHash = 0)
            {
                if (property is BinTreeContainer containerProp && parentClassHash != 0)
                {
                    MatchContainerElementEvidence(parentClassHash, property.NameHash, containerProp);
                }

                switch (property)
                {
                    case BinTreeStruct structure:
                        uint structClass = structure.ClassHash != 0 ? structure.ClassHash : parentClassHash;
                        foreach (BinTreeProperty child in structure.Properties.Values) Visit(child, structClass);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements)
                        {
                            if (child is BinTreeStruct childStruct && childStruct.ClassHash != 0)
                                Visit(childStruct, childStruct.ClassHash);
                            else
                                Visit(child, parentClassHash);
                        }
                        break;
                    case BinTreeOptional option when option.Value != null:
                        Visit(option.Value, parentClassHash);
                        break;
                    case BinTreeMap map:
                        MatchGenericHashLinkMap(map);
                        foreach (var child in map) { Visit(child.Key, parentClassHash); Visit(child.Value, parentClassHash); }
                        break;
                }
            }

            void MatchContainerElementEvidence(uint parentClassHash, uint fieldHash, BinTreeContainer container)
            {
                if (container.Elements.Count == 0 || fieldHash == 0) return;
                foreach (BinTreeProperty elem in container.Elements)
                {
                    if (elem is BinTreeStruct childStruct && childStruct.ClassHash != 0)
                    {
                        uint childClass = childStruct.ClassHash;
                        if (!matcher.IsRemaining(InternalHashKind.BinTypes, childClass)) continue;

                        string parentName = resolver?.ResolveBinType(parentClassHash);
                        string fieldName = resolver?.ResolveBinField(fieldHash);
                        if (string.IsNullOrWhiteSpace(parentName) || string.IsNullOrWhiteSpace(fieldName) ||
                            parentName.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                            fieldName.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string stem = StripContainerSuffix(parentName);
                        string singular = Singularize(fieldName);

                        var candidates = new List<string>(14)
                        {
                            stem + singular + "Definition",
                            stem + singular + "Data",
                            parentName + singular + "Definition",
                            parentName + singular + "Data",
                            singular + "Definition",
                            singular + "Data",
                            stem + singular,
                            parentName + singular
                        };

                        if (fieldName.EndsWith("tags", StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(stem + "TagDefinition");
                            candidates.Add(stem + "RegionTagDefinition");
                            candidates.Add(stem + "TerrainTagDefinition");
                            candidates.Add(stem + "TagsLink");
                            candidates.Add(stem + "RegionTagsLink");
                        }

                        if (fieldName.EndsWith("VfxSystems", StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(stem + "VfxData");
                            candidates.Add(stem + "VfxSystemData");
                        }

                        foreach (string candidate in candidates)
                        {
                            if (Fnv1a.HashLower(candidate) == childClass)
                            {
                                matcher.CheckContextualCandidate(
                                    InternalHashKind.BinTypes,
                                    candidate,
                                    path,
                                    wadPath,
                                    childClass,
                                    InternalHashEvidence.ContainerElementConvention);
                                break;
                            }
                        }
                    }
                }
            }

            static bool TryGetString(Dictionary<uint, BinTreeProperty> properties, string field, out string value) =>
                TryGetStringByHash(properties, Fnv1a.HashLower(field), out value);


            static bool TryGetStringOrOptional(Dictionary<uint, BinTreeProperty> properties, string field, out string value)
            {
                if (!properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property))
                {
                    value = null;
                    return false;
                }

                if (property is BinTreeString text)
                {
                    value = text.Value;
                    return true;
                }

                if (property is BinTreeOptional option && option.Value is BinTreeString optionalText)
                {
                    value = optionalText.Value;
                    return true;
                }

                value = null;
                return false;
            }

            static bool TryGetStringByHash(Dictionary<uint, BinTreeProperty> properties, uint fieldHash, out string value)
            {
                if (properties.TryGetValue(fieldHash, out BinTreeProperty property) && property is BinTreeString text)
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

            static string StripContainerSuffix(string name)
            {
                if (string.IsNullOrEmpty(name)) return name;
                foreach (string sfx in new[] { "Config", "Definition", "Settings", "Parameters", "Data", "Container", "List", "Manager", "System" })
                {
                    if (name.EndsWith(sfx, StringComparison.Ordinal) && name.Length > sfx.Length + 2)
                        return name[..^sfx.Length];
                }
                return name;
            }

            static string Singularize(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return name;
                if (name.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
                    return UpperFirst(name[..^3] + "y");
                if (name.EndsWith("List", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
                    return UpperFirst(name[..^4]);
                if (name.EndsWith("Map", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
                    return UpperFirst(name[..^3]);
                if (name.EndsWith("Array", StringComparison.OrdinalIgnoreCase) && name.Length > 5)
                    return UpperFirst(name[..^5]);
                if (name.EndsWith('s') || name.EndsWith('S'))
                {
                    if (name.EndsWith("ss", StringComparison.OrdinalIgnoreCase)) return UpperFirst(name);
                    return UpperFirst(name[..^1]);
                }
                return UpperFirst(name);
            }

            static string UpperFirst(string str) =>
                string.IsNullOrEmpty(str) ? str : char.ToUpperInvariant(str[0]) + str[1..];
        }
    }
}
