using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>Timeout == error — the guard itself.</b> A test during which a product-side timeout
/// terminal fires FAILS unless it declares that terminal (<c>TimeoutTerminalGuard.cs</c>,
/// <c>Doc/Architecture/TimeoutIsError</c>).
///
/// <para>This class is ARMED (<see cref="TimeoutTerminalsAreErrorsAttribute"/>), so every test in it
/// runs through the real <c>AutoTestLoggingAttribute.After</c> verdict. The first test fires a REAL
/// hub <c>RequestTimeout</c> and declares it — it passing at all proves the declared path, end to
/// end. The verdict tests prove the other three outcomes on the same function the hook calls; a
/// test that deliberately fails cannot live in a green suite, and running the first test with its
/// declaration removed is the manual negative control (it fails naming
/// <c>UNDECLARED RequestTimeout</c>).</para>
/// </summary>
[TimeoutTerminalsAreErrors]
public class TimeoutTerminalGuardTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record NeverAnswered : IRequest<NeverAnswer>;
    private record NeverAnswer;

    /// <summary>
    /// The host never answers <see cref="NeverAnswered"/>, so the requester can only end by the
    /// hub's own <c>RequestTimeout</c>. The bound is short because the timeout IS the subject here —
    /// nothing is being waited FOR.
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRequestTimeout(TestTimeouts.Quick / 100)
            .WithHandler<NeverAnswered>((_, delivery) => delivery.Processed());

    /// <summary>
    /// 🚨 THE DECLARED PATH, end to end. The terminal fires, the recorder in this test's container
    /// hears it (so the hub really reports — the seam is wired, not just defined), and because the
    /// test declares <c>RequestTimeout</c> the armed After hook lets it pass.
    /// </summary>
    [Fact]
    [ExpectsTimeoutTerminal(TimeoutTerminalKinds.RequestTimeout,
        "the hub's own RequestTimeout is the behaviour under test")]
    public async Task AFiredRequestTimeout_IsRecorded_AndADeclaringTestPasses()
    {
        var host = GetHost();
        var recorder = ServiceProvider.GetRequiredService<TimeoutTerminalRecorder>();

        var act = async () => await host
            .Observe<NeverAnswer>(new NeverAnswered(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<TimeoutException>();

        var fired = recorder.Fired.Where(t => t.Kind == TimeoutTerminalKinds.RequestTimeout).ToList();
        fired.Should().ContainSingle("the hub reports its RequestTimeout to the container's observers");
        fired[0].Message.Should().Contain("NeverAnswered", "the terminal carries the hub's own message");

        // The same verdict the After hook computes, for a test that did NOT declare it.
        TimeoutTerminalVerdict.Evaluate("Undeclared", recorder.Fired, ImmutableHashSet<string>.Empty)
            .Should().NotBeNull("an undeclared terminal that fired is a failure")
            .And.Contain("UNDECLARED RequestTimeout");
    }

    /// <summary>Nothing fired and nothing declared — the ordinary green test.</summary>
    [Fact]
    public void NoTerminal_NoDeclaration_IsNotAFailure()
        => TimeoutTerminalVerdict.Evaluate("T", [], ImmutableHashSet<string>.Empty).Should().BeNull();

    /// <summary>A terminal of another kind than the declared one is still undeclared.</summary>
    [Fact]
    public void ATerminalOfAnUndeclaredKind_IsAFailure_NamingItsOrigin()
    {
        var verdict = TimeoutTerminalVerdict.Evaluate("T",
            [new TimeoutTerminal(TimeoutTerminalKinds.QueryInitial, "nodeType:X", "did not emit an Initial")],
            [TimeoutTerminalKinds.QueryInitial, TimeoutTerminalKinds.RequestTimeout]);
        verdict.Should().NotBeNull()
            .And.Contain("DECLARED-BUT-NOT-FIRED RequestTimeout");

        TimeoutTerminalVerdict.Evaluate("T",
                [new TimeoutTerminal(TimeoutTerminalKinds.WriteVerdict, "acme/a", "no verdict")],
                [TimeoutTerminalKinds.RequestTimeout])
            .Should().Contain("UNDECLARED WriteVerdict at 'acme/a'");
    }

    /// <summary>
    /// A declaration whose terminal never fired is stale and fails too — otherwise an exemption
    /// outlives the timeout it was written for and hides the next real stall of that kind.
    /// </summary>
    [Fact]
    public void ADeclaredTerminalThatNeverFired_IsAFailure()
        => TimeoutTerminalVerdict.Evaluate("T", [], [TimeoutTerminalKinds.RequestTimeout])
            .Should().Contain("DECLARED-BUT-NOT-FIRED RequestTimeout");
}
