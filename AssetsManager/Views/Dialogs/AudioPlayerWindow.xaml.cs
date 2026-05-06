using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AssetsManager.Services.Audio;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Dialogs;

namespace AssetsManager.Views.Dialogs
{
    public partial class AudioPlayerWindow : HudWindow
    {
        public AudioPlayerModel ViewModel { get; }

        public AudioPlayerWindow(AudioPlayerService audioService)
        {
            InitializeComponent();
            ViewModel = new AudioPlayerModel(audioService);
            DataContext = ViewModel;
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
                ViewModel.Service.PlayItem(selectedItem);
                ViewModel.IsPlaying = true;
                CurrentTrackName.Text = selectedItem.Name;
            }
        }

        private void SkipForward_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Service.Playlist.Count == 0) return;
            int nextIndex = (PlaylistListBox.SelectedIndex + 1) % ViewModel.Service.Playlist.Count;
            PlaylistListBox.SelectedIndex = nextIndex;
            PlayPause_Click(null, null);
        }

        private void SkipBackward_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Service.Playlist.Count == 0) return;
            int prevIndex = (PlaylistListBox.SelectedIndex - 1 + ViewModel.Service.Playlist.Count) % ViewModel.Service.Playlist.Count;
            PlaylistListBox.SelectedIndex = prevIndex;
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
            ViewModel.IsPlaying = false;
            CurrentTrackName.Text = "No track selected";
        }

        private void AddUrl_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new InputDialog();
            dialog.Initialize("Add URL", "Paste the URL below:", "https://...");
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                ViewModel.Service.AddToPlaylist(Path.GetFileName(dialog.InputText), dialog.InputText);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Slider & Volume
        // ──────────────────────────────────────────────────────────────────────

        private void AudioSlider_DragStarted(object sender, DragStartedEventArgs e) { }
        private void AudioSlider_DragCompleted(object sender, DragCompletedEventArgs e) { }
        private void AudioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ViewModel != null)
            {
                ViewModel.Volume = VolumeSlider.Value;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Drag & Drop
        // ──────────────────────────────────────────────────────────────────────

        private void Window_Drop(object sender, DragEventArgs e)
        {
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