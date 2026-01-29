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
    /// <summary>
    /// Professional UI for automated hash discovery.
    /// Migrated from forensic engine concepts.
    /// </summary>
    public partial class ForensicWindow : UserControl
    {
        public HashDiscoveryService DiscoveryService { get; set; }
        private CancellationTokenSource _cts;
        private int _sessionFoundCount = 0;

        public ForensicWindow()
        {
            InitializeComponent();
            RefreshUnknownsCount();
        }

        private void RefreshUnknownsCount()
        {
            try
            {
                string exportDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hashes", "export");
                var uniqueHashes = new HashSet<string>();

                if (Directory.Exists(exportDir))
                {
                    var files = Directory.GetFiles(exportDir, "*.unknown.txt");
                    foreach (var file in files)
                    {
                        var lines = File.ReadLines(file);
                        foreach (var line in lines)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                                uniqueHashes.Add(line.Trim().ToLower());
                        }
                    }
                }

                if (uniqueHashes.Any())
                {
                    OperationsSummaryText.Text = $"DB STATUS: {uniqueHashes.Count} UNKNOWNS";
                }
                else
                {
                    // Fallback to primary unknowns file
                    string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hashes", "unknowns.txt");
                    int count = File.Exists(filePath) ? File.ReadLines(filePath).Count() : 0;
                    OperationsSummaryText.Text = $"DB STATUS: {count} UNKNOWNS";
                }
            }
            catch 
            {
                OperationsSummaryText.Text = "DB STATUS: ERROR";
            }
        }

        private async void Guess_Click(object sender, RoutedEventArgs e)
        {
            if (DiscoveryService == null) return;

            // UI Reset
            LoadingOverlay.Visibility = Visibility.Visible;
            DetailedLoadingText.Text = "INITIALIZING ENGINE...";
            DiscoveryList.Items.Clear();
            _sessionFoundCount = 0;
            SessionCountText.Text = "0";
            
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            var progress = new Progress<string>(msg => { 
                StatusText.Text = msg; 
                
                // Technical mapping for the big title
                if (msg.Contains("Sincronizando")) DetailedLoadingText.Text = "SYNCHRONIZING DATA...";
                else if (msg.Contains("Grepping Game")) DetailedLoadingText.Text = "SCANNING GAME WADS...";
                else if (msg.Contains("Grepping LCU")) DetailedLoadingText.Text = "SCANNING LCU WADS...";
                else if (msg.Contains("Guessing") || msg.Contains("Adivinación")) DetailedLoadingText.Text = "ENGINEERING HASHES...";
                else if (msg.Contains("Targeted")) DetailedLoadingText.Text = "TARGETED ATTACK...";
                else if (msg.Contains("TFT")) DetailedLoadingText.Text = "LCU PATTERN FORCE...";
                
                FooterStatusText.Text = $"ENGINE STATUS: {msg.ToUpper()}";

                // Real-time discovery insertion
                if (msg.StartsWith("[FOUND]"))
                {
                    var parts = msg.Substring(8).Split(' ', 2);
                    if (parts.Length == 2 && ulong.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out ulong h))
                    {
                        _sessionFoundCount++;
                        SessionCountText.Text = _sessionFoundCount.ToString();
                        
                        DiscoveryList.Items.Insert(0, new DiscoveredHash { 
                            Hash = h, 
                            Name = parts[1], 
                            Method = "ENGINE", 
                            SourceWad = "Detected" 
                        });
                    }
                }
            });

            try
            {
                // Execute Step 1: Preparation & Harvest
                StatusText.Text = "Initializing cluster synchronization...";
                var targets = await DiscoveryService.InitializeSessionAsync(progress, _cts.Token);
                RefreshUnknownsCount();

                if (!targets.Any())
                {
                    StatusText.Text = "No targets found in discovery buffers.";
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    return;
                }

                // Execute Steps 2-5: Core Engine
                StatusText.Text = "Launching brute-force discovery cycles...";
                var discovered = await DiscoveryService.GuessHashesAsync(targets, progress, _cts.Token);
                
                // Add final results if any were missing from real-time reporting
                foreach (var item in discovered) 
                {
                    bool exists = false;
                    foreach(DiscoveredHash existing in DiscoveryList.Items) {
                        if(existing.Hash == item.Hash) { exists = true; break; }
                    }
                    if(!exists) DiscoveryList.Items.Add(item);
                }

                RefreshUnknownsCount();
                FooterStatusText.Text = discovered.Any() 
                    ? $"ANALYSIS COMPLETE: {discovered.Count} PATHS RESOLVED" 
                    : "ANALYSIS COMPLETE: NOMINAL RESULTS";
                
                StatusIcon.Kind = discovered.Any() 
                    ? Material.Icons.MaterialIconKind.CheckCircleOutline 
                    : Material.Icons.MaterialIconKind.InformationOutline;
                StatusIcon.Foreground = discovered.Any() ? (System.Windows.Media.Brush)FindResource("AccentGreen") : (System.Windows.Media.Brush)FindResource("AccentBlue");
            }
            catch (OperationCanceledException)
            {
                FooterStatusText.Text = "ENGINE STATUS: ABORTED BY USER";
                StatusIcon.Kind = Material.Icons.MaterialIconKind.CloseCircleOutline;
            }
            catch (Exception ex)
            {
                FooterStatusText.Text = $"ENGINE ERROR: {ex.Message.ToUpper()}";
                StatusIcon.Kind = Material.Icons.MaterialIconKind.AlertCircleOutline;
            }
            finally 
            { 
                LoadingOverlay.Visibility = Visibility.Collapsed; 
            }
        }

        public void CleanupResources()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}