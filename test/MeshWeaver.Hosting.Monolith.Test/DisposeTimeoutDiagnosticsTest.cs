using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Negative coverage for <see cref="IMessageHub.GetDisposalDiagnostics"/> and the
/// dispose-timeout contract on <see cref="MonolithMeshTestBase.DisposeAsync"/>:
///
/// <list type="bullet">
/// <item><description>The diagnostic snapshot reports hub address, run-level,
/// disposal-task state, dataflow buffer counts, and one entry per hosted hub
/// recursively â€” so a CI hang failure points to the offending hub.</description></item>
/// <item><description>After a clean dispose the snapshot reports
/// <c>Disposal=Completed</c>; before any dispose call it reports
/// <c>&lt;not started&gt;</c>.</description></item>
/// </list>
///
/// The behavioural contract that the test base throws a
/// <see cref="TimeoutException"/> with these diagnostics on a hung hub is enforced
/// by <see cref="MonolithMeshTestBase"/> itself â€” we exercise the diagnostic-string
/// shape here, and rely on the base-class change to surface the timeout in CI logs
/// when a real hang occurs (rather than the previous silent-swallow regression).
/// </summary>
public class DisposeTimeoutDiagnosticsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public void Diagnostics_ReportAddressAndRunLevel()
    {
        var diag = Mesh.GetDisposalDiagnostics();

        diag.Should().Contain(Mesh.Address.ToString(),
            "diagnostics must name the hub address so a CI failure identifies which hub hung");
        diag.Should().Contain("RunLevel=",
            "diagnostics must report the hub's RunLevel so we know whether dispose ever started");
        diag.Should().Contain("Disposal=",
            "diagnostics must report whether the disposal task is started/pending/completed");
    }

    [Fact]
    public void Diagnostics_BeforeDispose_ReportNotStarted()
    {
        var diag = Mesh.GetDisposalDiagnostics();

        diag.Should().Contain("Disposal=<not started>",
            "before any Dispose() call the snapshot must say <not started>, not Pending or Completed");
    }

    [Fact]
    public void Diagnostics_IncludeQueueCounts()
    {
        var diag = Mesh.GetDisposalDiagnostics();

        diag.Should().Contain("Queue(",
            "diagnostics must surface dataflow buffer counts so a backlog from a re-posting handler is visible");
        diag.Should().Contain("buffer=",
            "queue snapshot must include the main buffer count");
        diag.Should().Contain("deferred=",
            "queue snapshot must include the deferred (pre-gate) buffer count");
        diag.Should().Contain("openGates=",
            "queue snapshot must include any still-closed initialization gates â€” a stuck gate is the canonical 'why didn't startup finish' cause");
    }

    [Fact]
    public void Diagnostics_WithHostedHub_ListChildAddress()
    {
        var childAddress = new Address("@diag/child");
        _ = Mesh.GetHostedHub(childAddress);

        var diag = Mesh.GetDisposalDiagnostics();

        diag.Should().Contain("HostedHubs",
            "diagnostics must enumerate hosted hubs so a hung child surfaces");
        diag.Should().Contain(childAddress.ToString(),
            "each hosted hub's address must appear so a hang on one specific child is identifiable");
    }

    [Fact]
    public async Task Diagnostics_AfterDispose_ReportCompleted()
    {
        // Use a hosted hub so we can dispose it while the outer Mesh stays alive
        // for the rest of the test class lifecycle.
        var disposable = Mesh.GetHostedHub(new Address("@diag/willDispose"));
        disposable.Dispose();

        // 5s safety net inside MessageHub.Dispose() force-completes if the normal
        // path stalls; wait that bound + headroom.
        var completed = await Task.Run(() => disposable.Disposal!.Wait(TimeSpan.FromSeconds(8)));
        completed.Should().BeTrue("MessageHub.Dispose has a 5s safety-net force-completion");

        var diag = disposable.GetDisposalDiagnostics();
        diag.Should().Contain("Disposal=Completed",
            "after a clean dispose the snapshot must report Completed, not Pending");
    }

    [Fact]
    public void DisposeTimeout_IsBoundedToTensOfSeconds()
    {
        // Belt-and-braces invariant: don't let DisposeTimeout drift back to the 30s+
        // silent-swallow bound. Anything > 30s makes a hung-test class a job-timeout
        // amplifier in CI â€” which is exactly the regression that triggered this
        // hardening (CI canceled at 9m19s of a 30m job because each disposed test
        // class was burning ~30s on hub-shutdown).
        MonolithMeshTestBase.DisposeTimeout.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30),
            "DisposeTimeout must stay tight so a hung test class fails fast instead of " +
            "stacking 30s+ stalls into the job-level CI timeout");
        MonolithMeshTestBase.DisposeTimeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10),
            "DisposeTimeout must clear MessageHub's 5s safety net + HostedHubsCollection's 10s fan-out cap " +
            "with headroom; otherwise we'd time out on healthy disposes");
    }
}
