using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Material.Icons.WPF;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public class ExplorerLoadingSpinnerTests
    {
        private static readonly XNamespace PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly XNamespace MaterialIconsNs = "clr-namespace:Material.Icons.WPF;assembly=Material.Icons.WPF";

        [Theory]
        [InlineData("Views", "Controls", "Explorer", "FileExplorerControl.xaml")]
        [InlineData("Themes", "Templates", "FileGridTemplate.xaml")]
        public void RegularItemIconDoesNotAttachSpinningStoryboard(params string[] relativePath)
        {
            string fullPath = Path.Combine(new[] { GetAppRoot() }.Concat(relativePath).ToArray());
            XDocument doc = XDocument.Load(fullPath);

            // Regular file/folder icons (Kind bound to PathToIconConverter) must never carry
            // SpinningIconAnimation, otherwise VirtualizingStackPanel recycling spins regular icons forever.
            var regularIcons = doc
                .Descendants(MaterialIconsNs + "MaterialIcon")
                .Where(icon => ((string)icon.Attribute("Kind") ?? string.Empty).Contains("PathToIconConverter", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(regularIcons);
            foreach (XElement regularIcon in regularIcons)
            {
                bool hasSpinStoryboard = regularIcon
                    .Descendants(PresentationNs + "BeginStoryboard")
                    .Any(e => string.Equals((string)e.Attribute("Storyboard"), "{StaticResource SpinningIconAnimation}", StringComparison.Ordinal));
                Assert.False(hasSpinStoryboard, $"Regular item icon in {fullPath} must not animate SpinningIconAnimation.");
            }

            // Dedicated Loading spinner must stop its own named storyboard when Visibility exits Visible.
            var dedicatedSpinner = doc
                .Descendants(MaterialIconsNs + "MaterialIcon")
                .Single(icon =>
                    string.Equals((string)icon.Attribute("Kind"), "Loading", StringComparison.Ordinal) &&
                    icon.Descendants(PresentationNs + "DataTrigger")
                        .Any(t => string.Equals((string)t.Attribute("Value"), "Loading...", StringComparison.Ordinal)));

            XElement begin = dedicatedSpinner
                .Descendants(PresentationNs + "BeginStoryboard")
                .Single(e => string.Equals((string)e.Attribute("Storyboard"), "{StaticResource SpinningIconAnimation}", StringComparison.Ordinal));
            string name = (string)begin.Attribute(XamlNs + "Name");
            Assert.False(string.IsNullOrWhiteSpace(name));

            bool hasStop = dedicatedSpinner
                .Descendants(PresentationNs + "StopStoryboard")
                .Any(s => string.Equals((string)s.Attribute("BeginStoryboardName"), name, StringComparison.Ordinal));
            Assert.True(hasStop, $"Dedicated spinner in {fullPath} must StopStoryboard '{name}' on exit.");
        }

        [Fact]
        public void TreeDedicatedSpinnerCollapsesAndRegularIconStaysUprightOnRecycle()
        {
            RunOnStaThread(() =>
            {
                var (regularIcon, spinnerIcon) = BuildIconsFromTemplate(
                    Path.Combine(GetAppRoot(), "Views", "Controls", "Explorer", "FileExplorerControl.xaml"),
                    bindingPath: "Name");

                AssertIconsSwitchCleanlyOnRecycle(
                    regularIcon,
                    spinnerIcon,
                    loadingContext: new DummyTreeNode("Loading..."),
                    regularContext: new DummyTreeNode("ahri_skin01.wem"));
            });
        }

        [Fact]
        public void FileGridDedicatedSpinnerCollapsesAndRegularIconStaysUprightOnRecycle()
        {
            RunOnStaThread(() =>
            {
                var (regularIcon, spinnerIcon) = BuildIconsFromTemplate(
                    Path.Combine(GetAppRoot(), "Themes", "Templates", "FileGridTemplate.xaml"),
                    bindingPath: "Node.Name");

                AssertIconsSwitchCleanlyOnRecycle(
                    regularIcon,
                    spinnerIcon,
                    loadingContext: new DummyGridItem("Loading..."),
                    regularContext: new DummyGridItem("lux_skin07.tex"));
            });
        }

        private static void AssertIconsSwitchCleanlyOnRecycle(
            MaterialIcon regularIcon,
            MaterialIcon spinnerIcon,
            object loadingContext,
            object regularContext)
        {
            var grid = new Grid { DataContext = loadingContext };
            grid.Children.Add(regularIcon);
            grid.Children.Add(spinnerIcon);

            var host = new Window
            {
                Width = 40,
                Height = 40,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Opacity = 0,
                Content = grid
            };

            try
            {
                host.Show();
                PumpDispatcher(60);

                Assert.Equal(Visibility.Collapsed, regularIcon.Visibility);
                Assert.Equal(Visibility.Visible, spinnerIcon.Visibility);
                Assert.True(regularIcon.RenderTransform == null || regularIcon.RenderTransform == Transform.Identity);

                var spinnerTransform = Assert.IsType<RotateTransform>(spinnerIcon.RenderTransform);
                Assert.True(spinnerTransform.Angle > 0d, $"Expected loading spinner to rotate, got Angle={spinnerTransform.Angle}.");

                // Simulate VirtualizingStackPanel recycling the container onto a real file/folder node.
                grid.DataContext = regularContext;
                PumpDispatcher(60);

                Assert.Equal(Visibility.Visible, regularIcon.Visibility);
                Assert.Equal(Visibility.Collapsed, spinnerIcon.Visibility);
                Assert.True(regularIcon.RenderTransform == null || regularIcon.RenderTransform == Transform.Identity);
                Assert.Equal(0d, spinnerTransform.Angle, 3);
            }
            finally
            {
                host.Close();
            }
        }

        private static (MaterialIcon Regular, MaterialIcon Spinner) BuildIconsFromTemplate(string xamlPath, string bindingPath)
        {
            XDocument animationsDoc = XDocument.Load(Path.Combine(GetAppRoot(), "Themes", "Animations.xaml"));
            XElement spinStoryboard = animationsDoc
                .Descendants(PresentationNs + "Storyboard")
                .Single(e => string.Equals((string)e.Attribute(XamlNs + "Key"), "SpinningIconAnimation", StringComparison.Ordinal));
            XElement spinningStyle = animationsDoc
                .Descendants(PresentationNs + "Style")
                .Single(e => string.Equals((string)e.Attribute(XamlNs + "Key"), "SpinningIconStyle", StringComparison.Ordinal));

            XDocument doc = XDocument.Load(xamlPath);
            XElement spinnerElement = doc
                .Descendants(MaterialIconsNs + "MaterialIcon")
                .Single(icon =>
                    string.Equals((string)icon.Attribute("Kind"), "Loading", StringComparison.Ordinal) &&
                    icon.Descendants(PresentationNs + "DataTrigger")
                        .Any(t => string.Equals((string)t.Attribute("Value"), "Loading...", StringComparison.Ordinal)));
            XElement spinnerStyleElement = spinnerElement.Descendants(PresentationNs + "Style").Single();

            string dictXaml = $@"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                    xmlns:materialIcons=""clr-namespace:Material.Icons.WPF;assembly=Material.Icons.WPF"">
    {spinStoryboard}
    <Style x:Key=""RegularIconStyle"" TargetType=""materialIcons:MaterialIcon"">
        <Setter Property=""Visibility"" Value=""Visible""/>
        <Style.Triggers>
            <DataTrigger Binding=""{{Binding {bindingPath}}}"" Value=""Loading..."">
                <Setter Property=""Visibility"" Value=""Collapsed""/>
            </DataTrigger>
        </Style.Triggers>
    </Style>
    {WithKey(spinnerStyleElement, "SpinnerIconStyle")}
</ResourceDictionary>";

            var dict = (ResourceDictionary)XamlReader.Parse(dictXaml);
            var regular = new MaterialIcon { Style = (Style)dict["RegularIconStyle"] };
            var spinner = new MaterialIcon
            {
                Kind = Material.Icons.MaterialIconKind.Loading,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0),
                Style = (Style)dict["SpinnerIconStyle"]
            };
            return (regular, spinner);
        }

        private static string WithKey(XElement styleElement, string key)
        {
            var clone = new XElement(styleElement);
            clone.SetAttributeValue(XamlNs + "Key", key);
            return clone.ToString();
        }

        private static string GetAppRoot()
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(repositoryRoot, "AssetsManager");
        }

        private static void PumpDispatcher(int durationMs)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(durationMs)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void RunOnStaThread(Action action)
        {
            Exception captured = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (captured != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
            }
        }

        private sealed class DummyTreeNode
        {
            public DummyTreeNode(string name) => Name = name;
            public string Name { get; }
        }

        private sealed class DummyGridItem
        {
            public DummyGridItem(string name) => Node = new DummyTreeNode(name);
            public DummyTreeNode Node { get; }
        }
    }
}
