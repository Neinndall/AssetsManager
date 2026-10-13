using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Shared;
using Microsoft.Win32;

namespace AssetsManager.Views.Controls.Monitor;

public partial class CdnTrackerCatalogControl : UserControl, INotifyPropertyChanged
{
    public AssetCatalogService CatalogService { get; set; }
    public AssetDownloader AssetDownloader { get; set; }
    public AppSettings AppSettings { get; set; }
    public LogService LogService { get; set; }
    public CustomMessageBoxService CustomMessageBoxService { get; set; }
    public PaginationModel<TrackedAsset> Paginator { get; } = new() { PageSize = 12 };
    public string Summary { get; private set; } = "Fetch Catalog to discover images.";
    public event Action<AssetCatalogImportBatch> CatalogFetched;
    public bool CanInteract => !_busy;
    public bool IsBusy => _busy;
    public event PropertyChangedEventHandler PropertyChanged;
    private CancellationTokenSource _lifetime;
    private bool _busy;

    public CdnTrackerCatalogControl()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += ControlLoaded;
        Unloaded += ControlUnloaded;
    }

    private void ControlLoaded(object sender, RoutedEventArgs e)
    {
        if (CatalogService == null) return;
        _lifetime = new();
        CatalogService.Changed -= CatalogChanged;
        CatalogService.Changed += CatalogChanged;
        if (AppSettings != null)
        {
            AppSettings.ConfigurationSaved -= SettingsChanged;
            AppSettings.ConfigurationSaved += SettingsChanged;
        }
        string selected = CategoryFilter.SelectedValue as string ?? "";
        CategoryFilter.ItemsSource = new[] { new AssetCategory { Id = "", Name = "All categories" } }.Concat(CatalogService.Categories).ToList();
        CategoryFilter.SelectedValue = selected;
        Refresh();
    }

    private void ControlUnloaded(object sender, RoutedEventArgs e)
    {
        if (CatalogService != null) CatalogService.Changed -= CatalogChanged;
        if (AppSettings != null) AppSettings.ConfigurationSaved -= SettingsChanged;
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private void CatalogChanged(object sender, EventArgs e) => _ = Dispatcher.InvokeAsync(() => { if (IsLoaded) Refresh(); });
    private void SettingsChanged(object sender, EventArgs e) => _ = Dispatcher.InvokeAsync(() => { if (IsLoaded) Refresh(); });
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Refresh(true); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) Refresh(true); }

    private void Refresh(bool resetPage = false)
    {
        if (CatalogService == null) return;
        string category = CategoryFilter.SelectedValue as string;
        IEnumerable<TrackedAsset> rows = CatalogService.GetCatalogAssets(string.IsNullOrEmpty(category) ? null : category)
            .Where(row => row.Source?.StartsWith("LCU", StringComparison.Ordinal) == true || row.CanDownload);
        string search = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(search)) rows = rows.Where(row => (row.DisplayName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
            || (row.Url ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.AssetId.ToString().Contains(search, StringComparison.Ordinal) || (row.ItemId?.ToString() ?? "").Contains(search, StringComparison.Ordinal));
        var list = rows.ToList();
        Paginator.SetFullList(list, !resetPage);
        Summary = list.Count == 0 ? "No images yet. Fetch Catalog from the connected client."
            : $"{list.Count} entries · {list.Where(row => row.CanDownload).Select(row => row.Url).Distinct(StringComparer.Ordinal).Count()} images available";
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || CatalogService == null || _lifetime == null) return;
        _busy = true;
        NotifyOperationState();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            LogService?.LogDebug("CDN Tracker catalog operation cancelled.");
        }
        catch (Exception ex)
        {
            LogService?.LogError(ex, "CDN Tracker catalog operation failed.");
            if (IsLoaded) CustomMessageBoxService?.ShowError("CDN Tracker", "The operation could not be completed. See the application log for details.", Window.GetWindow(this));
        }
        finally
        {
            _operation = null;
            _busy = false;
            NotifyOperationState();
            if (IsLoaded) Refresh();
        }
    }

    private CancellationTokenSource _operation;
    private void NotifyOperationState()
    {
        PropertyChanged?.Invoke(this, new(nameof(CanInteract)));
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
    }
    private void CancelClicked(object sender, RoutedEventArgs e) => _operation?.Cancel();
    public Task SynchronizeAsync()
    {
        string categoryId = CategoryFilter.SelectedValue as string;
        return RunAsync(async token =>
        {
            var batch = await CatalogService.SyncSourcesAsync(token, categoryId);
            CatalogFetched?.Invoke(batch);
            if (!batch.ApiReadSucceeded && IsLoaded)
                CustomMessageBoxService?.ShowError("Fetch Catalog", batch.Status, Window.GetWindow(this));
        });
    }

    public async Task VerifyAsync()
    {
        string selected = CategoryFilter.SelectedValue as string;
        await RunAsync(async token =>
        {
            foreach (var category in CatalogService.Categories.Where(item => string.IsNullOrEmpty(selected) || item.Id == selected))
            {
                var targets = CatalogService.GetCatalogAssets(category.Id).Where(row => row.Source?.StartsWith("LCU", StringComparison.Ordinal) == true && row.Url != null && !row.CanDownload)
                    .OrderBy(row => row.LastChecked ?? DateTime.MinValue)
                    .DistinctBy(row => row.Url, StringComparer.Ordinal)
                    .Take(Math.Max(1, category.ForwardScanWindow)).ToList();
                await CatalogService.ScanAsync(category, targets, token);
            }
        });
    }

    private void CopyUrlClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is TrackedAsset row && !string.IsNullOrEmpty(row.Url))
            try { Clipboard.SetText(row.Url); } catch (Exception ex) { LogService?.LogError(ex, "Could not copy the asset URL."); }
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as Button)?.Tag is not TrackedAsset row || !row.CanDownload || AssetDownloader == null) return;
        var dialog = new SaveFileDialog { FileName = Path.GetFileName(new Uri(row.Url).AbsolutePath), Filter = "Image files|*.png;*.jpg;*.jpeg;*.webp|All files|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await RunAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            await AssetDownloader.DownloadAssetToCustomPathAsync(row.Url, dialog.FileName, token);
        });
    }

    public async Task DownloadPageAsync()
    {
        if (_busy || AssetDownloader == null) return;
        var rows = Paginator.PagedItems.Where(row => row.CanDownload).DistinctBy(row => row.Url).ToArray();
        if (rows.Length == 0) return;
        var dialog = new OpenFolderDialog { Title = "Choose a folder for this page's verified images" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await RunAsync(async token =>
        {
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                string categoryFolder = string.Concat((row.CategoryName ?? "Assets").Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
                string folder = Path.Combine(dialog.FolderName, categoryFolder);
                Directory.CreateDirectory(folder);
                await AssetDownloader.DownloadAssetToCustomPathAsync(row.Url, Path.Combine(folder, Path.GetFileName(new Uri(row.Url).AbsolutePath)), token);
            }
        });
    }
}
