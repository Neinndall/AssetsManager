using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Material3D = System.Windows.Media.Media3D.Material;

namespace AssetsManager.Views.Helpers
{
    public static class SceneElements
    {
        public static ModelVisual3D CreateSidePlanes(Func<string, BitmapSource> loadTextureFunc, Action<string> logErrorFunc)
        {
            Model3DGroup finalGroup = new Model3DGroup();
            double size = 5000; // A large size for the skybox planes

            // 1. Load individual textures for each side and create their materials
            string frontTexturePath = "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_front.dds";
            BitmapSource frontTexture = loadTextureFunc(frontTexturePath);
            Material3D frontMaterial = (frontTexture != null) ? new DiffuseMaterial(new ImageBrush(frontTexture)) : new DiffuseMaterial(new SolidColorBrush(Colors.Gray));
            if (frontTexture == null) logErrorFunc($"Failed to load sky_front texture from {frontTexturePath}. Using solid color fallback.");

            string rightTexturePath = "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_right.dds";
            BitmapSource rightTexture = loadTextureFunc(rightTexturePath);
            Material3D rightMaterial = (rightTexture != null) ? new DiffuseMaterial(new ImageBrush(rightTexture)) : new DiffuseMaterial(new SolidColorBrush(Colors.Gray));
            if (rightTexture == null) logErrorFunc($"Failed to load sky_right texture from {rightTexturePath}. Using solid color fallback.");

            string backTexturePath = "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_back.dds";
            BitmapSource backTexture = loadTextureFunc(backTexturePath);
            Material3D backMaterial = (backTexture != null) ? new DiffuseMaterial(new ImageBrush(backTexture)) : new DiffuseMaterial(new SolidColorBrush(Colors.Gray));
            if (backTexture == null) logErrorFunc($"Failed to load sky_back texture from {backTexturePath}. Using solid color fallback.");

            string leftTexturePath = "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_left.dds";
            BitmapSource leftTexture = loadTextureFunc(leftTexturePath);
            Material3D leftMaterial = (leftTexture != null) ? new DiffuseMaterial(new ImageBrush(leftTexture)) : new DiffuseMaterial(new SolidColorBrush(Colors.Gray));
            if (leftTexture == null) logErrorFunc($"Failed to load sky_left texture from {leftTexturePath}. Using solid color fallback.");

            // Load sky_up texture
            string skyUpTexturePath = "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_up.dds";
            BitmapSource skyUpTexture = loadTextureFunc(skyUpTexturePath);
            Material3D skyUpMaterial = (skyUpTexture != null) ? new DiffuseMaterial(new ImageBrush(skyUpTexture)) : new DiffuseMaterial(new SolidColorBrush(Colors.LightBlue)); // Fallback color
            if (skyUpTexture == null) logErrorFunc($"Failed to load sky_up texture from {skyUpTexturePath}. Using solid color fallback.");

            // Load sky_down texture
            string skyDownTexturePath = "pack://application:,,,/AssetsManager;component/Resources/Scene/Sky/sky_down.dds";
            BitmapSource skyDownTexture = loadTextureFunc(skyDownTexturePath);
            Material3D skyDownMaterial = (skyDownTexture != null) ? new DiffuseMaterial(new ImageBrush(skyDownTexture)) : new DiffuseMaterial(new SolidColorBrush(Colors.DarkGray)); // Fallback color
            if (skyDownTexture == null) logErrorFunc($"Failed to load sky_down texture from {skyDownTexturePath}. Using solid color fallback.");

            // 2. Create a single, canonical plane geometry. By default, its front face points towards +Z.
            var planeMesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection()
                {
                    new Point3D(-size, -size, 0), // Bottom-left
                    new Point3D(size, -size, 0),  // Bottom-right
                    new Point3D(size, size, 0),   // Top-right
                    new Point3D(-size, size, 0)    // Top-left
                },
                TriangleIndices = new Int32Collection() { 0, 1, 2, 0, 2, 3 },
                TextureCoordinates = new PointCollection()
                {
                    new System.Windows.Point(0, 1),
                    new System.Windows.Point(1, 1),
                    new System.Windows.Point(1, 0),
                    new System.Windows.Point(0, 0)
                }
            };

