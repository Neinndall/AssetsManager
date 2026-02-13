using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AssetsManager.Services.Audio;
using AssetsManager.Views.Models.Dialogs;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System.Net.Http;
using System.Linq;
using VideoLibrary;
using AssetsManager.Services.Core;

namespace AssetsManager.Views.Dialogs
{
    public partial class AudioPlayerWindow : Window
    {
        private readonly AudioPlayerService _audioPlayerService;
        private readonly IServiceProvider _serviceProvider;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly AudioPlayerModel _viewModel;
        private readonly MediaPlayer _mediaPlayer = new MediaPlayer();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private bool _isDragging = false;

        public AudioPlayerWindow(
            AudioPlayerService audioPlayerService, 
            IServiceProvider serviceProvider,
            CustomMessageBoxService customMessageBoxService)
        {
            InitializeComponent();
            _audioPlayerService = audioPlayerService;
            _serviceProvider = serviceProvider;
            _customMessageBoxService = customMessageBoxService;
            
            _viewModel = new AudioPlayerModel(_audioPlayerService);
            this.DataContext = _viewModel;

            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += Timer_Tick;

            _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            _mediaPlayer.Volume = 0.5;
        }

        private async void AddUrl_Click(object sender, RoutedEventArgs e)
        {
            var inputDialog = _serviceProvider.GetRequiredService<InputDialog>();
            inputDialog.Owner = this;
            inputDialog.Initialize("Add Audio URL", "Enter a direct audio link or a YouTube URL:", "");

            if (inputDialog.ShowDialog() == true)
            {
                string url = inputDialog.InputText?.Trim();
                if (string.IsNullOrEmpty(url)) return;

                try
                {
                    // Check if it's a YouTube URL
                    if (url.Contains("youtube.com") || url.Contains("youtu.be"))
                    {
                        var youtube = YouTube.Default;
                        // VideoLibrary handles the 403 bypass internally in v3.3.1
                        var video = await youtube.GetVideoAsync(url);
                        
                        if (video != null)
                        {
                            _audioPlayerService.AddToPlaylist(video.FullName, video.Uri);
                        }
                    }
                    else
                    {
                        // Direct URL
                        string name = "Remote Audio";
                        if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                        {
                            name = Path.GetFileName(uri.LocalPath);
                            if (string.IsNullOrEmpty(name)) name = uri.Host;
                        }
                        _audioPlayerService.AddToPlaylist(name, url);
                    }
                }
                catch (Exception ex)
                {
                    _customMessageBoxService.ShowError("Error", $"Error processing URL: {ex.Message}\n\nNote: YouTube's protection is very strict. If this persists, the video might be region-locked or protected.", this);
                }
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!_isDragging && _mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                AudioSlider.Value = _mediaPlayer.Position.TotalSeconds;
                CurrentTimeText.Text = FormatTime(_mediaPlayer.Position);
            }
        }

        private void MediaPlayer_MediaOpened(object sender, EventArgs e)
        {
            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                AudioSlider.Maximum = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                TotalTimeText.Text = FormatTime(_mediaPlayer.NaturalDuration.TimeSpan);
            }
            _timer.Start();
        }

        private void MediaPlayer_MediaEnded(object sender, EventArgs e)
        {
            _timer.Stop();
            AudioSlider.Value = 0;
            CurrentTimeText.Text = "0:00";
            _viewModel.IsPlaying = false;
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = PlaylistListBox.SelectedItem as AudioPlaylistItem;
            if (selectedItem == null) return;

            if (!_viewModel.IsPlaying)
            {
                if (CurrentTrackName.Text != selectedItem.Name)
                {
                    _mediaPlayer.Open(new Uri(selectedItem.Url));
                    CurrentTrackName.Text = selectedItem.Name;
                }
                _mediaPlayer.Play();
                _viewModel.IsPlaying = true;
            }
            else
            {
                _mediaPlayer.Pause();
                _viewModel.IsPlaying = false;
            }
        }

        private void SkipForward_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                var newPos = _mediaPlayer.Position.Add(TimeSpan.FromSeconds(10));
                if (newPos > _mediaPlayer.NaturalDuration.TimeSpan)
                    newPos = _mediaPlayer.NaturalDuration.TimeSpan;
                _mediaPlayer.Position = newPos;
            }
        }

        private void SkipBackward_Click(object sender, RoutedEventArgs e)
        {
            var newPos = _mediaPlayer.Position.Subtract(TimeSpan.FromSeconds(10));
            if (newPos < TimeSpan.Zero)
                newPos = TimeSpan.Zero;
            _mediaPlayer.Position = newPos;
        }

        private string FormatTime(TimeSpan time)
        {
            return $"{(int)time.TotalMinutes}:{time.Seconds:D2}";
        }

        private void AudioSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDragging = true;
        }

        private void AudioSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isDragging = false;
            _mediaPlayer.Position = TimeSpan.FromSeconds(AudioSlider.Value);
        }

        private void AudioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDragging)
            {
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(AudioSlider.Value));
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Volume = e.NewValue;
            }

            if (_viewModel != null)
            {
                _viewModel.Volume = e.NewValue;
            }
        }

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AudioPlaylistItem item)
            {
                _audioPlayerService.RemoveFromPlaylist(item);
            }
        }

        private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
        {
            _audioPlayerService.ClearPlaylist();
            _mediaPlayer.Stop();
            CurrentTrackName.Text = "No track selected";
            CurrentTimeText.Text = "0:00";
            TotalTimeText.Text = "0:00";
            _viewModel.IsPlaying = false;
        }

        private void DropArea_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                DropAreaUI.Stroke = (Brush)FindResource("AccentBrush");
                DropAreaUI.Fill = (Brush)FindResource("HoverColor");
            }
        }

        private void DropArea_DragLeave(object sender, DragEventArgs e)
        {
            DropAreaUI.Stroke = (Brush)FindResource("BorderColor");
            DropAreaUI.Fill = (Brush)FindResource("SidebarBackground");
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            DropAreaUI.Stroke = (Brush)FindResource("BorderColor");
            DropAreaUI.Fill = (Brush)FindResource("SidebarBackground");

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (string file in files)
                {
                    string ext = Path.GetExtension(file).ToLower();
                    if (ext == ".mp3" || ext == ".wav" || ext == ".ogg")
                    {
                        _audioPlayerService.AddToPlaylist(Path.GetFileName(file), file);
                    }
                }
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _mediaPlayer.Stop();
            this.Close();
        }
    }
}
