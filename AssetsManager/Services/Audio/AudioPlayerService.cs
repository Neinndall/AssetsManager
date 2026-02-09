using System;
using System.Collections.ObjectModel;
using System.Linq;
using AssetsManager.Views.Models.Audio;

namespace AssetsManager.Services.Audio
{
    public class AudioPlayerService
    {
        public ObservableCollection<AudioPlaylistItem> Playlist { get; } = new ObservableCollection<AudioPlaylistItem>();
        
        public event EventHandler<AudioPlaylistItem> RequestPlay;

        public void AddToPlaylist(string name, string url, byte[] data = null)
        {
            if (Playlist.Any(x => x.Url == url)) return;

            var item = new AudioPlaylistItem
            {
                Name = name,
                Url = url,
                Data = data,
                AddedAt = DateTime.Now
            };

            Playlist.Add(item);
        }

        public void RemoveFromPlaylist(AudioPlaylistItem item)
        {
            Playlist.Remove(item);
        }

        public void ClearPlaylist()
        {
            Playlist.Clear();
        }

        public void PlayItem(AudioPlaylistItem item)
        {
            RequestPlay?.Invoke(this, item);
        }
    }

    public class AudioPlaylistItem
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public byte[] Data { get; set; }
        public DateTime AddedAt { get; set; }
    }
}
