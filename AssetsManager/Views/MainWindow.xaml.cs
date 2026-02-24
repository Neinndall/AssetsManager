using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
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
using AssetsManager.Views.Controls;
using AssetsManager.Views.Dialogs.Controls;
using AssetsManager.Views.Controls.Comparator;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Viewer;
using MahApps.Metro.Controls;

namespace AssetsManager.Views
{
    public partial class MainWindow : MetroWindow
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly LogService _logService;
        private readonly AppSettings _appSettings;
        private readonly UpdateManager _updateManager;
        private readonly WadComparatorService _wadComparatorService;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly DirectoriesCreator _directoriesCreator;
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
        private readonly WadPackagingService _wadPackagingService;

        private string _latestAppVersionAvailable;
        private NotificationHubWindow _notificationHubWindow;
        private bool _isExtractingAfterComparison = false;
        private string _extractionOldLolPath;
        private string _extractionNewLolPath;
        private List<SerializableChunkDiff> _diffsForExtraction;
        private string _lastAssignedFolder;
        private string _lastComparisonIdentity;
        private GridLength _lastLogHeight;
        private bool _isLogMinimized = false;

        public MainWindow(
            IServiceProvider serviceProvider,
            LogService logService,
            AppSettings appSettings,
            UpdateManager updateManager,
            WadComparatorService wadComparatorService,
            DirectoriesCreator directoriesCreator,
            CustomMessageBoxService customMessageBoxService,
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
            WadPackagingService wadPackagingService)
        {
            InitializeComponent();

            _serviceProvider = serviceProvider;
            _logService = logService;
            _appSettings = appSettings;
            _updateManager = updateManager;
            _wadComparatorService = wadComparatorService;
            _customMessageBoxService = customMessageBoxService;
            _directoriesCreator = directoriesCreator;
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
            _wadPackagingService = wadPackagingService;

            _progressUIManager.Initialize(StatusBar.ViewModel, this);
            StatusBar.ProgressSummaryClicked += (s, e) => _progressUIManager.ShowDetails();
            _logService.SetLogOutput(LogView.LogRichTextBox);
            LogView.ToggleLogSizeRequested += OnToggleLogSizeRequested;
            LogView.ClearStatusBarRequested += (s, e) => ClearStatusBar();
            LogView.LogExpandedManually += (s, e) => _isLogMinimized = false;
            LogView.NotificationClicked += OnNotificationHubRequested;

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
            _versionService.VersionDownloadProgressChanged += (sender, e) => _progressUIManager.OnVersionDownloadProgressChanged(sender, e);
            _versionService.VersionDownloadCompleted += (sender, e) => _progressUIManager.OnVersionDownloadCompleted(sender, e);

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
        }

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
            if (this.WindowState == WindowState.Maximized)
            {
                MainWindowBorder.CornerRadius = new CornerRadius(0);
                MainWindowBorder.BorderThickness = new Thickness(0);
                // When maximized, a 1px border can sometimes appear. 
                // We ensure it's flush with the screen.
                this.Padding = new Thickness(8); 
            }
            else
            {
                MainWindowBorder.CornerRadius = new CornerRadius(11);
                MainWindowBorder.BorderThickness = new Thickness(1);
                this.Padding = new Thickness(0);
            }

