using System;
using System.Collections.Generic;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public class VfxClipCueEvaluatorTests
{
    [Theory]
    [InlineData(0.5, 0.5333333611488342, "Wing", "R_Wing")]
    [InlineData(6.133333683013916, 6.1666669845581055, "Wing", "R_Wing")]
    [InlineData(1.0, 1.02, "Sword", "Empowered_Sword")]
    [InlineData(1.0, 1.02, "Empowered_Sword", "Sword")]
    public void PreviewReplacesTheWholeGroupWithoutAnEmptyFrame(double start, double replacement, string first, string second)
    {
        uint outgoing = Fnv1a.HashLower(first), incoming = Fnv1a.HashLower(second);
        uint cloak = Fnv1a.HashLower("Cloak");
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(start, 8d, Array.Empty<uint>(), new[] { outgoing, cloak }, 1d / 30d),
            new AnimationSubmeshVisibilityCue(replacement, 7d, new[] { incoming }, Array.Empty<uint>(), 1d / 30d)
        };
        var timeline = VfxClipCueEvaluator.BuildVisibilityTimeline(cues, new[] { incoming }, new[] { first, second, "Cloak" });
        double middle = (start + replacement) / 2d;
        foreach (double time in new[] { replacement, middle, start - 0.01d, middle, replacement })
        {
            var hidden = VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, time);
            Assert.Equal(time >= replacement, hidden.Contains(outgoing));
            Assert.Equal(time >= replacement, hidden.Contains(cloak));
            Assert.Equal(time < replacement, hidden.Contains(incoming));
        }
        Assert.Contains(incoming, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 7.1d));
        Assert.DoesNotContain(outgoing, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 8.1d));
        Assert.DoesNotContain(outgoing, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, VfxClipCueEvaluator.FoldedTime(9d, 9d)));
        Assert.Equal(start, cues[0].AtSeconds);
    }

    [Theory]
    [InlineData("Other", 1.02, 1d / 30d)]
    [InlineData("R_Wing", 1.1, 1d / 30d)]
    [InlineData("R_Wing", 1.02, 0d)]
    public void UnrelatedLongOrUntimedChangesKeepTheirAuthoredTimes(string second, double replacement, double frame)
    {
        uint outgoing = Fnv1a.HashLower("Wing"), incoming = Fnv1a.HashLower(second);
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(1d, null, Array.Empty<uint>(), new[] { outgoing }, frame),
            new AnimationSubmeshVisibilityCue(replacement, null, new[] { incoming }, Array.Empty<uint>(), frame)
        };
        var timeline = VfxClipCueEvaluator.BuildVisibilityTimeline(cues, new[] { incoming }, new[] { "Wing", second });
        Assert.Contains(outgoing, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 1.01d));
    }

    [Fact]
    public void ExplicitAuthoredModeAndAmbiguousVariantsKeepTheGap()
    {
        uint wing = Fnv1a.HashLower("Wing"), first = Fnv1a.HashLower("R_Wing"), second = Fnv1a.HashLower("Other_Wing");
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(1d, null, Array.Empty<uint>(), new[] { wing }, 1d / 30d),
            new AnimationSubmeshVisibilityCue(1.02d, null, new[] { first, second }, Array.Empty<uint>(), 1d / 30d)
        };
        var raw = VfxClipCueEvaluator.BuildVisibilityTimeline(cues, new[] { first, second });
        var ambiguous = VfxClipCueEvaluator.BuildVisibilityTimeline(cues, new[] { first, second }, new[] { "Wing", "R_Wing", "Other_Wing" });
        Assert.Contains(wing, VfxClipCueEvaluator.HiddenSubmeshesAt(raw, 1.01d));
        Assert.Contains(wing, VfxClipCueEvaluator.HiddenSubmeshesAt(ambiguous, 1.01d));
    }

    [Fact]
    public void UnsortedCuesCannotDelayTheIncomingEntryAgainAndReopenTheGap()
    {
        uint wing = Fnv1a.HashLower("Wing"), replacement = Fnv1a.HashLower("R_Wing");
        uint sword = Fnv1a.HashLower("Sword"), nextSword = Fnv1a.HashLower("Empowered_Sword");
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(1.02d, null, new[] { replacement }, new[] { sword }, 1d / 30d),
            new AnimationSubmeshVisibilityCue(1.04d, null, new[] { nextSword }, Array.Empty<uint>(), 1d / 30d),
            new AnimationSubmeshVisibilityCue(1d, null, Array.Empty<uint>(), new[] { wing }, 1d / 30d)
        };
        var timeline = VfxClipCueEvaluator.BuildVisibilityTimeline(cues, new[] { replacement, nextSword },
            new[] { "Wing", "R_Wing", "Sword", "Empowered_Sword" });
        var middle = VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 1.01d);
        Assert.DoesNotContain(wing, middle);
        Assert.DoesNotContain(sword, middle);
        var next = VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 1.03d);
        Assert.Contains(wing, next);
        Assert.DoesNotContain(replacement, next);
        Assert.Contains(sword, next);
    }

    [Fact]
    public void AShortVisibilityPulseIsNotExtendedIntoItsReplacement()
    {
        uint wing = Fnv1a.HashLower("Wing"), replacement = Fnv1a.HashLower("R_Wing");
        AnimationClipTimedCue[] cues =
        {
            new AnimationSubmeshVisibilityCue(1d, 1.01d, Array.Empty<uint>(), new[] { wing }, 1d / 30d),
            new AnimationSubmeshVisibilityCue(1.02d, null, new[] { replacement }, Array.Empty<uint>(), 1d / 30d)
        };
        var timeline = VfxClipCueEvaluator.BuildVisibilityTimeline(cues, new[] { replacement }, new[] { "Wing", "R_Wing" });
        Assert.Contains(wing, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 1.005d));
        Assert.DoesNotContain(wing, VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, 1.015d));
    }

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

