using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
        private bool _isUpdatingResultsColumns;

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
        }

        private async void UpdateUnknownCountAsync()
        {
            if (DomainSelector == null || TxtUnknownCount == null || TxtUnknownBreakdown == null) return;
            int selectedIndex = DomainSelector.SelectedIndex;
            try
            {
                if (selectedIndex < 2)
                {
                    var domain = selectedIndex == 0 ? HashGuessDomain.Game : HashGuessDomain.Lcu;
                    var summary = await Task.Run(() => _hashGuessingService.GetUnknownSummaryAsync(domain, CancellationToken.None));
                    if (DomainSelector != null && DomainSelector.SelectedIndex == selectedIndex)
                    {
                        TxtUnknownCount.Text = $"{summary.Total:N0} unresolved";
                        TxtUnknownBreakdown.Text = $"Current: {summary.Current:N0} · Recent: {summary.Recent:N0} · Historical: {summary.Historical:N0}";
                    }
                }
                else
                {
                    var summary = await Task.Run(() => _binRstHashGuessingService.GetSummaryAsync(CancellationToken.None));
                    if (DomainSelector != null && DomainSelector.SelectedIndex == selectedIndex)
                    {
                        if (selectedIndex == 2)
                        {
                            TxtUnknownCount.Text = $"{summary.BinTotal:N0} BIN unresolved";
                            TxtUnknownBreakdown.Text = $"Entries: {summary.BinEntries:N0} · Fields: {summary.BinFields:N0} · Types: {summary.BinTypes:N0} · Hashes: {summary.BinHashes:N0}";
                        }
                        else
                        {
                            TxtUnknownCount.Text = $"{summary.RstTotal:N0} RST unresolved";
                            TxtUnknownBreakdown.Text = $"XXH3: {summary.RstXxh3:N0} · XXH64: {summary.RstXxh64:N0}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Hash Lab could not refresh the unknown hash count.");
                TxtUnknownCount.Text = "Unknown";
                TxtUnknownBreakdown.Text = string.Empty;
            }
        }

        private void ShowLiveUnknownCount(int remaining, int resolved)
        {
            TxtUnknownCount.Text = $"{remaining + resolved:N0} session targets";
            TxtUnknownBreakdown.Text = $"Unresolved: {remaining:N0} · Resolved: {resolved:N0}";
        }

        private void ResultsListView_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(UpdateSourceWadColumnWidth, DispatcherPriority.Loaded);
        }

        private void ResultsListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSourceWadColumnWidth();
        }

        private void ResultsColumnHeader_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isUpdatingResultsColumns)
            {
                UpdateSourceWadColumnWidth();
            }
        }

        private void UpdateSourceWadColumnWidth()
        {
            if (_isUpdatingResultsColumns ||
                ResultsListView?.View is not GridView gridView ||
                gridView.Columns.Count != 5)
            {
                return;
            }

            var scrollViewer = FindScrollViewer(ResultsListView);
            if (scrollViewer == null || scrollViewer.ViewportWidth <= 0)
            {
                return;
            }

            double precedingWidth = gridView.Columns.Take(4).Sum(column => column.ActualWidth);
            double sourceWadWidth = Math.Max(100, scrollViewer.ViewportWidth - precedingWidth);

            if (Math.Abs(gridView.Columns[4].Width - sourceWadWidth) < 0.5)
            {
                return;
            }

            _isUpdatingResultsColumns = true;
            try
            {
                gridView.Columns[4].Width = sourceWadWidth;
            }
            finally
            {
                _isUpdatingResultsColumns = false;
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject element)
        {
            if (element is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            {
                var result = FindScrollViewer(VisualTreeHelper.GetChild(element, index));
                if (result != null)
                {
                    return result;
                }
            }

            return null;
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
        private async void RunLcuV1Paths_Click(object sender, RoutedEventArgs e) => await RunAsync(HashGuessMode.LcuV1Paths);
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
            _viewModel.ProgressText = "Scanning";
            _viewModel.IsProgressIndeterminate = mode != HashGuessMode.GrepGame && mode != HashGuessMode.GrepLcu;
            
            string currentStage = (mode == HashGuessMode.GrepGame || mode == HashGuessMode.GrepLcu) ? "Building unknown hash inventory..." : "Building structural candidates...";
            long totalChecked = 0;
            int totalWads = 0;
            int foundMatches = 0;

            _viewModel.Matches.Clear();
            var displayedMatchHashes = new System.Collections.Generic.HashSet<ulong>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            void UpdateStatus()
            {
                string timeText = FormatElapsedTime(stopwatch.Elapsed);
                _viewModel.StatusText = totalWads > 0
                    ? $"{currentStage} · {totalChecked:N0} checked · {foundMatches:N0} found · Time: {timeText}"
                    : $"{currentStage} · {foundMatches:N0} found · Time: {timeText}";
            }

            UpdateStatus();

            var timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            timer.Tick += (s, e) =>
            {
                if (_viewModel.IsRunning && stopwatch.IsRunning)
                {
                    UpdateStatus();
                }
            };
            timer.Start();

            try
            {
                var progress = new Progress<HashGuessProgress>(value =>
                {
                    ShowLiveUnknownCount(value.RemainingUnknowns, value.FoundMatches);
                    _viewModel.IsProgressIndeterminate = value.TotalWads == 0;
                    if (value.TotalWads > 0)
                    {
                        _viewModel.ProgressValue = value.ProcessedWads * 100d / value.TotalWads;
                        _viewModel.ProgressText = $"{_viewModel.ProgressValue:F0}%";
                    }
                    else
                    {
                        long checkedCount = value.CheckedCandidates > 0 ? value.CheckedCandidates : value.ProcessedChunks;
                        _viewModel.ProgressText = $"{checkedCount:N0} checked";
                    }
                    currentStage = string.IsNullOrEmpty(value.CurrentWad) ? currentStage : value.CurrentWad;
                    totalChecked = value.CheckedCandidates > 0 ? value.CheckedCandidates : value.ProcessedChunks;
                    totalWads = value.TotalWads;
                    foundMatches = value.FoundMatches;
                    UpdateStatus();
                });
                IProgress<HashGuessMatch> matchProgress =
                    (mode == HashGuessMode.GrepGame || mode == HashGuessMode.GrepLcu || mode == HashGuessMode.LcuV1Paths)
                    ? new Progress<HashGuessMatch>(match =>
                    {
                        if (displayedMatchHashes.Add(match.Hash))
                            _viewModel.Matches.Add(match);
                    })
                    : null;
                var result = mode switch
                {
                    HashGuessMode.RunCanonical => await _hashGuessingService.RunCanonicalGuessingAsync(domain, rootPath, progress, runCancellation.Token),
                    HashGuessMode.RunLocales => await _hashGuessingService.RunLanguageGuessingAsync(domain, rootPath, progress, runCancellation.Token),
                    HashGuessMode.RunNumbers => await _hashGuessingService.RunNumberGuessingAsync(domain, rootPath, progress, runCancellation.Token),
                    HashGuessMode.GameBasic => await _hashGuessingService.RunGameBasicGuessingAsync(rootPath, progress, runCancellation.Token),
                    HashGuessMode.GameExtended => await _hashGuessingService.RunGameExtendedGuessingAsync(rootPath, progress, runCancellation.Token),
                    HashGuessMode.LcuBasic => await _hashGuessingService.RunLcuBasicGuessingAsync(rootPath, progress, runCancellation.Token),
                    HashGuessMode.LcuAdvanced => await _hashGuessingService.RunLcuAdvancedGuessingAsync(rootPath, progress, runCancellation.Token),
                    HashGuessMode.LcuV1Paths => await _hashGuessingService.RunLcuV1PathGuessingAsync(rootPath, progress, runCancellation.Token, matchProgress),
                    HashGuessMode.GrepGame => await _hashGuessingService.RunEmbeddedPathGrepAsync(HashGuessDomain.Game, rootPath, progress, runCancellation.Token, matchProgress),
                    HashGuessMode.GrepLcu => await _hashGuessingService.RunEmbeddedPathGrepAsync(HashGuessDomain.Lcu, rootPath, progress, runCancellation.Token, matchProgress),
                    _ => throw new ArgumentOutOfRangeException(nameof(mode))
                };
                stopwatch.Stop();
                timer.Stop();
                string elapsedTime = FormatElapsedTime(stopwatch.Elapsed);
                _viewModel.Matches.AddRange(result.Matches.Where(match => displayedMatchHashes.Add(match.Hash)));
                _viewModel.ProgressValue = 100;
                _viewModel.ProgressText = "100%";
                _viewModel.IsProgressIndeterminate = false;
                if (result.Matches.Count > 0)
                {
                    await _hashGuessingService.SaveMatchesAsync(result.Matches, CancellationToken.None);
                    _viewModel.StatusText = $"Completed in {elapsedTime}: {result.Matches.Count:N0} paths resolved and automatically added to main hash files.";
                }
                else
                {
                    _viewModel.StatusText = $"Completed in {elapsedTime}: {result.Matches.Count:N0} paths resolved from {result.UnknownHashesAtStart:N0} unknown hashes.";
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                timer.Stop();
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _viewModel.StatusText = "Hash guessing cancelled.";
            }
            catch (InvalidOperationException ex)
            {
                stopwatch.Stop();
                timer.Stop();
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _logService.LogWarning(ex.Message);
                _viewModel.StatusText = "Pre-validation failed. Run WAD Path Grep first.";
                _messageBoxService.ShowWarning("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            catch (System.IO.DirectoryNotFoundException ex)
            {
                stopwatch.Stop();
                timer.Stop();
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _logService.LogWarning(ex.Message);
                _viewModel.StatusText = "Selected directory does not exist.";
                _messageBoxService.ShowWarning("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                timer.Stop();
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _logService.LogError(ex, "Hash guessing failed.");
                _viewModel.StatusText = "Hash guessing failed. Check application_errors.log.";
                _messageBoxService.ShowError("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            finally
            {
                timer.Stop();
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
            _viewModel.ProgressText = "Scanning";
            _viewModel.IsProgressIndeterminate = action == InternalHashAction.Structural;
            string internalDomain = includeBin ? "BIN" : "RST";
            
            string currentStage = action == InternalHashAction.Inventory ? $"Building {internalDomain} inventory..." : "Preparing internal hash scan...";
            long totalChecked = 0;
            int totalWads = 0;
            int foundMatches = 0;

            _viewModel.Matches.Clear();
            var displayedInternalMatches = new System.Collections.Generic.HashSet<(InternalHashKind Kind, ulong Hash, string Value)>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            void UpdateStatus()
            {
                string timeText = FormatElapsedTime(stopwatch.Elapsed);
                _viewModel.StatusText = totalWads > 0
                    ? $"{currentStage} · {totalChecked:N0} checked · {foundMatches:N0} found · Time: {timeText}"
                    : $"{currentStage} · {foundMatches:N0} found · Time: {timeText}";
            }

            UpdateStatus();

            var timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            timer.Tick += (s, e) =>
            {
                if (_viewModel.IsRunning && stopwatch.IsRunning)
                {
                    UpdateStatus();
                }
            };
            timer.Start();

            try
            {
                var progress = new Progress<InternalHashProgress>(value =>
                {
                    if (value.NewMatches.Count > 0)
                    {
                        var newMatches = value.NewMatches
                            .Where(match => displayedInternalMatches.Add((match.Kind, match.Hash, match.Value)))
                            .Cast<object>()
                            .ToList();
                        if (newMatches.Count > 0) _viewModel.Matches.AddRange(newMatches);
                    }
                    if (value.RemainingUnknowns.HasValue)
                    {
                        ShowLiveUnknownCount(value.RemainingUnknowns.Value, value.FoundMatches);
                    }
                    else
                    {
                        TxtUnknownCount.Text = "Scanning inventory";
                        TxtUnknownBreakdown.Text = $"Parsed: {value.ProcessedFiles:N0} files";
                    }
                    _viewModel.IsProgressIndeterminate = value.TotalWads == 0;
                    if (value.TotalWads > 0)
                    {
                        _viewModel.ProgressValue = value.ProcessedWads * 100d / value.TotalWads;
                        _viewModel.ProgressText = $"{_viewModel.ProgressValue:F0}%";
                    }
                    else
                    {
                        long checkedCount = value.CheckedCandidates > 0 ? value.CheckedCandidates : value.ProcessedFiles;
                        _viewModel.ProgressText = $"{checkedCount:N0} checked";
                    }
                    currentStage = string.IsNullOrEmpty(value.CurrentStage) ? currentStage : value.CurrentStage;
                    totalChecked = value.CheckedCandidates > 0 ? value.CheckedCandidates : value.ProcessedFiles;
                    totalWads = value.TotalWads;
                    foundMatches = value.FoundMatches;
                    UpdateStatus();
                });

                if (action == InternalHashAction.Inventory)
                {
                    var inventory = await _binRstHashGuessingService.BuildInventoryAsync(rootPath, includeBin, includeRst, progress, runCancellation.Token);
                    stopwatch.Stop();
                    timer.Stop();
                    string elapsedTime = FormatElapsedTime(stopwatch.Elapsed);
                    _viewModel.ProgressValue = 100;
                    _viewModel.ProgressText = "100%";
                    _viewModel.StatusText = includeBin
                        ? $"BIN inventory completed in {elapsedTime}: {inventory.ScannedBins:N0} files + Meta {inventory.MetaSchemaVersion} ({inventory.MetaSchemaTypes:N0} types, {inventory.MetaSchemaFields:N0} fields)."
                        : $"RST inventory completed in {elapsedTime}: {inventory.ScannedStringTables:N0} stringtables parsed.";
                }
                else
                {
                    InternalHashRunResult result = action switch
                    {
                        InternalHashAction.Content => await _binRstHashGuessingService.RunContentGuessingAsync(rootPath, includeBin, includeRst, progress, runCancellation.Token),
                        _ => await _binRstHashGuessingService.RunStructuralGuessingAsync(rootPath, includeBin, includeRst, progress, runCancellation.Token)
                    };
                    stopwatch.Stop();
                    timer.Stop();
                    string elapsedTime = FormatElapsedTime(stopwatch.Elapsed);
                    _viewModel.Matches.AddRange(result.Matches
                        .Where(match => displayedInternalMatches.Add((match.Kind, match.Hash, match.Value)))
                        .Cast<object>());
                    _viewModel.ProgressValue = 100;
                    _viewModel.ProgressText = "100%";
                    int verified = result.Matches.Count(match => match.CanPromote);
                    _viewModel.StatusText = $"Completed in {elapsedTime}: {verified:N0} verified, {result.Matches.Count - verified:N0} candidates.";
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                timer.Stop();
                _viewModel.StatusText = "Internal hash guessing cancelled.";
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                timer.Stop();
                _logService.LogError(ex, "Internal hash guessing failed.");
                _viewModel.StatusText = "Internal hash guessing failed. Check application_errors.log.";
                _messageBoxService.ShowError("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            finally
            {
                timer.Stop();
                if (ReferenceEquals(_cancellationTokenSource, runCancellation)) _cancellationTokenSource = null;
                runCancellation.Dispose();
                _viewModel.IsProgressIndeterminate = false;
                _viewModel.IsRunning = false;
                UpdateUnknownCountAsync();
            }
        }

        private static string FormatElapsedTime(TimeSpan elapsed)
        {
            if (elapsed.TotalHours >= 1)
                return elapsed.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
            return elapsed.ToString(@"mm\:ss\.f", System.Globalization.CultureInfo.InvariantCulture);
        }

        private enum HashGuessMode { GrepGame, GrepLcu, RunCanonical, RunLocales, RunNumbers, GameBasic, GameExtended, LcuBasic, LcuAdvanced, LcuV1Paths }
        private enum InternalHashAction { Inventory, Content, Structural }
    }
}
