using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Viewer
{
    public enum ChromaSourceKind
    {
        Current,
        Reference
    }

    public class ChromaSkinModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _name;
        private string _texturePath;
        private string _previewTextureName;
        private string _modelPath;
        private string _sourceRoot;
        private ChromaSourceKind _sourceKind;
        private Color _swatchColor = Colors.Transparent;
        private ImageSource _previewImage;

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        public string TexturePath
        {
            get => _texturePath;
            set { if (_texturePath != value) { _texturePath = value; OnPropertyChanged(); } }
        }

        public string PreviewTextureName
        {
            get => _previewTextureName;
            set { if (_previewTextureName != value) { _previewTextureName = value; OnPropertyChanged(); } }
        }

        public string ModelPath
        {
            get => _modelPath;
            set { if (_modelPath != value) { _modelPath = value; OnPropertyChanged(); } }
        }

        public string SourceRoot
        {
            get => _sourceRoot;
            set { if (_sourceRoot != value) { _sourceRoot = value; OnPropertyChanged(); } }
        }

        public ChromaSourceKind SourceKind
        {
            get => _sourceKind;
            set
            {
                if (_sourceKind == value) return;
                _sourceKind = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsReference));
                OnPropertyChanged(nameof(SourceLabel));
            }
        }

        public bool IsReference => SourceKind == ChromaSourceKind.Reference;
        public string SourceLabel => IsReference ? "REFERENCE" : "CURRENT";

        public Color SwatchColor
        {
            get => _swatchColor;
            set { if (_swatchColor != value) { _swatchColor = value; OnPropertyChanged(); } }
        }

        public ImageSource PreviewImage
        {
            get => _previewImage;
            set { if (_previewImage != value) { _previewImage = value; OnPropertyChanged(); } }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ChromaFamilyModel
    {
        public string Name { get; init; }
        public string ModelName { get; init; }
        public string ModelPath { get; init; }
        public ImageSource PreviewImage { get; init; }
        public Color SwatchColor { get; init; } = Colors.Transparent;
        public ObservableRangeCollection<ChromaSkinModel> Chromas { get; } = new();
        public int ChromaCount => Chromas.Count;
        public string ChromaCountText => ChromaCount == 1
            ? "1 CHROMA"
            : $"{ChromaCount} CHROMAS";
    }

    public class ChromaSelectionModel : INotifyPropertyChanged
    {
        private bool _isLoading;
        private string _statusText = "Ready to scan.";
        private string _currentSourcePath;
        private string _referenceSourcePath;
        private ChromaFamilyModel _selectedFamily;

        public ObservableRangeCollection<ChromaFamilyModel> AvailableFamilies { get; } = new();

        public bool IsLoading
        {
            get => _isLoading;
            set { if (_isLoading != value) { _isLoading = value; OnPropertyChanged(); } }
        }

        public string StatusText
        {
            get => _statusText;
            private set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public string CurrentSourcePath
        {
            get => _currentSourcePath;
            private set
            {
                if (_currentSourcePath == value) return;
                _currentSourcePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SourceCount));
            }
        }

        public string ReferenceSourcePath
        {
            get => _referenceSourcePath;
            private set
            {
                if (_referenceSourcePath == value) return;
                _referenceSourcePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasReference));
                OnPropertyChanged(nameof(SourceCount));
            }
        }

        public ChromaFamilyModel SelectedFamily
        {
            get => _selectedFamily;
            set
            {
                if (_selectedFamily == value) return;
                _selectedFamily = value;
                OnPropertyChanged();
            }
        }

        public bool HasReference => !string.IsNullOrWhiteSpace(ReferenceSourcePath);
        public int SourceCount => string.IsNullOrWhiteSpace(CurrentSourcePath)
            ? 0
            : HasReference ? 2 : 1;
        public int FamilyCount => AvailableFamilies.Count;
        public int ChromaCount => AvailableFamilies.Sum(family => family.ChromaCount);
        public int SelectedCount => AvailableFamilies.Sum(
            family => family.Chromas.Count(chroma => chroma.IsSelected));
        public bool HasSelection => SelectedCount > 0;
        public string SelectionText => SelectedCount == 1
            ? "1 CHROMA SELECTED"
            : $"{SelectedCount} CHROMAS SELECTED";
        public IEnumerable<ChromaSkinModel> SelectedChromas =>
            AvailableFamilies.SelectMany(family => family.Chromas)
                .Where(chroma => chroma.IsSelected);

        public void SetFamilies(IEnumerable<ChromaFamilyModel> families)
        {
            ClearFamilies();
            AvailableFamilies.ReplaceRange(families ?? Enumerable.Empty<ChromaFamilyModel>());
            foreach (ChromaSkinModel chroma in AvailableFamilies.SelectMany(family => family.Chromas))
                chroma.PropertyChanged += ChromaPropertyChanged;
            SelectedFamily = AvailableFamilies.FirstOrDefault();
            RefreshCounts();
        }

        public void RefreshSelection()
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionText));
        }

        public void SetScanningState(string folderName, string sourcePath)
        {
            IsLoading = true;
            ClearFamilies();
            SelectedFamily = null;
            CurrentSourcePath = sourcePath;
            ReferenceSourcePath = null;
            RefreshCounts();
            StatusText = $"Scanning chromas in: {folderName.ToUpperInvariant()}";
        }

        public void SetReferenceScanningState()
        {
            IsLoading = true;
            StatusText = "Scanning reference chromas...";
        }

        public void SetReferenceSource(string sourcePath)
        {
            ReferenceSourcePath = sourcePath;
        }

        public void ClearReferenceSource()
        {
            ReferenceSourcePath = null;
        }

        public void SetEmptyState()
        {
            IsLoading = false;
            StatusText = "No chroma families were found in this directory.";
        }

        public void SetSuccessState()
        {
            IsLoading = false;
            string familyText = FamilyCount == 1 ? "1 skin family" : $"{FamilyCount} skin families";
            string sourceSkinText = FamilyCount == 1 ? "1 source skin" : $"{FamilyCount} source skins";
            string chromaText = ChromaCount == 1 ? "1 chroma" : $"{ChromaCount} chromas";
            StatusText = SourceCount > 1
                ? $"{SourceCount} sources · {familyText} · {chromaText} detected"
                : $"{sourceSkinText} · {chromaText} detected";
        }

        public void SetErrorState(string message)
        {
            IsLoading = false;
            StatusText = $"Error: {message}";
        }

        public void Reset()
        {
            ClearFamilies();
            SelectedFamily = null;
            CurrentSourcePath = null;
            ReferenceSourcePath = null;
            StatusText = "Ready to scan.";
            IsLoading = false;
            RefreshCounts();
        }

        private void ChromaPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChromaSkinModel.IsSelected))
                RefreshSelection();
        }

        private void ClearFamilies()
        {
            foreach (ChromaSkinModel chroma in AvailableFamilies.SelectMany(family => family.Chromas))
                chroma.PropertyChanged -= ChromaPropertyChanged;
            AvailableFamilies.Clear();
        }

        private void RefreshCounts()
        {
            OnPropertyChanged(nameof(FamilyCount));
            OnPropertyChanged(nameof(ChromaCount));
            RefreshSelection();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
