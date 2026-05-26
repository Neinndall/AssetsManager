using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils;

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class ExplorerToolbarControl : UserControl
    {
        public ExplorerToolbarModel ViewModel => DataContext as ExplorerToolbarModel;

        // Flag to prevent SelectionChanged from closing the popup during programmatic updates
        private bool _suppressSelectionClose;

        public ExplorerToolbarControl()
        {
            InitializeComponent();
            Loaded += ExplorerToolbarControl_Loaded;
            Unloaded += ExplorerToolbarControl_Unloaded;
        }

        // ── Lifecycle: attach/detach global click listener ──

        private void ExplorerToolbarControl_Loaded(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.PreviewMouseDown += Window_PreviewMouseDown;
            }

            LoadHistory();
        }

        private void ExplorerToolbarControl_Unloaded(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.PreviewMouseDown -= Window_PreviewMouseDown;
            }
        }

        /// <summary>
        /// Global click handler: closes the history popup when clicking outside both 
        /// the SearchTextBox and the Popup content.
        /// </summary>
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!SearchHistoryPopup.IsOpen) return;

            // Check if the click landed inside the SearchTextBox
            if (IsDescendant(SearchTextBox, e.OriginalSource as DependencyObject))
                return;

            // Check if the click landed inside the Popup content
            if (SearchHistoryPopup.Child != null &&
                IsDescendant(SearchHistoryPopup.Child, e.OriginalSource as DependencyObject))
                return;

            // Click is outside — close the popup
            SearchHistoryPopup.IsOpen = false;
        }

        /// <summary>
        /// Checks whether <paramref name="target"/> is the same as or a visual/logical ancestor of <paramref name="source"/>.
        /// Implements the "Hybrid Tree Protocol" to handle FlowDocument and other non-Visual elements.
        /// </summary>
        private static bool IsDescendant(DependencyObject target, DependencyObject source)
        {
            if (source == null || target == null) return false;
            var current = source;
            while (current != null)
            {
                if (current == target) return true;

                // Handle Popup/Separate Visual Tree
                if (current is Popup popup)
                {
                    current = popup.PlacementTarget;
                }
                else
                {
                    // Use LogicalTreeHelper as fallback for non-Visual elements (like FlowDocument, Run, Span)
                    DependencyObject parent = null;
                    try
                    {
                        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                        {
                            parent = VisualTreeHelper.GetParent(current);
                        }
                    }
                    catch { /* Fallback to Logical tree */ }

                    // If visual parent is null (or it wasn't a visual), try logical parent
                    current = parent ?? LogicalTreeHelper.GetParent(current);
                }
            }
            return false;
        }

        // ── Toolbar button handlers ──

        private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (SearchToggleButton.IsChecked == true)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    SearchTextBox.Focus();
                }, DispatcherPriority.Input);
            }
            else
            {
                SearchHistoryPopup.IsOpen = false;
            }
        }

        private void CollapseToContainerButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ParentExplorer?.HandleCollapseToContainer();
        }

        private void LoadResultsButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ParentExplorer?.HandleLoadResults();
        }

        private void SwitchModeButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ParentExplorer?.HandleSwitchMode();
        }

        private void ImageMergerButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ParentExplorer?.HandleImageMergerClicked();
        }

        // ── Search History Functionality ──

        private void SearchTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ShowHistoryPopup();
        }

        private void SearchTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!SearchHistoryPopup.IsOpen)
            {
                ShowHistoryPopup();
            }
        }

        private void SearchTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!SearchHistoryPopup.IsKeyboardFocusWithin && !SearchTextBox.IsFocused)
                {
                    SearchHistoryPopup.IsOpen = false;
                }
            }), DispatcherPriority.Background);
        }

        private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!SearchHistoryPopup.IsOpen)
            {
                if (e.Key == Key.Down)
                {
                    ShowHistoryPopup();
                    e.Handled = true;
                }
                return;
            }

            int count = SearchHistoryListBox.Items.Count;
            if (count == 0) return;

            int currentIndex = SearchHistoryListBox.SelectedIndex;

            switch (e.Key)
            {
                case Key.Down:
                    _suppressSelectionClose = true;
                    SearchHistoryListBox.SelectedIndex = (currentIndex + 1) % count;
                    SearchHistoryListBox.ScrollIntoView(SearchHistoryListBox.SelectedItem);
                    _suppressSelectionClose = false;
                    e.Handled = true;
                    break;

                case Key.Up:
                    _suppressSelectionClose = true;
                    SearchHistoryListBox.SelectedIndex = (currentIndex - 1 + count) % count;
                    SearchHistoryListBox.ScrollIntoView(SearchHistoryListBox.SelectedItem);
                    _suppressSelectionClose = false;
                    e.Handled = true;
                    break;

                case Key.Enter:
                    if (SearchHistoryListBox.SelectedItem is string selectedText)
                    {
                        ApplyHistoryItem(selectedText);
                        e.Handled = true;
                    }
                    break;

                case Key.Escape:
                    SearchHistoryPopup.IsOpen = false;
                    e.Handled = true;
                    break;
            }
        }

        private void SearchHistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionClose) return;
            if (SearchHistoryListBox.SelectedItem is string selectedText)
            {
                ApplyHistoryItem(selectedText);
            }
        }

        private void ApplyHistoryItem(string text)
        {
            _suppressSelectionClose = true;
            SearchTextBox.Text = text;
            SearchHistoryPopup.IsOpen = false;
            SearchHistoryListBox.SelectedIndex = -1;
            _suppressSelectionClose = false;

            SearchTextBox.Focus();
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
        }

        private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            ClearHistory();
            SearchHistoryPopup.IsOpen = false;
        }

        private void DeleteHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string item)
            {
                RemoveHistoryItem(item);

                if (ViewModel?.SearchHistory.Count == 0)
                {
                    SearchHistoryPopup.IsOpen = false;
                }

                SearchTextBox.Focus();
            }
            e.Handled = true;
        }

        private void ShowHistoryPopup()
        {
            if (ViewModel?.SearchHistory != null && ViewModel.SearchHistory.Count > 0)
            {
                _suppressSelectionClose = true;
                SearchHistoryListBox.SelectedIndex = -1;
                _suppressSelectionClose = false;
                SearchHistoryPopup.IsOpen = true;
            }
            else
            {
                SearchHistoryPopup.IsOpen = false;
            }
        }

        // ── History Logic (Managed by the Control) ──

        public void LoadHistory()
        {
            try
            {
                var settings = App.ServiceProvider.GetRequiredService<AppSettings>();
                if (settings.SearchHistory != null && ViewModel != null)
                {
                    ViewModel.SearchHistory.ReplaceRange(settings.SearchHistory);
                }
            }
            catch { /* Silent Fail */ }
        }

        public void AddSearchToHistory(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            query = query.Trim();

            try
            {
                var settings = App.ServiceProvider.GetRequiredService<AppSettings>();

                // Deduplicate and keep most recent at the top
                settings.SearchHistory.Remove(query);
                settings.SearchHistory.Insert(0, query);

                // Limit to 5 items
                if (settings.SearchHistory.Count > 5)
                {
                    settings.SearchHistory = settings.SearchHistory.Take(5).ToList();
                }

                settings.Save();
                ViewModel?.SearchHistory.ReplaceRange(settings.SearchHistory);
            }
            catch { /* Silent Fail */ }
        }

        public void RemoveHistoryItem(string item)
        {
            try
            {
                var settings = App.ServiceProvider.GetRequiredService<AppSettings>();
                if (settings.SearchHistory.Remove(item))
                {
                    settings.Save();
                    ViewModel?.SearchHistory.ReplaceRange(settings.SearchHistory);
                }
            }
            catch { /* Silent Fail */ }
        }

        public void ClearHistory()
        {
            try
            {
                var settings = App.ServiceProvider.GetRequiredService<AppSettings>();
                settings.SearchHistory.Clear();
                settings.Save();
                ViewModel?.SearchHistory.Clear();
            }
            catch { /* Silent Fail */ }
        }
    }
}
