using System;

namespace AssetsManager.Services.Viewer.Semantics
{
    internal static class ViewerAutoRotation
    {
        internal static double Advance(double degrees, double deltaSeconds)
        {
            if (!double.IsFinite(degrees)) degrees = 0d;
            if (double.IsFinite(deltaSeconds) && deltaSeconds > 0d)
                degrees += 30d * deltaSeconds;
            double normalized = degrees % 360d;
            return normalized < 0d ? normalized + 360d : normalized;
        }
    }
}
