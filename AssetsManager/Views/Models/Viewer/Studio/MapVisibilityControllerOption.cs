using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using AssetsManager.Services.Viewer.Semantics;

namespace AssetsManager.Views.Models.Viewer;

public sealed class MapVisibilityControllerOption : INotifyPropertyChanged
{
    private readonly Action<uint, bool> _changed;
    private bool _isVisible;
    private bool _isOverridden;

    internal MapVisibilityControllerOption(MapControllerRow row, Action<uint, bool> changed)
    {
        PathHash = row.Controller.PathHash;
        Label = row.Controller.Name ?? $"0x{PathHash:x8}";
        DisplayLabel = row.Controller.Kind == MapVisibilityControllerKind.Driven ? $"Manual state · {Label}" : Label;
        IsDependent = row.Depth != 0;
        Indent = new Thickness(Math.Min(row.Depth, 12) * 12, 0, 0, 0);
        Relation = row.Relation;
        MeshCount = row.MeshCount;
        MaterialTooltip = string.Join(Environment.NewLine, row.Materials.Take(8));
        if (row.Materials.Count > 8) MaterialTooltip += $"{Environment.NewLine}+{row.Materials.Count - 8} more";
        string condition = row.Controller.ParentMode switch
        {
            MapVisibilityParentMode.All => "Shown while all parents are visible",
            MapVisibilityParentMode.Any => "Shown while any parent is visible",
            MapVisibilityParentMode.One => "Shown while exactly one parent is visible",
            MapVisibilityParentMode.None => "Hidden while any parent is visible",
            _ => string.Empty
        };
        string parents = row.Controller.Kind == MapVisibilityControllerKind.Child
            ? $"{Environment.NewLine}{condition}{Environment.NewLine}Parents: " +
              string.Join(", ", (row.Controller.Parents ?? Array.Empty<uint>()).Select(hash => $"0x{hash:x8}")) : string.Empty;
        Tooltip = $"{DisplayLabel}{Environment.NewLine}0x{PathHash:x8}{parents}";
        _changed = changed;
    }

    public uint PathHash { get; }
    public string Label { get; }
    public string DisplayLabel { get; }
    public string Tooltip { get; }
    public string MaterialTooltip { get; }
    public int MeshCount { get; }
    public string MeshCountText => MeshCount == 1 ? "1 mesh" : $"{MeshCount:N0} meshes";
    public bool HasMeshes => MeshCount != 0;
    public bool IsDependent { get; }
    public bool CanToggle => !IsDependent;
    public Thickness Indent { get; }
    public string Relation { get; }
    public string StateText => IsVisible ? "Visible" : "Hidden";
    public bool IsOverridden => _isOverridden;
    public string OverrideText => _isOverridden ? "Manual override" : "Computed state";

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!CanToggle || _isVisible == value) return;
            _changed?.Invoke(PathHash, value);
        }
    }

    internal void Sync(bool visible, bool overridden)
    {
        if (_isVisible != visible)
        {
            _isVisible = visible;
            Changed(nameof(IsVisible));
            Changed(nameof(StateText));
        }
        if (_isOverridden != overridden)
        {
            _isOverridden = overridden;
            Changed(nameof(IsOverridden));
            Changed(nameof(OverrideText));
        }
    }

    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public event PropertyChangedEventHandler PropertyChanged;
}
