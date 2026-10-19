using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Resources.Textures;

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

            _mipLevels = new Lazy<IReadOnlyList<BitmapSource>>(() => mipLevels);
            Width = mipLevels[0].PixelWidth;
            Height = mipLevels[0].PixelHeight;
            LevelCount = mipLevels.Count;
        }

        internal MapTextureImage(BlockTextureData blocks)
        {
            ArgumentNullException.ThrowIfNull(blocks);
            Blocks = blocks;
            Width = blocks.Levels[0].Width;
            Height = blocks.Levels[0].Height;
            LevelCount = blocks.Levels.Count;
            _mipLevels = new Lazy<IReadOnlyList<BitmapSource>>(blocks.Decode);
        }

        private readonly Lazy<IReadOnlyList<BitmapSource>> _mipLevels;
        internal BlockTextureData Blocks { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal int LevelCount { get; }
        internal bool PixelsDecoded => Blocks == null || _mipLevels.IsValueCreated;
        internal IReadOnlyList<BitmapSource> MipLevels => _mipLevels.Value;
        internal BitmapSource BaseLevel => MipLevels[0];
        internal bool HasAuthoredMipChain => LevelCount > 1;
    }
}
