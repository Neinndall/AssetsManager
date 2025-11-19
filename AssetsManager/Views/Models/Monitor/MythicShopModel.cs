using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Monitor
{
    public class MythicShopResponse
    {
        [JsonPropertyName("data")]
        public List<MythicShopData> Data { get; set; }
    }

    public class MythicShopData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("catalogEntries")]
        public List<MythicShopCatalogEntry> CatalogEntries { get; set; }
    }

    public class MythicShopCatalogEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("purchaseUnits")]
        public List<MythicShopPurchaseUnit> PurchaseUnits { get; set; }

        [JsonPropertyName("endTime")]
        public DateTime EndTime { get; set; }
    }

    public class MythicShopPurchaseUnit
    {
        [JsonPropertyName("paymentOptions")]
        public List<MythicShopPaymentOption> PaymentOptions { get; set; }

        [JsonPropertyName("fulfillment")]
        public MythicShopFulfillment Fulfillment { get; set; }
    }

    public class MythicShopPaymentOption
    {
        [JsonPropertyName("payments")]
        public List<MythicShopPayment> Payments { get; set; }
    }

    public class MythicShopPayment
    {
        [JsonPropertyName("delta")]
        public int Delta { get; set; }
    }

    public class MythicShopFulfillment
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public class MythicShopCategory : INotifyPropertyChanged
    {
        public string CategoryName { get; set; }
        public ObservableCollection<MythicShopModel> Items { get; set; }

        public MythicShopCategory()
        {
            Items = new ObservableCollection<MythicShopModel>();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class MythicShopModel : INotifyPropertyChanged
    {
        private string _name;
        public string Name { get => _name; set => SetProperty(ref _name, value); }

        private int _price;
        public int Price { get => _price; set => SetProperty(ref _price, value); }

        private string _endTime;
        public string EndTime { get => _endTime; set => SetProperty(ref _endTime, value); }

        public event PropertyChangedEventHandler PropertyChanged;
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
