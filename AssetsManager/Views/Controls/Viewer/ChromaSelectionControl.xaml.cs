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
        public ChromaSelectionModel ViewModel => DataContext as ChromaSelectionModel;

        public event EventHandler<ChromaSkinModel> ChromaSelected;
        public event EventHandler Cancelled;

        private readonly ChromaScannerService _scannerService;

        public ChromaSelectionControl(ChromaScannerService scannerService)
        {
            InitializeComponent();
            _scannerService = scannerService;
        }

        /// <summary>
        /// Starts the scanning process for a specific skins directory.
        /// </summary>
        public async Task InitializeAsync(string skinsPath)
        {
            if (ViewModel == null) return;

            ViewModel.IsLoading = true;
            ViewModel.AvailableSkins.Clear();
            ViewModel.StatusText = $"Scanning character skins in: {System.IO.Path.GetFileName(skinsPath)}";

            try
            {
                // 1. Scan for skin directories
                var skins = await _scannerService.ScanSkinsAsync(skinsPath);
                ViewModel.AvailableSkins.ReplaceRange(skins);

                // 2. Try to find the associated .skn model
                ViewModel.ModelPath = _scannerService.FindAssociatedModel(skinsPath);

                if (skins.Count == 0)
                {
                    ViewModel.StatusText = "No skins or textures found in this directory.";
                }
                else
                {
                    ViewModel.StatusText = $"Found {skins.Count} available skins/chromas.";
                }
            }
            catch (Exception ex)
            {
                ViewModel.StatusText = $"Error: {ex.Message}";
            }
            finally
            {
                ViewModel.IsLoading = false;
            }
        }

        private void ChromaCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ChromaSkinModel skin)
            {
                ViewModel.SelectedSkin = skin;
                
                // Fire selection event
                ChromaSelected?.Invoke(this, skin);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }
}
