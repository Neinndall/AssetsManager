using System.ComponentModel;
using System.Runtime.CompilerServices;
using Material.Icons;

namespace AssetsManager.Views.Models.Models3D
{
    public class ModelPanelModel : INotifyPropertyChanged
    {
        private bool _isMapMode;
        private string _loadButtonText = "Model";
        private string _loadButtonTooltip = "Load Model";
        private MaterialIconKind _loadButtonIcon = MaterialIconKind.CubeOutline;

        public bool IsMapMode
        {
            get => _isMapMode;
            set
            {
                if (_isMapMode != value)
                {
                    _isMapMode = value;
                    UpdateModeData();
                    OnPropertyChanged();
                }
            }
        }

        public string LoadButtonText
        {
            get => _loadButtonText;
            private set { _loadButtonText = value; OnPropertyChanged(); }
        }

        public string LoadButtonTooltip
        {
            get => _loadButtonTooltip;
            private set { _loadButtonTooltip = value; OnPropertyChanged(); }
        }

        public MaterialIconKind LoadButtonIcon
        {
            get => _loadButtonIcon;
            private set { _loadButtonIcon = value; OnPropertyChanged(); }
        }

        private void UpdateModeData()
        {
            if (_isMapMode)
            {
                LoadButtonText = "Map";
                LoadButtonTooltip = "Load MapGeometry";
                LoadButtonIcon = MaterialIconKind.Map;
            }
            else
            {
                LoadButtonText = "Model";
                LoadButtonTooltip = "Load Model";
                LoadButtonIcon = MaterialIconKind.CubeOutline;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
