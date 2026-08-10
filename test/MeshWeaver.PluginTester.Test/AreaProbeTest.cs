using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The Tests-area verdict pipeline, driven with synthetic frames through the
/// <see cref="AreaProbe.ClassifyTestsFrames"/> seam.
///
/// <para>The one behaviour these tests exist to pin: <b>an "Area not found" frame is transient,
/// never terminal</b>. Right after a (re)compile the instance hub re-registers its layout, so the
/// sync stream legitimately serves a frame in which the type's custom areas do not exist yet.
/// Latching that frame as a verdict turned the re-registration window into
/// <c>No renderer is registered for area `Tests` on hub `Store`</c> — a gate failure that fired
/// only on loaded CI runners (16 straight local runs, macOS and Linux, could not reproduce it)
/// and redded three unrelated core PRs in one day.</para>
/// </summary>
public class AreaProbeTest
{
    private static JsonElement Frame(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static readonly JsonElement NotFound = Frame(
        """{"areas":{"Tests":"**Area not found**\n\nNo renderer is registered for area `Tests` on hub `Store`."}}""");

    private static readonly JsonElement GreenTable = Frame(
        """{"areas":{"Tests":"✅ ManifestPaths_AreTopLevelIndexJsonOnly\n✅ PriceLabel_ZeroReadsFree\n2/2 passed"}}""");

    private static readonly JsonElement RedTable = Frame(
        """{"areas":{"Tests":"✅ First_Passes\n❌ Second_Fails: expected 42\n1/2 passed"}}""");

    /// <summary>
    /// The regression pin: a not-found frame followed by the real table must be GREEN. Before the
    /// fix, Take(1) latched the not-found frame and the run failed without the tests ever running.
    /// </summary>
    [Fact]
    public async Task NotFoundFrame_ThenGreenTable_IsPassed()
    {
        var frames = new[] { NotFound, GreenTable }.ToObservable();

        var verdict = await AreaProbe.ClassifyTestsFrames(frames, TimeSpan.FromSeconds(5))
            .FirstAsync().ToTask();

        Assert.Equal(CheckOutcome.Passed, verdict.Outcome);
        Assert.Equal("2/2 passed", verdict.Detail);
    }

    /// <summary>
    /// The backstop stays a real gate: an area that NEVER appears is red — and the verdict names
    /// the last transient state instead of the generic "no verdict", so a genuinely missing Tests
    /// area is distinguishable from a suite that hung.
    /// </summary>
    [Fact]
    public async Task OnlyNotFoundFrames_TimesOut_ReportingTheLastTransientState()
    {
        var frames = Observable.Return(NotFound).Concat(Observable.Never<JsonElement>());

        var verdict = await AreaProbe.ClassifyTestsFrames(frames, TimeSpan.FromMilliseconds(300))
            .FirstAsync().ToTask();

        Assert.Equal(CheckOutcome.Failed, verdict.Outcome);
        Assert.Contains("never became available", verdict.Detail);
        Assert.Contains("Area not found", verdict.Detail);
    }

    /// <summary>A red row still fails immediately — transience applies to not-found only.</summary>
    [Fact]
    public async Task RedRow_FailsImmediately()
    {
        var frames = Observable.Return(RedTable).Concat(Observable.Never<JsonElement>());

        var verdict = await AreaProbe.ClassifyTestsFrames(frames, TimeSpan.FromSeconds(5))
            .FirstAsync().ToTask();

        Assert.Equal(CheckOutcome.Failed, verdict.Outcome);
        Assert.Contains("Second_Fails", verdict.Detail);
    }

    /// <summary>An empty stream (no frames at all) still reports the generic no-verdict red.</summary>
    [Fact]
    public async Task NoFrames_TimesOut_WithTheGenericNoVerdict()
    {
        var verdict = await AreaProbe.ClassifyTestsFrames(
                Observable.Never<JsonElement>(), TimeSpan.FromMilliseconds(300))
            .FirstAsync().ToTask();

        Assert.Equal(CheckOutcome.Failed, verdict.Outcome);
        Assert.Contains("no verdict", verdict.Detail);
    }
}
