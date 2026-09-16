using System;
using LeagueToolkit.Core.Animation;

namespace AssetsManager.Views.Models.Viewer
{
    public class AnimationData : IDisposable
    {
        public IAnimationAsset AnimationAsset { get; set; }
        public string Name { get; set; }

        public void Dispose()
        {
            AnimationAsset?.Dispose();
            AnimationAsset = null;
        }
    }
}
