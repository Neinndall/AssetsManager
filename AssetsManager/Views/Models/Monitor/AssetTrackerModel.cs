using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Models.Monitor
{
    /// <summary>
    /// ENUM: Status of a category check
    /// </summary>
    public enum CategoryStatus { Idle, Checking, CompletedSuccess }
    public enum TrackedAssetState
    {
        Pending = 0,
        Checking = 1,
        Available = 2,
        Missing = 5,
        TemporaryError = 6,
        RemovedCandidate = 7,
        Removed = 8
    }

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

        public PaginationModel<TrackedAsset> Paginator { get; } = new() { PageSize = 12 };
        public bool CanInteract => !IsBusy;

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
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanInteract)); } }
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
        public List<string> Extensions { get; set; } = new List<string>();
        public List<string> FileNameSuffixes { get; set; } = new() { "" };
        public int ForwardScanWindow { get; set; } = 10;
        public int MaxConcurrency { get; set; } = 6;
        
        private long _start;
        public long Start
        {
            get => _start;
            set { if (_start != value) { _start = value; OnPropertyChanged(); } }
        }

        public List<string> UserRemovedAssetUrls { get; set; } = new List<string>();
        public Dictionary<long, AssetTrackerEntry> Entries { get; set; } = new Dictionary<long, AssetTrackerEntry>();

        public List<AssetTrackerRange> Ranges { get; set; } = new List<AssetTrackerRange>();

        public string RangeSummary => Ranges == null || Ranges.Count == 0
            ? string.Empty
            : string.Join(", ", Ranges.OrderBy(range => range.Start)
                .Select(range => $"{range.Start}->{range.NextId}"));

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
    /// Persistent forward cursor for one independent numeric discovery range.
    /// </summary>
    public sealed class AssetTrackerRange
    {
        public long Start { get; set; }
        public long NextId { get; set; }
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
        private TrackedAssetState _state;

        public TrackedAsset()
        {
            _state = TrackedAssetState.Pending;
            _status = "Pending";
        }

        public string Url
        {
            get => _url;
            set
            {
                if (_url != value)
                {
                    _url = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanDownload));
                    OnPropertyChanged(nameof(HasUrl));
                    OnPropertyChanged(nameof(StatusDescription));
                }
            }
        }

        public string DisplayName
        {
            get => _displayName;
            set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
        }

        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusDescription)); } }
        }

        public string Thumbnail
        {
            get => _thumbnail;
            set { if (_thumbnail != value) { _thumbnail = value; OnPropertyChanged(); } }
        }

        public long AssetId { get; set; }

        public string CategoryId { get; set; }
        public string CategoryName { get; set; }
        public string InventoryType { get; set; }
        public long? ItemId { get; set; }
        public string Source { get; set; }
        public string Environment { get; set; }
        public DateTime? FirstSeenCdn { get; set; }
        public DateTime? FirstSeenApi { get; set; }
        public DateTime? LastChecked { get; set; }
        public bool CanDownload => State == TrackedAssetState.Available && !string.IsNullOrWhiteSpace(Url);
        public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
        public string StatusDescription => HasUrl ? Status
            : "This catalog item has no saved image URL. An image may still exist, but it cannot be checked or downloaded here yet.";
        public string IdentityText { get; set; }
        public string TimingText { get; set; }
        public bool IsGenerated { get; set; }

        public TrackedAssetState State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _state = value;
                    Status = GetStatusText(value);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanDownload));
                }
            }
        }
        private static string GetStatusText(TrackedAssetState state) => state switch
        {
            TrackedAssetState.Available => "OK",
            TrackedAssetState.Checking => "Checking",
            TrackedAssetState.Missing => "Not Found",
            TrackedAssetState.TemporaryError => "Temporary Error",
            TrackedAssetState.RemovedCandidate => "Not Found",
            TrackedAssetState.Removed => "Not Found",
            _ => "Pending"
        };

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class AssetTrackerEntry
    {
        public long AssetId { get; set; }
        public string Url { get; set; }
        public string Extension { get; set; }
        public TrackedAssetState State { get; set; } = TrackedAssetState.Pending;
        public int? LastHttpStatus { get; set; }
        public int FailureCount { get; set; }
        public DateTime? FirstSeen { get; set; }
        public DateTime? LastSeen { get; set; }
        public DateTime? LastChecked { get; set; }
        public bool WasCdnProbed { get; set; }
    }
}
