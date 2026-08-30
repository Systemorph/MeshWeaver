using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #1790, tiers 2–3 — a service that RETURNS a system-scoped observable to a caller who subscribes
/// it from their own thread. <see cref="NotificationService.Dispatch"/> is the representative shape
/// (the click-action sites — plugin install, space/group invite, install-record removal — are all
/// the same one): the writes must run as System because the notification lands in the RECIPIENT's
/// partition, which the acting user has no rights on, but the acting user's thread must be handed
/// back untouched.
///
/// <para><b>The defect.</b> <c>Observable.Using(access.ImpersonateAsSystem, _ =&gt; writes)</c> opens
/// the AsyncLocal scope on whichever thread the CALLER subscribes from — a Blazor circuit, a hub
/// handler, a click action — and disposes it when the writes terminate, on the owning partition
/// hub's thread. So the caller kept <c>system-security</c> and the hub thread was handed the
/// caller's identity. Nothing failed; the notification was written either way. That is why the
/// assertion below is about the THREAD, not about the outcome.</para>
///
/// <para><b>Non-vacuity.</b> The dispatch is awaited and the recipient's bell node asserted present
/// in the same test, so a "fix" that stopped impersonating (and therefore stopped writing into a
/// partition the caller cannot touch) fails here rather than passing quietly. Reverting
/// <c>ImpersonationScopeExtensions</c> to its <c>Observable.Using</c> form turns the identity
/// assertion red.</para>
/// </summary>
public class NotificationDispatchIdentityLatchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Recipient = "latch_recipient";
    private const string SystemId = "system-security";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    private static readonly AccessContext Actor = new()
    {
        ObjectId = "granting_admin",
        Name = "Granting Admin",
        Email = "granting_admin@acme.com",
    };

    [Fact(Timeout = 120000)]
    public async Task DispatchingANotification_LeavesTheActorsIdentityOnTheCallingThread()
    {
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(new MeshNode(Recipient)
            {
                NodeType = "User",
                Name = Recipient,
                Content = new User { Email = $"{Recipient}@acme.com", FullName = Recipient },
            }).Should().Emit();

        string? identityRightAfterSubscribe;

        using (Access.SwitchAccessContext(Actor))
        {
            Access.Context?.ObjectId.Should().Be("granting_admin",
                "the premise: the acting user is ambient on this thread before the dispatch");

            // ToTask() subscribes SYNCHRONOUSLY here — the moment the scope is opened — and the
            // writes terminate on the recipient partition's hub thread, which is the moment the old
            // shape would have disposed it. Read the identity in between.
            var dispatch = NotificationService.Dispatch(
                    Mesh,
                    recipient: Recipient,
                    mainNodePath: Recipient,
                    title: "You've been given access to LatchSpace",
                    message: "You now have Editor access to \"LatchSpace\".",
                    type: NotificationType.AccessGranted,
                    targetNodePath: "LatchSpace",
                    createdBy: Actor.ObjectId)
                .Timeout(60.Seconds())
                .Await(TestContext.Current.CancellationToken);

            identityRightAfterSubscribe = Access.Context?.ObjectId;

            await dispatch;
        }

        identityRightAfterSubscribe.Should().NotBe(SystemId,
            "a service that runs its own writes as System must not hand the caller an elevated "
            + "thread. Observing 'system-security' here means everything the caller does after the "
            + "click — further writes, permission checks, the next render — runs with Permission.All "
            + "(#1790)");
        identityRightAfterSubscribe.Should().Be("granting_admin",
            "and it is the actor's own identity that must come back, not merely 'not System'");

        // Non-vacuity: the write really happened, into a partition the actor has no rights on — so
        // the impersonation was genuinely in force for the work.
        await Mesh.GetWorkspace()
            .GetQuery($"latch-notif|{Recipient}",
                $"path:{Recipient}/_Notification scope:children nodeType:Notification")
            .Where(nodes => (nodes ?? []).Any(n =>
                n.ContentAs<Notification>(Json) is { NotificationType: NotificationType.AccessGranted }))
            .FirstAsync().Timeout(60.Seconds());

        Access.Context?.ObjectId.Should().BeNull(
            "and the enclosing scope restores the thread to what the test host left it");
    }
}
