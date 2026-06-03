using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models.Viewer;
using System.Collections.Generic;

namespace AssetsManager.Views.Models.Viewer
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
                    _cachedProgressText = null;
                    OnPropertyChanged(nameof(ProgressText));
                }
            }
        }

        private double _totalDuration;
        public double TotalDuration
        {
            get => _totalDuration;
            set { _cachedProgressText = null; SetField(ref _totalDuration, value); }
        }

        private double _speed = 1.0;
        public double Speed
        {
            get => _speed;
            set => SetField(ref _speed, value);
        }

        private string _cachedProgressText;
        public string ProgressText
        {
            get
            {
                if (_cachedProgressText != null) return _cachedProgressText;

                try
                {
                    if (double.IsNaN(TotalDuration) || double.IsInfinity(TotalDuration) || TotalDuration <= 0 ||
                        double.IsNaN(CurrentTime) || double.IsInfinity(CurrentTime))
                    {
                        _cachedProgressText = "--:-- / --:--";
                        return _cachedProgressText;
                    }

                    var clampedCurrentTime = Math.Max(0, Math.Min(CurrentTime, TotalDuration));

                    var totalStr = TotalDuration.ToString("0.0000", CultureInfo.InvariantCulture);
                    var currentStr = clampedCurrentTime.ToString("0.0000", CultureInfo.InvariantCulture);

                    _cachedProgressText = $"{currentStr} / {totalStr}";
                    return _cachedProgressText;
                }
                catch (Exception)
                {
                    _cachedProgressText = "Error";
                    return _cachedProgressText;
                }
            }
        }

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
