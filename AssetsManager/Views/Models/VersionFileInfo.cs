using System.ComponentModel;

namespace AssetsManager.Views.Models
{
    public class VersionFileInfo : INotifyPropertyChanged
    {
        public string FileName { get; set; }
        public string Content { get; set; }
        public string Date { get; set; }
        public string Category { get; set; } // To group files like 'league-client', 'lol-game-client'

        private bool _isSelected;
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
