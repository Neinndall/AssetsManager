using AssetsManager.Views.Models;
using System.Collections.Generic;

namespace AssetsManager.Views.Models
{
  public static class DefaultCategories
  {
    public static List<AssetCategory> Get()
    {
      return new List<AssetCategory>
            {
                new AssetCategory { Id = "1", Name = "Bundles Chromas", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/bundles/", Start = 79900846, Extension = "jpg" },
                new AssetCategory { Id = "2", Name = "Bundles Skins and Borders", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/bundles/", Start = 99901560, Extension = "png" },
                new AssetCategory { Id = "3", Name = "Bundles Skins", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/bundles/", Start = 89900041, Extension = "jpg" },
                new AssetCategory { Id = "4", Name = "Bundles Showcase", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/bundles/", Start = 69901023, Extension = "png" },
                new AssetCategory { Id = "5", Name = "Battle Passes", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/eventpass/", Start = 52, Extension = "png" },
                new AssetCategory { Id = "6", Name = "Currency", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/storeCards/Currency/", Start = 36, Extension = "png" },
                // new AssetCategory { Id = "7", Name = "TFT Battle Passes", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/eventpass/", Start = 3333049, Extension = "png" },
                new AssetCategory { Id = "7", Name = "Capsules and Orbs", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/hextechCrafting/", Start = 777, Extension = "png" },
                // new AssetCategory { Id = "8", Name = "Chibis", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/bundles/", Start = 33300247, Extension = "png" },
                new AssetCategory { Id = "8", Name = "Banners", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/regaliaBanner/regalia_banner_", Start = 13, Extension = "png" },
                new AssetCategory { Id = "9", Name = "Skins Augments", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/skinaugment/", Start = 4, Extension = "png" },
                new AssetCategory { Id = "10", Name = "Skins Variants", BaseUrl = "https://d392eissrffsyf.cloudfront.net/storeImages/bundles/", Start = 91000006, Extension = "jpg" }
            };
    }
  }
}
