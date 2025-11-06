
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models;
using System.Collections.Generic;

namespace AssetsManager.Views.Models
{
    public class AnimationModel : INotifyPropertyChanged
    {
        public AnimationData AnimationData { get; }

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetField(ref _isPlaying, value);
        }

        private double _currentTime;
        public double CurrentTime
        {
            get => _currentTime;
            set
            {
                if (SetField(ref _currentTime, value))
                {
                    OnPropertyChanged(nameof(ProgressText));
                }
            }
        }

        private double _totalDuration;
        public double TotalDuration
        {
            get => _totalDuration;
            set => SetField(ref _totalDuration, value);
        }

        public string ProgressText => $"{TimeSpan.FromSeconds(CurrentTime):mm:ss} / {TimeSpan.FromSeconds(TotalDuration):mm:ss}";

        public string Name => AnimationData.Name;

        public AnimationModel(AnimationData animationData)
        {
            AnimationData = animationData;
            TotalDuration = animationData.AnimationAsset.Duration;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
