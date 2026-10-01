using System;
using System.ComponentModel;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>A mutator some visibility controller of the map reads.</summary>
    public sealed class MapMutatorOption : INotifyPropertyChanged
    {
        private readonly Action _changed;
        private bool _isEnabled;

        internal MapMutatorOption(string name, string label, bool isEnabled, Action changed)
        {
            Name = name;
            Label = label;
            _isEnabled = isEnabled;
            _changed = changed;
        }

        public string Name { get; }
        public string Label { get; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
                _changed?.Invoke();
            }
        }

        internal void Sync(bool isEnabled)
        {
            if (_isEnabled == isEnabled) return;
            _isEnabled = isEnabled;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
