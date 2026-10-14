using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Tests.Support;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-clip-event-census [--bones]`: walks every animation BIN of the installed champion WADs and counts how
    /// ParticleEventData authors its optional fields (scale, detachable, loop, kill), with examples. With --bones it
    /// also loads each skin's skeleton and counts event bones (mBoneName) the skeleton does not have.
    /// </summary>
    internal static class VfxClipEventCensusDiagnostic
    {
        private static readonly uint ParticleEventClass = Fnv1a.HashLower("ParticleEventData");
        private static readonly uint PairList = Fnv1a.HashLower("mParticleEventDataPairList");
        private static readonly uint BoneName = Fnv1a.HashLower("mBoneName");

        public static void Run(string[] args)
        {
            var fields = new Dictionary<string, uint>
            {
                ["scale"] = Fnv1a.HashLower("scale"),
                ["mIsDetachable"] = Fnv1a.HashLower("mIsDetachable"),
                ["mIsLoop"] = Fnv1a.HashLower("mIsLoop"),
                ["mIsKillEvent"] = Fnv1a.HashLower("mIsKillEvent"),
                ["mEndFrame"] = Fnv1a.HashLower("mEndFrame"),
                ["mFireIfAnimationEndsEarly"] = Fnv1a.HashLower("mFireIfAnimationEndsEarly"),
                ["SkipIfPastEndFrame"] = Fnv1a.HashLower("SkipIfPastEndFrame"),
                ["mScalePlaySpeedWithAnimation"] = Fnv1a.HashLower("mScalePlaySpeedWithAnimation"),
                ["mIsSelfOnly"] = Fnv1a.HashLower("mIsSelfOnly"),
                ["mEnemyEffectKey"] = Fnv1a.HashLower("mEnemyEffectKey")
            };
            var byHash = fields.ToDictionary(pair => pair.Value, pair => pair.Key);
            var counts = new Dictionary<string, int>();
            var examples = new Dictionary<string, List<string>>();
            var scales = new Dictionary<string, int>();
            int events = 0, bins = 0, bonesChecked = 0, bonesMissing = 0, skinsWithoutSkeleton = 0;
            bool checkBones = args.Contains("--bones");
            HashSet<uint> joints = null;
            var missingExamples = new List<string>();

            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var binPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string path = line[(space + 1)..];
                if (path.StartsWith("data/characters/", StringComparison.Ordinal) && path.Contains("/animations/", StringComparison.Ordinal) &&
                    path.EndsWith(".bin", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    binPaths[hash] = path;
            }

            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL\Champions");
            var seen = new HashSet<ulong>();
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client")
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys)
                {
                    if (!binPaths.TryGetValue(chunk, out string binPath) || !seen.Add(chunk)) continue;
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }
                    bins++;
                    joints = checkBones ? SkeletonJoints(wad, binPath) : null;
                    if (checkBones && joints == null) skinsWithoutSkeleton++;
                    foreach (BinTreeObject item in tree.Objects.Values)
                        foreach (BinTreeProperty property in item.Properties.Values)
                            Walk(property, binPath);
                }
            }

            Console.WriteLine($"[ClipEvents] bins={bins} ParticleEventData={events}");
            foreach ((string name, int count) in counts.OrderByDescending(pair => pair.Value))
            {
                Console.WriteLine($"[ClipEvents]   {name}: {count}");
                foreach (string example in examples[name].Take(4))
                    Console.WriteLine($"[ClipEvents]     e.g. {example}");
            }
            if (checkBones)
            {
                Console.WriteLine($"[ClipEvents] bones checked={bonesChecked} missing from skeleton={bonesMissing} skins without skeleton={skinsWithoutSkeleton}");
                foreach (string example in missingExamples)
                    Console.WriteLine($"[ClipEvents]   missing {example}");
            }
            Console.WriteLine("[ClipEvents] scale values: " + string.Join(", ", scales.OrderByDescending(pair => pair.Value).Take(12).Select(pair => $"{pair.Key}x{pair.Value}")));

            void Walk(BinTreeProperty property, string binPath)
            {
                switch (property)
                {
                    case BinTreeStruct structure:
                        if (structure.ClassHash == ParticleEventClass)
                            Count(structure, binPath);
                        foreach (BinTreeProperty child in structure.Properties.Values)
                            Walk(child, binPath);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements)
                            Walk(child, binPath);
                        break;
                    case BinTreeMap map:
                        foreach (KeyValuePair<BinTreeProperty, BinTreeProperty> pair in map)
                            Walk(pair.Value, binPath);
                        break;
                    case BinTreeOptional optional when optional.Value != null:
                        Walk(optional.Value, binPath);
                        break;
                }
            }

            void Count(BinTreeStruct particle, string binPath)
            {
                events++;
                if (joints != null && particle.Properties.TryGetValue(PairList, out BinTreeProperty pairs) && pairs is BinTreeContainer pairList)
                {
                    foreach (BinTreeStruct pair in pairList.Elements.OfType<BinTreeStruct>())
                    {
                        uint? bone = pair.Properties.TryGetValue(BoneName, out BinTreeProperty value)
                            ? value switch { BinTreeHash h => h.Value, BinTreeString text => Fnv1a.HashLower(text.Value), _ => null }
                            : null;
                        if (bone is not { } hash || hash == 0) continue;
                        bonesChecked++;
                        if (joints.Contains(hash)) continue;
                        bonesMissing++;
                        if (missingExamples.Count < 12) missingExamples.Add($"{binPath} bone=0x{hash:x8}{(pair.Properties[BoneName] is BinTreeString named ? " " + named.Value : string.Empty)}");
                    }
                }
                foreach ((uint hash, BinTreeProperty value) in particle.Properties)
                {
                    if (!byHash.TryGetValue(hash, out string name)) continue;
                    string shown = value switch
                    {
                        BinTreeF32 f => f.Value.ToString("0.###", CultureInfo.InvariantCulture),
                        BinTreeBool b => b.Value ? "true" : "false",
                        BinTreeI32 i => i.Value.ToString(CultureInfo.InvariantCulture),
                        BinTreeU32 u => u.Value.ToString(CultureInfo.InvariantCulture),
                        _ => value.GetType().Name
                    };
                    if (value is BinTreeBool { Value: false }) continue;
                    if (name == "scale")
                    {
                        scales[shown] = scales.GetValueOrDefault(shown) + 1;
                        if (shown == "1") continue;
                    }
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                    if (!examples.TryGetValue(name, out var list)) examples[name] = list = new List<string>();
                    if (list.Count < 4) list.Add($"{binPath} {name}={shown}");
                }
            }
        }

        /// <summary>Joint name hashes of the skeleton the matching skin BIN (skins/skinN.bin) authors, or null.</summary>
        private static HashSet<uint> SkeletonJoints(WadFile wad, string animationBin)
        {
            string skinBin = animationBin.Replace("/animations/", "/skins/", StringComparison.Ordinal);
            if (!wad.Chunks.ContainsKey(XxHash64Ext.Hash(skinBin))) return null;
            try
            {
                using var data = wad.LoadChunkDecompressed(XxHash64Ext.Hash(skinBin));
                using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                var owner = AssetsManager.Services.Viewer.Vfx.Parsing.VfxAnimationParser.ExtractOwnerSceneContext(new BinTree(stream));
                if (string.IsNullOrWhiteSpace(owner?.SkeletonPath)) return null;
                ulong skeleton = XxHash64Ext.Hash(owner.SkeletonPath.ToLowerInvariant());
                if (!wad.Chunks.ContainsKey(skeleton)) return null;
                using var rigData = wad.LoadChunkDecompressed(skeleton);
                using var rigStream = new MemoryStream(rigData.Span.ToArray(), writable: false);
                var rig = new LeagueToolkit.Core.Animation.RigResource(rigStream);
                return rig.Joints.Select(joint => Fnv1a.HashLower(joint.Name)).ToHashSet();
            }
            catch
            {
                return null;
            }
        }
    }
}
