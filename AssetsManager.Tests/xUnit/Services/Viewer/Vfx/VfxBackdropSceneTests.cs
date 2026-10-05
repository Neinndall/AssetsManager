using System.Numerics;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxBackdropSceneTests
    {
        [Fact]
        public void BackdropCopyKeepsSourceSceneAndMutableActorStateIndependent()
        {
            var original = new StudioSceneActor(new StudioSkinItem { OwnerName = "Aatrox", BinPath = @"C:\project\skin0.bin" })
            {
                PositionX = 12,
                RotationY = 25,
                PlacementCustomized = true,
                PlacedOnKey = "old-map",
                SelectedCharacterFormPathHash = 42,
                SelectedAnimationOwnerPathHash = 77,
                IsPlaybackPaused = true,
                ScaleMultiplier = 2
            };
            original.EnabledGameStates.Add("Transformed");
            original.SubmeshOverrides[9] = false;
            var source = new StudioWorkspaceTab { Key = "skin:aatrox", Kind = StudioWorkspaceTabKind.Skin, CharacterEffectsEnabled = true, MapEffectsEnabled = false, ShadersEnabled = true };
            source.Actors.Add(original);
            source.FocusedActor = original;

            StudioWorkspaceTab copy = source.CopyForBackdrop("copy", "map11/base_srx", "base_srx");
            StudioSceneActor actor = copy.FocusedActor;
            Assert.NotSame(original, actor);
            Assert.Same(original.Skin, actor.Skin);
            Assert.Equal(12d, actor.PositionX);
            Assert.Equal(25d, actor.RotationY);
            Assert.Equal(original.SelectedCharacterFormPathHash, actor.SelectedCharacterFormPathHash);
            Assert.Equal(original.SelectedAnimationOwnerPathHash, actor.SelectedAnimationOwnerPathHash);
            Assert.True(actor.IsPlaybackPaused);
            Assert.Equal(2d, actor.ScaleMultiplier);
            Assert.False(actor.PlacementCustomized);
            Assert.Null(actor.PlacedOnKey);
            Assert.True(copy.CharacterEffectsEnabled);
            Assert.False(copy.MapEffectsEnabled);
            Assert.True(copy.ShadersEnabled);
            Assert.True(copy.CharacterBackdropEnabled);
            Assert.Null(source.CharacterBackdropKey);
            Assert.False(source.CharacterBackdropEnabled);
            Assert.Same(original, source.FocusedActor);
            Assert.Contains("base_srx", copy.Title);
            actor.EnabledGameStates.Clear();
            actor.SubmeshOverrides[9] = true;
            actor.PositionX = 30;
            copy.CharacterEffectsEnabled = false;
            copy.MapEffectsEnabled = true;
            Assert.Contains("Transformed", original.EnabledGameStates);
            Assert.False(original.SubmeshOverrides[9]);
            Assert.Equal(12d, original.PositionX);
            Assert.True(source.CharacterEffectsEnabled);
            Assert.False(source.MapEffectsEnabled);
        }

        [Fact]
        public void MultiActorBackdropPreservesFocusAndRelativePlacement()
        {
            var skin = new StudioSkinItem { BinPath = @"C:\project\skin0.bin" };
            var source = new StudioWorkspaceTab { Key = "source", Kind = StudioWorkspaceTabKind.Skin };
            var first = new StudioSceneActor(skin) { PositionX = 10 };
            var second = new StudioSceneActor(skin) { PositionX = 110, IsVisible = false };
            source.Actors.Add(first);
            source.Actors.Add(second);
            source.FocusedActor = second;
            var copy = source.CopyForBackdrop("copy", "map11/base_srx", "base_srx");
            Assert.Equal(2, copy.Actors.Count);
            Assert.Same(copy.Actors[1], copy.FocusedActor);
            Assert.Equal(100d, copy.Actors[1].PositionX - copy.Actors[0].PositionX);
            Assert.False(copy.Actors[1].IsVisible);
            Assert.Equal("source", copy.BackdropSourceKey);
            var next = copy.CopyForBackdrop("next", "map30/base", "base");
            Assert.Equal("source", next.BackdropSourceKey);
        }

        [Theory]
        [InlineData(false, 45d)]
        [InlineData(true, 500d)]
        public void BackdropCameraFollowsSubjectWithoutChangingFramingOrSource(bool orthographic, double span)
        {
            var state = new StudioWorkspaceCameraState(new Point3D(20, 220, 500),
                new Vector3D(-10, -100, -500), new Vector3D(0, 1, 0), orthographic, span,
                StudioCameraPreset.Orbit);
            var source = new StudioWorkspaceTab { Key = "source", Kind = StudioWorkspaceTabKind.Skin, CameraState = state };
            var actor = new StudioSceneActor(new StudioSkinItem { BinPath = @"C:\project\skin0.bin" });
            source.Actors.Add(actor);
            source.FocusedActor = actor;
            var copy = source.CopyForBackdrop("copy", "map11/base_srx", "base_srx");
            Assert.True(copy.PreserveBackdropFraming);
            Assert.Equal(state, copy.CameraState);
            var displacement = new Vector3(-9800, -82, 4397);
            copy.CameraState = copy.CameraState.Translate(displacement);
            Assert.Equal(new Point3D(-9780, 138, 4897), copy.CameraState.Position);
            Assert.Equal(state.LookDirection, copy.CameraState.LookDirection);
            Assert.Equal(state.UpDirection, copy.CameraState.UpDirection);
            Assert.Equal(state.ProjectionSpan, copy.CameraState.ProjectionSpan);
            Assert.Equal(state.Orthographic, copy.CameraState.Orthographic);
            Assert.Equal(state, source.CameraState);
            Assert.Equal(new Point3D(20, 220, 500), source.CameraState.Position);
            Matrix4x4 projection = orthographic
                ? Matrix4x4.CreateOrthographic((float)span, (float)span, 1f, 30000f)
                : Matrix4x4.CreatePerspectiveFieldOfView((float)(span * System.Math.PI / 180d), 1f, 1f, 30000f);
            Vector4 subject = new(12, 0, 0, 1);
            Vector3 eye = new(20, 220, 500);
            Vector3 look = new(-10, -100, -500);
            Vector4 before = Vector4.Transform(subject, Matrix4x4.CreateLookAt(eye, eye + look, Vector3.UnitY) * projection);
            Vector4 after = Vector4.Transform(subject + new Vector4(displacement, 0),
                Matrix4x4.CreateLookAt(eye + displacement, eye + displacement + look, Vector3.UnitY) * projection);
            Assert.InRange(System.Math.Abs(before.X / before.W - after.X / after.W), 0f, 0.0001f);
            Assert.InRange(System.Math.Abs(before.Y / before.W - after.Y / after.W), 0f, 0.0001f);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void BackdropKeepsChampionAndMapEffectsIndependent(bool characterEffects, bool mapEffects)
        {
            var model = new StudioModel
            {
                CharacterEffectsEnabled = characterEffects,
                MapParticlesVisible = mapEffects
            };
            Assert.Equal(characterEffects, model.CharacterEffectsEnabled);
            Assert.Equal(mapEffects, model.MapParticlesVisible);
            var source = new StudioWorkspaceTab
            {
                Key = "source",
                Kind = StudioWorkspaceTabKind.Skin,
                CharacterEffectsEnabled = characterEffects,
                MapEffectsEnabled = mapEffects
            };
            var actor = new StudioSceneActor(new StudioSkinItem { BinPath = @"C:\project\skin0.bin" });
            source.Actors.Add(actor);
            source.FocusedActor = actor;
            var copy = source.CopyForBackdrop("copy", "map11/base_srx", "base_srx");
            Assert.Equal(characterEffects, copy.CharacterEffectsEnabled);
            Assert.Equal(mapEffects, copy.MapEffectsEnabled);
            copy.CharacterEffectsEnabled = !characterEffects;
            copy.MapEffectsEnabled = !mapEffects;
            Assert.Equal(characterEffects, source.CharacterEffectsEnabled);
            Assert.Equal(mapEffects, source.MapEffectsEnabled);
        }
    }
}
