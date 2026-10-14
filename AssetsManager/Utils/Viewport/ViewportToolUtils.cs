using System;

namespace AssetsManager.Utils.Viewport
{
    internal static class ViewportToolUtils
    {
        internal static double AdvanceAutoRotation(double degrees, double deltaSeconds)
        {
            if (!double.IsFinite(degrees)) degrees = 0d;
            if (double.IsFinite(deltaSeconds) && deltaSeconds > 0d)
                degrees += 30d * deltaSeconds;
            double normalized = degrees % 360d;
            return normalized < 0d ? normalized + 360d : normalized;
        }
    }
}
