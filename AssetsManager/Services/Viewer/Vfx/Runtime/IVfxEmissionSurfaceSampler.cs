using System.Numerics;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    /// <summary>A sampled particle-birth point in emitter-local space.</summary>
    public readonly record struct VfxSurfaceBirth(Vector3 Position, Vector3 Normal);

    /// <summary>
    /// Graphics-independent contract for a loaded VFX emission surface. Mesh/skeleton asset
    /// loading and posing live outside the simulator; the runtime only consumes deterministic samples.
    /// </summary>
    public interface IVfxEmissionSurfaceSampler
    {
        bool TrySample(float time, VfxLtkRandom rng, out VfxSurfaceBirth birth);
    }
}
