using System;
using System.IO;
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using CommunityToolkit.HighPerformance;

namespace AssetsManager.Services.Viewer.Vfx.Resources
{
    /// <summary>Decodes the six-face DDS reflection resources consumed by League's mesh shader.</summary>
    internal static class VfxCubeMapDecoder
    {
        internal static VfxCubeMapData Decode(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return Decode(stream);
            }
            catch
            {
                return null;
            }
        }

        internal static VfxCubeMapData Decode(Stream stream)
        {
            if (stream == null || !stream.CanRead) return null;
            try
            {
                DdsFile dds = DdsFile.Load(stream);
                if (dds.Faces.Count != 6) return null;

                var decoder = new BcDecoder();
                var format = decoder.GetFormat(dds);
                var faces = new byte[6][];
                int width = 0, height = 0;
                for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                {
                    DdsFace face = dds.Faces[faceIndex];
                    if (face.MipMaps is not { Length: > 0 }) return null;
                    DdsMipMap mip = face.MipMaps[0];
                    int faceWidth = checked((int)mip.Width);
                    int faceHeight = checked((int)mip.Height);
                    if (faceIndex == 0)
                    {
                        width = faceWidth;
                        height = faceHeight;
                    }
                    else if (faceWidth != width || faceHeight != height)
                    {
                        return null;
                    }

                    var decoded = decoder.DecodeRaw2D(mip.Data, faceWidth, faceHeight, format);
                    if (!decoded.TryGetMemory(out Memory<ColorRgba32> memory)) return null;
                    faces[faceIndex] = MemoryMarshal.AsBytes(memory.Span).ToArray();
                }
                return new VfxCubeMapData(width, height, faces);
            }
            catch
            {
                // Only a genuine six-face DDS is a cube resource. Flat TEX/DDS content is optional
                // and simply leaves the preview without a cube background/reflection stage.
                return null;
            }
        }

    }
}