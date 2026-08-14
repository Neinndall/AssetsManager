using System.Collections.Generic;
using System.Linq;

namespace AssetsManager.Views.Models.Viewer
{
    public enum VfxCompatibilityLevel
    {
        Ready,
        ContextRequired,
        Approximate,
        Limited
    }

    public enum VfxCompatibilitySeverity
    {
        Context,
        Approximation,
        Unsupported
    }

    public sealed record VfxCompatibilityIssue(
        string Code,
        string EmitterName,
        string Title,
        string Detail,
        VfxCompatibilitySeverity Severity)
    {
        public string SeverityText => Severity switch
        {
            VfxCompatibilitySeverity.Context => "CONTEXT",
            VfxCompatibilitySeverity.Approximation => "APPROXIMATION",
            _ => "UNSUPPORTED"
        };

        public string SourceText => string.IsNullOrWhiteSpace(EmitterName) ? "VFX SYSTEM" : EmitterName;
    }

    public sealed record VfxCompatibilityReport(
        VfxCompatibilityLevel Level,
        IReadOnlyList<VfxCompatibilityIssue> Issues)
    {
        public int ContextCount => Issues.Count(issue => issue.Severity == VfxCompatibilitySeverity.Context);
        public int ApproximationCount => Issues.Count(issue => issue.Severity == VfxCompatibilitySeverity.Approximation);
        public int UnsupportedCount => Issues.Count(issue => issue.Severity == VfxCompatibilitySeverity.Unsupported);

        public string StatusText => Level switch
        {
            VfxCompatibilityLevel.Ready => "READY",
            VfxCompatibilityLevel.ContextRequired => "CONTEXT",
            VfxCompatibilityLevel.Approximate => "APPROXIMATE",
            _ => "LIMITED"
        };
    }
}
