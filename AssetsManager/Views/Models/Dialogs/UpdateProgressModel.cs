using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Dialogs
{
    public class UpdateProgressModel : INotifyPropertyChanged
    {
        private int _progressPercentage;
        private string _detailsText = "Initializing connection...";
        private string _operationTitle = "DOWNLOADING...";

        public event PropertyChangedEventHandler PropertyChanged;

        public int ProgressPercentage
        {
            get => _progressPercentage;
            set { if (_progressPercentage != value) { _progressPercentage = value; OnPropertyChanged(); } }
        }

        public string DetailsText
        {
            get => _detailsText;
            set { if (_detailsText != value) { _detailsText = value; OnPropertyChanged(); } }
        }

        public string OperationTitle
        {
            get => _operationTitle;
            set { if (_operationTitle != value) { _operationTitle = value; OnPropertyChanged(); } }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
