using System.Collections.Generic;

namespace AssetsManager.Views.Models.Audio
{
    public class SavedAudioTrack
    {
        public string Name { get; set; }
        public string Url { get; set; }
    }

    public class AudioPlaylistPack
    {
        public string Name { get; set; }
        public List<SavedAudioTrack> Tracks { get; set; } = new List<SavedAudioTrack>();
    }
}
