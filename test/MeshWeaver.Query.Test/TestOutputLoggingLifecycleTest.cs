using System.Collections.Immutable;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Pins the TEST-LOG SINK itself — the thing every CI-only flake investigation depends on and
/// nobody checks, because a dead sink looks exactly like "the code logged nothing".
///
/// <para><b>What broke.</b> <c>XUnitFileLogger.Log</c> drops a record unless
/// <c>XUnitFileOutputHelper.IsInTestMethod()</c> is true, and the only writer of that flag is
/// <c>AutoTestLoggingAttribute.Before</c> → <c>XUnitFileOutputRegistry.GetAnyActiveOutputHelper()</c>.
/// That registry is an <see cref="AsyncLocal{T}"/>, and the registration used to happen inside
/// <c>TestBase.InitializeAsync</c>. <see cref="MonolithMeshTestBase"/> overrides that hook as
/// <c>async</c> — and an AsyncLocal written inside an <c>async</c> method lives in that method's
/// copied <c>ExecutionContext</c> and is discarded when it returns. So the registry was empty by
/// the time xUnit ran <c>Before</c>, <c>SetCurrentTestMethod</c> was never called, and
/// <b>every ILogger record of every Monolith test was silently dropped</b> — at every level, on
/// every platform. The tell is the missing <c>=== TEST START: … ===</c> marker.</para>
///
/// <para><b>What it cost.</b> The Debug channels added to
/// <c>test/MeshWeaver.Query.Test/appsettings.json</c> to diagnose the #993
/// <c>ActivityTrackingHubTest</c> flake produced no output for weeks. #997 repaired one half of
/// that (the appsettings copy race that let MSBuild land the shared Warning-only file instead of
/// the project-local one) and the channels STILL emitted nothing, because the sink downstream of
/// them was closed. Both halves have to hold for a diagnostic to reach CI, so both are pinned:
/// #979/#997 guard the config reaching <c>$(TargetDir)</c> at build time, this guards the sink at
/// run time.</para>
/// </summary>
public class TestOutputLoggingLifecycleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The lifecycle pin: a test class whose <c>InitializeAsync</c> is <c>async</c> (which
    /// <see cref="MonolithMeshTestBase"/>'s is) must still have a LIVE log sink by the time its
    /// body runs. Both assertions were false before the registration moved to the constructor.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public void AsyncInitializeAsync_StillLeavesTheTestLogSinkOpen()
    {
        XUnitFileOutputRegistry.GetAnyActiveOutputHelper().Should().NotBeNull(
            "the output-helper registry is an AsyncLocal — registering it from an `async` "
            + "InitializeAsync discards the write, which leaves AutoTestLoggingAttribute.Before "
            + "with nothing to mark and silently kills logging for the whole test");

        FileOutput.IsInTestMethod().Should().BeTrue(
            "XUnitFileLogger drops every record unless the helper is inside a test method; this "
            + "flag being false is what made the #993 Debug channels emit nothing on CI");
    }

    /// <summary>
    /// The channel pin: this project raises specific channels to Debug in its own
    /// <c>appsettings.json</c> precisely to diagnose #993. Assert the level filter actually
    /// resolves to Debug for the tracking channel — if the shared Warning-only appsettings ever
    /// wins the copy again, or the project file loses its overrides, this fails loudly instead of
    /// producing another silent, evidence-free CI run.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public void ActivityTrackingDebugChannel_IsActuallyEnabled()
    {
        var factory = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>();
        foreach (var channel in ImmutableList.Create(
                     "MeshWeaver.Graph.ActivityTracking",
                     "MeshWeaver.Hosting.MeshNodeStreamCache"))
        {
            factory.CreateLogger(channel).IsEnabled(LogLevel.Debug).Should().BeTrue(
                $"'{channel}' is raised to Debug in test/MeshWeaver.Query.Test/appsettings.json "
                + "for the #993 investigation; a Warning-only config here means the diagnostic is "
                + "dead again");
        }
    }
}
