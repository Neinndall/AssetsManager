using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Tests.Diagnostics.Viewer;

internal static class StudioActorBrowserDiagnostic
{
    internal static void Run()
    {
        Exception failure = null;
        string originalDirectory = Environment.CurrentDirectory;
        string scratch = Path.Combine(Path.GetTempPath(), "AssetsManager-ActorBrowser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var thread = new Thread(() =>
        {
            try
            {
                Environment.CurrentDirectory = scratch;
                RunHost();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Serilog.Log.CloseAndFlush();
                Environment.CurrentDirectory = originalDirectory;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(45)), "Studio browser diagnostic timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunHost()
    {
        // The CLI entry assembly owns no application resources. Use the production application's themes.
        typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, typeof(App).Assembly);
        var app = new App();
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var studio = new StudioControl();
        var model = (StudioModel)Field("_model").GetValue(studio);
        var listener = (PropertyChangedEventHandler)Method("OnModelPropertyChanged")
            .CreateDelegate(typeof(PropertyChangedEventHandler), studio);
        // Synthetic identities must not load any real game assets.
        model.PropertyChanged -= listener;
        var browser = new StudioBrowserControl { DataContext = model, Owner = studio };
        Field("StudioBrowserView").SetValue(studio, browser);
        var root = new StudioBrowserFolder("Project", StudioBrowserFolderKind.Root);
        var folder = new StudioBrowserFolder("Evelynn", StudioBrowserFolderKind.Character);
        root.Children.Add(folder);
        model.BrowserRoots.Add(root);
        for (int index = 0; index < 1000; index++)
            folder.Children.Add(new StudioSkinItem { SkinIndex = index, OwnerName = "Evelynn", BinPath = $"missing{index}.bin" });

        var host = new Window
        {
            Content = browser, Width = 350, Height = 550,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0
        };
        try
        {
            host.Show();
            host.UpdateLayout();
            var effect = new VfxSystemDiagnosticItem { Name = "Evelynn_YellowEye", PathHash = 24 };
            model.SelectedSystem = effect;
            model.IsRawSystemsMode = true;
            model.IsPlaying = false;
            model.CurrentTime = 2.6;
            foreach (int index in new[] { 850, 24, 999, 65 })
            {
                var skin = (StudioSkinItem)folder.Children[index];
                model.SelectedSkin = skin;
                root.IsExpanded = false;
                folder.IsExpanded = false;
                Reveal(skin);
                Check(ReferenceEquals(browser.BrowserTree.SelectedItem, skin), $"Skin{index} was not selected.");
                Check(root.IsExpanded && folder.IsExpanded, "Ancestors were not expanded.");
                Check(ReferenceEquals(effect, model.SelectedSystem) && model.IsRawSystemsMode &&
                    !model.IsPlaying && model.CurrentTime == 2.6, "Reveal changed the paused System or transport.");
                Console.WriteLine($"PASS: reveal offscreen Skin{index} through collapsed ancestors, preserving paused System.");
            }

            model.SearchQuery = "does-not-match";
            Method("RefreshProjectBrowserSearch").Invoke(studio, null);
            var target = (StudioSkinItem)folder.Children[64];
            model.SelectedSkin = target;
            Reveal(target);
            Check(ReferenceEquals(browser.BrowserTree.SelectedItem, target) && model.SearchQuery == string.Empty,
                "Search hiding the actor was not cleared.");
            Console.WriteLine("PASS: search hiding the selected skin is cleared.");

            model.SelectedSkin = (StudioSkinItem)folder.Children[24];
            Method("RevealSceneActorInBrowser").Invoke(studio, new object[] { model.SelectedSkin });
            target = (StudioSkinItem)folder.Children[65];
            model.SelectedSkin = target;
            Reveal(target);
            Check(ReferenceEquals(browser.BrowserTree.SelectedItem, target), "An outdated reveal selected the wrong actor.");
            Console.WriteLine("PASS: the latest actor wins over an outdated reveal.");

            var tab = new StudioWorkspaceTab { Kind = StudioWorkspaceTabKind.Skin };
            var first = new StudioSceneActor((StudioSkinItem)folder.Children[24]);
            var second = new StudioSceneActor((StudioSkinItem)folder.Children[65]);
            tab.Actors.Add(first);
            tab.Actors.Add(second);
            tab.FocusedActor = first;
            model.SelectedWorkspaceTab = tab;
            model.SelectedSkin = first.Skin;
            Method("FocusSceneActor").Invoke(studio, new object[] { second, false });
            WaitForReveal();
            Check(ReferenceEquals(browser.BrowserTree.SelectedItem, second.Skin), "Changing the focused actor did not reveal its skin.");
            root.IsExpanded = false;
            Method("FocusSceneActor").Invoke(studio, new object[] { second, false });
            WaitForReveal();
            Check(root.IsExpanded && ReferenceEquals(browser.BrowserTree.SelectedItem, second.Skin),
                "Selecting the already focused actor did not reveal its skin again.");
            Console.WriteLine("PASS: actor focus and repeated actor selection reveal the matching skin.");

            void Reveal(StudioSkinItem skin)
            {
                Method("RevealSceneActorInBrowser").Invoke(studio, new object[] { skin });
                WaitForReveal();
            }

            void WaitForReveal()
            {
                var frame = new DispatcherFrame();
                var clock = Stopwatch.StartNew();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    if (!(bool)Field("_isRevealingBrowserSkin").GetValue(studio) || clock.Elapsed.TotalSeconds > 8)
                        frame.Continue = false;
                };
                timer.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }
                Check(clock.Elapsed.TotalSeconds < 8, "Revealing a skin timed out.");
            }
        }
        finally { host.Close(); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static FieldInfo Field(string name) => typeof(StudioControl)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static MethodInfo Method(string name) => typeof(StudioControl)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
}
