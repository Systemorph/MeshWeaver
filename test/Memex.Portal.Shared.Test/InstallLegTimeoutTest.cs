using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A bound inside the install chain NAMES the leg it bounded when it expires (MeshWeaver#2254,
/// #5826). Rx's bare <c>Timeout</c> faults with "The operation has timed out." — a message that
/// named no leg in a chain carrying five such bounds, which is how a boot logged a failed package
/// nobody could attribute to a wait.
///
/// <para>The five legs <see cref="InstallLegTimeout.TimeoutNamingTheLeg{T}"/> wraps all complete
/// inside their bound in every other suite, so without this test the expiry branch never ran in CI
/// and a regression back to the bare form would pass. The bound here is small and the source never
/// emits, so the branch under test is the only way out.</para>
/// </summary>
public class InstallLegTimeoutTest
{
    private const string Leg = "reading the install record Plugins/Anthropic";

    /// <summary>The terminal of <paramref name="source"/>: null when it emitted, else its fault.</summary>
    private static Task<Exception?> TerminalOf<T>(IObservable<T> source) =>
        source.Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .FirstAsync()
            // The outer wait is the test's own bound; it must never be what ends the sequence.
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

    [Fact]
    public async Task AnExpiredBound_FaultsNamingTheLegAndTheBudget()
    {
        var fault = await TerminalOf(
            Observable.Never<int>().TimeoutNamingTheLeg(TimeSpan.FromMilliseconds(200), Leg));

        var timeout = fault.Should().BeOfType<TimeoutException>(
            "the bound keeps its exception type — callers that catch a TimeoutException still do").Subject;
        timeout.Message.Should().Be($"Install leg timed out after 0.2s: {Leg}.",
            "the fault says WHICH wait expired and after how long; Rx's bare 'The operation has "
            + "timed out.' is the #2254 symptom this helper exists to remove");
    }

    [Fact]
    public async Task ASourceThatAnswersInsideTheBound_PassesThroughUntouched()
    {
        var value = await Observable.Return(42)
            .TimeoutNamingTheLeg(TestTimeouts.Convergence, Leg)
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        value.Should().Be(42);
    }

    [Fact]
    public async Task ASourceThatFaultsInsideTheBound_KeepsItsOwnFault()
    {
        var fault = await TerminalOf(
            Observable.Throw<int>(new InvalidOperationException("the read itself failed"))
                .TimeoutNamingTheLeg(TestTimeouts.Convergence, Leg));

        fault.Should().BeOfType<InvalidOperationException>(
            "only an EXPIRED bound is renamed; a real failure must not be reported as a timeout")
            .Subject.Message.Should().Be("the read itself failed");
    }
}
