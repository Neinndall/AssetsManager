using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Controls.Explorer
{
    public sealed class BreadcrumbItem
    {
        public BreadcrumbItem(string displayName, object value, bool isEnabled = true)
        {
            DisplayName = displayName;
            Value = value;
            IsEnabled = isEnabled;
        }

        public string DisplayName { get; }
        public object Value { get; }
        public bool IsEnabled { get; }
    }

    public sealed class BreadcrumbItemClickedEventArgs : EventArgs
    {
        public BreadcrumbItemClickedEventArgs(object value) => Value = value;

        public object Value { get; }
    }

    public partial class BreadcrumbControl : UserControl
    {
        private const int MaxVisibleItems = 5;

        public BreadcrumbControl()
        {
            InitializeComponent();
            DataContext = this;
            Items = new ObservableRangeCollection<BreadcrumbItem>();
        }

        public ObservableRangeCollection<BreadcrumbItem> Items
        {
            get => (ObservableRangeCollection<BreadcrumbItem>)GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public static readonly DependencyProperty ItemsProperty =
            DependencyProperty.Register(
                nameof(Items),
                typeof(ObservableRangeCollection<BreadcrumbItem>),
                typeof(BreadcrumbControl),
                new PropertyMetadata(null));

        public event EventHandler<BreadcrumbItemClickedEventArgs> ItemClicked;

        public void Clear() => Items.Clear();

        public void SetPath<T>(IReadOnlyList<T> path, Func<T, string> displayNameSelector)
        {
            Items.ReplaceRange(BuildItems(path, displayNameSelector));
        }

        internal static IReadOnlyList<BreadcrumbItem> BuildItems<T>(
            IReadOnlyList<T> path,
            Func<T, string> displayNameSelector)
        {
            if (path == null || path.Count == 0) return Array.Empty<BreadcrumbItem>();

            var items = new List<BreadcrumbItem>(Math.Min(path.Count, MaxVisibleItems));
            if (path.Count <= MaxVisibleItems)
            {
                foreach (T value in path)
                    items.Add(new BreadcrumbItem(displayNameSelector(value), value));
                return items;
            }

            items.Add(new BreadcrumbItem(displayNameSelector(path[0]), path[0]));
            items.Add(new BreadcrumbItem(displayNameSelector(path[1]), path[1]));
            items.Add(new BreadcrumbItem("...", null, false));
            items.Add(new BreadcrumbItem(displayNameSelector(path[^2]), path[^2]));
            items.Add(new BreadcrumbItem(displayNameSelector(path[^1]), path[^1]));
            return items;
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button &&
                button.DataContext is BreadcrumbItem item &&
                item.IsEnabled)
            {
                ItemClicked?.Invoke(this, new BreadcrumbItemClickedEventArgs(item.Value));
            }
        }
    }
}
