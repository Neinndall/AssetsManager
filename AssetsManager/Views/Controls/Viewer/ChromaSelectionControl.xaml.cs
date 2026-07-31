using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Viewer.Models;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class ChromaSelectionControl : UserControl
    {
        private readonly ChromaSelectionModel _viewModel;

        public ChromaSelectionModel ViewModel => _viewModel;

        public ChromaScannerService ScannerService { get; set; }

        public ViewerPanelControl ParentPanel { get; set; }

        public ChromaSelectionControl()
        {
            InitializeComponent();
            
            _viewModel = new ChromaSelectionModel();
            DataContext = _viewModel;
        }

        public async Task InitializeAsync(string skinsPath)
        {
            if (ScannerService == null) return;

            _viewModel.SetScanningState(System.IO.Path.GetFileName(skinsPath));

            try
            {
                var families = await ScannerService.ScanSkinsAsync(skinsPath);
                _viewModel.SetFamilies(families);

                if (families.Count == 0)
                {
                    _viewModel.SetEmptyState();
                }
                else
                {
                    _viewModel.SetSuccessState();
                }
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
            {
                ParentPanel?.HandleMultipleChromasSelected(selectedSkins);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (ParentPanel?.ViewModel != null)
            {
                ParentPanel.ViewModel.IsChromaGalleryVisible = false;
            }
            _viewModel?.Reset();
        }
    }
}
