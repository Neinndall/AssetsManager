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
        GeneratingDiffMap,
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
            ApplyInitialDefaults(); // Initialize with defaults
        }

        private string _batchFileType = "file";

        public void SetBatchIndex(int currentFile, int totalFiles, string fileType = "file")
        {
            _currentBatchFile = currentFile;
            _totalBatchFiles = totalFiles;
            _batchFileType = fileType ?? "file";
        }

        public void SetState(DiffLoadingState state)
        {
            switch (state)
            {
                case DiffLoadingState.Idle:
                    ApplyInitialDefaults();
                    break;

                // TEXT PROCESS (Progression: 20 → 45 → 70 → 90 → 100)
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
                    ProgressValue = 45;
                    break;
                case DiffLoadingState.CalculatingDifferences:
                    Description = "Analyzing line-by-line differences...";
                    ProgressValue = 70;
                    break;
                case DiffLoadingState.RenderingUI:
                    Description = "Preparing visual editor...";
                    ProgressValue = 90;
                    break;

                // IMAGE PROCESS (Progression: 25 → 60 → 85 → 100)
                case DiffLoadingState.AcquiringTextureData:
                    Description = "Acquiring texture data from WADs...";
                    ProgressValue = 25;
                    break;
                case DiffLoadingState.DecodingTextures:
                    Description = "Decoding texture surfaces...";
                    ProgressValue = 60;
                    break;
                case DiffLoadingState.GeneratingDiffMap:
                    Description = "Generating visual difference map...";
                    ProgressValue = 85;
                    break;

                // AUDIO PROCESS (Progression: 20 → 35 → 50 → 65)
                case DiffLoadingState.LinkingAudio:
                    Description = "Linking audio bank dependencies...";
                    ProgressValue = 35;
                    break;
                case DiffLoadingState.AcquiringAudioComponents:
                    Description = "Acquiring binary components...";
                    ProgressValue = 50;
                    break;
                case DiffLoadingState.ParsingAudioHierarchy:
                    Description = "Parsing audio hierarchy...";
                    ProgressValue = 65;
                    break;

                case DiffLoadingState.Finalizing:
                    Description = "Finalizing data structure...";
                    ProgressValue = 95;
                    break;

                case DiffLoadingState.Ready:
                    Description = "Ready! Displaying results...";
                    ProgressValue = 100;
                    break;

                // BATCH STATES
                case DiffLoadingState.BatchLoadingFile:
                {
                    var share = 100.0 / _totalBatchFiles;
                    Description = $"Loading {_batchFileType} {_currentBatchFile} of {_totalBatchFiles}...";
                    ProgressValue = (_currentBatchFile - 1) * share + share * 0.15;
                    break;
                }
                case DiffLoadingState.BatchFormattingFile:
                {
                    var share = 100.0 / _totalBatchFiles;
                    var action = _batchFileType == "texture" ? "Decoding" : (_batchFileType == "audio bank" ? "Parsing" : "Formatting");
                    Description = $"{action} {_batchFileType} {_currentBatchFile} of {_totalBatchFiles}...";
                    ProgressValue = (_currentBatchFile - 1) * share + share * 0.55;
                    break;
                }
                case DiffLoadingState.BatchFileReady:
                {
                    var share = 100.0 / _totalBatchFiles;
                    var capitalizedFileType = char.ToUpper(_batchFileType[0]) + _batchFileType.Substring(1);
                    Description = $"{capitalizedFileType} {_currentBatchFile} of {_totalBatchFiles} ready.";
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

        public void ApplyInitialDefaults()
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
