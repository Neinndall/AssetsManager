using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Services.Viewer;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class ChromaSelectionControl : UserControl
    {
        // Now using the shared ViewerPanelModel from the module
        public ViewerPanelModel ViewModel => DataContext as ViewerPanelModel;

        // Internal list model for the gallery items (could be moved to ViewerPanelModel if needed)
        // For now, we'll keep a dedicated model for the gallery logic to avoid bloating the panel model
        private readonly ChromaSelectionModel _galleryData = new ChromaSelectionModel();
        public ChromaSelectionModel GalleryData => _galleryData;

        // Dependency Injection via Property
        public ChromaScannerService ScannerService { get; set; }

        // Peer Reference
        public ViewerPanelControl ParentPanel { get; set; }

        public ChromaSelectionControl()
        {
            InitializeComponent();
            // We don't set DataContext here anymore, it's injected from ViewerWindow.xaml
        }

        /// <summary>
        /// Starts the scanning process for a specific skins directory.
        /// </summary>
        public async Task InitializeAsync(string skinsPath)
        {
            if (ScannerService == null) return;

            _galleryData.SetScanningState(System.IO.Path.GetFileName(skinsPath));

            try
            {
                var skins = await ScannerService.ScanSkinsAsync(skinsPath);
                _galleryData.AvailableSkins.ReplaceRange(skins);
                _galleryData.ModelPath = ScannerService.FindAssociatedModel(skinsPath);

                if (skins.Count == 0)
                {
                    _galleryData.SetEmptyState();
                }
                else
                {
                    _galleryData.SetSuccessState(skins.Count);
                }
            }
            catch (Exception ex)
            {
                _galleryData.SetErrorState(ex.Message);
            }
        }

        private void ChromaCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ChromaSkinModel skin)
            {
                _galleryData.SelectedSkin = skin;
                
                // Direct Peer Call
                ParentPanel?.HandleChromaSelected(skin);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.IsChromaGalleryVisible = false;
            }
        }
    }
}
