using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// One named bit of a map visibility domain (MapVisibilityFlagDefinition).
    /// </summary>
    internal sealed record MapVisibilityFlagData(int BitIndex, string Name, string PublicName)
    {
        public int Flag => 1 << BitIndex;

        public string Label =>
            !string.IsNullOrWhiteSpace(PublicName) ? PublicName :
            !string.IsNullOrWhiteSpace(Name) ? Name :
            BitIndex == 0 ? "Base" : $"Layer {BitIndex + 1}";
    }

    /// <summary>
    /// A set of mutually exclusive map states sharing one 8-bit mask. The primary domain drives
    /// MAPGEO/placeable masks (rift transformations); the secondary one only feeds controllers (e.g. Baron pit).
    /// </summary>
    internal sealed record MapVisibilityDomainData(
        IReadOnlyList<MapVisibilityFlagData> Flags,
        int InitialMask,
        int MinIndex,
        int MaxIndex)
    {
        public bool IsEmpty => Flags == null || Flags.Count == 0;

        public MapVisibilityFlagData Find(int bitIndex) =>
            Flags?.FirstOrDefault(flag => flag.BitIndex == bitIndex);
    }

    /// <summary>
    /// Visibility domains declared by the owning Map object (map&lt;id&gt;.bin).
    /// </summary>
    internal sealed record MapVisibilityDefinitions(
        MapVisibilityDomainData Primary,
        MapVisibilityDomainData Secondary)
    {
        public static MapVisibilityDefinitions Empty { get; } = new(null, null);
    }

    internal enum MapVisibilityControllerKind
    {
        Unknown,
        Mutator,
        Child,
        PrimaryFlags,
        SecondaryFlags,
        Named
    }

    /// <summary>
    /// ParentMode of ChildMapVisibilityController. Values 1 and 3 are observed on Summoner's Rift
    /// as OR (base or upgraded Baron pit) and NOR (base pieces removed by any transformation).
    /// </summary>
    internal enum MapVisibilityParentMode : uint
    {
        All = 0,
        Any = 1,
        NotAll = 2,
        None = 3
    }

    internal sealed record MapVisibilityControllerData(
        uint PathHash,
        MapVisibilityControllerKind Kind,
        int Mask = 0,
        bool DefaultVisible = true,
        string MutatorName = null,
        IReadOnlyList<uint> Parents = null,
        MapVisibilityParentMode ParentMode = MapVisibilityParentMode.All);

    /// <summary>
    /// The game state a MAP preview is evaluated against: primary/secondary domain masks and the
    /// mutators the game would have applied. Immutable with value equality so "no change" is detectable.
    /// </summary>
    internal sealed class MapVisibilityState : IEquatable<MapVisibilityState>
    {
        private static readonly string[] NoMutators = Array.Empty<string>();

        public int Flags { get; }
        public int SecondaryFlags { get; }
        public IReadOnlyList<string> Mutators { get; }

        public MapVisibilityState(int flags, int secondaryFlags = 1, IEnumerable<string> mutators = null)
        {
            Flags = flags & 0xff;
            SecondaryFlags = secondaryFlags & 0xff;
            Mutators = mutators?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? NoMutators;
        }

        public static MapVisibilityState FromFlags(int flags) => new(flags);

        public bool HasMutator(string name) =>
            !string.IsNullOrWhiteSpace(name) &&
            Mutators.Any(mutator => string.Equals(mutator, name, StringComparison.OrdinalIgnoreCase));

        public MapVisibilityState WithFlags(int flags) => new(flags, SecondaryFlags, Mutators);

        public MapVisibilityState WithSecondaryFlags(int flags) => new(Flags, flags, Mutators);

        public MapVisibilityState WithMutator(string name, bool applied) =>
            new(Flags, SecondaryFlags, applied
                ? Mutators.Append(name)
                : Mutators.Where(mutator => !string.Equals(mutator, name, StringComparison.OrdinalIgnoreCase)));

        public bool Equals(MapVisibilityState other) =>
            other != null &&
            Flags == other.Flags &&
            SecondaryFlags == other.SecondaryFlags &&
            Mutators.SequenceEqual(other.Mutators, StringComparer.OrdinalIgnoreCase);

        public override bool Equals(object obj) => Equals(obj as MapVisibilityState);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Flags);
            hash.Add(SecondaryFlags);
            foreach (string mutator in Mutators)
                hash.Add(mutator, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }

        public override string ToString() =>
            $"flags=0x{Flags:x2}, secondary=0x{SecondaryFlags:x2}, mutators=[{string.Join(", ", Mutators)}]";
    }

    /// <summary>
    /// Visibility contract of one decoded map scene: declared domains, the controller graph of its
    /// materials BIN and the state it opens on.
    /// </summary>
    internal sealed class MapSceneVisibility
    {
        public static MapSceneVisibility Empty { get; } = new(
            MapVisibilityDefinitions.Empty,
            new Dictionary<uint, MapVisibilityControllerData>(),
            MapVisibilityState.FromFlags(1));

        public MapVisibilityDefinitions Definitions { get; }
        public IReadOnlyDictionary<uint, MapVisibilityControllerData> Controllers { get; }
        public MapVisibilityState Opening { get; }

        public MapSceneVisibility(
            MapVisibilityDefinitions definitions,
            IReadOnlyDictionary<uint, MapVisibilityControllerData> controllers,
            MapVisibilityState opening)
        {
            Definitions = definitions ?? MapVisibilityDefinitions.Empty;
            Controllers = controllers ?? new Dictionary<uint, MapVisibilityControllerData>();
            Opening = opening ?? MapVisibilityState.FromFlags(1);
        }

        public IReadOnlyList<string> MutatorNames => Controllers.Values
            .Where(controller => controller.Kind == MapVisibilityControllerKind.Mutator &&
                                 !string.IsNullOrWhiteSpace(controller.MutatorName))
            .Select(controller => controller.MutatorName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        /// <summary>Secondary-domain bits some controller reads, used when the Map object is not available.</summary>
        public int SecondaryMaskInUse => Controllers.Values
            .Where(controller => controller.Kind == MapVisibilityControllerKind.SecondaryFlags)
            .Aggregate(0, (mask, controller) => mask | controller.Mask);

        public MapSceneVisibility WithOpening(MapVisibilityState opening) =>
            new(Definitions, Controllers, opening);
    }
}
