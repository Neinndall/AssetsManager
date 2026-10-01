using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Item model representing a single VFX system definition inside the inspector.
    /// </summary>
    public class VfxSystemDiagnosticItem : INotifyPropertyChanged
    {
        private string _name;
        private uint _pathHash;
        private VfxSystemDefinition _definition;
        private int _emitterCount;
        private int _textureCount;
        private int _meshCount;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public uint PathHash
        {
            get => _pathHash;
            set { _pathHash = value; OnPropertyChanged(); }
        }

        public VfxSystemDefinition Definition
        {
            get => _definition;
            set { _definition = value; OnPropertyChanged(); }
        }

        public int EmitterCount
        {
            get => _emitterCount;
            set { _emitterCount = value; OnPropertyChanged(); }
        }

        public int TextureCount
        {
            get => _textureCount;
            set { _textureCount = value; OnPropertyChanged(); }
        }

        public int MeshCount
        {
            get => _meshCount;
            set { _meshCount = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
