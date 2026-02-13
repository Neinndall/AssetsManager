using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Explorer;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Explorer
{
    public class PinnedFilesManager : INotifyPropertyChanged
    {
        public ObservableRangeCollection<PinnedFileModel> PinnedFiles { get; set; }

        private PinnedFileModel _selectedFile;
        public PinnedFileModel SelectedFile
        {
            get => _selectedFile;
            set
            {
                if (_selectedFile != value)
                {
                    if (_selectedFile != null) _selectedFile.IsSelected = false;
                    _selectedFile = value;
                    if (_selectedFile != null) _selectedFile.IsSelected = true;
                    OnPropertyChanged(nameof(SelectedFile));
                }
            }
        }

        public PinnedFilesManager()
        {
            PinnedFiles = new ObservableRangeCollection<PinnedFileModel>();
        }

        public void PinFile(FileSystemNodeModel node)
        {
            if (node == null) return;

            // Determine category
            bool isImage = SupportedFileTypes.Images.Contains(node.Extension) || 
                           SupportedFileTypes.Textures.Contains(node.Extension) || 
                           SupportedFileTypes.VectorImages.Contains(node.Extension);

            // Find an existing pin of the same category
            var categoryPin = PinnedFiles.FirstOrDefault(p => {
                bool pIsImage = SupportedFileTypes.Images.Contains(p.Node.Extension) || 
                                SupportedFileTypes.Textures.Contains(p.Node.Extension) || 
                                SupportedFileTypes.VectorImages.Contains(p.Node.Extension);
                return pIsImage == isImage;
            });

            if (categoryPin != null)
            {
                // Recycle the existing tab slot
                categoryPin.Node = node;
                
                // Force a property change notification even if the reference is the same,
                // to ensure the UI and PreviewService react to the new node.
                var temp = SelectedFile;
                _selectedFile = null; 
                SelectedFile = temp;
            }
            else
            {
                // Create a new slot if none exists for this category
                var newPinnedFile = new PinnedFileModel(node);
                PinnedFiles.Add(newPinnedFile);
                SelectedFile = newPinnedFile;
            }
        }

        public void UnpinFile(PinnedFileModel fileToUnpin)
        {
            if (fileToUnpin == null) return;

            int index = PinnedFiles.IndexOf(fileToUnpin);
            if (index != -1)
            {
                PinnedFiles.RemoveAt(index);
                if (SelectedFile == fileToUnpin)
                {
                    SelectedFile = PinnedFiles.Count > 0 ? PinnedFiles[System.Math.Max(0, index - 1)] : null;
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
