using System.Numerics;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    /// <summary>
    /// Provides one animated VFX mesh joint in the mesh's local engine space.
    /// The time is the owning particle's age, matching LTK's per-particle mesh pose clock.
    /// </summary>
    public interface IVfxMeshJointProvider
    {
        bool TryGetJointTransform(string jointName, float time, out Matrix4x4 transform);
    }
}
