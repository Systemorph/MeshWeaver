using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>The sender of a post is the hub that posts it</b> (#6553, Doc/Architecture/SenderIsThePostingHub).
///
/// <para>A hub guard that decides by sender — the stamped-field guard admits only writes from the
/// owning hub — is only as good as the sender. <see cref="PostOptions"/> is a record, so posting code
/// can write <c>o with { Sender = … }</c>; before the fix the delivery left with whatever address the
/// caller wrote, and in-process code could pass such a guard by naming the guarded hub's own
/// address. The posting hub now overwrites a foreign sender with its own address.</para>
///
/// <para>Both arms are read off the RECEIVER: the host replies with the sender it was handed, and
/// the reply arriving back at the real poster is itself the proof — a delivery that left under the
/// forged address would have been answered to that address, and the poster's wait would time out.</para>
/// </summary>
public class SenderIsThePostingHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record WhoSentMe : IRequest<SenderSeen>;

    private record SenderSeen(Address Sender);

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(WhoSentMe), typeof(SenderSeen))
            .WithHandler<WhoSentMe>((hub, request) =>
            {
                hub.Post(new SenderSeen(request.Sender), o => o.ResponseFor(request));
                return request.Processed();
            });

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(WhoSentMe), typeof(SenderSeen));

    private static Address Bare(Address address) => address with { Host = null };

    /// <summary>The control: an ordinary post is seen with the poster's address.</summary>
    [HubFact]
    public async Task AnOrdinaryPost_ReachesTheTargetUnderThePostersAddress()
    {
        var client = GetClient();
        var response = await client
            .Observe<SenderSeen>(new WhoSentMe(), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit("an ordinary request is answered",
                cancellationToken: TestContext.Current.CancellationToken);
        Bare(response.Message.Sender).Should().Be(Bare(client.Address));
    }

    /// <summary>
    /// The impersonation: the client names the TARGET's own address as sender (the shape that would
    /// pass an owner-only guard), or an unrelated hub's. The target must see the real poster.
    /// </summary>
    [Theory(Timeout = 120000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AForgedSender_IsReplacedByThePostingHubsAddress(bool claimTheTarget)
    {
        var client = GetClient();
        var host = CreateHostAddress();
        var forged = claimTheTarget ? host : new Address("forged", "someone-else");

        var response = await client
            .Observe<SenderSeen>(new WhoSentMe(), o => o.WithTarget(host) with { Sender = forged })
            .Should().Within(10.Seconds()).Emit(
                "the post leaves as the client, so the reply comes back to the client",
                cancellationToken: TestContext.Current.CancellationToken);

        Bare(response.Message.Sender).Should().Be(Bare(client.Address),
            "the target is handed the address of the hub that actually posted, never the one the caller wrote");
        Bare(response.Message.Sender).Should().NotBe(Bare(forged));
    }
}
