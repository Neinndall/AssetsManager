using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public class VfxClipCueEvaluatorTests
{
    [Fact]
    public void VisibilityCueAppliesAtStartAndRestoresAtEnd()
    {
        const uint noodles = 0x10;
        const uint chopsticks = 0x20;
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(2d, 5d, new[] { noodles }, new[] { chopsticks })
        };
        IReadOnlyList<VfxClipCueEvaluator.VisibilityEntry> timeline =
            VfxClipCueEvaluator.BuildVisibilityTimeline(cues, new[] { noodles });

        Assert.Contains(noodles, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 1d));

        IReadOnlySet<uint> active = VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 3d);
        Assert.DoesNotContain(noodles, active);
        Assert.Contains(chopsticks, active);

        IReadOnlySet<uint> restored = VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 6d);
        Assert.Contains(noodles, restored);
        Assert.DoesNotContain(chopsticks, restored);
    }

    [Fact]
    public void OverlappingVisibilityCuesAreFoldedInTimelineOrder()
    {
        const uint mesh = 0x33;
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(1d, 4d, Array.Empty<uint>(), new[] { mesh }),
            new AnimationSubmeshVisibilityCue(2d, 3d, new[] { mesh }, Array.Empty<uint>())
        };
        IReadOnlyList<VfxClipCueEvaluator.VisibilityEntry> timeline =
            VfxClipCueEvaluator.BuildVisibilityTimeline(cues, Array.Empty<uint>());

        Assert.Contains(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 1.5d));
        Assert.DoesNotContain(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 2.5d));
        Assert.Contains(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 3.5d));
        Assert.DoesNotContain(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 4.5d));
    }

    [Fact]
    public void SameTimeVisibilityChangesPublishOneFinalState()
    {
        const uint first = 0x44;
        const uint second = 0x55;
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(2d, null, Array.Empty<uint>(), new[] { first }),
            new AnimationSubmeshVisibilityCue(2d, null, new[] { first }, new[] { second })
        };

        IReadOnlyList<VfxClipCueEvaluator.VisibilityEntry> timeline =
            VfxClipCueEvaluator.BuildVisibilityTimeline(cues, Array.Empty<uint>());

        Assert.Equal(2, timeline.Count);
        IReadOnlySet<uint> hidden = VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 2d);
        Assert.DoesNotContain(first, hidden);
        Assert.Contains(second, hidden);
    }

    [Fact]
    public void EqualVisibilityEndRestoresBaseAtTheSameAuthoredMoment()
    {
        const uint mesh = 0x65;
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(2d, 2d, Array.Empty<uint>(), new[] { mesh })
        };

        IReadOnlyList<VfxClipCueEvaluator.VisibilityEntry> timeline =
            VfxClipCueEvaluator.BuildVisibilityTimeline(cues, Array.Empty<uint>());

        Assert.Equal(2, timeline.Count);
        Assert.DoesNotContain(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 2d));
    }

    [Fact]
    public void BackwardVisibilityEndIsAppliedBeforeItsStartLikeLtkTimeline()
    {
        const uint mesh = 0x67;
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(2d, 1d, Array.Empty<uint>(), new[] { mesh })
        };

        IReadOnlyList<VfxClipCueEvaluator.VisibilityEntry> timeline =
            VfxClipCueEvaluator.BuildVisibilityTimeline(cues, new[] { mesh });

        Assert.DoesNotContain(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 1.5d));
        Assert.Contains(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 2d));
    }

    [Fact]
    public void ClipEndFoldsVisibilityBackToFirstPassLikePose()
    {
        const uint mesh = 0x66;
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(1d, null, Array.Empty<uint>(), new[] { mesh })
        };
        IReadOnlyList<VfxClipCueEvaluator.VisibilityEntry> timeline =
            VfxClipCueEvaluator.BuildVisibilityTimeline(cues, Array.Empty<uint>());

        double folded = VfxClipCueEvaluator.FoldedTime(3d, 3d);

        Assert.Equal(0d, folded);
        Assert.DoesNotContain(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, folded));
    }
}

