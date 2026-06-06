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
        RenderingUI,
        DecodingTextures,
        Finalizing,
        Ready,
        BatchLoadingFile,
        BatchFormattingFile,
        BatchFileReady
    }

    public class LoadingDiffModel : INotifyPropertyChanged
    {
        private string _title;
        private string _description;
        private double _progressValue;
        private bool _isBusy;
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

        public LoadingDiffModel()
        {
            Reset(); // Initialize with defaults
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
                    Description = "Formatting and beautifying content...";
                    ProgressValue = 55;
                    break;
                case DiffLoadingState.CalculatingDifferences:
                    Description = "Analyzing line-by-line differences...";
                    ProgressValue = 80;
                    break;
                case DiffLoadingState.RenderingUI:
                    Description = "Preparing visual editor...";
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
                    ProgressValue = 35;
                    break;
                case DiffLoadingState.AcquiringAudioComponents:
                    Description = "Acquiring binary components...";
                    ProgressValue = 55;
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
                    Description = "Ready! Displaying results...";
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
            _currentBatchFile = 0;
            _totalBatchFiles = 0;
        }
    }
}
