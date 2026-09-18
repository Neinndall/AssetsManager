using System;
using System.Collections.Generic;
using System.Globalization;

namespace AssetsManager.Views.Models.Viewer
{
    public enum AnimationGraphDeckView
    {
        Timeline,
        Graph
    }

    public enum AnimationGraphInspectorTab
    {
        Clips,
        Tracks,
        Masks,
        SyncGroups
    }

    public sealed record AnimationGraphChildInspectorItem(
        uint Hash,
        string Name,
        bool Declared,
        float? Parameter,
        bool IsPlayable)
    {
        public string ParameterText => Parameter.HasValue && float.IsFinite(Parameter.Value)
            ? Parameter.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : string.Empty;

        public string DisplayText => string.IsNullOrWhiteSpace(ParameterText)
            ? Name
            : $"{Name}  {ParameterText}";
    }

    public sealed record AnimationGraphEventInspectorItem(
        string Kind,
        string FrameRange,
        string Summary);

    public sealed record AnimationGraphClipInspectorItem(
        AnimationClipDefinition Definition,
        AnimationClipCatalogItem CatalogItem,
        string Name,
        string Kind,
        string RateText,
        string TrackName,
        bool TrackDeclared,
        string MaskName,
        bool MaskDeclared,
        string SyncGroupName,
        bool SyncGroupDeclared,
        int EventCount,
        IReadOnlyList<AnimationGraphChildInspectorItem> Children,
        IReadOnlyList<AnimationGraphEventInspectorItem> Events)
    {
        public uint Hash => Definition?.OwnerPathHash ?? 0u;
        public uint GraphHash => Definition?.GraphPathHash ?? 0u;
        public bool IsPlayable => CatalogItem != null;
        public bool HasChildren => Children is { Count: > 0 };
        public bool HasEvents => Events is { Count: > 0 };
        public string HashText => $"0x{Hash:x8}";
        public string TimingText => Definition == null
            ? "-"
            : $"frames {Definition.StartFrame:0.###} → {Definition.EndFrame:0.###} · tick {Definition.TickDuration:0.####}";
        public string FlagsText => Definition?.Flags > 0 ? $"0x{Definition.Flags:x8}" : "-";
        public string TrackDisplay => ReferenceDisplay(TrackName, TrackDeclared);
        public string MaskDisplay => ReferenceDisplay(MaskName, MaskDeclared);
        public string SyncGroupDisplay => ReferenceDisplay(SyncGroupName, SyncGroupDeclared);
        public string InterruptionGroupsText => Definition?.InterruptionGroups is { Count: > 0 }
            ? string.Join(", ", Definition.InterruptionGroups)
            : "-";
        public string AnimationPath => string.IsNullOrWhiteSpace(Definition?.AnimationFilePath)
            ? "-"
            : Definition.AnimationFilePath;

        private static string ReferenceDisplay(string name, bool declared)
            => string.IsNullOrWhiteSpace(name) || declared ? name : $"{name} !";
    }

    public sealed record AnimationMaskInspectorItem(
        AnimationMaskDefinition Definition,
        int WeightedJointCount)
    {
        public uint Hash => Definition?.Hash ?? 0u;
        public string Name => Definition?.Name ?? string.Empty;
        public uint Id => Definition?.Id ?? 0u;
        public int TotalJointCount => Definition?.Weights?.Count ?? 0;
        public string JointCountText => $"{WeightedJointCount}/{TotalJointCount}";
    }

    public sealed record AnimationMaskJointInspectorItem(
        int Slot,
        string JointName,
        float Weight)
    {
        public string WeightText => Weight.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
