using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Views
{
    public partial class HashGuessingWindow : UserControl
    {
        private readonly HashGuessingService _hashGuessingService;
        private readonly BinRstHashGuessingService _binRstHashGuessingService;
        private readonly AppSettings _appSettings;
        private readonly CustomMessageBoxService _messageBoxService;
        private readonly LogService _logService;
        private readonly HashGuessLabModel _viewModel = new();
        private CancellationTokenSource _cancellationTokenSource;

        public HashGuessingWindow(
            HashGuessingService hashGuessingService,
            BinRstHashGuessingService binRstHashGuessingService,
            AppSettings appSettings,
            CustomMessageBoxService messageBoxService,
            LogService logService)
        {
            InitializeComponent();
            _hashGuessingService = hashGuessingService;
            _binRstHashGuessingService = binRstHashGuessingService;
            _appSettings = appSettings;
            _messageBoxService = messageBoxService;
            _logService = logService;
            DataContext = _viewModel;
            Unloaded += OnUnloaded;
            UpdateUnknownCountAsync();
        }

        private async void UpdateUnknownCountAsync()
        {
            if (DomainSelector == null || TxtUnknownCount == null) return;
            try
            {
                if (DomainSelector.SelectedIndex < 2)
                {
                    var domain = DomainSelector.SelectedIndex == 0 ? HashGuessDomain.Game : HashGuessDomain.Lcu;
                    var summary = await _hashGuessingService.GetUnknownSummaryAsync(domain, CancellationToken.None);
                    TxtUnknownCount.Text = summary.Recent + summary.Historical == 0
                        ? $"{summary.Current:N0} current"
                        : $"{summary.Current:N0} current · {summary.Recent:N0} recent · {summary.Historical:N0} historical";
                }
                else
                {
                    var summary = await _binRstHashGuessingService.GetSummaryAsync(CancellationToken.None);
                    TxtUnknownCount.Text = DomainSelector.SelectedIndex == 2
                        ? $"{summary.BinTotal:N0} BIN unknowns"
                        : $"{summary.RstTotal:N0} RST unknowns";
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Hash Lab could not refresh the unknown hash count.");
                TxtUnknownCount.Text = "Unknown";
            }
        }

        private void DomainSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateUnknownCountAsync();
        }

        private async void RunGrepGame_Click(object sender, RoutedEventArgs e) => await RunAsync(HashGuessMode.GrepGame);
        private async void RunGrepLcu_Click(object sender, RoutedEventArgs e) => await RunAsync(HashGuessMode.GrepLcu);
        private async void RunCanonical_Click(object sender, RoutedEventArgs e) => await RunAsync(HashGuessMode.RunCanonical);
        private async void RunLocales_Click(object sender, RoutedEventArgs e) => await RunAsync(HashGuessMode.RunLocales);
        private async void RunNumbers_Click(object sender, RoutedEventArgs e) => await RunAsync(HashGuessMode.RunNumbers);
        private async void RunGameBasic_Click(object sender, RoutedEventArgs e) => await RunAsync(HashGuessMode.GameBasic);
        private async void RunGameExtended_Click(object sender, RoutedEventArgs e) => await RunAsync(HashGuessMode.GameExtended);
        private async void RunLcuBasic_Click(object sender, RoutedEventArgs e) => await RunAsync(HashGuessMode.LcuBasic);
        private async void RunLcuAdvanced_Click(object sender, RoutedEventArgs e) => await RunAsync(HashGuessMode.LcuAdvanced);
        private async void BuildInternalInventory_Click(object sender, RoutedEventArgs e) => await RunInternalAsync(InternalHashAction.Inventory);
        private async void RunInternalContent_Click(object sender, RoutedEventArgs e) => await RunInternalAsync(InternalHashAction.Content);
        private async void RunInternalStructural_Click(object sender, RoutedEventArgs e) => await RunInternalAsync(InternalHashAction.Structural);
        private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellationTokenSource?.Cancel();

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        private async System.Threading.Tasks.Task RunAsync(HashGuessMode mode)
        {
            if (_viewModel.IsRunning) return;

            var domain = DomainSelector.SelectedIndex == 0 ? HashGuessDomain.Game : HashGuessDomain.Lcu;
            if (mode == HashGuessMode.GrepGame) domain = HashGuessDomain.Game;
            else if (mode == HashGuessMode.GrepLcu) domain = HashGuessDomain.Lcu;

            string rootPath = _appSettings.LolPbeDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(rootPath) || !System.IO.Directory.Exists(rootPath))
            {
                _messageBoxService.ShowError("Hash Guessing Lab", "Please configure the LoL PBE Install Directory in Settings first.", Window.GetWindow(this));
                return;
            }
            var runCancellation = new CancellationTokenSource();
            _cancellationTokenSource = runCancellation;
            _viewModel.IsRunning = true;
            _viewModel.ProgressValue = 0;
            _viewModel.IsProgressIndeterminate = mode != HashGuessMode.GrepGame && mode != HashGuessMode.GrepLcu;
            _viewModel.StatusText = (mode == HashGuessMode.GrepGame || mode == HashGuessMode.GrepLcu) ? "Building unknown hash inventory..." : "Building structural candidates...";
            _viewModel.Matches.Clear();

            try
            {
                var progress = new Progress<HashGuessProgress>(value =>
                {
                    _viewModel.IsProgressIndeterminate = value.TotalWads == 0;
                    if (value.TotalWads > 0)
                        _viewModel.ProgressValue = value.ProcessedWads * 100d / value.TotalWads;
                    _viewModel.StatusText = $"Scanning {value.CurrentWad} · {value.ProcessedChunks:N0} chunks · {value.FoundMatches:N0} matches";
                });
                var result = mode switch
                {
                    HashGuessMode.RunCanonical => await _hashGuessingService.RunCanonicalGuessingAsync(domain, rootPath, progress, runCancellation.Token),
                    HashGuessMode.RunLocales => await _hashGuessingService.RunLanguageGuessingAsync(domain, rootPath, progress, runCancellation.Token),
                    HashGuessMode.RunNumbers => await _hashGuessingService.RunNumberGuessingAsync(domain, rootPath, progress, runCancellation.Token),
                    HashGuessMode.GameBasic => await _hashGuessingService.RunGameBasicGuessingAsync(rootPath, progress, runCancellation.Token),
                    HashGuessMode.GameExtended => await _hashGuessingService.RunGameExtendedGuessingAsync(rootPath, progress, runCancellation.Token),
                    HashGuessMode.LcuBasic => await _hashGuessingService.RunLcuBasicGuessingAsync(rootPath, progress, runCancellation.Token),
                    HashGuessMode.LcuAdvanced => await _hashGuessingService.RunLcuAdvancedGuessingAsync(rootPath, progress, runCancellation.Token),
                    HashGuessMode.GrepGame => await _hashGuessingService.RunEmbeddedPathGrepAsync(HashGuessDomain.Game, rootPath, progress, runCancellation.Token),
                    HashGuessMode.GrepLcu => await _hashGuessingService.RunEmbeddedPathGrepAsync(HashGuessDomain.Lcu, rootPath, progress, runCancellation.Token),
                    _ => throw new ArgumentOutOfRangeException(nameof(mode))
                };
                _viewModel.Matches.AddRange(result.Matches);
                _viewModel.ProgressValue = 100;
                _viewModel.IsProgressIndeterminate = false;
                if (result.Matches.Count > 0)
                {
                    await _hashGuessingService.SaveMatchesAsync(result.Matches, CancellationToken.None);
                    _viewModel.StatusText = $"Completed: {result.Matches.Count:N0} paths resolved and automatically added to main hash files.";
                }
                else
                {
                    _viewModel.StatusText = $"Completed: {result.Matches.Count:N0} paths resolved from {result.UnknownHashesAtStart:N0} unknown hashes.";
                }
            }
            catch (OperationCanceledException)
            {
                _viewModel.StatusText = "Hash guessing cancelled.";
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Hash guessing failed.");
                _viewModel.StatusText = "Hash guessing failed. Check application_errors.log.";
                _messageBoxService.ShowError("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            finally
            {
                if (ReferenceEquals(_cancellationTokenSource, runCancellation))
                    _cancellationTokenSource = null;
                runCancellation.Dispose();
                _viewModel.IsProgressIndeterminate = false;
                _viewModel.IsRunning = false;
                UpdateUnknownCountAsync();
            }
        }

        private async System.Threading.Tasks.Task RunInternalAsync(InternalHashAction action)
        {
            if (_viewModel.IsRunning) return;
            string rootPath = _appSettings.LolPbeDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(rootPath) || !System.IO.Directory.Exists(rootPath))
            {
                _messageBoxService.ShowError("Hash Guessing Lab", "Please configure the LoL PBE Install Directory in Settings first.", Window.GetWindow(this));
                return;
            }

            bool includeBin = DomainSelector.SelectedIndex == 2;
            bool includeRst = DomainSelector.SelectedIndex == 3;
            var runCancellation = new CancellationTokenSource();
            _cancellationTokenSource = runCancellation;
            _viewModel.IsRunning = true;
            _viewModel.ProgressValue = 0;
            _viewModel.IsProgressIndeterminate = action == InternalHashAction.Structural;
            string internalDomain = includeBin ? "BIN" : "RST";
            _viewModel.StatusText = action == InternalHashAction.Inventory ? $"Building {internalDomain} inventory..." : "Preparing internal hash candidates...";
            _viewModel.Matches.Clear();

            try
            {
                var progress = new Progress<InternalHashProgress>(value =>
                {
                    _viewModel.IsProgressIndeterminate = value.TotalWads == 0;
                    if (value.TotalWads > 0) _viewModel.ProgressValue = value.ProcessedWads * 100d / value.TotalWads;
                    _viewModel.StatusText = $"{value.CurrentStage} · {value.ProcessedFiles:N0} files/candidates · {value.FoundMatches:N0} matches";
                });

                if (action == InternalHashAction.Inventory)
                {
                    var inventory = await _binRstHashGuessingService.BuildInventoryAsync(rootPath, includeBin, includeRst, progress, runCancellation.Token);
                    _viewModel.ProgressValue = 100;
                    _viewModel.StatusText = includeBin
                        ? $"BIN inventory completed: {inventory.ScannedBins:N0} files parsed."
                        : $"RST inventory completed: {inventory.ScannedStringTables:N0} stringtables parsed.";
                }
                else
                {
                    InternalHashRunResult result = action switch
                    {
                        InternalHashAction.Content => await _binRstHashGuessingService.RunContentGuessingAsync(rootPath, includeBin, includeRst, progress, runCancellation.Token),
                        _ => await _binRstHashGuessingService.RunStructuralGuessingAsync(rootPath, includeBin, includeRst, progress, runCancellation.Token)
                    };
                    _viewModel.Matches.AddRange(result.Matches.Cast<object>());
                    _viewModel.ProgressValue = 100;
                    _viewModel.StatusText = $"Completed: {result.Matches.Count:N0} internal hashes resolved and saved.";
                }
            }
            catch (OperationCanceledException)
            {
                _viewModel.StatusText = "Internal hash guessing cancelled.";
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Internal hash guessing failed.");
                _viewModel.StatusText = "Internal hash guessing failed. Check application_errors.log.";
                _messageBoxService.ShowError("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            finally
            {
                if (ReferenceEquals(_cancellationTokenSource, runCancellation)) _cancellationTokenSource = null;
                runCancellation.Dispose();
                _viewModel.IsProgressIndeterminate = false;
                _viewModel.IsRunning = false;
                UpdateUnknownCountAsync();
            }
        }

        private void ListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ListView listView && listView.View is GridView gridView)
            {
                UpdateColumnWidths(listView, gridView);
            }
        }

        private void UpdateColumnWidths(ListView listView, GridView gridView)
        {
            var scrollViewer = GetScrollViewer(listView);
            double availableWidth = 0;

            if (scrollViewer != null)
            {
                // ViewportWidth is the exact visible width of the content area (scrollbars subtracted automatically)
                availableWidth = scrollViewer.ViewportWidth;

                // Subscribe to ScrollChanged to recalculate when the viewport size changes
                if (scrollViewer.Tag == null)
                {
                    scrollViewer.ScrollChanged += (s, args) =>
                    {
                        if (args.ViewportWidthChange != 0 || args.ViewportHeightChange != 0 || args.ExtentHeightChange != 0)
                        {
                            UpdateColumnWidths(listView, gridView);
                        }
                    };
                    scrollViewer.Tag = true;
                }
            }

            if (availableWidth <= 0)
            {
                double totalWidth = listView.ActualWidth;
                if (totalWidth <= 0) return;
                availableWidth = totalWidth - 22; // Fallback
            }

            // Subtract 2 pixels to prevent minor floating point rounding from triggering a horizontal scrollbar
            availableWidth = Math.Max(300, availableWidth - 2);

            double fixedWidths = 140 + 80 + 120; // Hash (140), Domain (80), Strategy (120)
            double remainingWidth = availableWidth - fixedWidths;
            if (remainingWidth < 150) remainingWidth = 150;

            gridView.Columns[0].Width = 140;
            gridView.Columns[1].Width = 80;
            
            double resolvedWidth = Math.Floor(remainingWidth * 0.45);
            gridView.Columns[2].Width = resolvedWidth;
            gridView.Columns[3].Width = 120;
            
            // Last column takes EXACTLY the remaining width to leave 0px empty gap
            gridView.Columns[4].Width = Math.Max(100, availableWidth - (140 + 80 + resolvedWidth + 120));
        }

        private static ScrollViewer GetScrollViewer(DependencyObject depObj)
        {
            if (depObj is ScrollViewer) return (ScrollViewer)depObj;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        private enum HashGuessMode { GrepGame, GrepLcu, RunCanonical, RunLocales, RunNumbers, GameBasic, GameExtended, LcuBasic, LcuAdvanced }
        private enum InternalHashAction { Inventory, Content, Structural }
    }
}
