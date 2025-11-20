using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Updater;
using AssetsManager.Services.Versions;
using AssetsManager.Utils;
using AssetsManager.Views.Controls;
using AssetsManager.Views.Controls.Comparator;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models.Wad;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

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

        private NotifyIcon _notifyIcon;
        private string _latestAppVersionAvailable;
        private readonly List<string> _notificationMessages = new List<string>();

        // New fields to manage the state of the extraction after comparison
        private bool _isExtractingAfterComparison = false;
        private string _extractionOldLolPath;
        private string _extractionNewLolPath;
        private List<SerializableChunkDiff> _diffsForExtraction;
        private CancellationTokenSource _extractionCts;

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
            ExtractionService extractionService)
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

            _progressUIManager.Initialize(ProgressSummaryButton, ProgressIcon, this);

            _logService.SetLogOutput(LogView.richTextBoxLogs);

            _assetDownloader.DownloadStarted += _progressUIManager.OnDownloadStarted;
            _assetDownloader.DownloadProgressChanged += _progressUIManager.OnDownloadProgressChanged;
            _assetDownloader.DownloadCompleted += _progressUIManager.OnDownloadCompleted;

            _wadComparatorService.ComparisonStarted += _progressUIManager.OnComparisonStarted;
            _wadComparatorService.ComparisonProgressChanged += _progressUIManager.OnComparisonProgressChanged;
            _wadComparatorService.ComparisonCompleted += _progressUIManager.OnComparisonCompleted;
            _wadComparatorService.ComparisonCompleted += OnWadComparisonCompleted;
            
            _extractionService.ExtractionStarted += OnExtractionStarted;
            _extractionService.ExtractionProgressChanged += OnExtractionProgressChanged;
            _extractionService.ExtractionCompleted += OnExtractionCompleted;

            _versionService.VersionDownloadStarted += (sender, e) => _progressUIManager.OnVersionDownloadStarted(sender, e);
            _versionService.VersionDownloadProgressChanged += (sender, e) => _progressUIManager.OnDownloadProgressChanged(e.CurrentValue, e.TotalValue, e.CurrentFile, true, null);
            _versionService.VersionDownloadCompleted += (sender, e) => _progressUIManager.OnDownloadCompleted();

            _updateCheckService.UpdatesFound += OnUpdatesFound;

            Sidebar.NavigationRequested += OnSidebarNavigationRequested;
            LoadDownloaderWindow();

            if (IsAnySettingActive())
            {
                _logService.Log("Settings configured on startup.");
            }

            _updateCheckService.Start();
            InitializeApplicationAsync();

            InitializeNotifyIcon();
            StateChanged += MainWindow_StateChanged;
            Closing += MainWindow_Closing;
        }

        private async void InitializeApplicationAsync()
        {
            await _updateCheckService.CheckForAllUpdatesAsync();
            await _hashResolverService.StartupTask;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            SingleInstance.RegisterWindow(this, () =>
            {
                Show();
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Activate();
                _notifyIcon.Visible = false;
            });
        }

        private void InitializeNotifyIcon()
        {
            _notifyIcon = new NotifyIcon();
            var iconUri = new Uri("pack://application:,,,/AssetsManager;component/Resources/Img/logo.ico", UriKind.RelativeOrAbsolute);
            _notifyIcon.Icon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(iconUri).Stream);
            _notifyIcon.Text = "AssetsManager";
            _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

            var contextMenu = new ContextMenuStrip();
            var exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += ExitMenuItem_Click;
            contextMenu.Items.Add(exitMenuItem);

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void NotifyIcon_DoubleClick(object sender, EventArgs e)
        {
            Show();
            WindowState = WindowState.Normal;
            _notifyIcon.Visible = false;
        }

        private void ExitMenuItem_Click(object sender, EventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _appSettings.MinimizeToTrayOnClose)
            {
                Hide();
                _notifyIcon.Visible = true;

                // Use Dispatcher to avoid COMException when showing toast on state change
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    new ToastContentBuilder()
                        .AddText("AssetsManager")
                        .AddText("ℹ️ The application has been minimized to the tray.")
                        .Show();
                }));
            }
        }

        private void OnUpdatesFound(string message, string latestVersion)
        {
            if (!string.IsNullOrEmpty(latestVersion))
            {
                _latestAppVersionAvailable = latestVersion;
            }

            // Use the compat manager for robust notification support in WPF
            if (Visibility != Visibility.Visible)
            {
                new ToastContentBuilder()
                    .AddText("AssetsManager")
                    .AddText(message)
                    .Show();
            }

            ShowNotification(true, message);
        }
        
        private void OnExtractionStarted(object sender, string message)
        {
            Dispatcher.Invoke(() =>
            {
                _progressUIManager.OnExtractionStarted(sender, message);
            });
        }

        private void OnExtractionProgressChanged(object sender, (double percentage, string message) progress)
        {
            Dispatcher.Invoke(() =>
            {
                _progressUIManager.OnExtractionProgressChanged(progress.percentage, progress.message);
            });
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
            _extractionCts = new CancellationTokenSource();
            await _extractionService.ExtractNewFilesFromComparisonAsync(_diffsForExtraction, _extractionNewLolPath, _extractionCts.Token);
        }
        
        private void OnWadComparisonCompleted(List<ChunkDiff> allDiffs, string oldLolPath, string newLolPath)
        {
            var serializableDiffs = allDiffs?.Select(d => new SerializableChunkDiff
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

            if (serializableDiffs == null || !serializableDiffs.Any())
            {
                _logService.Log("Comparison completed with no differences found.");
                return;
            }

            if (_appSettings.ExtractNewFilesAfterComparison)
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

        private bool IsAnySettingActive()
        {
            return _appSettings.SyncHashesWithCDTB ||
                   _appSettings.AutoCopyHashes ||
                   _appSettings.CreateBackUpOldHashes ||
                   _appSettings.ExtractNewFilesAfterComparison ||
                   _appSettings.OnlyCheckDifferences ||
                   _appSettings.CheckJsonDataUpdates ||
                   _appSettings.SaveDiffHistory ||
                   _appSettings.BackgroundUpdates;
        }

        public void ShowNotification(bool show, string message = "Updates have been detected. Click to dismiss.")
        {
            Dispatcher.Invoke(() =>
            {
                if (show)
                {
                    if (!_notificationMessages.Contains(message))
                    {
                        _notificationMessages.Add(message);
                    }
                }
                else
                {
                    _notificationMessages.Clear();
                }

                UpdateNotificationIcon.Visibility = _notificationMessages.Any() ? Visibility.Visible : Visibility.Collapsed;
                NotificationTextBlock.Text = string.Join(Environment.NewLine, _notificationMessages);
            });
        }

        private async void UpdateNotificationIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ShowNotification(false);
            if (!string.IsNullOrEmpty(_latestAppVersionAvailable))
            {
                await _updateManager.CheckForUpdatesAsync(this, true);
                _latestAppVersionAvailable = null;
            }
            e.Handled = true;
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
                case "Downloader": LoadDownloaderWindow(); break;
                case "Explorer": LoadExplorerWindow(); break;
                case "Comparator": LoadComparatorWindow(); break;
                case "Models": LoadModelWindow(); break;
                case "Monitor": LoadMonitorWindow(); break;
                case "Settings": btnSettings_Click(null, null); break;
                case "Help": btnHelp_Click(null, null); break;
            }
        }

        private void LoadDownloaderWindow()
        {
            MainContentArea.Content = _serviceProvider.GetRequiredService<DownloaderWindow>();
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
            if (MainContentArea.Content is DownloaderWindow downloaderView)
            {
                downloaderView.UpdateSettings(_appSettings, e.WasResetToDefaults);
            }
            _updateCheckService.Stop();
            _updateCheckService.Start();
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            _notifyIcon.DoubleClick -= NotifyIcon_DoubleClick;
            if (_notifyIcon.ContextMenuStrip != null && _notifyIcon.ContextMenuStrip.Items.Count > 0)
            {
                _notifyIcon.ContextMenuStrip.Items[0].Click -= ExitMenuItem_Click;
            }
            StateChanged -= MainWindow_StateChanged;
            _notifyIcon?.Dispose();
        }
    }
}
