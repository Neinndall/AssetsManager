using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Dialogs.Controls;

public sealed class SknDiffInspectionModel : INotifyPropertyChanged
{
    public IReadOnlyList<string> Attributes { get; } = new[]
    {
        "All attributes", "Positions", "Normals", "UV", "Colors", "Bone indices", "Bone weights", "Tangents", "Attribute combinations"
    };

    private string _attribute = "All attributes";
    private string _summary = "No vertex data changes";
    private bool _vertices = true, _normals, _edges, _surfaces, _changedOnly = true, _throughMesh;
    private double _pointSize = 4, _normalLength = 3;

    public string Attribute { get => _attribute; set { if (value != null) Set(ref _attribute, value); } }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public bool Vertices { get => _vertices; set => Set(ref _vertices, value); }
    public bool Normals { get => _normals; set => Set(ref _normals, value); }
    public bool Edges { get => _edges; set => Set(ref _edges, value); }
    public bool Surfaces { get => _surfaces; set => Set(ref _surfaces, value); }
    public bool ChangedOnly { get => _changedOnly; set => Set(ref _changedOnly, value); }
    public bool ThroughMesh { get => _throughMesh; set => Set(ref _throughMesh, value); }
    public double PointSize { get => _pointSize; set => Set(ref _pointSize, value); }
    public double NormalLength { get => _normalLength; set => Set(ref _normalLength, value); }

    public event PropertyChangedEventHandler PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new(property));
    }
}
