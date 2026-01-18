using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls; // Added for ContextMenu/MenuItem if needed, though they are in System.Windows.Controls
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification; // Hardcodet Namespace
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Updater;
using AssetsManager.Views.Controls;
using AssetsManager.Views.Controls.Comparator;
using AssetsManager.Views.Dialogs;

namespace AssetsManager.Views
{
    public partial class MainWindow : Window
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly LogService _logService;
        private readonly AppSettings _appSettings;
        private readonly UpdateManager _updateManager;
        private readonly AssetDownloader _assetDownloader;
        private readonly WadComparatorService _wadComparatorService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly WadDifferenceService _wadDifferenceService;
        private readonly WadPackagingService _wadPackagingService;
        private readonly BackupManager _backupManager;
        private readonly HashResolverService _hashResolverService;
        private readonly WadNodeLoaderService _wadNodeLoaderService;
        private readonly ExplorerPreviewService _explorerPreviewService;
        private readonly ProgressUIManager _progressUIManager;
        private readonly UpdateCheckService _updateCheckService;
        private readonly DiffViewService _diffViewService;
        private readonly MonitorService _monitorService;
        private readonly VersionService _versionService;
        private readonly ExtractionService _extractionService;
        private readonly ReportGenerationService _reportGenerationService;
        private readonly TaskCancellationManager _taskCancellationManager;

        private string _latestAppVersionAvailable;
        
        // New fields to manage the state of the extraction after comparison
        private bool _isExtractingAfterComparison = false;
        private string _extractionOldLolPath;
        private string _extractionNewLolPath;
        private List<SerializableChunkDiff> _diffsForExtraction;

        private GridLength _lastLogHeight;
        private bool _isLogMinimized = false;

        public MainWindow(
            IServiceProvider serviceProvider,
            LogService logService,
            AppSettings appSettings,
            UpdateManager updateManager,
            AssetDownloader assetDownloader,
            WadComparatorService wadComparatorService,
            DirectoriesCreator directoriesCreator,
            CustomMessageBoxService customMessageBoxService,
            WadDifferenceService wadDifferenceService,
            WadPackagingService wadPackagingService,
            BackupManager backupManager,
            HashResolverService hashResolverService,
            WadNodeLoaderService wadNodeLoaderService,
            ExplorerPreviewService explorerPreviewService,
            UpdateCheckService updateCheckService,
            ProgressUIManager progressUIManager,
            DiffViewService diffViewService,
            MonitorService monitorService,
            VersionService versionService,
            ExtractionService extractionService,
            ReportGenerationService reportGenerationService,
            TaskCancellationManager taskCancellationManager)
        {
            InitializeComponent();

            _serviceProvider = serviceProvider;
            _logService = logService;
            _appSettings = appSettings;
            _updateManager = updateManager;
            _assetDownloader = assetDownloader;
            _wadComparatorService = wadComparatorService;
            _customMessageBoxService = customMessageBoxService;
            _directoriesCreator = directoriesCreator;
            _wadDifferenceService = wadDifferenceService;
            _wadPackagingService = wadPackagingService;
            _backupManager = backupManager;
            _hashResolverService = hashResolverService;
            _wadNodeLoaderService = wadNodeLoaderService;
            _explorerPreviewService = explorerPreviewService;
            _updateCheckService = updateCheckService;
            _progressUIManager = progressUIManager;
            _diffViewService = diffViewService;
            _monitorService = monitorService;
            _versionService = versionService;
            _extractionService = extractionService;
            _reportGenerationService = reportGenerationService;
            _taskCancellationManager = taskCancellationManager;

            // Initialize ProgressUIManager with controls from the new StatusBarView
            _progressUIManager.Initialize(StatusBar.ViewModel, this);
            
            // Subscribe to Status Bar events
            StatusBar.NotificationClicked += StatusBar_NotificationClicked;
            StatusBar.ProgressSummaryClicked += (s, e) => _progressUIManager.ShowDetails();
            
            _logService.SetLogOutput(LogView.LogRichTextBox);
            LogView.ToggleLogSizeRequested += OnToggleLogSizeRequested;
            LogView.ClearStatusBarRequested += (s, e) => ClearStatusBar();
            LogView.LogExpandedManually += (s, e) => _isLogMinimized = false;

            _wadComparatorService.ComparisonStarted += _progressUIManager.OnComparisonStarted;
            _wadComparatorService.ComparisonProgressChanged += _progressUIManager.OnComparisonProgressChanged;
            _wadComparatorService.ComparisonCompleted += _progressUIManager.OnComparisonCompleted;
            _wadComparatorService.ComparisonCompleted += OnWadComparisonCompleted;

            _extractionService.ExtractionStarted += _progressUIManager.OnExtractionStarted;
            _extractionService.ExtractionProgressChanged += (sender, progress) => _progressUIManager.OnExtractionProgressChanged(progress.extractedCount, progress.totalFiles, progress.message);
            _extractionService.ExtractionCompleted += (sender, e) => OnExtractionCompleted(sender, e);

            _versionService.VersionDownloadStarted += (sender, e) => _progressUIManager.OnVersionDownloadStarted(sender, e);
            _versionService.VersionDownloadProgressChanged += (sender, e) => _progressUIManager.OnDownloadProgressChanged(e.CurrentValue, e.TotalValue, e.CurrentFile, true, null);
            _versionService.VersionDownloadCompleted += (sender, e) => _progressUIManager.OnDownloadCompleted();

            _updateCheckService.UpdatesFound += OnUpdatesFound;

            Sidebar.NavigationRequested += OnSidebarNavigationRequested;
            LoadHomeWindow();

            _updateCheckService.Start();
            InitializeApplicationAsync();

            StateChanged += MainWindow_StateChanged;
            Closing += MainWindow_Closing;
        }

