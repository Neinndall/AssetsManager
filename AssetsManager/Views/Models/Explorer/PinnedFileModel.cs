using System.ComponentModel;

namespace AssetsManager.Views.Models.Explorer
{
    public class PinnedFileModel : INotifyPropertyChanged
    {
        private FileSystemNodeModel _node;
        public FileSystemNodeModel Node
        {
            get => _node;
            set
            {
                if (_node != value)
                {
                    _node = value;
                    OnPropertyChanged(nameof(Node));
                    OnPropertyChanged(nameof(Header));
                }
            }
        }

        public bool IsDetailsTab { get; set; }
        public string Header => IsDetailsTab ? "Details" : Node?.Name;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public PinnedFileModel(FileSystemNodeModel node)
        {
            Node = node;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
