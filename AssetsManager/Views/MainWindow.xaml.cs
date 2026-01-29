using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Notifications;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Updater;
using AssetsManager.Services.Backup;
using AssetsManager.Views.Controls;
using AssetsManager.Views.Dialogs.Controls;
using AssetsManager.Views.Controls.Comparator;
using AssetsManager.Views.Dialogs;

namespace AssetsManager.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
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
        private readonly NotificationService _notificationService;
        private readonly ComparisonHistoryService _comparisonHistoryService;
        private readonly HashDiscoveryService _hashDiscoveryService;

        private string _latestAppVersionAvailable;
        private NotificationHubWindow _notificationHubWindow;
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
            TaskCancellationManager taskCancellationManager, 
            NotificationService notificationService, 
            ComparisonHistoryService comparisonHistoryService, 
            HashDiscoveryService hashDiscoveryService)
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
            _notificationService = notificationService;
            _comparisonHistoryService = comparisonHistoryService;
            _hashDiscoveryService = hashDiscoveryService;

            InitializeMainWindow();
        }

        private void InitializeMainWindow()
        {
            // Setup Progress UI
            _progressUIManager.Initialize(StatusBar.ViewModel, this);
            StatusBar.ProgressSummaryClicked += (s, e) => _progressUIManager.ShowDetails();

            // Setup Logging
            _logService.SetLogOutput(LogView.LogRichTextBox);
            LogView.ToggleLogSizeRequested += OnToggleLogSizeRequested;
            LogView.ClearStatusBarRequested += (s, e) => ClearStatusBar();
            LogView.LogExpandedManually += (s, e) => _isLogMinimized = false;
            LogView.NotificationClicked += OnNotificationHubRequested;

            // Subscribe to Services
            _wadComparatorService.ComparisonStarted += _progressUIManager.OnComparisonStarted;
            _wadComparatorService.ComparisonProgressChanged += _progressUIManager.OnComparisonProgressChanged;
            _wadComparatorService.ComparisonCompleted += _progressUIManager.OnComparisonCompleted;
            _wadComparatorService.ComparisonCompleted += OnWadComparisonCompleted;

            _extractionService.ExtractionStarted += _progressUIManager.OnExtractionStarted;
            _extractionService.ExtractionProgressChanged += (sender, progress) => _progressUIManager.OnExtractionProgressChanged(progress.extractedCount, progress.totalFiles, progress.message);
            _extractionService.ExtractionCompleted += (sender, e) => OnExtractionCompleted(sender, e);

            _backupManager.BackupStarted += _progressUIManager.OnBackupStarted;
            _backupManager.BackupProgressChanged += _progressUIManager.OnBackupProgressChanged;
            _backupManager.BackupCompleted += _progressUIManager.OnBackupCompleted;

            _versionService.VersionDownloadStarted += (sender, e) => _progressUIManager.OnVersionDownloadStarted(sender, e);
            _versionService.VersionDownloadProgressChanged += (sender, e) => _progressUIManager.OnDownloadProgressChanged(e.CurrentValue, e.TotalValue, e.CurrentFile, true, null);
            _versionService.VersionDownloadCompleted += (sender, e) => _progressUIManager.OnDownloadCompleted();

            _updateCheckService.UpdatesFound += OnUpdatesFound;

            // Navigation
            Sidebar.NavigationRequested += OnSidebarNavigationRequested;
            LoadHomeWindow();

            // Initialization
            _updateCheckService.Start();
            InitializeApplicationAsync();

            // Window Events
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

            SingleInstance.SetCurrentProcessExplicitAppUserModelID(SingleInstance.AppId);
        }

        #region Tray and Window State

        private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) => ShowAppFromTray();
        private void MenuItem_Show_Click(object sender, RoutedEventArgs e) => ShowAppFromTray();
        private void MenuItem_Exit_Click(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();

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
                TrayIcon.ShowBalloonTip("AssetsManager", "The application has been minimized to the tray.", BalloonIcon.Info);
            }
        }

        #endregion

        #region Event Handlers

        private void OnUpdatesFound(string message, string latestVersion)
        {
            if (!string.IsNullOrEmpty(latestVersion))
                _latestAppVersionAvailable = latestVersion;

            if (Visibility != Visibility.Visible)
                TrayIcon.ShowBalloonTip("AssetsManager", message, BalloonIcon.Info);

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
                    _isExtractingAfterComparison = false;
                }
            });
        }

        private async void StartExtractionAsync()
        {
            var ct = _taskCancellationManager.PrepareNewOperation();
            await _extractionService.ExtractNewFilesFromComparisonAsync(_diffsForExtraction, _extractionNewLolPath, ct);
        }

        private async void OnWadComparisonCompleted(List<ChunkDiff> allDiffs, string oldLolPath, string newLolPath)
        {
            if (allDiffs == null) return;

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

            if (!serializableDiffs.Any()) return;

            if (_appSettings.ReportGeneration.Enabled)
            {
                await _reportGenerationService.GenerateReportAsync(serializableDiffs, oldLolPath, newLolPath);
            }
            else if (_appSettings.EnableExtraction)
            {
                _isExtractingAfterComparison = true;
                _diffsForExtraction = serializableDiffs;
                _extractionOldLolPath = oldLolPath;
                _extractionNewLolPath = newLolPath;
                Dispatcher.Invoke(StartExtractionAsync);
            }
            else
            {
                if (_appSettings.SaveWadComparisonHistory)
                {
                    string displayName = "Unknown";
                    var uniqueWads = serializableDiffs.Select(d => d.SourceWadFile).Distinct().ToList();
                    if (uniqueWads.Count == 1)
                        displayName = System.IO.Path.GetFileName(uniqueWads[0]).Split('.')[0];
                    else
                        displayName = System.IO.Path.GetFileName(newLolPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

                    if (string.IsNullOrEmpty(displayName)) displayName = "Root";

                    _ = _comparisonHistoryService.SaveComparisonAsync(serializableDiffs, oldLolPath, newLolPath, $"Comparison from {displayName}");
                }

                Dispatcher.Invoke(() => ShowComparisonResultWindow(serializableDiffs, oldLolPath, newLolPath));
            }
        }

        #endregion

        #region UI Helpers

        private void ShowComparisonResultWindow(List<SerializableChunkDiff> diffs, string oldPath, string newPath)
        {
            var resultWindow = new WadComparisonResultWindow(
                diffs, _serviceProvider, _customMessageBoxService, _directoriesCreator, 
                _assetDownloader, _logService, _wadDifferenceService, _wadPackagingService, 
                _diffViewService, _hashResolverService, _appSettings, oldPath, newPath);
            
            resultWindow.Owner = this;
            resultWindow.Show();
        }

        public void ShowNotification(bool show, string msg = "Updates detected.")
        {
            if (show)
                _notificationService.AddNotification("System Notification", msg, NotificationType.Info);
        }

        public void ClearStatusBar()
        {
            _progressUIManager.ClearStatusText();
            StatusBar.ClearStatusBar();
        }

        private void OnToggleLogSizeRequested(object sender, EventArgs e)
        {
            if (_isLogMinimized || LogRowDefinition.ActualHeight <= 45)
            {
                if (!_lastLogHeight.IsAbsolute || _lastLogHeight.Value <= 45)
                    _lastLogHeight = new GridLength(185);

                LogRowDefinition.Height = _lastLogHeight;
                _isLogMinimized = false;
            }
            else
            {
                _lastLogHeight = LogRowDefinition.Height;
                LogRowDefinition.Height = GridLength.Auto;
                _isLogMinimized = true;
            }
        }

        private async void OnNotificationHubRequested(object sender, EventArgs e)
        {
            if (_notificationHubWindow == null)
            {
                _notificationHubWindow = _serviceProvider.GetRequiredService<NotificationHubWindow>();
                _notificationHubWindow.Owner = this;
            }

            _notificationHubWindow.ShowHub(this);

            if (!string.IsNullOrEmpty(_latestAppVersionAvailable))
            {
                await _updateManager.CheckForUpdatesAsync(this, true);
                _latestAppVersionAvailable = null;
            }
        }

        #endregion

        #region Navigation

        private void OnSidebarNavigationRequested(string viewTag)
        {
            // Resource cleanup for heavy views
            if (viewTag != "Settings" && viewTag != "Help")
            {
                if (MainContentArea.Content is ExplorerWindow ew)
                    ew.CleanupResources();
                else if (MainContentArea.Content is ModelWindow mw)
                    mw.CleanupResources();
                else if (MainContentArea.Content is ForensicWindow fw)
                    fw.CleanupResources();
            }

            switch (viewTag)
            {
                case "Home": LoadHomeWindow(); break;
                case "Explorer": LoadExplorerWindow(); break;
                case "Comparator": LoadComparatorWindow(); break;
                case "Models": LoadModelWindow(); break;
                case "Forensic": LoadForensicWindow(); break;
                case "Monitor": LoadMonitorWindow(); break;
                case "Settings": btnSettings_Click(null, null); break;
                case "Help": btnHelp_Click(null, null); break;
            }
        }

        private void LoadForensicWindow()
        {
            var forensicView = _serviceProvider.GetRequiredService<ForensicWindow>();
            forensicView.DiscoveryService = _hashDiscoveryService;
            MainContentArea.Content = forensicView;
        }

        private void LoadHomeWindow()
        {
            var homeView = _serviceProvider.GetRequiredService<HomeWindow>();
            homeView.NavigationRequested += (tag) =>
            {
                Sidebar.SelectNavigationItem(tag);
                OnSidebarNavigationRequested(tag);
            };
            MainContentArea.Content = homeView;
        }

        private void LoadExplorerWindow() => MainContentArea.Content = _serviceProvider.GetRequiredService<ExplorerWindow>();
        private void LoadComparatorWindow() => MainContentArea.Content = _serviceProvider.GetRequiredService<ComparatorWindow>();
        private void LoadModelWindow() => MainContentArea.Content = _serviceProvider.GetRequiredService<ModelWindow>();
        private void LoadMonitorWindow() => MainContentArea.Content = _serviceProvider.GetRequiredService<MonitorWindow>();

        #endregion

        #region Other Windows

        private void btnHelp_Click(object sender, RoutedEventArgs e) => _serviceProvider.GetRequiredService<HelpWindow>().ShowDialog();

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

        #endregion
    }
}
