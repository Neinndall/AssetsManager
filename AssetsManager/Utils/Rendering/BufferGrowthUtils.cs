using System;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>
    /// Utility functions for power-of-two capacity calculations for dynamic vertex and instance buffers.
    /// </summary>
    public static class BufferGrowthUtils
    {
        /// <summary>
        /// Calculates the next expanded buffer capacity guaranteeing exponential power-of-two growth.
        /// </summary>
        public static int CalculateNextCapacity(int currentLength, int requiredLength, int minimumLength = 64)
        {
            if (requiredLength <= currentLength) return currentLength;
            int next = Math.Max(Math.Max(1, currentLength), minimumLength);
            while (next < requiredLength)
            {
                if (next > int.MaxValue / 2) return requiredLength;
                next *= 2;
            }
            return next;
        }
    }
}