            // Back Plane (at z=size, needs to face origin at -Z)
            var backTransform = new Transform3DGroup();
            backTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 180)));
            backTransform.Children.Add(new TranslateTransform3D(new Vector3D(0, 0, size)));
            var backPlane = new GeometryModel3D(planeMesh, backMaterial);
            backPlane.Transform = backTransform;
            finalGroup.Children.Add(backPlane);

            // Front Plane (at z=-size, needs to face origin at +Z)
            var frontPlane = new GeometryModel3D(planeMesh, frontMaterial);
            frontPlane.Transform = new TranslateTransform3D(0, 0, -size);
            finalGroup.Children.Add(frontPlane);

            // Left Plane (at x=-size, needs to face origin at +X)
            var leftTransform = new Transform3DGroup();
            leftTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 90)));
            leftTransform.Children.Add(new TranslateTransform3D(new Vector3D(-size, 0, 0)));
            var leftPlane = new GeometryModel3D(planeMesh, leftMaterial);
            leftPlane.Transform = leftTransform;
            finalGroup.Children.Add(leftPlane);

            // Right Plane (at x=size, needs to face origin at -X)
            var rightTransform = new Transform3DGroup();
            rightTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), -90)));
            rightTransform.Children.Add(new TranslateTransform3D(new Vector3D(size, 0, 0)));
            var rightPlane = new GeometryModel3D(planeMesh, rightMaterial);
            rightPlane.Transform = rightTransform;
            finalGroup.Children.Add(rightPlane);

            // Top Plane (at y=size, needs to face origin at -Y)
            var topTransform = new Transform3DGroup();
            topTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 90))); // Rotate to face down
            topTransform.Children.Add(new TranslateTransform3D(new Vector3D(0, size, 0))); // Move to top
            var topPlane = new GeometryModel3D(planeMesh, skyUpMaterial);
            topPlane.Transform = topTransform;
            finalGroup.Children.Add(topPlane);

            // Bottom Plane (at y=-size, needs to face origin at +Y)
            var bottomTransform = new Transform3DGroup();
            bottomTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), -90))); // Rotate to face up
            bottomTransform.Children.Add(new TranslateTransform3D(new Vector3D(0, -size, 0))); // Move to bottom
            var bottomPlane = new GeometryModel3D(planeMesh, skyDownMaterial);
            bottomPlane.Transform = bottomTransform;
            finalGroup.Children.Add(bottomPlane);

            return new ModelVisual3D { Content = finalGroup };
        }

        public static ModelVisual3D CreateGroundPlane(Func<string, BitmapSource> loadTextureFunc, Action<string> logErrorFunc)
        {
            MeshGeometry3D groundMesh = new MeshGeometry3D();

            // Define vertices for a large square plane (e.g., 800x800 units)
            // Y-coordinate is 0 to place it at the base of the model
            groundMesh.Positions = new Point3DCollection()
            {
                new Point3D(-1600, 0, -1600), // Bottom-left
                new Point3D(1600, 0, -1600),  // Bottom-right
                new Point3D(1600, 0, 1600),   // Top-right
                new Point3D(-1600, 0, 1600)   // Top-left
            };

            // Define triangle indices (two triangles for a square)
            groundMesh.TriangleIndices = new Int32Collection() { 0, 3, 2, 0, 2, 1 };

            // Define texture coordinates (simple mapping for a solid color)
            groundMesh.TextureCoordinates = new PointCollection()
            {
                new System.Windows.Point(0, 1),
                new System.Windows.Point(1, 1),
                new System.Windows.Point(1, 0),
                new System.Windows.Point(0, 0)
            };

            // Load the ground texture
            string groundTexturePath = "pack://application:,,,/AssetsManager;component/Resources/Scene/Floor/ground_rift.dds"; // Assuming ground_rift.dds is in the app directory
            BitmapSource groundTexture = loadTextureFunc(groundTexturePath);

            Material3D groundMaterial;
            if (groundTexture != null)
            {
                groundMaterial = new DiffuseMaterial(new ImageBrush(groundTexture));
            }
            else
            {
                // Fallback to a solid color if texture loading fails
                groundMaterial = new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 120, 80))); // Earthy color
                logErrorFunc($"Failed to load ground texture from {groundTexturePath}. Using solid color fallback.");
            }

            // Create the GeometryModel3D and ModelVisual3D
            GeometryModel3D groundModel = new GeometryModel3D(groundMesh, groundMaterial);
            return new ModelVisual3D { Content = groundModel };
        }
    }
}
