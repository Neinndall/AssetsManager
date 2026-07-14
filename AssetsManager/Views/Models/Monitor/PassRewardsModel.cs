using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Monitor
{
    // DTOs for Progression API
    public class ProgressionResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("milestones")]
        public List<ProgressionMilestone> Milestones { get; set; }
    }

    public class ProgressionMilestone
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("properties")]
        public Dictionary<string, object> Properties { get; set; }
    }

    // DTOs for Rewards API
    public class RewardsResponse
    {
        [JsonPropertyName("data")]
        public List<RewardGroup> Data { get; set; }
    }

    public class RewardGroup
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("rewards")]
        public List<RewardItem> Rewards { get; set; }
    }

    public class RewardItem
    {
        [JsonPropertyName("quantity")]
        public long Quantity { get; set; }

        [JsonPropertyName("media")]
        public RewardMedia Media { get; set; }

        [JsonPropertyName("localizations")]
        public RewardLocalization Localizations { get; set; }
    }

    public class RewardMedia
    {
        [JsonPropertyName("iconUrl")]
        public string IconUrl { get; set; }
    }

    public class RewardLocalization
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("details")]
        public string Details { get; set; }
    }

    // ViewModel for UI display
    public class PassRewardModel : INotifyPropertyChanged
    {
        private string _level;
        public string Level { get => _level; set => SetProperty(ref _level, value); }

        private string _title;
        public string Title { get => _title; set => SetProperty(ref _title, value); }

        private string _details;
        public string Details { get => _details; set => SetProperty(ref _details, value); }

        private string _iconUrl;
        public string IconUrl { get => _iconUrl; set => SetProperty(ref _iconUrl, value); }

        private long _quantity;
        public long Quantity { get => _quantity; set => SetProperty(ref _quantity, value); }

        private bool _isFree;
        public bool IsFree { get => _isFree; set => SetProperty(ref _isFree, value); }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
