using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Dialogs.Controls
{
    public enum DiffLoadingState
    {
        Idle,
        AcquiringBinaryData,
        AcquiringTextureData,
        LinkingAudio,
        AcquiringAudioComponents,
        ParsingTextContent,
        ParsingAudioHierarchy,
        CalculatingDifferences,
        DecodingTextures,
        Finalizing,
        Ready
    }

    public class LoadingDiffModel : INotifyPropertyChanged
    {
        private string _title = "Processing Files";
        private string _description = "Please wait while the differences are being calculated...";
        private double _progressValue = 0;
        private bool _isBusy = false;

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        public double ProgressValue
        {
            get => _progressValue;
            set { _progressValue = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); }
        }

        public void SetState(DiffLoadingState state)
        {
            switch (state)
            {
                case DiffLoadingState.Idle:
                    Description = "Please wait while the differences are being calculated...";
                    ProgressValue = 0;
                    break;

                // TEXT PROCESS
                case DiffLoadingState.AcquiringBinaryData:
                    Description = "Acquiring binary data from WADs...";
                    ProgressValue = 20;
                    break;
                case DiffLoadingState.ParsingTextContent:
                    Description = "Parsing and formatting content...";
                    ProgressValue = 50;
                    break;
                case DiffLoadingState.CalculatingDifferences:
                    Description = "Calculating differences...";
                    ProgressValue = 80;
                    break;

                // IMAGE PROCESS
                case DiffLoadingState.AcquiringTextureData:
                    Description = "Acquiring texture data...";
                    ProgressValue = 30;
                    break;
                case DiffLoadingState.DecodingTextures:
                    Description = "Decoding texture surfaces...";
                    ProgressValue = 60;
                    break;

                // AUDIO PROCESS
                case DiffLoadingState.LinkingAudio:
                    Description = "Linking audio bank dependencies...";
                    ProgressValue = 15;
                    break;
                case DiffLoadingState.AcquiringAudioComponents:
                    Description = "Acquiring binary components...";
                    ProgressValue = 40;
                    break;
                case DiffLoadingState.ParsingAudioHierarchy:
                    Description = "Parsing audio hierarchy...";
                    ProgressValue = 60;
                    break;

                case DiffLoadingState.Finalizing:
                    Description = "Finalizing data structure...";
                    ProgressValue = 85;
                    break;

                case DiffLoadingState.Ready:
                    Description = "Ready";
                    ProgressValue = 100;
                    break;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void Reset()
        {
            Title = "Processing Files";
            Description = "Please wait while the differences are being calculated...";
            ProgressValue = 0;
            IsBusy = false;
        }
    }
}
