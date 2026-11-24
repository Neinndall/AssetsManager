using System;
using System.IO;
using System.Reflection;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Dialogs;
using AssetsManager.Views.Models.Explorer;
using Serilog;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public class AudioPlaybackCleanupTests
    {
        [Fact]
        public void ClosingAudioTabInDualViewDeactivatesWebViewMedia()
        {
            var (previewService, mediaService, viewModel) = CreatePreviewHarness();
            var imageNode = CreateNode("splash.png", "assets/splash.png", NodeType.VirtualFile);
            var audioNode = CreateNode("ahri_vo.wem", "assets/ahri_vo.wem", NodeType.WemFile);

            viewModel.PinnedFilesManager.PinFile(imageNode);
            viewModel.PinnedFilesManager.PinFile(audioNode);
            SetActiveContentWebView(previewService, audioNode);

            previewService.CloseSlotByCategory(audioNode);

            Assert.Equal(1, mediaService.DeactivateCallCount);
            Assert.Equal(PreviewState.Empty, viewModel.ContentPreviewState);
            Assert.Null(GetPrivateField<FileSystemNodeModel>(previewService, "_currentContentNode"));
        }

        [Fact]
        public void ClosingInactiveTextTabDoesNotInterruptPlayingAudio()
        {
            var (previewService, mediaService, viewModel) = CreatePreviewHarness();
            var audioNode = CreateNode("ahri_vo.wem", "assets/ahri_vo.wem", NodeType.WemFile);
            var jsonNode = CreateNode("data.json", "assets/data.json", NodeType.VirtualFile);

            viewModel.PinnedFilesManager.PinFile(audioNode, isExplicitPin: true);
            viewModel.PinnedFilesManager.PinFile(jsonNode, isExplicitPin: true);
            SetActiveContentWebView(previewService, audioNode);

            previewService.CloseSlotByCategory(jsonNode);

            Assert.Equal(0, mediaService.DeactivateCallCount);
            Assert.Same(audioNode, GetPrivateField<FileSystemNodeModel>(previewService, "_currentContentNode"));
        }

        [Fact]
        public void RemovingPlayingTrackStopsPlaybackAndPreservesPackWhenTracksRemain()
        {
            var settings = new AppSettings();
            var audioService = new AudioPlayerService(settings);
            var model = new AudioPlayerModel(audioService);

            audioService.ActivePackName = "PBE OST";
            audioService.AddToPlaylist("Track A", "file:///a.ogg");
            audioService.AddToPlaylist("Track B", "file:///b.ogg");
            AudioPlaylistItem trackA = audioService.Playlist[0];

            model.CurrentTrackName = trackA.Name;
            model.IsPlaying = true;
            model.CurrentTime = 42;

            bool shouldStop = model.RemoveTrack(trackA);

            Assert.True(shouldStop);
            Assert.False(model.IsPlaying);
            Assert.Equal("No track selected", model.CurrentTrackName);
            Assert.Equal(0, model.CurrentTime);
            Assert.Equal("PBE OST", model.ActivePackName);
            Assert.Single(audioService.Playlist);
        }

        [Fact]
        public void RemovingLastTrackStopsPlaybackAndResetsPackName()
        {
            var settings = new AppSettings();
            var audioService = new AudioPlayerService(settings);
            var model = new AudioPlayerModel(audioService);

            audioService.ActivePackName = "PBE OST";
            audioService.AddToPlaylist("Only Track", "file:///only.ogg");
            AudioPlaylistItem onlyTrack = audioService.Playlist[0];

            model.CurrentTrackName = onlyTrack.Name;
            model.IsPlaying = true;

            bool shouldStop = model.RemoveTrack(onlyTrack);

            Assert.True(shouldStop);
            Assert.False(model.IsPlaying);
            Assert.Equal("No track selected", model.CurrentTrackName);
            Assert.Equal("New Playlist", model.ActivePackName);
            Assert.Empty(audioService.Playlist);
        }

        [Fact]
        public void RemovingNonPlayingTrackKeepsCurrentPlaybackRunning()
        {
            var settings = new AppSettings();
            var audioService = new AudioPlayerService(settings);
            var model = new AudioPlayerModel(audioService);

            audioService.AddToPlaylist("Playing Track", "file:///playing.ogg");
            audioService.AddToPlaylist("Other Track", "file:///other.ogg");
            AudioPlaylistItem otherTrack = audioService.Playlist[1];

            model.CurrentTrackName = "Playing Track";
            model.IsPlaying = true;

            bool shouldStop = model.RemoveTrack(otherTrack);

            Assert.False(shouldStop);
            Assert.True(model.IsPlaying);
            Assert.Equal("Playing Track", model.CurrentTrackName);
            Assert.Single(audioService.Playlist);
        }

        private static FileSystemNodeModel CreateNode(string name, string virtualPath, NodeType type)
        {
            return new FileSystemNodeModel(name)
            {
                VirtualPath = virtualPath,
                Type = type
            };
        }

        private static (ExplorerPreviewService PreviewService, MediaWebViewPreviewService MediaService, FilePreviewerModel ViewModel) CreatePreviewHarness()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"AssetsManager_AudioCleanup_{Guid.NewGuid():N}");
            var logService = new LogService(Log.Logger);
            var directories = new DirectoriesCreator(tempRoot);
            var tempStore = new MediaTempFileStore(directories, logService);
            var mediaService = new MediaWebViewPreviewService(logService, directories, tempStore);
            var previewService = new ExplorerPreviewService(
                logService,
                contentFormatterService: null,
                audioConversionService: null,
                wadContentProvider: null,
                svgParser: null,
                narrativeMetadataService: null,
                diffViewService: null,
                mediaWebViewPreviewService: mediaService);

            var viewModel = new FilePreviewerModel();
            previewService.Initialize(imagePreview: null, webViewContainer: null, textEditor: null, viewModel: viewModel);
            return (previewService, mediaService, viewModel);
        }

        private static void SetActiveContentWebView(ExplorerPreviewService service, FileSystemNodeModel node)
        {
            Type previewerEnum = typeof(ExplorerPreviewService).GetNestedType("Previewer", BindingFlags.NonPublic);
            object webViewValue = Enum.Parse(previewerEnum, "WebView");

            typeof(ExplorerPreviewService)
                .GetField("_currentContentNode", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(service, node);
            typeof(ExplorerPreviewService)
                .GetField("_activeContentPreviewer", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(service, webViewValue);
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            return (T)instance.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(instance);
        }
    }
}
