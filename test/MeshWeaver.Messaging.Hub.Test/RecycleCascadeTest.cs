using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The <see cref="RecycleCascade"/> seam: a routed <see cref="DisposeRequest"/> that STARTS a recycle
/// hands the request to the cascade exactly once, before the teardown; a request that is itself a
/// cascade (<see cref="DisposeRequest.CascadedFrom"/> set) never fans out again; and a direct
/// <c>Dispose()</c> — how a parent tears its children down — cascades nothing.
/// </summary>
public class RecycleCascadeTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly Address MainAddress = new("cascade", "main");
    private static readonly Address CascadedAddress = new("cascade", "sub");
    private static readonly Address DirectAddress = new("cascade", "direct");

    [HubFact]
    public async Task RoutedDisposeRequest_CascadesOnce_WithTheRequest_BeforeTheTeardownStarts()
    {
        var host = GetHost();
        var cascades = 0;
        DisposeRequest? seen = null;
        var disposingWhenCascaded = true;

        var main = host.GetHostedHub(MainAddress, c => c.WithInitialization(h =>
            h.Set(new RecycleCascade(request =>
            {
                Interlocked.Increment(ref cascades);
                seen = request;
                disposingWhenCascaded = h.IsDisposing;
            }))));
        main.Should().NotBeNull();
        await main!.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        host.Post(new DisposeRequest { Reason = "the operator recycled the main bit" },
            o => o.WithTarget(MainAddress));

        await main.DisposalCompleted.FirstOrDefaultAsync()
            .Await(TestContext.Current.CancellationToken);

        cascades.Should().Be(1,
            "the dependency network is derived ONCE, at the main node — a second derivation would "
            + "post a second wave of dispose requests onto addresses already re-activating");
        seen.Should().NotBeNull();
        seen!.Reason.Should().Be("the operator recycled the main bit",
            "the cascade carries the operator's reason onto every fanned-out request");
        seen.CascadedFrom.Should().BeNull("the request that STARTS a recycle is not itself a cascade");
        disposingWhenCascaded.Should().BeFalse(
            "the cascade runs on the recycle's own turn, BEFORE Dispose() — the hub is still whole "
            + "enough to compute its network, and the requests are posted by a survivor, not by it");
    }

    [HubFact]
    public async Task ACascadedDisposeRequest_NeverCascadesAgain()
    {
        var host = GetHost();
        var cascades = 0;

        var sub = host.GetHostedHub(CascadedAddress, c => c.WithInitialization(h =>
            h.Set(new RecycleCascade(_ => Interlocked.Increment(ref cascades)))));
        sub.Should().NotBeNull();
        await sub!.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        host.Post(new DisposeRequest { Reason = "cascade", CascadedFrom = "SomeType" },
            o => o.WithTarget(CascadedAddress));

        await sub.DisposalCompleted.FirstOrDefaultAsync()
            .Await(TestContext.Current.CancellationToken);
        sub.RunLevel.Should().Be(MessageHubRunLevel.Dead, "a cascaded request still tears the hub down");

        cascades.Should().Be(0,
            "a request that carries CascadedFrom is one wave of a cascade computed at the main node; "
            + "fanning out again is how a cycle among NodeTypes would become a storm");
    }

    [HubFact]
    public async Task DirectDispose_DoesNotCascade()
    {
        var host = GetHost();
        var cascades = 0;

        var direct = host.GetHostedHub(DirectAddress, c => c.WithInitialization(h =>
            h.Set(new RecycleCascade(_ => Interlocked.Increment(ref cascades)))));
        direct.Should().NotBeNull();
        await direct!.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        direct.Dispose();
        await direct.DisposalCompleted.FirstOrDefaultAsync()
            .Await(TestContext.Current.CancellationToken);

        cascades.Should().Be(0,
            "a direct Dispose() is an ancestor's cascade or a host teardown — the address is not "
            + "coming back, and nothing downstream is being asked to re-read");
    }
}
