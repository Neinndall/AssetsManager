using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Formatting;
using AssetsManager.Views.Converters;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Models.Dialogs.Controls
{
    public enum NewAssetExportStatus
    {
        NotExported,
        Success,
        Failed
    }

    /// <summary>
    /// Grid card model for the "Results" view. Wraps a diff plus its extraction
    /// outcome and exposes the exact properties the card template binds to.
    /// </summary>
    public class WadResultItemModel : INotifyPropertyChanged
    {
        private static readonly ChunkDiffTypeToBrushConverter _diffTypeToBrush = new();
        private static readonly Brush QueuedBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00));

        private ExtractResultItem _result;
        private bool _isMultiSelected;

        public WadResultItemModel(SerializableChunkDiff diff)
        {
            Diff = diff;
            Diff.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SerializableChunkDiff.ImagePreview))
                {
                    OnPropertyChanged(nameof(ImagePreview));
                }
            };
        }

        public WadResultItemModel(ExtractResultItem result) : this(result.Diff)
        {
            _result = result;
        }

        public SerializableChunkDiff Diff { get; }

        public NewAssetExportStatus Status =>
            _result == null ? NewAssetExportStatus.NotExported
            : _result.Success ? NewAssetExportStatus.Success
            : NewAssetExportStatus.Failed;

        public string FileName => Diff.FileName;
        public string DisplayPath => Diff.DisplayPath;
        public string NewSizeString => Diff.NewSizeString;
        public string ErrorMessage => _result?.ErrorMessage;
        public string OutputPath => _result?.OutputPath;

        public WadExportMode Mode =>
            _result?.Mode ?? ((Path.GetExtension(Diff.FileName).ToLower() is ".bnk" or ".wpk")
                ? WadExportMode.Original
                : WadExportMode.Smart);

        public string ExtensionDisplay => Path.GetExtension(Diff.FileName).TrimStart('.').ToUpper();

        public bool IsSuccess => Status == NewAssetExportStatus.Success;
        public bool IsFailed => Status == NewAssetExportStatus.Failed;
        public bool IsNotExported => Status == NewAssetExportStatus.NotExported;

        public string StatusLabel
        {
            get
            {
                if (Diff.Type == ChunkDiffType.New)
                {
                    return Status switch
                    {
                        NewAssetExportStatus.Success => "EXPORTED",
                        NewAssetExportStatus.Failed => "FAILED",
                        _ => "QUEUED"
                    };
                }

                return Diff.Type.ToString().ToUpper();
            }
        }

        public Brush StatusBrush
        {
            get
            {
                if (Diff.Type == ChunkDiffType.New)
                {
                    return Status switch
                    {
                        NewAssetExportStatus.Success => Brushes.Green,
                        NewAssetExportStatus.Failed => Brushes.Red,
                        _ => QueuedBrush
                    };
                }

                return (Brush)_diffTypeToBrush.Convert(Diff.Type, typeof(Brush), null, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        public string ModeText
        {
            get
            {
                if (_result != null)
                {
                    return _result.Mode == WadExportMode.Smart ? "Smart" : "Original";
                }

                string ext = Path.GetExtension(Diff.FileName).ToLower();
                return (ext == ".bnk" || ext == ".wpk") ? "Original" : "Smart";
            }
        }

        public ImageSource ImagePreview
        {
            get => Diff.ImagePreview;
            set => Diff.ImagePreview = value;
        }

        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set { if (_isMultiSelected != value) { _isMultiSelected = value; OnPropertyChanged(); } }
        }

        public void UpdateResult(ExtractResultItem result)
        {
            _result = result;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsSuccess));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsNotExported));
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(OutputPath));
            OnPropertyChanged(nameof(Mode));
            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusBrush));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
