using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Monitor
{
    /// <summary>
    /// ENUM: Status of a category check
    /// </summary>
    public enum CategoryStatus { Idle, Checking, CompletedSuccess }

    /// <summary>
    /// MAIN MODEL: State of the Asset Tracker Control
    /// </summary>
    public class AssetTrackerModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private ObservableRangeCollection<AssetCategory> _categories;
        private AssetCategory _selectedCategory;
        private ObservableRangeCollection<TrackedAsset> _assets;
        private bool _isBusy;

        public AssetTrackerModel()
        {
            Categories = new ObservableRangeCollection<AssetCategory>();
            Assets = new ObservableRangeCollection<TrackedAsset>();
        }

        public ObservableRangeCollection<AssetCategory> Categories
        {
            get => _categories;
            set { _categories = value; OnPropertyChanged(); }
        }

        public AssetCategory SelectedCategory
        {
            get => _selectedCategory;
            set { if (_selectedCategory != value) { _selectedCategory = value; OnPropertyChanged(); } }
        }

        public ObservableRangeCollection<TrackedAsset> Assets
        {
            get => _assets;
            set { _assets = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); } }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// SUB-MODEL: Individual Asset Category
    /// </summary>
    public class AssetCategory : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string BaseUrl { get; set; }
        public string Extension { get; set; }
        
        private long _start;
        public long Start
        {
            get => _start;
            set { if (_start != value) { _start = value; OnPropertyChanged(); } }
        }

        public long LastValid { get; set; }
        public List<long> FoundUrls { get; set; } = new List<long>();
        public List<long> FailedUrls { get; set; } = new List<long>();
        public List<long> UserRemovedUrls { get; set; } = new List<long>();
        public Dictionary<long, string> FoundUrlOverrides { get; set; } = new Dictionary<long, string>();

        private bool _hasNewAssets;
        public bool HasNewAssets
        {
            get => _hasNewAssets;
            set { if (_hasNewAssets != value) { _hasNewAssets = value; OnPropertyChanged(); } }
        }

        private CategoryStatus _status;
        public CategoryStatus Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// SUB-MODEL: Individual Tracked Asset
    /// </summary>
    public class TrackedAsset : INotifyPropertyChanged
    {
        private string _status;
        private string _thumbnail;
        private string _displayName;
        private string _url;

        public string Url
        {
            get => _url;
            set { if (_url != value) { _url = value; OnPropertyChanged(); } }
        }

        public string DisplayName
        {
            get => _displayName;
            set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
        }

        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(); } }
        }

        public string Thumbnail
        {
            get => _thumbnail;
            set { if (_thumbnail != value) { _thumbnail = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
