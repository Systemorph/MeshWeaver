using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 Timeout == error reaches the Orleans containers (Doc/Architecture/TimeoutIsError). A silo and
/// the Orleans client host each build their own container, so a timeout terminal reported there
/// never reached the test container's recorder, and the guard under-reported every Orleans test.
/// Each test reports a terminal of its own kind through ONE of those containers and DECLARES it:
/// the guard fails a declared terminal that did not reach the test's window
/// (DECLARED-BUT-NOT-FIRED), so the test fails if that container's recorder is not wired into it.
/// </summary>
public class TimeoutTerminalSiloCoverageTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    private const string SiloProbe = nameof(SiloProbe);
    private const string ClientProbe = nameof(ClientProbe);

    /// <summary>A terminal reported in a SILO's container reaches the test's window.</summary>
    [Fact]
    [ExpectsTimeoutTerminal(SiloProbe, "the probe reports one through the silo's container")]
    public void ATerminalReportedOnASilo_ReachesTheGuard()
        => SiloServices().TimeoutTerminalReporter()(
            new TimeoutTerminal(SiloProbe, "silo", "a terminal reported in the silo's container"));

    /// <summary>A terminal reported in the Orleans CLIENT host's container reaches the test's window.</summary>
    [Fact]
    [ExpectsTimeoutTerminal(ClientProbe, "the probe reports one through the client host's container")]
    public void ATerminalReportedOnTheClientHost_ReachesTheGuard()
        => ClientServices.TimeoutTerminalReporter()(
            new TimeoutTerminal(ClientProbe, "client", "a terminal reported in the client host's container"));
}
