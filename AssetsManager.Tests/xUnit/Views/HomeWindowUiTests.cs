using System;
using System.ComponentModel;
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

namespace AssetsManager.Tests.xUnit.Views
{
    public class HomeWindowUiTests
    {
        private static readonly XNamespace PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        [Fact]
        public void DashboardTileBottomEdgeRemainsHitTestableWhenLifted()
        {
            RunOnStaThread(() =>
            {
                XDocument doc = XDocument.Load(GetHomeWindowXamlPath());
                XElement tileStyle = doc
                    .Descendants(PresentationNs + "Style")
                    .Single(e => string.Equals((string)e.Attribute(XamlNs + "Key"), "DashboardTileStyle", StringComparison.Ordinal));

                XElement templateRoot = tileStyle
                    .Descendants(PresentationNs + "ControlTemplate")
                    .Single()
                    .Elements()
                    .First();

                Assert.Equal("Grid", templateRoot.Name.LocalName);
                Assert.Equal("Transparent", (string)templateRoot.Attribute("Background"));
                Assert.Null(templateRoot.Element(PresentationNs + "Grid.RenderTransform"));

                string dictXaml = $@"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <SolidColorBrush x:Key=""CardBackground"" Color=""#1A1D24""/>
    <SolidColorBrush x:Key=""BorderColor"" Color=""#2B303A""/>
    <SolidColorBrush x:Key=""HoverColor"" Color=""#252A34""/>
    {tileStyle}
</ResourceDictionary>";

                var dict = (ResourceDictionary)XamlReader.Parse(dictXaml);
                var button = new Button
                {
                    Width = 120,
                    Height = 100,
                    Tag = Brushes.DodgerBlue,
                    Style = (Style)dict["DashboardTileStyle"]
                };

                var host = new Window
                {
                    Width = 160,
                    Height = 140,
                    Left = -10000,
                    Top = -10000,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Opacity = 0,
                    Content = button
                };

                try
                {
                    host.Show();
                    button.ApplyTemplate();
                    button.UpdateLayout();

                    var visualRoot = Assert.IsType<Grid>(button.Template.FindName("RootGrid", button));
                    Assert.IsType<TranslateTransform>(visualRoot.RenderTransform);
                    visualRoot.RenderTransform = new TranslateTransform(0, -4);
                    button.UpdateLayout();

                    // Bottom 1-4px edge must still hit-test inside the button so hover never oscillates.
                    HitTestResult bottomEdgeHit = VisualTreeHelper.HitTest(button, new Point(60, 99));
                    Assert.NotNull(bottomEdgeHit);
                }
                finally
                {
                    host.Close();
                }
            });
        }

        [Fact]
        public void ConfigIncompletePulseStopsWhenConfigurationCompletes()
        {
            RunOnStaThread(() =>
            {
                XDocument doc = XDocument.Load(GetHomeWindowXamlPath());
                XElement pulseTrigger = doc
                    .Descendants(PresentationNs + "DataTrigger")
                    .Single(e =>
                        string.Equals((string)e.Attribute("Binding"), "{Binding IsConfigIncomplete}", StringComparison.Ordinal) &&
                        e.Descendants(PresentationNs + "DoubleAnimation").Any());

                XElement statusStyle = pulseTrigger.Parent.Parent;
                var clone = new XElement(statusStyle);
                clone.SetAttributeValue(XamlNs + "Key", "StatusIconStyle");

                string dictXaml = $@"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                    xmlns:materialIcons=""clr-namespace:Material.Icons.WPF;assembly=Material.Icons.WPF"">
    <SolidColorBrush x:Key=""AccentGreen"" Color=""#2ECC71""/>
    <SolidColorBrush x:Key=""AccentOrange"" Color=""#E67E22""/>
    {clone}
</ResourceDictionary>";

                var dict = (ResourceDictionary)XamlReader.Parse(dictXaml);
                var statusVm = new ConfigStatusStub { IsConfigIncomplete = true };
                var icon = new MaterialIcon
                {
                    DataContext = statusVm,
                    Style = (Style)dict["StatusIconStyle"]
                };

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
                    Content = icon
                };

                try
                {
                    host.Show();
                    PumpDispatcher(120);

                    Assert.True(icon.Opacity < 0.99, $"Expected incomplete-config icon to pulse, got Opacity={icon.Opacity}.");

                    statusVm.IsConfigIncomplete = false;
                    PumpDispatcher(120);

                    Assert.Equal(1.0, icon.Opacity, 3);
                }
                finally
                {
                    host.Close();
                }
            });
        }

        private static string GetHomeWindowXamlPath()
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(repositoryRoot, "AssetsManager", "Views", "HomeWindow.xaml");
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

        private sealed class ConfigStatusStub : INotifyPropertyChanged
        {
            private bool _isConfigIncomplete;

            public bool IsConfigIncomplete
            {
                get => _isConfigIncomplete;
                set
                {
                    if (_isConfigIncomplete != value)
                    {
                        _isConfigIncomplete = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConfigIncomplete)));
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
