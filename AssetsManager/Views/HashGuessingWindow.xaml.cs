using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
        private readonly AppSettings _appSettings;
        private readonly CustomMessageBoxService _messageBoxService;
        private readonly HashGuessLabModel _viewModel = new();
        private CancellationTokenSource _cancellationTokenSource;

        public HashGuessingWindow(HashGuessingService hashGuessingService, AppSettings appSettings, CustomMessageBoxService messageBoxService)
        {
            InitializeComponent();
            _hashGuessingService = hashGuessingService;
            _appSettings = appSettings;
            _messageBoxService = messageBoxService;
            DataContext = _viewModel;
            UpdateUnknownCountAsync();
        }

        private async void UpdateUnknownCountAsync()
        {
            if (DomainSelector == null || TxtUnknownCount == null) return;
            try
            {
                var domain = DomainSelector.SelectedIndex == 0 ? HashGuessDomain.Game : HashGuessDomain.Lcu;
                var unknown = await _hashGuessingService.GetStoreUnknownsAsync(domain, CancellationToken.None);
                TxtUnknownCount.Text = $"{unknown.Count:N0} hashes";
            }
            catch
            {
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

        private async System.Threading.Tasks.Task RunAsync(HashGuessMode mode)
        {
            var domain = DomainSelector.SelectedIndex == 0 ? HashGuessDomain.Game : HashGuessDomain.Lcu;
            if (mode == HashGuessMode.GrepGame) domain = HashGuessDomain.Game;
            else if (mode == HashGuessMode.GrepLcu) domain = HashGuessDomain.Lcu;

            string rootPath = _appSettings.LolPbeDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(rootPath) || !System.IO.Directory.Exists(rootPath))
            {
                _messageBoxService.ShowError("Hash Guessing Lab", "Please configure the LoL PBE Install Directory in Settings first.", Window.GetWindow(this));
                return;
            }
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();
            _viewModel.IsRunning = true;
            _viewModel.ProgressValue = 0;
            _viewModel.StatusText = (mode == HashGuessMode.GrepGame || mode == HashGuessMode.GrepLcu) ? "Building unknown hash inventory..." : "Building structural candidates...";
            _viewModel.Matches.Clear();

            try
            {
                var progress = new Progress<HashGuessProgress>(value =>
                {
                    _viewModel.ProgressValue = value.TotalWads == 0 ? 0 : value.ProcessedWads * 100d / value.TotalWads;
                    _viewModel.StatusText = $"Scanning {value.CurrentWad} · {value.ProcessedChunks:N0} chunks · {value.FoundMatches:N0} matches";
                });
                var result = mode switch
                {
                    HashGuessMode.RunCanonical => await _hashGuessingService.RunCanonicalGuessingAsync(domain, rootPath, progress, _cancellationTokenSource.Token),
                    HashGuessMode.RunLocales => await _hashGuessingService.RunLanguageGuessingAsync(domain, rootPath, progress, _cancellationTokenSource.Token),
                    HashGuessMode.RunNumbers => await _hashGuessingService.RunNumberGuessingAsync(domain, rootPath, progress, _cancellationTokenSource.Token),
                    HashGuessMode.GameBasic => await _hashGuessingService.RunGameBasicGuessingAsync(rootPath, progress, _cancellationTokenSource.Token),
                    HashGuessMode.GameExtended => await _hashGuessingService.RunGameExtendedGuessingAsync(rootPath, progress, _cancellationTokenSource.Token),
                    HashGuessMode.LcuBasic => await _hashGuessingService.RunLcuBasicGuessingAsync(rootPath, progress, _cancellationTokenSource.Token),
                    HashGuessMode.LcuAdvanced => await _hashGuessingService.RunLcuAdvancedGuessingAsync(rootPath, progress, _cancellationTokenSource.Token),
                    HashGuessMode.GrepGame => await _hashGuessingService.RunEmbeddedPathGrepAsync(HashGuessDomain.Game, rootPath, progress, _cancellationTokenSource.Token),
                    HashGuessMode.GrepLcu => await _hashGuessingService.RunEmbeddedPathGrepAsync(HashGuessDomain.Lcu, rootPath, progress, _cancellationTokenSource.Token),
                    _ => throw new ArgumentOutOfRangeException(nameof(mode))
                };
                foreach (var match in result.Matches) _viewModel.Matches.Add(match);
                _viewModel.ProgressValue = 100;
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
                _viewModel.StatusText = "Hash guessing failed. Check application_errors.log.";
                _messageBoxService.ShowError("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            finally
            {
                _viewModel.IsRunning = false;
                UpdateUnknownCountAsync();
            }
        }

        private enum HashGuessMode { GrepGame, GrepLcu, RunCanonical, RunLocales, RunNumbers, GameBasic, GameExtended, LcuBasic, LcuAdvanced }
    }
}