            if (WindowState == WindowState.Minimized && _appSettings.MinimizeToTrayOnClose)
            {
                TrayIcon.Visibility = Visibility.Visible;
                Hide();
                TrayIcon.ShowBalloonTip("AssetsManager", "The application has been minimized to the tray.", BalloonIcon.Info);
            }
        }

        private void OnUpdatesFound(string message, string latestVersion)
        {
            if (!string.IsNullOrEmpty(latestVersion))
            {
                _latestAppVersionAvailable = latestVersion;
            }
            if (Visibility != Visibility.Visible)
            {
                TrayIcon.ShowBalloonTip("AssetsManager", message, BalloonIcon.Info);
            }
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
            var cancellationToken = _taskCancellationManager.PrepareNewOperation();
            await _extractionService.ExtractNewFilesFromComparisonAsync(_diffsForExtraction, _extractionNewLolPath, cancellationToken);
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

            string currentIdentity = CalculateComparisonIdentity(serializableDiffs, oldLolPath, newLolPath);
            bool isSameComparison = currentIdentity == _lastComparisonIdentity;

            if (!isSameComparison)
            {
                _lastAssignedFolder = null;
                _lastComparisonIdentity = currentIdentity;
            }

            if (_appSettings.SaveWadComparisonHistory && string.IsNullOrEmpty(_lastAssignedFolder))
            {
                string displayName = "Unknown";
                var uniqueWads = serializableDiffs.Select(d => d.SourceWadFile).Distinct().ToList();
                if (uniqueWads.Count == 1) displayName = Path.GetFileName(uniqueWads[0]).Split('.')[0];
                else
                {
                    displayName = Path.GetFileName(newLolPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrEmpty(displayName)) displayName = "Root";
                }

                try
                {
                    var folderInfo = _directoriesCreator.GetNewWadComparisonFolderInfo();
                    _lastAssignedFolder = folderInfo.FolderName;
                    _ = Task.Run(async () => 
                    {
                        await _wadPackagingService.SaveBackupAsync(serializableDiffs, oldLolPath, newLolPath, folderInfo.FullPath);
                        _comparisonHistoryService.RegisterComparisonInHistory(_lastAssignedFolder, $"Comparison from {displayName}", oldLolPath, newLolPath);
                    });
                }
                catch (Exception ex) { _logService.LogError(ex, "Failed to auto-save comparison history."); }
            }

            if (_appSettings.ReportGeneration.Enabled) await _reportGenerationService.GenerateReportAsync(serializableDiffs, oldLolPath, newLolPath);
            else if (_appSettings.EnableExtraction)
            {
                _isExtractingAfterComparison = true;
                _diffsForExtraction = serializableDiffs;
                _extractionOldLolPath = oldLolPath;
                _extractionNewLolPath = newLolPath;
                Dispatcher.Invoke(StartExtractionAsync);
            }
            else Dispatcher.Invoke(() => ShowComparisonResultWindow(serializableDiffs, oldLolPath, newLolPath));
        }

        private string CalculateComparisonIdentity(List<SerializableChunkDiff> diffs, string oldPath, string newPath)
        {
            var sb = new StringBuilder();
            sb.Append(oldPath).Append(newPath).Append(diffs.Count);
            if (diffs.Count > 0)
            {
                var first = diffs[0];
                var last = diffs[^1];
                sb.Append(first.NewPathHash).Append(first.OldPathHash).Append(last.NewPathHash).Append(last.OldPathHash);
            }
            return sb.ToString();
        }

        private void ShowComparisonResultWindow(List<SerializableChunkDiff> diffs, string oldPath, string newPath)
        {
            var resultWindow = _serviceProvider.GetRequiredService<WadComparisonResultWindow>();
            resultWindow.Initialize(diffs, oldPath, newPath, null, _lastAssignedFolder);
            resultWindow.Owner = this;
            resultWindow.Show();
        }

        public void ShowNotification(bool show, string message = "Updates have been detected.")
        {
            if (show) _notificationService.AddNotification("System Notification", message, NotificationType.Info);
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
                if (!_lastLogHeight.IsAbsolute || _lastLogHeight.Value <= 45) _lastLogHeight = new GridLength(180);
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

        private void OnSidebarNavigationRequested(string viewTag)
        {
            if (viewTag != "Settings" && viewTag != "Help")
            {
                if (MainContentArea.Content is ExplorerWindow explorerWindow) explorerWindow.CleanupResources();
                else if (MainContentArea.Content is ViewerWindow viewerWindow) viewerWindow.CleanupResources();
            }
            switch (viewTag)
            {
                case "Home": LoadHomeWindow(); break;
                case "Explorer": LoadExplorerWindow(); break;
                case "Comparator": LoadComparatorWindow(); break;
                case "Viewer": LoadViewerWindow(); break;
                case "Monitor": LoadMonitorWindow(); break;
                case "Settings": btnSettings_Click(null, null); break;
                case "Help": btnHelp_Click(null, null); break;
            }
        }

        private void LoadHomeWindow()
        {
            var homeWindow = _serviceProvider.GetRequiredService<HomeWindow>();
            homeWindow.NavigationRequested += (tag) => { Sidebar.SelectNavigationItem(tag); OnSidebarNavigationRequested(tag); };
            MainContentArea.Content = homeWindow;
        }

        private void LoadExplorerWindow() => MainContentArea.Content = _serviceProvider.GetRequiredService<ExplorerWindow>();
        private void LoadComparatorWindow() => MainContentArea.Content = _serviceProvider.GetRequiredService<ComparatorWindow>();
        private void LoadViewerWindow() => MainContentArea.Content = _serviceProvider.GetRequiredService<ViewerWindow>();
        private void LoadMonitorWindow() => MainContentArea.Content = _serviceProvider.GetRequiredService<MonitorWindow>();

        private void btnHelp_Click(object sender, RoutedEventArgs e) => _serviceProvider.GetRequiredService<HelpWindow>().ShowDialog();

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
            settingsWindow.Owner = this;
            settingsWindow.SettingsChanged += OnSettingsChanged;
            settingsWindow.ShowDialog();
        }

        private void OnSettingsChanged(object sender, SettingsChangedEventArgs e) { _updateCheckService.Stop(); _updateCheckService.Start(); }
        private void MainWindow_Closing(object sender, CancelEventArgs e) { StateChanged -= MainWindow_StateChanged; TrayIcon?.Dispose(); }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                SystemCommands.RestoreWindow(this);
            }
            else
            {
                SystemCommands.MaximizeWindow(this);
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }
    }
}