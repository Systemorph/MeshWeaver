// <meshweaver>
// Id: Testing/Hosting/MeshTeardownSignalTest
// DisplayName: Testing/Hosting/MeshTeardownSignalTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Reactive.Assertions;

/// <summary>
/// Pins the terminal "all is done" contract of <see cref="MeshTeardownSignal"/>: fired exactly
/// once by the teardown orchestrator, replayed to late subscribers (so nothing that orders on
/// teardown can miss it), first report wins, and the report's <see cref="TeardownReport.Clean"/>
/// truthfully folds the drain outcomes.
/// </summary>
public class MeshTeardownSignalTest
{
    [MeshFact]
    public async Task Completed_replays_the_report_to_a_subscriber_that_attaches_after_teardown()
    {
        var signal = new MeshTeardownSignal();
        signal.SignalCompleted(new TeardownReport(0, true));

        // The subscriber attaches AFTER teardown already finished — the exact shape of the
        // next test / scope disposal ordering on "all is done". It must still see the report.
        var report = await signal.Completed.Should().Within(TimeSpan.FromSeconds(1)).Emit();
        report.Clean.Should().BeTrue();
    }

    [MeshFact]
    public async Task SignalCompleted_is_idempotent_and_the_first_report_wins()
    {
        var signal = new MeshTeardownSignal();
        signal.SignalCompleted(new TeardownReport(3, false));
        signal.SignalCompleted(new TeardownReport(0, true)); // late duplicate — must not overwrite

        var report = await signal.Completed.Should().Within(TimeSpan.FromSeconds(1)).Emit();
        report.LeakedIoLeaves.Should().Be(3, "the FIRST report is the terminal truth");
        report.Clean.Should().BeFalse();
    }

    [MeshFact]
    public void TeardownReport_is_clean_only_when_nothing_survived()
    {
        new TeardownReport(0, true).Clean.Should().BeTrue();
        new TeardownReport(1, true).Clean.Should().BeFalse("a leaked I/O leaf is live code surviving teardown");
        new TeardownReport(0, false).Clean.Should().BeFalse("an unfinished async cleanup is live code surviving teardown");
    }
}
