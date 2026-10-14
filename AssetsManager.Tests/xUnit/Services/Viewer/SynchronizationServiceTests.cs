using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer;

public sealed class SynchronizationServiceTests
{
    [Fact]
    public void VisibilityMatchesNamesAndDoesNotReenterThroughPartEvents()
    {
        var synchronization = new SynchronizationService();
        var first = Model("Body");
        var second = Model("BODY");
        var unrelated = Model("Sword");
        var models = new[] { first, second, unrelated };
        int events = 0;
        foreach (SceneModel model in models)
        {
            ModelPart part = model.Parts[0];
            part.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ModelPart.IsVisible)) return;
                events++;
                synchronization.SynchronizeParts(part, models, textures: false);
            };
        }
        first.Parts[0].IsVisible = false;
        Assert.False(second.Parts[0].IsVisible);
        Assert.True(unrelated.Parts[0].IsVisible);
        Assert.Equal(2, events);
    }

    [Fact]
    public void TextureSynchronizationPreservesVariantBitmapsAndOriginalSelectionSlot()
    {
        var synchronization = new SynchronizationService();
        var first = Model("Body", "base", "extra");
        var second = Model("body", "other", "base.dds");
        var third = Model("body", "chroma", "chroma_extra");
        var models = new[] { first, second, third };
        foreach (SceneModel model in models)
        {
            ModelPart part = model.Parts[0];
            part.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModelPart.SelectedTextureName))
                    synchronization.SynchronizeParts(part, models, textures: true);
            };
        }
        first.Parts[0].SelectedTextureName = "base";
        Assert.Equal("base.dds", second.Parts[0].SelectedTextureName);
        Assert.Equal("chroma", third.Parts[0].SelectedTextureName);
        Assert.Empty(third.Parts[0].AllTextures);
        first.Parts[0].SelectedTextureName = "extra";
        Assert.Equal("chroma_extra", third.Parts[0].SelectedTextureName);
    }

    [Fact]
    public void MissingTextureSlotLeavesTargetSelectionUntouched()
    {
        var first = Model("Body", "base", "extra");
        var second = Model("body", "chroma");
        first.Parts[0].SelectedTextureName = "extra";
        second.Parts[0].SelectedTextureName = "chroma";
        new SynchronizationService().SynchronizeParts(first.Parts[0], new[] { second }, textures: true);
        Assert.Equal("chroma", second.Parts[0].SelectedTextureName);
    }

    [Fact]
    public void SceneSettingsAreIndependentAndReactToActorCount()
    {
        var first = new StudioWorkspaceTab { Kind = StudioWorkspaceTabKind.Skin };
        var second = new StudioWorkspaceTab { Kind = StudioWorkspaceTabKind.Skin };
        var model = new StudioModel { SelectedWorkspaceTab = first };
        var changed = new List<string>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        first.Actors.Add(Actor());
        Assert.False(model.CanSynchronizeScene);
        first.Actors.Add(Actor());
        Assert.True(model.CanSynchronizeScene);
        first.IsMeshSyncEnabled = true;
        first.IsAnimationPlaybackSyncEnabled = true;
        Assert.Contains(nameof(StudioModel.CanSynchronizeScene), changed);
        Assert.Contains(nameof(StudioWorkspaceTab.IsMeshSyncEnabled), changed);
        model.SelectedWorkspaceTab = second;
        Assert.False(model.CanSynchronizeScene);
        Assert.False(second.IsMeshSyncEnabled);
        Assert.False(second.IsAnimationPlaybackSyncEnabled);
        model.SelectedWorkspaceTab = first;
        first.Actors.RemoveAt(1);
        Assert.False(model.CanSynchronizeScene);
        Assert.True(first.IsMeshSyncEnabled);
        var copy = first.CopyForBackdrop("copy", "map", "Map");
        Assert.True(copy.IsMeshSyncEnabled);
        Assert.True(copy.IsAnimationPlaybackSyncEnabled);
        copy.IsMeshSyncEnabled = false;
        Assert.True(first.IsMeshSyncEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClipImportsRequireMatchingJointHierarchy(bool differentHierarchy)
    {
        var sourceRig = Rig("Root", "Hand", child: true);
        var target = new SceneModel { Skeleton = Rig("ROOT", "HAND", child: !differentHierarchy) };
        var actor = Actor();
        var bundle = new VfxLoadingService.Bundle();
        using var catalog = new VfxClipCatalog();
        bundle.Clips.Add(Clip("Idle", "idle.anm"));
        var item = Assert.Single(catalog.BuildMetadata(bundle, path => path));
        var source = new SynchronizedAnimationSource(item, bundle, "source", SynchronizationService.SkeletonSignature(sourceRig));
        SynchronizationService.ImportClips(actor, target, new[] { source }, Array.Empty<AnimationClipCatalogItem>());
        var merged = SynchronizationService.MergeClips(actor, target, Array.Empty<AnimationClipCatalogItem>());
        Assert.Equal(differentHierarchy ? 0 : 1, merged.Count);
        if (!differentHierarchy)
        {
            Assert.Same(source, merged[0].SharedSource);
            var own = item with { FilePath = "own.anm" };
            Assert.Same(own, Assert.Single(SynchronizationService.MergeClips(actor, target, new[] { own })));
            target.Skeleton = Rig("Root", "Hand", child: false);
            Assert.Empty(SynchronizationService.MergeClips(actor, target, merged));
        }
    }

    [Fact]
    public async Task ImportedClipsHaveIndependentAssetsAndExcludeSourceSkinEvents()
    {
        string directory = Path.Combine(Path.GetTempPath(), "am-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "idle.anm");
            WriteAnimation(path);
            var sourceBundle = new VfxLoadingService.Bundle();
            sourceBundle.Clips.Add(Clip("Idle", path) with
            {
                Events = new AnimationClipEventDefinition[]
                {
                    new AnimationJointSnapEventDefinition(1, 0, -1, Fnv1a.HashLower("Root"), Fnv1a.HashLower("Hand"), Vector3.UnitX)
                }
            });
            using var firstCatalog = new VfxClipCatalog();
            using var secondCatalog = new VfxClipCatalog();
            using var loading = new VfxLoadingService();
            var metadata = Assert.Single(firstCatalog.BuildMetadata(sourceBundle, p => p));
            var actor = Actor();
            var model = new SceneModel { Skeleton = Rig("Root", "Hand", true) };
            var source = new SynchronizedAnimationSource(metadata, sourceBundle, directory,
                SynchronizationService.SkeletonSignature(model.Skeleton));
            SynchronizationService.ImportClips(actor, model, new[] { source }, Array.Empty<AnimationClipCatalogItem>());
            var imported = Assert.Single(SynchronizationService.MergeClips(actor, model, Array.Empty<AnimationClipCatalogItem>()));
            var own = await SynchronizationService.PrepareClipAsync(metadata, firstCatalog, sourceBundle, loading, directory, null, CancellationToken.None);
            var shared = await SynchronizationService.PrepareClipAsync(imported, secondCatalog, new VfxLoadingService.Bundle(), loading, "unused", null, CancellationToken.None);
            Assert.NotNull(own.AnimationAsset);
            Assert.NotNull(shared.AnimationAsset);
            Assert.NotSame(own.AnimationAsset, shared.AnimationAsset);
            Assert.NotEmpty(own.TimedCues);
            Assert.Empty(shared.TimedCues);
            Assert.Null(shared.Composition);
            firstCatalog.Dispose();
            Assert.False(shared.AnimationAsset.IsDisposed);
            var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
            shared.AnimationAsset.Evaluate(0.5f, pose);
            Assert.NotEmpty(pose);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public Task BackgroundTransportFollowsSeekPauseAndSceneClockWithoutWrappingAtClipEnd() => RunOnDispatcher(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "am-sync-clock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "idle.anm");
            WriteAnimation(path);
            var bundle = new VfxLoadingService.Bundle();
            bundle.Clips.Add(Clip("Idle", path));
            using var loading = new VfxLoadingService();
            using var session = new VfxRenderSession(null, loading);
            var catalog = new VfxClipCatalog();
            var actor = Actor();
            var runtime = new StudioSceneActorRuntime(loading, bundle, null, new SceneModel(), "model.skn",
                new AnimationService(), session, catalog, directory);
            try
            {
                var selected = Assert.Single(runtime.OwnClips(actor));
                Assert.False(await runtime.PlaySynchronizedClipAsync(selected, actor, null, () => false));
                Assert.Null(runtime.Clip);
                Assert.True(await runtime.PlaySynchronizedClipAsync(selected, actor, null, () => true));
                runtime.Seek(0.75, loop: false);
                runtime.Advance(1f, false, 2f, actor, transportTime: 0.75);
                Assert.Equal(0.75, session.PlaybackTime, 5);
                runtime.Advance(0.1f, true, 2f, actor, transportTime: 0.85);
                Assert.Equal(0.85, session.PlaybackTime, 5);
                runtime.Advance(1f, true, 1f, actor, transportTime: 10d);
                Assert.Equal(runtime.LoopDuration, session.PlaybackTime, 5);
                Assert.True(await runtime.PlaySynchronizedClipAsync(AnimationClipCatalogItem.CreateBindPoseItem(), actor, null, () => true));
                Assert.True(actor.ShowsBindPose);
                Assert.Null(runtime.Model.CurrentAnimation);
            }
            finally { runtime.ReleaseCpuState(); }
        }
        finally { Directory.Delete(directory, true); }
    });

    private static Task RunOnDispatcher(Func<Task> action)
    {
        var completion = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static StudioSceneActor Actor() => new(new StudioSkinItem { BinPath = "skin.bin", BrowserTitle = "Skin", OwnerName = "Owner" });

    private static SceneModel Model(string part, params string[] textures)
    {
        var model = new SceneModel();
        var mesh = new ModelPart { Name = part };
        mesh.AvailableTextureNames.AddRange(textures);
        model.AddParts(new[] { mesh });
        return model;
    }

    private static RigResource Rig(string root, string hand, bool child)
    {
        var builder = new RigResourceBuilder();
        var joint = builder.CreateJoint(root);
        if (child) joint.CreateJoint(hand); else builder.CreateJoint(hand);
        return builder.Build();
    }

    private static AnimationClipDefinition Clip(string name, string path) => new(
        Fnv1a.HashLower(name), Fnv1a.HashLower("AtomicClipData"), 1f / 30f, 0, -1,
        Array.Empty<AnimationClipEventDefinition>(), name, path, 1);

    private static void WriteAnimation(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("r3d2anmd"));
        writer.Write(3u); writer.Write(0u); writer.Write(1); writer.Write(60); writer.Write(30);
        byte[] name = new byte[32]; Encoding.ASCII.GetBytes("Root").CopyTo(name, 0); writer.Write(name);
        writer.Write(0u);
        for (int frame = 0; frame < 60; frame++)
        {
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write((float)frame); writer.Write(0f); writer.Write(0f);
        }
    }
}