        private async void InitializeApplicationAsync()
        {
            await _updateCheckService.CheckForAllUpdatesAsync();
            await _hashResolverService.LoadAllHashesAsync();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Register window hook to handle single instance restoration message
            var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == SingleInstance.WM_SHOW_APP)
                {
                    ShowAppFromTray();
                    handled = true;
                }
                return IntPtr.Zero;
            });
            
            // SingleInstance registration removed as it is no longer needed for basic notifications
            // If advanced command line handling is needed for the tray icon, it can be re-added here.
        }

        // --- Taskbar / NotifyIcon Logic ---

        private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            ShowAppFromTray();
        }

        private void MenuItem_Show_Click(object sender, RoutedEventArgs e)
        {
            ShowAppFromTray();
        }

        private void MenuItem_Exit_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void ShowAppFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            TrayIcon.Visibility = Visibility.Collapsed;
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _appSettings.MinimizeToTrayOnClose)
            {
                TrayIcon.Visibility = Visibility.Visible;
                Hide();
                // Show a balloon tip when minimized to tray
                TrayIcon.ShowBalloonTip("AssetsManager", "The application has been minimized to the tray.", BalloonIcon.Info);
            }
        }

        // --- End Taskbar Logic ---
        private void OnUpdatesFound(string message, string latestVersion)
        {
            if (!string.IsNullOrEmpty(latestVersion))
            {
                _latestAppVersionAvailable = latestVersion;
            }

            // Show System Tray Balloon Notification if window is not visible
            if (Visibility != Visibility.Visible)
            {
                TrayIcon.ShowBalloonTip("AssetsManager", message, BalloonIcon.Info);
            }

            // Always update internal notification system
            ShowNotification(true, message);
        }
        
        private void OnExtractionCompleted(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _progressUIManager.OnExtractionCompleted();
                if (_isExtractingAfterComparison)
                {
                    ShowComparisonResultWindow(_diffsForExtraction, _extractionOldLolPath, _extractionNewLolPath);
                    _isExtractingAfterComparison = false; // Reset flag
                }
            });
        }

        private async void StartExtractionAsync()
        {
            var cancellationToken = _taskCancellationManager.PrepareNewOperation();
            await _extractionService.ExtractNewFilesFromComparisonAsync(_diffsForExtraction, _extractionNewLolPath, cancellationToken);
        }
        
        private async void OnWadComparisonCompleted(List<ChunkDiff> allDiffs, string oldLolPath, string newLolPath)
        {
            if (allDiffs == null)
            {
                return;
            }

            var serializableDiffs = allDiffs.Select(d => new SerializableChunkDiff
            {
                Type = d.Type,
                OldPath = d.OldPath,
                NewPath = d.NewPath,
                SourceWadFile = d.SourceWadFile,
                OldPathHash = d.OldChunk.PathHash,
                NewPathHash = d.NewChunk.PathHash,
                OldUncompressedSize = (d.Type == ChunkDiffType.New) ? (ulong?)null : (ulong)d.OldChunk.UncompressedSize,
                NewUncompressedSize = (d.Type == ChunkDiffType.Removed) ? (ulong?)null : (ulong)d.NewChunk.UncompressedSize,
                OldCompressionType = (d.Type == ChunkDiffType.New) ? null : d.OldChunk.Compression,
                NewCompressionType = (d.Type == ChunkDiffType.Removed) ? null : d.NewChunk.Compression
            }).ToList();

            if (!serializableDiffs.Any())
            {
                _logService.Log("Comparison completed with no differences found.");
                return;
            }

            if (_appSettings.ReportGeneration.Enabled) // Prioritize report generation
            {
                await _reportGenerationService.GenerateReportAsync(serializableDiffs, oldLolPath, newLolPath);
            }
            else if (_appSettings.EnableExtraction) // Only extract if report generation is NOT enabled
            {
                _isExtractingAfterComparison = true;
                _diffsForExtraction = serializableDiffs;
                _extractionOldLolPath = oldLolPath;
                _extractionNewLolPath = newLolPath;
                
                Dispatcher.Invoke(StartExtractionAsync);
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    ShowComparisonResultWindow(serializableDiffs, oldLolPath, newLolPath);
                });
            }
        }

        private void ShowComparisonResultWindow(List<SerializableChunkDiff> diffs, string oldPath, string newPath)
        {
            var resultWindow = new WadComparisonResultWindow(diffs, _serviceProvider, _customMessageBoxService, _directoriesCreator, _assetDownloader, _logService, _wadDifferenceService, _wadPackagingService, _diffViewService, _hashResolverService, _appSettings, oldPath, newPath);
            resultWindow.Owner = this;
            resultWindow.Show();
        }

        public void ShowNotification(bool show, string message = "Updates have been detected. Click to dismiss.")
        {
            // Delegate to StatusBar
            StatusBar.ShowNotification(show, message);
        }

        public void ClearStatusBar()
        {
            _progressUIManager.ClearStatusText();
            // Delegate to StatusBar
            StatusBar.ClearStatusBar();
        }

        private void OnToggleLogSizeRequested(object sender, EventArgs e)
        {
            // If the log is currently showing only the toolbar (effectively hidden by manual resize)
            // or is explicitly minimized, we want to expand it.
            if (_isLogMinimized || LogRowDefinition.ActualHeight <= 45)
            {
                // Restore / Expand
                // If the last height was too small (due to manual resize), use a default height
                if (!_lastLogHeight.IsAbsolute || _lastLogHeight.Value <= 45)
                {
                    _lastLogHeight = new GridLength(185);
                }

                LogRowDefinition.Height = _lastLogHeight;
                _isLogMinimized = false;
            }
            else
            {
                // Minimize
                _lastLogHeight = LogRowDefinition.Height;
                LogRowDefinition.Height = GridLength.Auto;
                _isLogMinimized = true;
            }
        }

        private async void StatusBar_NotificationClicked(object sender, EventArgs e)
        {
            StatusBar.ShowNotification(false);
            if (!string.IsNullOrEmpty(_latestAppVersionAvailable))
            {
                await _updateManager.CheckForUpdatesAsync(this, true);
                _latestAppVersionAvailable = null;
            }
        }

        private void OnSidebarNavigationRequested(string viewTag)
        {
            // Only clean up the current view if we are navigating to a *main content view*
            // Dialogs (Settings, Help) do not replace the main content, so no cleanup is needed.
            if (viewTag != "Settings" && viewTag != "Help")
            {
                if (MainContentArea.Content is ExplorerWindow explorerWindow)
                {
                    explorerWindow.CleanupResources();
                }
                else if (MainContentArea.Content is ModelWindow modelWindow)
                {
                    modelWindow.CleanupResources();
                }
            }

            switch (viewTag)
            {
                case "Home": LoadHomeWindow(); break;
                case "Explorer": LoadExplorerWindow(); break;
                case "Comparator": LoadComparatorWindow(); break;
                case "Models": LoadModelWindow(); break;
                case "Monitor": LoadMonitorWindow(); break;
                case "Settings": btnSettings_Click(null, null); break;
                case "Help": btnHelp_Click(null, null); break;
            }
        }

        private void LoadHomeWindow()
        {
            var homeWindow = _serviceProvider.GetRequiredService<HomeWindow>();
            homeWindow.NavigationRequested += (tag) =>
            {
                Sidebar.SelectNavigationItem(tag);
                OnSidebarNavigationRequested(tag);
            };
            MainContentArea.Content = homeWindow;
        }

        private void LoadExplorerWindow()
        {
            MainContentArea.Content = _serviceProvider.GetRequiredService<ExplorerWindow>();
        }

        private void LoadComparatorWindow()
        {
            var comparatorWindow = _serviceProvider.GetRequiredService<ComparatorWindow>();
            MainContentArea.Content = comparatorWindow;
        }

        private void LoadModelWindow()
        {
            MainContentArea.Content = _serviceProvider.GetRequiredService<ModelWindow>();
        }

        private void LoadMonitorWindow()
        {
            MainContentArea.Content = _serviceProvider.GetRequiredService<MonitorWindow>();
        }

        private void btnHelp_Click(object sender, RoutedEventArgs e)
        {
            var helpWindow = _serviceProvider.GetRequiredService<HelpWindow>();
            helpWindow.ShowDialog();
        }

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
            settingsWindow.Owner = this;
            settingsWindow.SettingsChanged += OnSettingsChanged;
            settingsWindow.ShowDialog();
        }

        private void OnSettingsChanged(object sender, SettingsChangedEventArgs e)
        {
            _updateCheckService.Stop();
            _updateCheckService.Start();
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            StateChanged -= MainWindow_StateChanged;
            TrayIcon?.Dispose();
        }
    }
}
