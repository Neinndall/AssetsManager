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

        public void PinFile(FileSystemNodeModel node, bool isExplicitPin = false)
        {
            if (node == null) return;

            // Determine category
            bool isImage = SupportedFileTypes.Images.Contains(node.Extension) || 
                           SupportedFileTypes.Textures.Contains(node.Extension) || 
                           SupportedFileTypes.VectorImages.Contains(node.Extension);

            // 1. If it's an explicit pin, find the current pin for this node and fix it
            if (isExplicitPin)
            {
                var existing = PinnedFiles.FirstOrDefault(p => p.Node == node);
                if (existing != null)
                {
                    existing.IsPinned = true;
                    SelectedFile = existing;
                    return;
                }
                
                // If not even present, create it as pinned
                var newPin = new PinnedFileModel(node) { IsPinned = true };
                PinnedFiles.Add(newPin);
                SelectedFile = newPin;
                return;
            }

            // 2. If it's an auto-pin (click), look for a RECYCLABLE pin of the same category (IsPinned == false)
            var recyclablePin = PinnedFiles.FirstOrDefault(p => {
                if (p.IsPinned) return false; // Pinned tabs are never recycled
                
                bool pIsImage = SupportedFileTypes.Images.Contains(p.Node.Extension) || 
                                SupportedFileTypes.Textures.Contains(p.Node.Extension) || 
                                SupportedFileTypes.VectorImages.Contains(p.Node.Extension);
                return pIsImage == isImage;
            });

            if (recyclablePin != null)
            {
                // Recycle the temporary slot
                recyclablePin.Node = node;
                
                // Force notification
                var temp = SelectedFile;
                _selectedFile = null; 
                SelectedFile = temp;
            }
            else
            {
                // Check if the file is already open in a pinned tab
                var pinnedVersion = PinnedFiles.FirstOrDefault(p => p.Node == node);
                if (pinnedVersion != null)
                {
                    SelectedFile = pinnedVersion;
                }
                else
                {
                    // Create a new temporary slot
                    var newPinnedFile = new PinnedFileModel(node) { IsPinned = false };
                    PinnedFiles.Add(newPinnedFile);
                    SelectedFile = newPinnedFile;
                }
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
