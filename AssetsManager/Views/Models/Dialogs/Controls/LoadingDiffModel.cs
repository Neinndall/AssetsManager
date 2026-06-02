using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Dialogs.Controls
{
    public enum DiffLoadingState
    {
        Idle,
        AcquiringBinaryData,
        ReadingLocalFiles,
        AcquiringTextureData,
        LinkingAudio,
        AcquiringAudioComponents,
        ParsingTextContent,
        ParsingAudioHierarchy,
        CalculatingDifferences,
        DecodingTextures,
        Finalizing,
        Ready,
        BatchLoadingFile,
        BatchFormattingFile,
        BatchFileReady
    }

    public class LoadingDiffModel : INotifyPropertyChanged
    {
        private string _title = "Processing Files";
        private string _description = "Please wait while the differences are being calculated...";
        private double _progressValue = 0;
        private bool _isBusy = false;
        private int _currentBatchFile;
        private int _totalBatchFiles;

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

        public void SetBatchIndex(int currentFile, int totalFiles)
        {
            _currentBatchFile = currentFile;
            _totalBatchFiles = totalFiles;
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
                case DiffLoadingState.ReadingLocalFiles:
                    Description = "Reading local files from disk...";
                    ProgressValue = 30;
                    break;
                case DiffLoadingState.ParsingTextContent:
                    Description = "Parsing and formatting content...";
                    ProgressValue = 60;
                    break;
                case DiffLoadingState.CalculatingDifferences:
                    Description = "Calculating differences...";
                    ProgressValue = 95;
                    break;

                // IMAGE PROCESS
                case DiffLoadingState.AcquiringTextureData:
                    Description = "Acquiring texture data...";
                    ProgressValue = 40;
                    break;
                case DiffLoadingState.DecodingTextures:
                    Description = "Decoding texture surfaces...";
                    ProgressValue = 95;
                    break;

                // AUDIO PROCESS
                case DiffLoadingState.LinkingAudio:
                    Description = "Linking audio bank dependencies...";
                    ProgressValue = 20;
                    break;
                case DiffLoadingState.AcquiringAudioComponents:
                    Description = "Acquiring binary components...";
                    ProgressValue = 50;
                    break;
                case DiffLoadingState.ParsingAudioHierarchy:
                    Description = "Parsing audio hierarchy...";
                    ProgressValue = 90;
                    break;

                case DiffLoadingState.Finalizing:
                    Description = "Finalizing data structure...";
                    ProgressValue = 98;
                    break;

                case DiffLoadingState.Ready:
                    Description = "Done! Displaying results...";
                    ProgressValue = 100;
                    break;

                // BATCH STATES
                case DiffLoadingState.BatchLoadingFile:
                {
                    var share = 100.0 / _totalBatchFiles;
                    Description = $"Loading file {_currentBatchFile} of {_totalBatchFiles}...";
                    ProgressValue = (_currentBatchFile - 1) * share + share * 0.15;
                    break;
                }
                case DiffLoadingState.BatchFormattingFile:
                {
                    var share = 100.0 / _totalBatchFiles;
                    Description = $"Formatting file {_currentBatchFile} of {_totalBatchFiles}...";
                    ProgressValue = (_currentBatchFile - 1) * share + share * 0.55;
                    break;
                }
                case DiffLoadingState.BatchFileReady:
                {
                    var share = 100.0 / _totalBatchFiles;
                    Description = $"File {_currentBatchFile} of {_totalBatchFiles} ready.";
                    ProgressValue = _currentBatchFile * share;
                    break;
                }
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
