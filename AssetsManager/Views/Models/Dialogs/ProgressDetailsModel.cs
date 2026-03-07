using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AssetsManager.Views.Models.Dialogs
{
    public class ProgressDetailsModel : INotifyPropertyChanged
    {
        private string _operationVerb;
        private string _itemProgressText = "0 of 0";
        private string _currentFileName = "...";
        private double _progressValue = 0;
        private string _subProgressText = "0 of 0";
        private string _estimatedTimeText = "Calculating...";
        private bool _showChunks = false;
        private bool _isFinished = false;

        public event PropertyChangedEventHandler PropertyChanged;

        public string OperationVerb
        {
            get => _operationVerb;
            set { if (_operationVerb != value) { _operationVerb = value; OnPropertyChanged(); } }
        }

        public string ItemProgressText
        {
            get => _itemProgressText;
            set { if (_itemProgressText != value) { _itemProgressText = value; OnPropertyChanged(); } }
        }

        public string CurrentFileName
        {
            get => _currentFileName;
            set { if (_currentFileName != value) { _currentFileName = value; OnPropertyChanged(); } }
        }

        public double ProgressValue
        {
            get => _progressValue;
            set { if (_progressValue != value) { _progressValue = value; OnPropertyChanged(); } }
        }

        public string SubProgressText
        {
            get => _subProgressText;
            set { if (_subProgressText != value) { _subProgressText = value; OnPropertyChanged(); } }
        }

        public string EstimatedTimeText
        {
            get => _estimatedTimeText;
            set { if (_estimatedTimeText != value) { _estimatedTimeText = value; OnPropertyChanged(); } }
        }

        public bool ShowChunks
        {
            get => _showChunks;
            set 
            { 
                if (_showChunks != value) 
                { 
                    _showChunks = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(SubProgressVisibility));
                } 
            }
        }

        public Visibility SubProgressVisibility => ShowChunks ? Visibility.Visible : Visibility.Collapsed;

        public bool IsFinished
        {
            get => _isFinished;
            set { if (_isFinished != value) { _isFinished = value; OnPropertyChanged(); } }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
