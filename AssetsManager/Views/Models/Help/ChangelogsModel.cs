using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Material.Icons;
using System.Windows.Media;

namespace AssetsManager.Views.Models.Help
{
    public class ChangeItem
    {
        public string Text { get; set; }
        public bool IsSubheading { get; set; }
        public bool IsDescription { get; set; }
        public int IndentationLevel { get; set; }
    }

    public class ChangeGroup
    {
        public string Title { get; set; }
        public MaterialIconKind Icon { get; set; }
        public SolidColorBrush IconColor { get; set; }
        public List<ChangeItem> Changes { get; set; } = new List<ChangeItem>();
    }

    public class ChangelogVersion : INotifyPropertyChanged
    {
        public string Version { get; set; }
        public bool IsLatest { get; set; }
        public string UpdateType { get; set; }
        public SolidColorBrush UpdateTypeColor { get; set; } = Brushes.Transparent;
        public string UpdateDescription { get; set; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public List<ChangeGroup> Groups { get; set; } = new List<ChangeGroup>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
