using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Interaction
{
    internal static class ViewerInteractionService
    {
        public static IReadOnlyList<double> CalculateCenteredPositions(
            IReadOnlyList<double> widths,
            double gap)
        {
            if (widths == null) throw new ArgumentNullException(nameof(widths));
            if (widths.Count == 0) return Array.Empty<double>();
            if (!double.IsFinite(gap) || gap < 0)
                throw new ArgumentOutOfRangeException(nameof(gap));

            double totalWidth = widths.Sum(width => Math.Max(0, width)) + gap * (widths.Count - 1);
            double cursor = -totalWidth / 2;
            var positions = new double[widths.Count];
            for (int i = 0; i < widths.Count; i++)
            {
                double width = Math.Max(0, widths[i]);
                positions[i] = cursor + width / 2;
                cursor += width + gap;
            }
            return positions;
        }

        public static void ArrangeModels(IReadOnlyList<SceneModel> models)
        {
            if (models == null || models.Count == 0) return;

            double[] widths = models
                .Select(model => Math.Max(1, GetLocalBounds(model).SizeX * Math.Abs(model.Scale)))
                .ToArray();
            double gap = Math.Max(25, widths.Max() * 0.15);
            IReadOnlyList<double> positions = CalculateCenteredPositions(widths, gap);

            for (int i = 0; i < models.Count; i++)
            {
                models[i].PositionX = positions[i];
                models[i].PositionZ = 0;
            }
        }

        public static Rect3D GetLocalBounds(SceneModel model)
        {
            Rect3D bounds = Rect3D.Empty;
            if (model?.Parts == null) return bounds;

            foreach (ModelPart part in model.Parts)
            {
                if (part.IsVisible &&
                    part.Geometry?.Geometry is MeshGeometry3D mesh &&
                    !mesh.Bounds.IsEmpty)
                {
                    if (bounds.IsEmpty) bounds = mesh.Bounds;
                    else bounds.Union(mesh.Bounds);
                }
            }
            return bounds;
        }

        public static SceneModel PickModel(
            IEnumerable<SceneModel> models,
            System.Windows.Point screenPoint,
            double viewportWidth,
            double viewportHeight,
            PerspectiveCamera camera)
        {
            if (models == null || camera == null || viewportWidth <= 0 || viewportHeight <= 0)
                return null;
            if (!TryCreateRay(screenPoint, viewportWidth, viewportHeight, camera, out Vector3 origin, out Vector3 direction))
                return null;

            SceneModel closest = null;
            float closestDistance = float.PositiveInfinity;
            foreach (SceneModel model in models)
            {
                if (model == null || !model.IsVisible) continue;
                Rect3D bounds = GetLocalBounds(model);
                if (bounds.IsEmpty) continue;

                Matrix4x4 world = CreateWorldMatrix(model);
                if (!Matrix4x4.Invert(world, out Matrix4x4 inverseWorld)) continue;

                Vector3 localOrigin = Vector3.Transform(origin, inverseWorld);
                Vector3 localDirection = Vector3.Normalize(Vector3.TransformNormal(direction, inverseWorld));
                if (TryIntersectBounds(localOrigin, localDirection, bounds, out float localDistance))
                {
                    Vector3 localHit = localOrigin + localDirection * localDistance;
                    Vector3 worldHit = Vector3.Transform(localHit, world);
                    float worldDistance = Vector3.Distance(origin, worldHit);
                    if (worldDistance < closestDistance)
                    {
                        closestDistance = worldDistance;
                        closest = model;
                    }
                }
            }
            return closest;
        }

        public static bool TryProject(
            Vector3 worldPoint,
            Matrix4x4 viewProjection,
            double width,
            double height,
            out System.Windows.Point screenPoint)
        {
            Vector4 clip = Vector4.Transform(new Vector4(worldPoint, 1), viewProjection);
            if (clip.W <= float.Epsilon)
            {
                screenPoint = default;
                return false;
            }

            Vector3 ndc = new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
            screenPoint = new System.Windows.Point(
                (ndc.X + 1) * 0.5 * width,
                (1 - ndc.Y) * 0.5 * height);
            return float.IsFinite(ndc.X) && float.IsFinite(ndc.Y);
        }

        public static Matrix4x4 CreateWorldMatrix(SceneModel model)
        {
            float pitch = (float)(model.RotationX * Math.PI / 180);
            float yaw = (float)(model.RotationY * Math.PI / 180);
            float roll = (float)(model.RotationZ * Math.PI / 180);
            return Matrix4x4.CreateScale((float)model.Scale) *
                   Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll) *
                   Matrix4x4.CreateTranslation(
                       (float)model.PositionX,
                       (float)model.PositionY,
                       (float)model.PositionZ);
        }

        private static bool TryCreateRay(
            System.Windows.Point point,
            double width,
            double height,
            PerspectiveCamera camera,
            out Vector3 origin,
            out Vector3 direction)
        {
            Vector3 eye = new((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z);
            Vector3 look = new((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z);
            Vector3 up = new((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z);
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye + look, up);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                (float)(camera.FieldOfView * Math.PI / 180),
                (float)(width / height),
                10,
                Math.Max(100000, look.Length() * 4));
            if (!Matrix4x4.Invert(view * projection, out Matrix4x4 inverse))
            {
                origin = direction = default;
                return false;
            }

            float x = (float)(point.X / width * 2 - 1);
            float y = (float)(1 - point.Y / height * 2);
            Vector4 near = Vector4.Transform(new Vector4(x, y, 0, 1), inverse);
            Vector4 far = Vector4.Transform(new Vector4(x, y, 1, 1), inverse);
            if (Math.Abs(near.W) <= float.Epsilon || Math.Abs(far.W) <= float.Epsilon)
            {
                origin = direction = default;
                return false;
            }

            origin = new Vector3(near.X, near.Y, near.Z) / near.W;
            Vector3 farPoint = new Vector3(far.X, far.Y, far.Z) / far.W;
            direction = Vector3.Normalize(farPoint - origin);
            return true;
        }

        private static bool TryIntersectBounds(
            Vector3 origin,
            Vector3 direction,
            Rect3D bounds,
            out float distance)
        {
            Vector3 min = new((float)bounds.X, (float)bounds.Y, (float)bounds.Z);
            Vector3 max = min + new Vector3((float)bounds.SizeX, (float)bounds.SizeY, (float)bounds.SizeZ);
            float near = 0;
            float far = float.PositiveInfinity;

            for (int axis = 0; axis < 3; axis++)
            {
                float rayOrigin = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
                float rayDirection = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
                float boxMin = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
                float boxMax = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
                if (Math.Abs(rayDirection) < 1e-6f)
                {
                    if (rayOrigin < boxMin || rayOrigin > boxMax)
                    {
                        distance = 0;
                        return false;
                    }
                    continue;
                }

                float first = (boxMin - rayOrigin) / rayDirection;
                float second = (boxMax - rayOrigin) / rayDirection;
                if (first > second) (first, second) = (second, first);
                near = Math.Max(near, first);
                far = Math.Min(far, second);
                if (near > far)
                {
                    distance = 0;
                    return false;
                }
            }

            distance = near;
            return far >= 0;
        }
    }
}
