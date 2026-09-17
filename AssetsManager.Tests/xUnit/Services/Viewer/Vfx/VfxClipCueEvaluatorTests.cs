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

        Assert.Contains(noodles, VfxClipCueEvaluator.HiddenSubmeshesAt(cues, new[] { noodles }, 1d));

        IReadOnlySet<uint> active = VfxClipCueEvaluator.HiddenSubmeshesAt(cues, new[] { noodles }, 3d);
        Assert.DoesNotContain(noodles, active);
        Assert.Contains(chopsticks, active);

        IReadOnlySet<uint> restored = VfxClipCueEvaluator.HiddenSubmeshesAt(cues, new[] { noodles }, 6d);
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

        Assert.Contains(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(cues, Array.Empty<uint>(), 1.5d));
        Assert.DoesNotContain(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(cues, Array.Empty<uint>(), 2.5d));
        Assert.Contains(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(cues, Array.Empty<uint>(), 3.5d));
        Assert.DoesNotContain(mesh, VfxClipCueEvaluator.HiddenSubmeshesAt(cues, Array.Empty<uint>(), 4.5d));
    }
}

