using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Views
{
    public partial class ForensicWindow : UserControl
    {
        public HashForensicService ForensicService { get; set; }
        public HashDiscoveryService DiscoveryService { get; set; }
        private CancellationTokenSource _cts;
        private List<DiscoveredHash> _harvestedUnknowns = new List<DiscoveredHash>();

        public ForensicWindow()
        {
            InitializeComponent();
            RefreshUnknownsCount();
        }

        private void RefreshUnknownsCount()
        {
            try
            {
                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hashes", "unknowns.txt");
                if (File.Exists(filePath))
                {
                    int count = File.ReadLines(filePath).Count();
                    OperationsSummaryText.Text = $"| {count} UNKNOWNS STORED";
                    GuessButton.IsEnabled = count > 0;
                }
                else
                {
                    OperationsSummaryText.Text = "| 0 UNKNOWNS STORED";
                    GuessButton.IsEnabled = false;
                }
            }
            catch 
            {
                OperationsSummaryText.Text = "| ERROR READING UNKNOWNS";
            }
        }

        private async void Harvest_Click(object sender, RoutedEventArgs e)
        {
            if (DiscoveryService == null) return;

            LoadingOverlay.Visibility = Visibility.Visible;
            DetailedLoadingText.Text = "HARVESTING UNKNOWN CHUNKS...";
            _harvestedUnknowns.Clear();
            DiscoveryList.Items.Clear();
            
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => { StatusText.Text = msg; DetailedLoadingText.Text = msg.ToUpper(); });

            try
            {
                _harvestedUnknowns = await DiscoveryService.GetUnknownHashesAsync(progress, _cts.Token);
                RefreshUnknownsCount();
                
                StatusText.Text = $"Harvest complete. Found {_harvestedUnknowns.Count} unknown assets.";
                StatusIcon.Kind = Material.Icons.MaterialIconKind.CheckCircleOutline;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Harvest error: {ex.Message}";
            }
            finally { LoadingOverlay.Visibility = Visibility.Collapsed; }
        }

        private async void Guess_Click(object sender, RoutedEventArgs e)
        {
            if (DiscoveryService == null) return;

            // Always verify disk state first
            _harvestedUnknowns = await DiscoveryService.LoadUnknownsFromDiskAsync();

            if (!_harvestedUnknowns.Any())
            {
                StatusText.Text = "No unknowns found on disk to process.";
                RefreshUnknownsCount();
                return;
            }

            LoadingOverlay.Visibility = Visibility.Visible;
            DetailedLoadingText.Text = "INITIALIZING GUESS ENGINE...";
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => { StatusText.Text = msg; DetailedLoadingText.Text = msg.ToUpper(); });

            try
            {
                var discovered = await DiscoveryService.GuessHashesAsync(_harvestedUnknowns, progress, _cts.Token);
                
                foreach (var item in discovered) DiscoveryList.Items.Add(item);

                RefreshUnknownsCount(); // Auto-update after cleaning solved ones
                StatusText.Text = $"Analysis complete. Discovered {discovered.Count} new paths!";
                StatusIcon.Kind = Material.Icons.MaterialIconKind.AutoFix;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Guessing error: {ex.Message}";
            }
            finally { LoadingOverlay.Visibility = Visibility.Collapsed; }
        }

        private async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            if (ForensicService == null || string.IsNullOrWhiteSpace(HashInput.Text)) return;

            string input = HashInput.Text.Trim();
            
            // UI State
            ResultsPanel.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Visible;
            StatusText.Text = "Scanning cluster...";
            
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                var result = await ForensicService.ScanHashAsync(input, _cts.Token);

                LoadingOverlay.Visibility = Visibility.Collapsed;
                
                if (result != null)
                {
                    ResultsPanel.Visibility = Visibility.Visible;
                    NameText.Text = result.ResolvedName ?? "Unknown";
                    ContainerText.Text = result.SourceContainer ?? "External/Dictionary only";
                    ExtensionText.Text = result.Extension?.ToUpper() ?? "Unknown";
                    CompressionText.Text = result.Compression ?? "N/A";
                    SizeText.Text = result.FileSize > 0 ? $"{(result.FileSize / 1024.0):F2} KB" : "N/A";
                    AlgorithmText.Text = input.Length > 8 ? "XXHash64 (WAD Path)" : "FNV-1a (BIN Data)";
                    
                    StatusText.Text = result.StatusMessage;
                    StatusIcon.Kind = result.IsFound ? Material.Icons.MaterialIconKind.CheckDecagram : Material.Icons.MaterialIconKind.AlertDecagram;
                    
                    if (result.IsFound)
                    {
                        StatusIcon.Foreground = Application.Current.Resources["AccentGreen"] as System.Windows.Media.Brush;
                    }
                    else
                    {
                        StatusIcon.Foreground = Application.Current.Resources["AccentRed"] as System.Windows.Media.Brush;
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Diagnostic failed: {ex.Message}";
                EmptyState.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void HashInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Analyze_Click(null, null);
        }

        public void CleanupResources()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}