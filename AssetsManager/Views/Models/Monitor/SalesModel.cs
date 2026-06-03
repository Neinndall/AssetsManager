using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Monitor
{
    public class SalesCatalog
    {
        [JsonPropertyName("catalog")]
        public List<CatalogItem> Catalog { get; set; }
    }

    public class CatalogItem : INotifyPropertyChanged
    {
        [JsonPropertyName("inventoryType")]
        public string InventoryType { get; set; }

        [JsonPropertyName("subInventoryType")]
        public string SubInventoryType { get; set; }

        [JsonPropertyName("rp")]
        public int? Rp { get; set; }

        private string _formattedOriginalPrice;
        public string FormattedOriginalPrice => _formattedOriginalPrice ??= Rp.HasValue ? $"Original Price: {Rp.Value} RP" : "Original Price: N/A";

        [JsonPropertyName("ip")]
        public int? Ip { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("sale")]
        public SaleInfo Sale { get; set; }

        private string _imagePath;
        [JsonIgnore]
        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (_imagePath != value)
                {
                    _imagePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class SaleInfo
    {
        [JsonPropertyName("rp")]
        public int Rp { get; set; }

        [JsonPropertyName("percentOff")]
        public int PercentOff { get; set; }

        [JsonPropertyName("endDate")]
        public DateTime EndDateValue { get; set; }

        private string _formattedEndDate;
        public string FormattedEndDate => _formattedEndDate ??= FormatUtils.FormatTimeRemaining(EndDateValue);

        private string _formattedSaleDetails;
        public string FormattedSaleDetails => _formattedSaleDetails ??= $"Sale: {Rp} RP ({PercentOff}% dto)";
    }
}
