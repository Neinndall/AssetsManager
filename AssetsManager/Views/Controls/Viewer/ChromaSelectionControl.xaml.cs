using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Services.Viewer;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    /// <summary>
    /// ChromaSelectionControl: A specialized container for skin selection.
    /// Following the "Contenedores (Dueños)" pattern from PBE.md.
    /// </summary>
    public partial class ChromaSelectionControl : UserControl
    {
        private readonly ChromaSelectionModel _viewModel;

        // The control owns its ViewModel
        public ChromaSelectionModel ViewModel => _viewModel;

        // Dependency Injection via Property
        public ChromaScannerService ScannerService { get; set; }

        // Peer Reference to the Parent Panel (Needed for model loading)
        public ViewerPanelControl ParentPanel { get; set; }

        public ChromaSelectionControl()
        {
            InitializeComponent();
            
            // Pattern: Instancian su ViewModel en el constructor y lo asignan al DataContext
            _viewModel = new ChromaSelectionModel();
            DataContext = _viewModel;
        }

        /// <summary>
        /// Starts the scanning process for a specific skins directory.
        /// </summary>
        public async Task InitializeAsync(string skinsPath)
        {
            if (ScannerService == null || _viewModel == null) return;

            _viewModel.SetScanningState(System.IO.Path.GetFileName(skinsPath));

            try
            {
                var skins = await ScannerService.ScanSkinsAsync(skinsPath);
                _viewModel.AvailableSkins.ReplaceRange(skins);
                _viewModel.ModelPath = ScannerService.FindAssociatedModel(skinsPath);

                if (skins.Count == 0)
                {
                    _viewModel.SetEmptyState();
                }
                else
                {
                    _viewModel.SetSuccessState(skins.Count);
                }
            }
            catch (Exception ex)
            {
                _viewModel.SetErrorState(ex.Message);
            }
        }

        private void ChromaCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ChromaSkinModel skin)
            {
                // Toggle selection
                skin.IsSelected = !skin.IsSelected;
                _viewModel.SelectedSkin = skin;
            }
        }

        private void LoadSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedSkins = _viewModel.AvailableSkins.Where(s => s.IsSelected).ToList();
            if (selectedSkins.Count > 0)
            {
                ParentPanel?.HandleMultipleChromasSelected(selectedSkins);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // We use the Peer Reference to notify the parent to hide the gallery
            if (ParentPanel?.ViewModel != null)
            {
                ParentPanel.ViewModel.IsChromaGalleryVisible = false;
            }
        }
    }
}
