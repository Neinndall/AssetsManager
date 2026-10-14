using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// One MAP material texture together with the authored mip chain the viewport can upload.
    /// Levels are ordered widest to smallest, matching the current LTK MAIN viewport contract.
    /// </summary>
    internal sealed class MapTextureImage
    {
        internal MapTextureImage(IReadOnlyList<BitmapSource> mipLevels)
        {
            if (mipLevels == null || mipLevels.Count == 0)
                throw new ArgumentException("A map texture must contain at least one mip level.", nameof(mipLevels));

            for (int level = 0; level < mipLevels.Count; level++)
            {
                if (mipLevels[level] == null)
                    throw new ArgumentException("A map texture mip level cannot be null.", nameof(mipLevels));
            }

            MipLevels = mipLevels;
        }

        internal IReadOnlyList<BitmapSource> MipLevels { get; }
        internal BitmapSource BaseLevel => MipLevels[0];
        internal bool HasAuthoredMipChain => MipLevels.Count > 1;
    }
}
