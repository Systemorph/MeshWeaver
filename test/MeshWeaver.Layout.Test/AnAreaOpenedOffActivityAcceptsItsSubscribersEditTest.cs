using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// A person's edit on a layout area must travel under the identity the area was subscribed for,
/// whatever ambient identity happened to be set on the thread that OPENED the area.
///
/// <para>The production shape (MeshWeaver.Education journey run 39600717306, the Store paywall's
/// coupon box). A portal circuit's page opens a layout area in a render, and Blazor runs a render
/// that a parent requested from an Rx callback on the CALLER's execution context. When that caller
/// is a hub turn that carries the platform identity (a navigation that resolved only once the
/// course's root node was written by its install), the ambient <c>AccessService.Context</c> during
/// the render is <c>system-security</c>. The area's SubscribeRequest is then stamped with the
/// circuit user by the portal hub's own post step, so the owner records the person as the
/// subscriber — but the client stream also took a snapshot of the ambient context when it was
/// created and stamped THAT on every edit it sent. The owner accepts input only from the
/// subscriber, so it refused the edit ("this view was opened under a different sign-in"), the
/// coupon model kept its blank seed, and the Redeem click that followed answered "Enter a coupon
/// code." for a box that read WELCOME2026.</para>
///
/// <para>The client hub here carries the portal hub's post step (stamp the circuit user when a post
/// has no identity), exactly as <c>PortalApplication.DefaultPortalConfig</c> installs it. The area
/// is opened once under the platform identity (the defect's shape) and once under the circuit
/// user (the control); in both, the person types the code inside an inbound activity — the circuit
/// identity set, the request identity not — and presses Redeem as themselves.</para>
/// </summary>
public class AnAreaOpenedOffActivityAcceptsItsSubscribersEditTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "CouponPaywall";
    private const string RedeemArea = Area + "/Redeem";
    private const string ModelId = "store-plugin-coupon";
    private const string Code = "WELCOME2026";

    /// <summary>The signed-in person of the circuit.</summary>
    private static readonly AccessContext Learner = new() { ObjectId = "e2e-admin", Name = "Learner" };

    private readonly AsyncSubject<LayoutAreaHost> owner = new();

    /// <summary>What the owner's Redeem action read out of the coupon model.</summary>
    private readonly ReplaySubject<string> redeemed = new(1);

    /// <summary>The paywall's coupon model: one member, bound by the field.</summary>
    public record CouponModel
    {
        /// <summary>The code the person typed.</summary>
        public string? Code { get; init; }
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithType(typeof(CouponModel), nameof(CouponModel))
            .AddLayout(layout => layout.WithView(Area, (host, _) =>
            {
                owner.OnNext(host);
                owner.OnCompleted();
                // The paywall's guarded seed: a blank model when the slot is empty, so an edit that
                // never arrives and an empty box are the same state.
                host.Update(LayoutAreaReference.Data, data => data.GetInstance(ModelId) is null
                    ? data.SetItem(ModelId, new CouponModel()) : data);
                return Controls.Stack
                    .WithView(Controls.Text(new JsonPointerReference("code")) with
                    {
                        DataContext = LayoutAreaReference.GetDataPointer(ModelId),
                        Immediate = true,
                    }, "Code")
                    .WithView(Controls.Button("Redeem").WithClickAction(ctx =>
                    {
                        // A one-shot read of the model at the click, as CouponRedemption.RedeemBounded does.
                        ctx.Host.GetDataStream<CouponModel>(ModelId).Take(1)
                            .Subscribe(model => redeemed.OnNext(model?.Code ?? ""), redeemed.OnError);
                    }), "Redeem");
            }));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithType(typeof(CouponModel), nameof(CouponModel))
            .AddLayoutClient()
            // The portal hub's post step (PortalApplication.DefaultPortalConfig): a post that carries
            // no identity is stamped with the circuit user; an explicit identity is left alone.
            .AddPostPipeline(pipeline => pipeline.AddPipeline((delivery, next) =>
                next(delivery.AccessContext is null ? delivery.SetAccessContext(Learner) : delivery)));

    /// <summary>
    /// The code the person typed is the code the Redeem click reads, however the area was opened.
    /// </summary>
    /// <param name="openedUnder">The ambient identity on the thread that opened the area:
    /// <c>platform</c> — a render run on a platform hub's turn (the defect); <c>circuit</c> — a render
    /// run inside the person's own inbound activity (the control).</param>
    [Theory]
    [InlineData("platform")]
    [InlineData("circuit")]
    public async Task TheTypedCodeIsWhatTheRedeemClickReads(string openedUnder)
    {
        var ct = TestContext.Current.CancellationToken;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var client = GetClient();
        var failures = new ReplaySubject<DeliveryFailure>();
        using var watch = client.Register<DeliveryFailure>(delivery =>
        {
            failures.OnNext(delivery.Message);
            return delivery.Processed();
        });

        var stream = Open(client, access, openedUnder);
        await stream.GetControlStream(RedeemArea).Should().Within(TestTimeouts.Convergence)
            .Match(control => control is not null, "the paywall is rendered for the person", cancellationToken: ct);
        var host = await owner.Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        // The person types the code: an inbound activity, so the circuit identity is set and the
        // request identity is not — what CircuitAccessHandler establishes around every Blazor event.
        InsideAnInboundActivity(access, () =>
            stream.UpdatePointer(Code, LayoutAreaReference.GetDataPointer(ModelId), new JsonPointerReference("code")));

        // Either the edit is refused (the defect) or it lands in the owner's coupon model.
        var outcome = await failures
            .Where(failure => failure.Delivery.Message is PatchDataChangeRequest)
            .Select(failure => $"refused: {failure.Message}")
            .Merge(host.GetDataStream<CouponModel>(ModelId)
                .Where(model => model?.Code == Code)
                .Select(_ => "applied"))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the typed code either reaches the owner or is answered with a refusal", cancellationToken: ct);
        outcome.Should().Be("applied",
            "the edit is the subscriber's own input. The area was subscribed for the person (the portal "
            + "hub stamps the circuit user on the subscribe), so the edit must carry the person too — "
            + "not the identity that happened to be ambient on the thread that opened the area");

        // The Redeem click, sent the way a view sends it: as the person.
        var refusal = await Observable.Create<string?>(observer => stream.SubmitUserAction(
                new ClickedEvent(RedeemArea, stream.StreamId), Learner,
                onRefused: sentence => { observer.OnNext(sentence); observer.OnCompleted(); },
                onAccepted: () => { observer.OnNext(null); observer.OnCompleted(); }))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the Redeem click is answered", cancellationToken: ct);
        refusal.Should().BeNull("the person's own click is accepted");

        var read = await redeemed.Should().Within(TestTimeouts.Convergence)
            .Emit("the Redeem action ran and reported what it read", cancellationToken: ct);
        read.Should().Be(Code, "the click reads the code the person typed, never the blank seed");
    }

    /// <summary>Opens the area the way a portal render does, under the given ambient identity.</summary>
    private ISynchronizationStream<JsonElement> Open(IMessageHub client, AccessService access, string openedUnder)
    {
        var reference = new LayoutAreaReference(Area);
        if (openedUnder == "platform")
        {
            // A render Blazor runs on the caller's execution context, where the caller is a hub turn
            // that carries the platform identity.
            using (access.ImpersonateAsSystem())
                return client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);
        }

        ISynchronizationStream<JsonElement>? opened = null;
        InsideAnInboundActivity(access, () =>
            opened = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference));
        return opened!;
    }

    /// <summary>
    /// Runs <paramref name="action"/> as CircuitAccessHandler runs an inbound Blazor activity: the
    /// circuit identity set for its duration and cleared afterwards, the request identity untouched.
    /// </summary>
    private static void InsideAnInboundActivity(AccessService access, Action action)
    {
        access.SetCircuitContext(Learner);
        try
        {
            action();
        }
        finally
        {
            access.SetCircuitContext(null);
        }
    }
}
