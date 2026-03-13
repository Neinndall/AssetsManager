using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Explorer;
using AssetsManager.Views.Models.Dialogs;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Dialogs
{
    public partial class ImageMergerWindow : HudWindow
    {
        public ImageMergerModel ViewModel { get; set; }
        private readonly ImageMergerService _imageMergerService;

        public ImageMergerWindow(ImageMergerService imageMergerService)
        {
            InitializeComponent();
            _imageMergerService = imageMergerService;
            
            ViewModel = new ImageMergerModel(_imageMergerService.Items);
            this.DataContext = ViewModel;

            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            _imageMergerService.Items.CollectionChanged += OnItemsCollectionChanged;
            
            this.Closing += OnWindowClosing;
            RequestRender();
        }

        private void OnItemsCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RequestRender();

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ImageMergerModel.Columns) || e.PropertyName == nameof(ImageMergerModel.Margin))
            {
                RequestRender();
            }
        }

        private void RequestRender() => _ = _imageMergerService.RenderMergedImageAsync(ViewModel);

        private async void AddImage_Click(object sender, RoutedEventArgs e) => await _imageMergerService.AddImagesFromDialogAsync();

        private void ClearAll_Click(object sender, RoutedEventArgs e) => _imageMergerService.Clear();

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ImageMergerItem item)
            {
                int index = ViewModel.Items.IndexOf(item);
                if (index > 0) _imageMergerService.Items.Move(index, index - 1);
            }
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ImageMergerItem item)
            {
                int index = ViewModel.Items.IndexOf(item);
                if (index != -1 && index < ViewModel.Items.Count - 1) _imageMergerService.Items.Move(index, index + 1);
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ImageMergerItem item) _imageMergerService.Items.Remove(item);
        }

        private async void Export_Click(object sender, RoutedEventArgs e) => await _imageMergerService.ExportImageAsync(ViewModel.PreviewImage, this);
    }
}
