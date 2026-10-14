using System;
using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views.Controls.Shared
{
    /// <summary>
    /// Shared segmented "FILTER BY" chips (All / Images / Audio / 3D / Data).
    /// Raises <see cref="FilterChanged"/> when the user picks a filter and keeps the
    /// selection in <see cref="SelectedFilter"/>.
    /// </summary>
    public partial class FileTypeFilterControl : UserControl
    {
        public event Action<string> FilterChanged;

        public static readonly DependencyProperty SelectedFilterProperty =
            DependencyProperty.Register(
                nameof(SelectedFilter),
                typeof(string),
                typeof(FileTypeFilterControl),
                new PropertyMetadata("All", OnSelectedFilterChanged));

        public string SelectedFilter
        {
            get => (string)GetValue(SelectedFilterProperty);
            set => SetValue(SelectedFilterProperty, value);
        }

        public FileTypeFilterControl()
        {
            InitializeComponent();
        }

        private static void OnSelectedFilterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileTypeFilterControl control)
            {
                control.SyncRadioButtons(e.NewValue as string);
            }
        }

        private void SyncRadioButtons(string filter)
        {
            AllRadioButton.IsChecked = filter == "All";
            ImagesRadioButton.IsChecked = filter == "Images";
            AudioRadioButton.IsChecked = filter == "Audio";
            Model3DRadioButton.IsChecked = filter == "3D";
            DataRadioButton.IsChecked = filter == "Data";
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string filterType })
            {
                SelectedFilter = filterType;
                FilterChanged?.Invoke(filterType);
            }
        }
    }
}
