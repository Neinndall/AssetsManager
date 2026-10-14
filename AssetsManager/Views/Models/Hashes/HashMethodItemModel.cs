using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Material.Icons;

namespace AssetsManager.Views.Models.Hashes
{
    public sealed class HashMethodSubItemModel : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string BadgeText { get; init; } = string.Empty;
        public Brush BadgeBrush { get; init; }
        public string EstimatedTime { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class HashMethodItemModel : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private string _name = string.Empty;
        private string _description = string.Empty;
        private string _category = string.Empty;
        private MaterialIconKind _iconKind = MaterialIconKind.LightningBolt;
        private string _badgeText = string.Empty;
        private Brush _badgeBrush;
        private string _estimatedTime = string.Empty;
        private int _domainIndex;

        public string Id
        {
            get => _id;
            init => _id = value;
        }

        public string Name
        {
            get => _name;
            init => _name = value;
        }

        public string Description
        {
            get => _description;
            init => _description = value;
        }

        public string Category
        {
            get => _category;
            init => _category = value;
        }

        public MaterialIconKind IconKind
        {
            get => _iconKind;
            init => _iconKind = value;
        }

        public string BadgeText
        {
            get => _badgeText;
            init => _badgeText = value;
        }

        public Brush BadgeBrush
        {
            get => _badgeBrush;
            init => _badgeBrush = value;
        }

        public string EstimatedTime
        {
            get => _estimatedTime;
            init => _estimatedTime = value;
        }

        public int DomainIndex
        {
            get => _domainIndex;
            init => _domainIndex = value;
        }

        public ObservableCollection<HashMethodSubItemModel> SubMethods { get; } = new();

        public bool HasSubMethods => SubMethods.Count > 0;

        public int SelectedSubMethodsCount => SubMethods.Count(s => s.IsSelected);

        public void SelectAllSubMethods(bool select)
        {
            foreach (var sub in SubMethods)
            {
                sub.IsSelected = select;
            }
            OnPropertyChanged(nameof(SelectedSubMethodsCount));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
