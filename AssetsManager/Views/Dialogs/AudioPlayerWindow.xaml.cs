using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Dialogs;
using VideoLibrary;
using Microsoft.Extensions.DependencyInjection;

namespace AssetsManager.Views.Dialogs
{
    public partial class AudioPlayerWindow : HudWindow
    {
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly AppSettings _settings;
        private readonly MediaPlayer _mediaPlayer = new MediaPlayer();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private bool _isDragging = false;

        public AudioPlayerModel ViewModel { get; }

        public AudioPlayerWindow(AudioPlayerService audioService, CustomMessageBoxService customMessageBoxService, AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            ViewModel = new AudioPlayerModel(audioService);
            DataContext = ViewModel;
            _customMessageBoxService = customMessageBoxService;

            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += Timer_Tick;

            _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            _mediaPlayer.Volume = ViewModel.Volume;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Playback Controls
        // ──────────────────────────────────────────────────────────────────────

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Service.Playlist.Count == 0) return;

            var selectedItem = PlaylistListBox.SelectedItem as AudioPlaylistItem;
            if (selectedItem != null)
            {
                if (ViewModel.CurrentTrackName != selectedItem.Name)
                {
                    _mediaPlayer.Open(new Uri(selectedItem.Url));
                    ViewModel.CurrentTrackName = selectedItem.Name;
                    _mediaPlayer.Play();
                    ViewModel.IsPlaying = true;
                }
                else
                {
                    if (ViewModel.IsPlaying)
                    {
                        _mediaPlayer.Pause();
                        ViewModel.IsPlaying = false;
                    }
                    else
                    {
                        _mediaPlayer.Play();
                        ViewModel.IsPlaying = true;
                    }
                }
            }
        }

