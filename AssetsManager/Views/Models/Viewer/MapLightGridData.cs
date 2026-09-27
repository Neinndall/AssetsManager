using System;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    internal sealed class MapLightGridData
    {
        private readonly byte[] _cells;
        internal int Width { get; }
        internal int Height { get; }
        internal float ExtentX { get; }
        internal float ExtentZ { get; }
        internal float Scale { get; }
        internal float FullBright { get; }

        internal MapLightGridData(int width, int height, float extentX, float extentZ,
            float scale, float fullBright, byte[] cells)
        {
            Width = width;
            Height = height;
            ExtentX = extentX;
            ExtentZ = extentZ;
            Scale = scale;
            FullBright = fullBright;
            _cells = cells;
        }

        internal void SampleSceneCube(Vector3 position, Span<Vector3> faces)
        {
            if (faces.Length < 6) throw new ArgumentException("Six ambient faces are required.", nameof(faces));
            int column = (int)Math.Clamp(MathF.Truncate(-position.X * Width / ExtentX), 0, Width - 1);
            int row = (int)Math.Clamp(MathF.Truncate(position.Z * Height / ExtentZ), 0, Height - 1);
            int cell = (column + row * Width) * 24;
            float unit = Scale / 255f;
            for (int face = 0; face < 6; face++)
            {
                // The file is BGR; mirror the X-facing pair into viewport coordinates.
                int sourceFace = face < 2 ? 1 - face : face;
                int at = cell + sourceFace * 4;
                faces[face] = new Vector3(_cells[at + 2], _cells[at + 1], _cells[at]) * unit;
            }
        }
    }
}
