using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Utils.Rendering;

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    public sealed partial class VfxRenderSession
    {
        public void UpdateBoneTransforms(Func<string, uint, Matrix4x4?> boneTransformProvider)
        {
            bool sourceChanged = _boneTransformSampler is null && !Equals(_boneTransformProvider, boneTransformProvider);
            _boneTransformProvider = boneTransformProvider;
            ApplyBoneTransforms(boneTransformProvider);
            if (sourceChanged) ReplayAfterLineageResourceChange();
        }

        private void ApplyBoneTransforms(Func<string, uint, Matrix4x4?> boneTransformProvider)
        {
            if (_graphs.Count == 0) return;

            Func<string, Matrix4x4?> jointProvider = boneTransformProvider is null
                ? null
                : boneName => boneTransformProvider(boneName, 0) is { } raw
                    ? PrepareBoneTransform(raw)
                    : null;

            foreach (VfxPlaybackGraphRuntime graph in _graphs)
            {
                if (_spellSteps.ContainsKey(graph))
                {
                    // Ability projectile/impact rigs are independent scene rigs in LTK; they do
                    // not inherit the owner's live joint table even while the champion animates.
                    graph.SetJointTransformProvider(null);
                    continue;
                }

                // boneToSpawnAt children use the same live skeleton as clip/idle attachments.
                graph.SetJointTransformProvider(jointProvider);

                if (_graphAttachments.TryGetValue(graph, out var attachment) && boneTransformProvider != null)
                {
                    Matrix4x4? boneMatrix = ResolveAttachmentBone(
                        boneTransformProvider,
                        attachment.BoneName,
                        attachment.BoneHash);
                    Matrix4x4? targetBoneMatrix = ResolveAttachmentBone(
                        boneTransformProvider,
                        attachment.TargetBoneName,
                        attachment.TargetBoneHash);

                    if (boneMatrix.HasValue)
                    {
                        Matrix4x4 boneTransform = PrepareBoneAnchorTransform(
                            boneMatrix.Value,
                            attachment.LocalOffset,
                            CurrentSkinScale);
                        if (attachment.IsDetachable && attachment.HasBoneTransform)
                            boneTransform = attachment.BoneTransform;
                        if (attachment.IsDetachable && _activeSystem?.CurrentTime >= attachment.StartTime)
                        {
                            attachment.BoneTransform = boneTransform;
                            attachment.HasBoneTransform = true;
                        }

                        Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                        Matrix4x4 orientationRoot =
                            attachment.BaseTransform * Matrix4x4.CreateTranslation(boneTransform.Translation) * authoredWorld;
                        graph.SetTransform(
                            attachment.BaseTransform * boneTransform * authoredWorld,
                            orientationRoot);

                        Vector3 target = targetBoneMatrix.HasValue
                            ? Vector3.Transform(PrepareBoneTransform(targetBoneMatrix.Value).Translation, authoredWorld)
                            : Vector3.Transform(
                                boneTransform.Translation + new Vector3(VfxRigMotion.TargetReach, 0f, 0f),
                                authoredWorld);
                        graph.SetTarget(target);
                        continue;
                    }

                    // LTK's jointAnchor(slot=-1) is the skeleton origin with an identity basis.
                    // A valid target joint still aims beams even when the source joint is missing.
                    Matrix4x4 fallbackPlacement = IdleFallbackTransform(
                        attachment.BaseTransform,
                        attachment.LocalOffset,
                        CurrentSkinScale,
                        Matrix4x4.Identity);
                    Matrix4x4 fallbackWorld = RootAuthoredWorld(graph);
                    Matrix4x4 fallbackOrientationRoot =
                        Matrix4x4.CreateTranslation(fallbackPlacement.Translation) * fallbackWorld;
                    graph.SetTransform(fallbackPlacement * fallbackWorld, fallbackOrientationRoot);
                    Vector3 fallbackTarget = targetBoneMatrix.HasValue
                        ? Vector3.Transform(PrepareBoneTransform(targetBoneMatrix.Value).Translation, fallbackWorld)
                        : Vector3.Transform(
                            fallbackPlacement.Translation + new Vector3(VfxRigMotion.TargetReach, 0f, 0f),
                            fallbackWorld);
                    graph.SetTarget(fallbackTarget);
                    continue;
                }

                if (_graphAttachments.TryGetValue(graph, out attachment) && attachment.IsIdleEffect)
                {
                    // LTK keeps an idle effect at the skeleton origin when its authored bone
                    // cannot be resolved, while still applying the authored local position.
                    Matrix4x4 fallbackPlacement = IdleFallbackTransform(
                        attachment.BaseTransform,
                        attachment.LocalOffset,
                        CurrentSkinScale,
                        Matrix4x4.Identity);
                    Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                    Matrix4x4 orientationRoot =
                        Matrix4x4.CreateTranslation(fallbackPlacement.Translation) * authoredWorld;
                    graph.SetTransform(fallbackPlacement * authoredWorld, orientationRoot);
                    continue;
                }

                if (_graphPlacements.TryGetValue(graph, out var basePlacement))
                {
                    Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                    Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(basePlacement.Translation) * authoredWorld;
                    graph.SetTransform(basePlacement * authoredWorld, orientationRoot);
                }
            }
        }

        private static Matrix4x4? ResolveAttachmentBone(
            Func<string, uint, Matrix4x4?> provider,
            string name,
            uint hash)
        {
            if (provider == null) return null;
            if (!string.IsNullOrEmpty(name) && provider(name, hash) is { } named)
                return named;
            return hash != 0 ? provider(null, hash) : null;
        }

        private float CurrentSkinScale
            => _ownerSceneContext is { SkinScale: > 0f } context && float.IsFinite(context.SkinScale)
                ? context.SkinScale
                : 1f;

        private Matrix4x4 RootAuthoredWorld(VfxPlaybackGraphRuntime graph)
            => _worldTransform;

        internal static Matrix4x4 IdleFallbackTransform(
            Matrix4x4 baseTransform,
            Vector3 localOffset,
            float skinScale,
            Matrix4x4 worldTransform)
        {
            float scale = skinScale > 0f && float.IsFinite(skinScale) ? skinScale : 1f;
            return baseTransform * Matrix4x4.CreateTranslation(localOffset * scale) * worldTransform;
        }

        private Matrix4x4 PrepareBoneTransform(Matrix4x4 transform)
            => PrepareBoneAnchorTransform(transform, Vector3.Zero, CurrentSkinScale);

        internal static Matrix4x4 PrepareBoneAnchorTransform(
            Matrix4x4 transform,
            Vector3 localOffset,
            float skinScale)
        {
            float scale = skinScale > 0f && float.IsFinite(skinScale) ? skinScale : 1f;
            Vector3 right = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitX, transform), Vector3.UnitX);
            Vector3 up = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitY, transform), Vector3.UnitY);
            Vector3 forward = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitZ, transform), Vector3.UnitZ);
            // LTK's jointAnchor transforms the authored offset by the joint's complete
            // posed frame, then applies the character skin scale to the resulting origin.
            Vector3 translation = Vector3.Transform(localOffset, transform) * scale;
            return new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                forward.X, forward.Y, forward.Z, 0f,
                translation.X, translation.Y, translation.Z, 1f);
        }
    }
}
