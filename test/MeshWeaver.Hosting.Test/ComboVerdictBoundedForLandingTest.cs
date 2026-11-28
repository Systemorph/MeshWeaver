using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>MeshWeaver#4413: a Red verdict carrying the gate's unbounded diagnostics was refused by the
/// landing route with HTTP 413, so the finding was never recorded. The verdict is bounded for landing.</summary>
public sealed class ComboVerdictBoundedForLandingTest
{
    [Fact]
    public void AnOversizedVerdict_IsCutAndSaysSo_KeepingKindAndModules()
    {
        var verdict = new ComboVerification
        {
            CandidateTag = "1.0.0",
            Verdict = ComboVerdictKind.Red,
            Caveats = [new string('c', 50_000)],
            Modules =
            [
                new ModuleVerification
                {
                    ModuleId = "Widget",
                    Outcome = ModuleVerificationOutcome.Failed,
                    Failures = [.. Enumerable.Range(0, 5_000).Select(i => $"Widget/T{i}: compile Failed — " + new string('x', 10_000))],
                },
            ],
        };

        var bounded = verdict.BoundedForLanding();

        Assert.Equal(ComboVerdictKind.Red, bounded.Verdict);
        var module = Assert.Single(bounded.Modules);
        Assert.Equal(ModuleVerificationOutcome.Failed, module.Outcome);
        Assert.Equal(ComboVerification.MaxLinesPerList + 1, module.Failures.Count);
        Assert.Contains("more line(s) cut", module.Failures[^1]);
        Assert.All(module.Failures.Take(ComboVerification.MaxLinesPerList),
            f => Assert.True(f.Length < ComboVerification.MaxLineLength + 100));
        Assert.Contains("more characters cut", Assert.Single(bounded.Caveats));
        Assert.True(System.Text.Json.JsonSerializer.Serialize(bounded).Length < 400_000);
    }

    [Fact]
    public void ASmallVerdict_IsReturnedUnchanged()
    {
        var verdict = new ComboVerification
        {
            CandidateTag = "1.0.0",
            Verdict = ComboVerdictKind.Green,
            Caveats = ["fine"],
            Modules = [new ModuleVerification { ModuleId = "W", Outcome = ModuleVerificationOutcome.Passed }],
        };
        Assert.Equal(verdict.Caveats, verdict.BoundedForLanding().Caveats);
        Assert.Equal(verdict.Modules[0].Failures, verdict.BoundedForLanding().Modules[0].Failures);
    }
}
