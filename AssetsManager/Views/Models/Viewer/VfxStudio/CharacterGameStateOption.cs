using System;
using System.ComponentModel;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>A buff the champion's dynamic materials read (e.g. AatroxInCombat), offered as a preview game state.</summary>
    public sealed class CharacterGameStateOption : INotifyPropertyChanged
    {
        private readonly Action _changed;
        private bool _isEnabled;

        internal CharacterGameStateOption(string name, bool enabled, Action changed)
        {
            Name = name;
            _isEnabled = enabled;
            _changed = changed;
        }

        public string Name { get; }

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

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
