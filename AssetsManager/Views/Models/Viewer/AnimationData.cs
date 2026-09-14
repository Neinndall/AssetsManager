using System;
using LeagueToolkit.Core.Animation;

namespace AssetsManager.Views.Models.Viewer
{
    public class AnimationData : IDisposable
    {
        public IAnimationAsset AnimationAsset { get; set; }
        public string Name { get; set; }
        public string FilePath { get; set; }
        public AnimationClipCatalogItem AuthoredClip { get; set; }
        public AnimationClipVfxContext ClipVfxContext { get; set; }

        public bool IsAuthoredClip => AuthoredClip != null;
        public int EventCount => AuthoredClip?.EventCount ?? 0;
        public int VfxEventCount => AuthoredClip?.VfxEventCount ?? 0;
        public string KindLabel => IsAuthoredClip ? "CLIP" : "ANIM";
        public string EventSummary => IsAuthoredClip
            ? $"{EventCount} events · {VfxEventCount} VFX"
            : "Standalone .anm";

        public void Dispose()
        {
            AnimationAsset?.Dispose();
            AnimationAsset = null;
            AuthoredClip = null;
            ClipVfxContext = null;
        }
    }
}