        private void SkipForward_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Service.Playlist.Count == 0) return;
            int nextIndex = (PlaylistListBox.SelectedIndex + 1) % ViewModel.Service.Playlist.Count;
            PlaylistListBox.SelectedIndex = nextIndex;
            ViewModel.CurrentTrackName = ""; // Force reload
            PlayPause_Click(null, null);
        }

        private void SkipBackward_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Service.Playlist.Count == 0) return;
            int prevIndex = (PlaylistListBox.SelectedIndex - 1 + ViewModel.Service.Playlist.Count) % ViewModel.Service.Playlist.Count;
            PlaylistListBox.SelectedIndex = prevIndex;
            ViewModel.CurrentTrackName = ""; // Force reload
            PlayPause_Click(null, null);
        }

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AudioPlaylistItem item)
            {
                ViewModel.Service.RemoveFromPlaylist(item);
            }
        }

        private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.Service.ClearPlaylist();
            _mediaPlayer.Stop();
            ViewModel.IsPlaying = false;
            ViewModel.ResetToDefault();
        }

        private async void AddUrl_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new InputDialog();
            dialog.Initialize("Add URL", "Paste the YouTube URL below:", string.Empty);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                string url = dialog.InputText.Trim();

                try
                {
                    if (url.Contains("youtube.com") || url.Contains("youtu.be"))
                    {
                        var youtube = YouTube.Default;
                        var video = await youtube.GetVideoAsync(url);
                        if (video != null)
                        {
                            ViewModel.Service.AddToPlaylist(video.FullName, video.Uri);
                        }
                    }
                    else
                    {
                        ViewModel.Service.AddToPlaylist(Path.GetFileName(url), url);
                    }
                }
                catch (Exception ex)
                {
                    _customMessageBoxService.ShowError("Error", $"Error processing URL: {ex.Message}", this);
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Playlist Hub (Library & Saving)
        // ──────────────────────────────────────────────────────────────────────

        private void SavePlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Service.Playlist.Count == 0)
            {
                _customMessageBoxService.ShowError("Playlist Empty", "You cannot save an empty playlist.", this);
                return;
            }

            string initialName = ViewModel.ActivePackName == "New Playlist" ? "" : ViewModel.ActivePackName;
            var dialog = new InputDialog();
            dialog.Initialize("Save Playlist", "Enter a name for this playlist pack:", initialName);
            dialog.Owner = this;

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                string packName = dialog.InputText.Trim();
                ViewModel.Service.SavePlaylist(packName);
                ViewModel.ActivePackName = packName;
                
                _customMessageBoxService.ShowSuccess("Playlist Saved", $"Playlist '{packName}' saved successfully!", this);
            }
        }

        private void ShowLibrary_Click(object sender, RoutedEventArgs e)
        {
            var contextMenu = new ContextMenu();

            if (!_settings.AudioPlaylists.Any())
            {
                var emptyItem = new MenuItem { Header = "No saved playlists", IsEnabled = false };
                contextMenu.Items.Add(emptyItem);
            }
            else
            {
                foreach (var pack in _settings.AudioPlaylists)
                {
                    var item = new MenuItem { Header = pack.Name };
                    
                    var loadItem = new MenuItem { Header = "Load Playlist" };
                    loadItem.Click += (s, args) => 
                    {
                        ViewModel.Service.LoadPlaylist(pack.Name);
                        ViewModel.ActivePackName = pack.Name;
                        ViewModel.IsPlaying = false;
                        _mediaPlayer.Stop();
                        ViewModel.ResetToDefault(); // Reset track info on load
                        ViewModel.ActivePackName = pack.Name; // Restore name after reset
                    };

                    var deleteItem = new MenuItem { Header = "Delete Pack" };
                    deleteItem.Click += (s, args) => 
                    {
                        if (_customMessageBoxService.ShowYesNo("Delete Pack", $"Are you sure you want to delete '{pack.Name}'?", this) == true)
                        {
                            ViewModel.Service.DeletePlaylist(pack.Name);
                            if (ViewModel.ActivePackName == pack.Name)
                            {
                                ViewModel.ActivePackName = "New Playlist";
                            }
                        }
                    };

                    item.Items.Add(loadItem);
                    item.Items.Add(deleteItem);
                    contextMenu.Items.Add(item);
                }
            }

            var clearItem = new MenuItem { Header = "New (Clear Current)" };
            clearItem.Click += (s, args) => ClearPlaylist_Click(null, null);
            contextMenu.Items.Add(clearItem);

            contextMenu.PlacementTarget = LibraryButton;
            contextMenu.Placement = PlacementMode.Bottom;
            contextMenu.IsOpen = true;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Player Events
        // ──────────────────────────────────────────────────────────────────────

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!_isDragging && _mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                ViewModel.CurrentTime = _mediaPlayer.Position.TotalSeconds;
                ViewModel.CurrentTimeText = FormatTime(_mediaPlayer.Position);
            }
        }

        private void MediaPlayer_MediaOpened(object sender, EventArgs e)
        {
            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                ViewModel.TotalTime = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                ViewModel.TotalTimeText = FormatTime(_mediaPlayer.NaturalDuration.TimeSpan);
            }
            _timer.Start();
        }

        private void MediaPlayer_MediaEnded(object sender, EventArgs e)
        {
            _timer.Stop();
            ViewModel.IsPlaying = false;
            if (ViewModel.Service.Playlist.Count > 1)
            {
                SkipForward_Click(null, null);
            }
        }

        private string FormatTime(TimeSpan time)
        {
            return $"{(int)time.TotalMinutes}:{time.Seconds:D2}";
        }

        // ──────────────────────────────────────────────────────────────────────
        // Slider & Volume
        // ──────────────────────────────────────────────────────────────────────

        private void AudioSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDragging = true;
        }

        private void AudioSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isDragging = false;
            _mediaPlayer.Position = TimeSpan.FromSeconds(ViewModel.CurrentTime);
        }

        private void AudioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDragging)
            {
                ViewModel.CurrentTimeText = FormatTime(TimeSpan.FromSeconds(ViewModel.CurrentTime));
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ViewModel != null)
            {
                ViewModel.Volume = VolumeSlider.Value;
                _mediaPlayer.Volume = ViewModel.Volume;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Drag & Drop
        // ──────────────────────────────────────────────────────────────────────

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (FindResource("BorderColor") is Brush brush)
                DropAreaUI.Stroke = brush;

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (SupportedFileTypes.Media.Contains(ext))
                    {
                        ViewModel.Service.AddToPlaylist(Path.GetFileName(file), file);
                    }
                }
            }
        }

        private void DropArea_DragEnter(object sender, DragEventArgs e)
        {
            if (FindResource("AccentBrush") is Brush brush)
                DropAreaUI.Stroke = brush;
        }

        private void DropArea_DragLeave(object sender, DragEventArgs e)
        {
            if (FindResource("BorderColor") is Brush brush)
                DropAreaUI.Stroke = brush;
        }
    }
}
