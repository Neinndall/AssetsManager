using System;
using System.Collections.Generic;

namespace AssetsManager.Views.Models.Monitor;

public sealed class AssetCatalogArticle
{
    public string Environment { get; set; }
    public string InventoryType { get; set; }
    public string SubInventoryType { get; set; }
    public long ItemId { get; set; }
    public string Name { get; set; }
    public string CategoryId { get; set; }
    public string ImageUrl { get; set; }
    public string CandidateUrl { get; set; }
    public bool? Active { get; set; }
    public string Source { get; set; }
    public DateTime? FirstSeenApi { get; set; }
    public DateTime? LastSeenApi { get; set; }

    public string Key => $"{Environment}:{InventoryType}:{ItemId}";
}

public sealed class AssetCatalogImage : AssetTrackerEntry
{
    public List<string> CategoryIds { get; set; } = new();
    public List<string> ArticleKeys { get; set; } = new();
}

public sealed class AssetCatalogImportBatch
{
    public string Environment { get; set; }
    public string Status { get; set; }
    public bool ClientReadSucceeded { get; set; }
    public long? SummonerId { get; set; }
    public string Region { get; set; }
    public bool ApiReadSucceeded { get; set; }
    public List<AssetCatalogArticle> Articles { get; set; } = new();
}

public sealed class AssetCatalogQuery
{
    public string CategoryId { get; init; }
    public List<(string Type, long Id)> KnownItems { get; init; } = new();
}
