using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
using AssetsManager.Services.Versions;
using AssetsManager.Utils;
using AssetsManager.Views.Controls;
using AssetsManager.Views.Controls.Comparator;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models;
using CommunityToolkit.WinUI.Notifications;
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
        private readonly VersionService _versionService; // Add this
        private readonly WadExtractionService _wadExtractionService;

        private NotifyIcon _notifyIcon;
        private string _latestAppVersionAvailable;
        private readonly List<string> _notificationMessages = new List<string>();

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
            VersionService versionService, // Add this
            WadExtractionService wadExtractionService)
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
            _versionService = versionService; // Add this
            _wadExtractionService = wadExtractionService;

            _progressUIManager.Initialize(ProgressSummaryButton, ProgressIcon, this);

            _logService.SetLogOutput(LogView.richTextBoxLogs);

            _assetDownloader.DownloadStarted += _progressUIManager.OnDownloadStarted;
            _assetDownloader.DownloadProgressChanged += _progressUIManager.OnDownloadProgressChanged;
            _assetDownloader.DownloadCompleted += _progressUIManager.OnDownloadCompleted;

            _wadComparatorService.ComparisonStarted += _progressUIManager.OnComparisonStarted;
            _wadComparatorService.ComparisonProgressChanged += _progressUIManager.OnComparisonProgressChanged;
            _wadComparatorService.ComparisonCompleted += _progressUIManager.OnComparisonCompleted;
            _wadComparatorService.ComparisonCompleted += OnWadComparisonCompleted;

            _versionService.VersionDownloadStarted += (sender, e) => _progressUIManager.OnVersionDownloadStarted(sender, e);
            _versionService.VersionDownloadProgressChanged += (sender, e) => _progressUIManager.OnDownloadProgressChanged(e.CurrentValue, e.TotalValue, e.CurrentFile, true, null);
            _versionService.VersionDownloadCompleted += (sender, e) => _progressUIManager.OnDownloadCompleted();

            _updateCheckService.UpdatesFound += OnUpdatesFound;

            Sidebar.NavigationRequested += OnSidebarNavigationRequested;
            LoadHomeWindow();

            if (IsAnySettingActive())
            {
                _logService.Log("Settings configured on startup.");
            }

            _updateCheckService.Start();
            _ = _updateCheckService.CheckForAllUpdatesAsync();

            InitializeNotifyIcon();
            Closing += MainWindow_Closing;
        }

        private void InitializeNotifyIcon()
        {
            _notifyIcon = new NotifyIcon();
            var iconUri = new Uri("pack://application:,,,/AssetsManager;component/Resources/img/logo.ico", UriKind.RelativeOrAbsolute);
            _notifyIcon.Icon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(iconUri).Stream);
            _notifyIcon.Text = "AssetsManager";
            _notifyIcon.DoubleClick += (s, args) =>
            {
                Show();
                WindowState = WindowState.Normal;
                _notifyIcon.Visible = false;
            };

            var contextMenu = new ContextMenuStrip();
            var exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += ExitApplication_Click;
            contextMenu.Items.Add(exitMenuItem);

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void ExitApplication_Click(object sender, EventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void OnWadComparisonCompleted(List<ChunkDiff> allDiffs, string oldLolPath, string newLolPath)
        {
            Dispatcher.Invoke(() =>
            {
                if (allDiffs != null)
                {
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

                    var resultWindow = new WadComparisonResultWindow(serializableDiffs, _serviceProvider, _customMessageBoxService, _directoriesCreator, _assetDownloader, _logService, _wadDifferenceService, _wadPackagingService, _diffViewService, _hashResolverService, _wadExtractionService, _appSettings, oldLolPath, newLolPath);
                    resultWindow.Owner = this;
                    resultWindow.Show();
                }
            });
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
                ToastNotificationManagerCompat.CreateToastNotifier().Show(new ToastNotification(new ToastContentBuilder()
                    .AddText("AssetsManager")
                    .AddText(message)
                    .GetToastContent().GetXml()));
            }

            ShowNotification(true, message);
        }

        private bool IsAnySettingActive()
        {
            return _appSettings.SyncHashesWithCDTB ||
                   _appSettings.AutoCopyHashes ||
                   _appSettings.CreateBackUpOldHashes ||
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
            MainContentArea.Content = _serviceProvider.GetRequiredService<HomeWindow>();
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
            if (MainContentArea.Content is HomeWindow homeView)
            {
                homeView.UpdateSettings(_appSettings, e.WasResetToDefaults);
            }
            _updateCheckService.Stop();
            _updateCheckService.Start();
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_appSettings.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon.Visible = true;
                _notifyIcon.ShowBalloonTip(1000, "AssetsManager", "The application has been minimized to the system tray.", ToolTipIcon.Info);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _notifyIcon.Dispose();
            base.OnClosed(e);
        }
    }
}