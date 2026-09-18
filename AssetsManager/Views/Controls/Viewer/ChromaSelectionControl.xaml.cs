using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using Microsoft.Win32;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class ChromaSelectionControl : UserControl
    {
        private readonly ChromaSelectionModel _viewModel;
        private List<ChromaFamilyModel> _currentFamilies = new();
        private List<ChromaFamilyModel> _referenceFamilies = new();
        private string _currentSkinsPath;

        public ChromaSelectionModel ViewModel => _viewModel;

        public ChromaLoadingService ChromaLoadingService { get; set; }

        public ViewerPanelControl ParentPanel { get; set; }

        public ChromaSelectionControl()
        {
            InitializeComponent();

            _viewModel = new ChromaSelectionModel();
            DataContext = _viewModel;
        }

        public async Task InitializeAsync(string skinsPath)
        {
            if (ChromaLoadingService == null) return;

            _currentSkinsPath = NormalizePath(skinsPath);
            _currentFamilies = new List<ChromaFamilyModel>();
            _referenceFamilies = new List<ChromaFamilyModel>();
            _viewModel.SetScanningState(Path.GetFileName(_currentSkinsPath), _currentSkinsPath);

            try
            {
                var families = await ChromaLoadingService.LoadFamiliesAsync(skinsPath);
                TagSource(families, ChromaSourceKind.Current, _currentSkinsPath);
                _currentFamilies = families;
                ApplyCombinedFamilies();

                if (families.Count == 0)
                    _viewModel.SetEmptyState();
                else
                    _viewModel.SetSuccessState();
            }
            catch (Exception ex)
            {
                _viewModel.SetErrorState(ex.Message);
            }
        }

        private void FamilyListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FamilyListBox.SelectedItem is ChromaFamilyModel family)
                _viewModel.SelectedFamily = family;
        }

        private async void AddReferenceButton_Click(object sender, RoutedEventArgs e)
        {
            if (ChromaLoadingService == null || string.IsNullOrWhiteSpace(_currentSkinsPath))
                return;

            var dialog = new OpenFolderDialog
            {
                Title = "Select reference skins folder",
                InitialDirectory = _currentSkinsPath
            };

            if (dialog.ShowDialog() != true)
                return;

            string referencePath = NormalizePath(dialog.FolderName);
            if (PathsEqual(_currentSkinsPath, referencePath))
            {
                ParentPanel?.CustomMessageBoxService?.ShowWarning(
                    "Same Chroma Source",
                    "Choose a different skins folder to use as the reference source.",
                    Window.GetWindow(this));
                return;
            }

            _viewModel.SetReferenceScanningState();
            try
            {
                var families = await ChromaLoadingService.LoadFamiliesAsync(referencePath);
                if (families.Count == 0)
                {
                    _viewModel.SetSuccessState();
                    ParentPanel?.CustomMessageBoxService?.ShowWarning(
                        "Reference Not Found",
                        "No chroma families were found in the selected reference folder.",
                        Window.GetWindow(this));
                    return;
                }

                TagSource(families, ChromaSourceKind.Reference, referencePath);
                _referenceFamilies = families;
                _viewModel.SetReferenceSource(referencePath);
                ApplyCombinedFamilies();
                _viewModel.SetSuccessState();
            }
            catch (Exception ex)
            {
                _viewModel.SetSuccessState();
                ParentPanel?.CustomMessageBoxService?.ShowWarning(
                    "Reference Load Failed",
                    $"Could not load the reference chromas: {ex.Message}",
                    Window.GetWindow(this));
            }
        }

        private void RemoveReferenceButton_Click(object sender, RoutedEventArgs e)
        {
            _referenceFamilies = new List<ChromaFamilyModel>();
            _viewModel.ClearReferenceSource();
            ApplyCombinedFamilies();
            _viewModel.SetSuccessState();
        }

        private void SelectFamilyButton_Click(object sender, RoutedEventArgs e)
        {
            SetCurrentFamilySelection(true);
        }

        private void ClearFamilyButton_Click(object sender, RoutedEventArgs e)
        {
            SetCurrentFamilySelection(false);
        }

        private void SetCurrentFamilySelection(bool isSelected)
        {
            if (_viewModel.SelectedFamily == null) return;
            foreach (ChromaSkinModel chroma in _viewModel.SelectedFamily.Chromas)
                chroma.IsSelected = isSelected;
        }

        private void LoadSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedSkins = _viewModel.SelectedChromas.ToList();
            if (selectedSkins.Count > 0)
                ParentPanel?.HandleMultipleChromasSelected(selectedSkins);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (ParentPanel?.ViewModel != null)
                ParentPanel.ViewModel.IsChromaGalleryVisible = false;

            _currentFamilies.Clear();
            _referenceFamilies.Clear();
            _currentSkinsPath = null;
            _viewModel.Reset();
        }

        private void ApplyCombinedFamilies()
        {
            string selectedFamilyKey = _viewModel.SelectedFamily == null
                ? null
                : GetFamilyKey(_viewModel.SelectedFamily);
            List<ChromaFamilyModel> mergedFamilies = MergeFamilies(_currentFamilies, _referenceFamilies);
            _viewModel.SetFamilies(mergedFamilies);

            if (!string.IsNullOrWhiteSpace(selectedFamilyKey))
            {
                ChromaFamilyModel selectedFamily = mergedFamilies.FirstOrDefault(
                    family => string.Equals(GetFamilyKey(family), selectedFamilyKey, StringComparison.OrdinalIgnoreCase));
                if (selectedFamily != null)
                    _viewModel.SelectedFamily = selectedFamily;
            }
        }

        private static List<ChromaFamilyModel> MergeFamilies(
            IReadOnlyList<ChromaFamilyModel> currentFamilies,
            IReadOnlyList<ChromaFamilyModel> referenceFamilies)
        {
            // A family name such as SKIN01 is only meaningful inside a champion/model set.
            // Matching the model identity as well prevents unrelated champions from being
            // folded together when a reference folder is added for comparison.
            var referenceByKey = referenceFamilies.ToDictionary(
                GetFamilyKey,
                StringComparer.OrdinalIgnoreCase);
            var merged = new List<ChromaFamilyModel>();
            var consumedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ChromaFamilyModel currentFamily in currentFamilies)
            {
                string familyKey = GetFamilyKey(currentFamily);
                referenceByKey.TryGetValue(familyKey, out ChromaFamilyModel referenceFamily);
                merged.Add(MergeFamily(currentFamily, referenceFamily));
                consumedKeys.Add(familyKey);
            }

            foreach (ChromaFamilyModel referenceFamily in referenceFamilies)
            {
                string familyKey = GetFamilyKey(referenceFamily);
                if (!consumedKeys.Contains(familyKey))
                    merged.Add(MergeFamily(null, referenceFamily));
            }

            return merged;
        }

        private static string GetFamilyKey(ChromaFamilyModel family)
        {
            if (family == null)
                return string.Empty;

            string modelIdentity = string.IsNullOrWhiteSpace(family.ModelName)
                ? Path.GetFileNameWithoutExtension(family.ModelPath) ?? string.Empty
                : family.ModelName;

            // Keep the champion/model identity, but ignore skin-number formatting differences
            // such as lillia_skin1 vs lillia_skin01 between extracted versions.
            int skinMarker = modelIdentity.IndexOf("_skin", StringComparison.OrdinalIgnoreCase);
            if (skinMarker > 0)
                modelIdentity = modelIdentity[..skinMarker];

            return $"{family.Name}\u001F{modelIdentity}";
        }

        private static ChromaFamilyModel MergeFamily(
            ChromaFamilyModel currentFamily,
            ChromaFamilyModel referenceFamily)
        {
            ChromaFamilyModel preferredFamily = currentFamily ?? referenceFamily;
            var mergedFamily = new ChromaFamilyModel
            {
                Name = preferredFamily.Name,
                ModelName = preferredFamily.ModelName,
                ModelPath = preferredFamily.ModelPath,
                PreviewImage = preferredFamily.PreviewImage,
                SwatchColor = preferredFamily.SwatchColor
            };

            IEnumerable<ChromaSkinModel> chromas =
                (currentFamily == null ? Enumerable.Empty<ChromaSkinModel>() : currentFamily.Chromas)
                .Concat(referenceFamily == null ? Enumerable.Empty<ChromaSkinModel>() : referenceFamily.Chromas)
                .OrderBy(chroma => GetChromaOrder(chroma.Name))
                .ThenBy(chroma => chroma.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(chroma => chroma.SourceKind);

            mergedFamily.Chromas.ReplaceRange(chromas);
            return mergedFamily;
        }

        private static void TagSource(
            IEnumerable<ChromaFamilyModel> families,
            ChromaSourceKind sourceKind,
            string sourceRoot)
        {
            foreach (ChromaSkinModel chroma in families.SelectMany(family => family.Chromas))
            {
                chroma.SourceKind = sourceKind;
                chroma.SourceRoot = sourceRoot;
            }
        }

        private static int GetChromaOrder(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return int.MaxValue;

            int start = name.Length;
            while (start > 0 && char.IsDigit(name[start - 1]))
                start--;

            return start < name.Length && int.TryParse(name[start..], out int value)
                ? value
                : int.MaxValue;
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool PathsEqual(string firstPath, string secondPath)
        {
            return string.Equals(
                NormalizePath(firstPath),
                NormalizePath(secondPath),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
