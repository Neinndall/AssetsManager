using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Explorer;

namespace AssetsManager.Views.Models
{
    public class PinnedFilesManager : INotifyPropertyChanged
    {
        public ObservableCollection<PinnedFileModel> PinnedFiles { get; set; }

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
            PinnedFiles = new ObservableCollection<PinnedFileModel>();
        }

        public void PinFile(FileSystemNodeModel node)
        {
            if (node == null) return;

            var existingPin = PinnedFiles.FirstOrDefault(p => p.Node == node);

            if (existingPin != null)
            {
                SelectedFile = existingPin;
            }
            else
            {
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
