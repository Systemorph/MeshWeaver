using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Systemorph/MeshWeaver#6553 — the sender of a delivery is the hub that POSTED it, not a value the
/// posting code chose.
///
/// <para><b>The gap.</b> <c>PostOptions</c> is a public record whose <c>Sender</c> is a public init
/// property, and <c>IMessageHub.Post</c> hands the options its caller returns from <c>configure</c>
/// to the delivery unchanged. Code that runs in the portal process (a compiled module, a
/// mesh-compiled NodeType) can therefore post a message whose <c>Sender</c> is ANOTHER hub's address,
/// and every guard that admits a write because "the sender is the owning hub" (the stamped-field
/// guard of #6519 is one) takes that for the owning hub.</para>
///
/// <para><b>What is pinned.</b> A post that names a foreign sender in its options is delivered with
/// the address of the hub that actually posted it. The control posts without naming a sender and
/// must be green before and after: it shows that the receiver's handler reads the sender at all, so
/// the red of the first test can only be the foreign sender surviving.</para>
/// </summary>
public class PostStampsTheSenderTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record SenderProbe(string Note);

    private static readonly Address PosterAddress = new("sender-stamp-poster", "1");
    private static readonly Address NamedAsSenderAddress = new("sender-stamp-named", "1");
    private static readonly Address ReceiverAddress = new("sender-stamp-receiver", "1");

    /// <summary>
    /// Posts one probe from the poster hub to the receiver hub with the options
    /// <paramref name="configure"/> returns, and answers the sender the RECEIVER's handler read off the
    /// delivery (its Host, which tracks the routing path, stripped — the inner address is the identity).
    /// </summary>
    private async Task<Address?> SenderTheReceiverSees(Func<PostOptions, PostOptions> configure, CancellationToken ct)
    {
        var seen = new AsyncSubject<Address?>();

        Mesh.GetHostedHub(ReceiverAddress, c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(SenderProbe))
            .WithHandler<SenderProbe>((_, d) =>
            {
                Address? sender = d.Sender is { } s ? s with { Host = null } : null;
                seen.OnNext(sender);
                seen.OnCompleted();
                return d.Processed();
            }), HostedHubCreation.Always);
        Mesh.GetHostedHub(NamedAsSenderAddress, c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(SenderProbe)), HostedHubCreation.Always);
        var poster = Mesh.GetHostedHub(PosterAddress, c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(SenderProbe)), HostedHubCreation.Always)!;

        poster.Post(new SenderProbe("who sent this?"), o => configure(o).WithTarget(ReceiverAddress));

        return await seen.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task Post_WithAForeignSenderInTheOptions_IsDeliveredFromThePostingHub()
    {
        var seen = await SenderTheReceiverSees(
            o => o with { Sender = NamedAsSenderAddress },
            TestContext.Current.CancellationToken);

        Output.WriteLine($"poster={PosterAddress} named-as-sender={NamedAsSenderAddress} receiver-saw={seen}");
        Assert.True(PosterAddress.Equals(seen),
            $"the receiver saw the sender '{seen}'. The hub that posted is '{PosterAddress}'; '{NamedAsSenderAddress}' is "
            + "the hub the post NAMED as its sender in its own options. A sender that the posting code can choose "
            + "lets in-process code pass every guard that admits a write because the sender is the owning hub (#6553).");
    }

    [Fact(Timeout = 120_000)]
    public async Task Post_WithoutAnExplicitSender_IsDeliveredFromThePostingHub()
    {
        var seen = await SenderTheReceiverSees(o => o, TestContext.Current.CancellationToken);

        Output.WriteLine($"poster={PosterAddress} receiver-saw={seen}");
        Assert.True(PosterAddress.Equals(seen),
            $"CONTROL: the receiver saw the sender '{seen}' for a post that named no sender; the poster is "
            + $"'{PosterAddress}'. If this is red the harness is wrong, not the platform.");
    }
}
