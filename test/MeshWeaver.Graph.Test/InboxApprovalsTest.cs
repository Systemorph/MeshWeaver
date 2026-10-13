using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The inbox's Approvals source and Approve all / Reject all. An approval request is the "approval
/// required" notification its flow sent the approver; deciding it sends the node it points at a
/// <see cref="DecideApprovalRequest"/> AS THE APPROVER, and the node's own type decides. Here that type
/// is a test approval that records who decided what, and refuses one id as "already decided".
/// </summary>
public class InboxApprovalsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ApprovalType = "TestApproval";
    private const string Refused = "already decided";
    private const string Held = "recorded, awaits confirmation";
    private const string HeldBinding = "binding-of-the-held-decision";

    private readonly ConcurrentQueue<(string Path, string Decision, string? Author)> decided = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(new MeshNode(ApprovalType)
        {
            HubConfiguration = config => config.WithHandler<DecideApprovalRequest>((hub, request) =>
            {
                var path = hub.Address.Path;
                if (path.EndsWith("/held", StringComparison.Ordinal))
                {
                    // Done = true on purpose: a held decision is held whatever Done says.
                    hub.Post(new DecideApprovalResponse(true, Held) { StepUpRequired = true, StepUpBinding = HeldBinding },
                        o => o.ResponseFor(request));
                    return request.Processed();
                }
                if (path.EndsWith("/stale", StringComparison.Ordinal))
                {
                    hub.Post(new DecideApprovalResponse(false, Refused), o => o.ResponseFor(request));
                    return request.Processed();
                }
                decided.Enqueue((path, request.Message.Decision, request.AccessContext?.ObjectId));
                hub.Post(new DecideApprovalResponse(true), o => o.ResponseFor(request));
                return request.Processed();
            }),
        });

    /// <summary>
    /// Every stored notification type is classified: <see cref="NotificationType.ApprovalRequired"/> —
    /// the enum default, omitted from stored JSON — is an approval request, and every other value is
    /// listed among the STAMPED types the Notifications source selects and the Approvals source
    /// negates. A type appended to the enum and not listed would silently be shown as an approval.
    /// </summary>
    [Fact]
    public void EveryStampedNotificationType_IsClassified()
        => InboxQueries.StampedNotificationTypes.Should().BeEquivalentTo(
            Enum.GetNames<NotificationType>().Where(n => n != nameof(NotificationType.ApprovalRequired)),
            new System.Text.Json.JsonSerializerOptions());

    /// <summary>
    /// Approval requests are their own source — shown by default — and the notification feed (off by
    /// default) no longer carries them; ordinary notifications are the other way round.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ApprovalRequests_AreTheirOwnSource_AndLeaveTheNotificationFeed()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-approvals-legs";
        await CreateUserAsync(owner, ct);
        await Notify(owner, "ask", NotificationType.ApprovalRequired, "TestApprovals/a1", ct);
        await Notify(owner, "news", NotificationType.General, null, ct);

        (await Ids(Legs(owner, InboxQueries.ApprovalsSource), ids => ids.Length > 0, ct)).Should().Equal(["ask"]);
        (await Ids(Legs(owner, "Notifications"), ids => ids.Length > 0, ct)).Should().Equal(["news"]);
        InboxQueries.BuiltIn.Where(l => l.Source == InboxQueries.ApprovalsSource).Should()
            .OnlyContain(l => l.ShownByDefault, "approval requests need the viewer — they are never a switch away");
    }

    /// <summary>
    /// Approve sends the target a decision AS THE APPROVER; once the target records it the request
    /// leaves the inbox. A refusal is reported with the target's own reason, and that request stays.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Approve_DecidesAsTheApprover_AndFilesTheRequestAway_ARefusalStays()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-approvals-decide";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("a1", "TestApprovals") { NodeType = ApprovalType, Name = "a1" }, ct);
        await CreateAsync(new MeshNode("stale", "TestApprovals") { NodeType = ApprovalType, Name = "stale" }, ct);
        var ok = await Notify(owner, "ok", NotificationType.ApprovalRequired, "TestApprovals/a1", ct);
        var stale = await Notify(owner, "late", NotificationType.ApprovalRequired, "TestApprovals/stale", ct);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var approver = new AccessContext { ObjectId = owner, Name = owner };

        IObservable<InboxItemOutcome> Decide(string path)
        {
            using (access.SwitchAccessContext(approver))
                return InboxApprovals.Decide(Mesh, path, ApprovalDecisions.Approve, "no answer");
        }

        (await Decide(ok).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be(new InboxItemOutcome(ok, true));
        decided.Should().ContainSingle().Which.Should().Be(("TestApprovals/a1", ApprovalDecisions.Approve, owner),
            "the target decided, and the decision is authored by the approver — never System");
        (await Content<Notification>(ok, n => n.Dismissed, ct)).Dismissed.Should().BeTrue("a decided request leaves the inbox");

        (await Decide(stale).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Be(new InboxItemOutcome(stale, false, Refused), "the target's own reason is reported");
        (await Content<Notification>(stale, _ => true, ct)).Dismissed.Should().BeFalse("an undecided request stays");
    }

    /// <summary>
    /// A decision the target HOLDS for the approver's step-up comes back with its binding, stays in
    /// the inbox, and is collected — each held decision once — into the batch's ONE step-up.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AHeldDecision_CarriesItsStepUp_AndTheBatchStepsUpOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-approvals-held";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("held", "TestApprovals") { NodeType = ApprovalType, Name = "held" }, ct);
        var request = await Notify(owner, "held", NotificationType.ApprovalRequired, "TestApprovals/held", ct);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        IObservable<InboxItemOutcome> outcome;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            outcome = InboxApprovals.Decide(Mesh, request, ApprovalDecisions.Approve, "no answer");
        var held = await outcome.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        held.Done.Should().BeFalse("a held decision is not decided yet");
        held.Reason.Should().Be(Held);
        held.StepUp.Should().Be(new StepUpTarget { ActionPath = "TestApprovals/held", Binding = HeldBinding });
        (await Content<Notification>(request, _ => true, ct)).Dismissed.Should().BeFalse("it stays until the step-up confirms it");

        var other = new StepUpTarget { ActionPath = "TestApprovals/other", Binding = "b2" };
        InboxLayoutArea.StepUpOf([held, held, new InboxItemOutcome("x", true), new InboxItemOutcome("y", false) { StepUp = other }])
            .Should().Equal([held.StepUp!, other], "each held decision once, in batch order; nothing for a decided or refused item");
        InboxLayoutArea.StepUpOf([new InboxItemOutcome("x", true)]).Should().BeEmpty("no step-up when nothing is held");
    }

    private static string[] Legs(string owner, string source)
        => [.. InboxQueries.Resolve(InboxQueries.BuiltIn, owner).Where(l => l.Source == source).Select(l => l.Query)];

    private async Task<string> Notify(string owner, string id, NotificationType type, string? target, CancellationToken ct)
    {
        var ns = $"{owner}/{NotificationService.SatelliteSegment}";
        await CreateAsync(new MeshNode(id, ns)
        {
            NodeType = NotificationNodeType.NodeType, Name = id, MainNode = owner, State = MeshNodeState.Active,
            Content = new Notification { Id = id, Title = id, Recipient = owner, NotificationType = type, TargetNodePath = target },
        }, ct);
        return $"{ns}/{id}";
    }

    private async Task<string[]> Ids(string[] queries, Func<string[], bool> until, CancellationToken ct)
        => await queries.Select(query => Mesh.GetQuery($"test:{Guid.NewGuid():N}", query)).CombineLatest()
            .Select(legs => legs.SelectMany(nodes => nodes.Select(n => n.Id)).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray())
            .Where(until)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private async Task<T> Content<T>(string path, Func<T, bool> until, CancellationToken ct) where T : class
        => await Mesh.ServiceProvider.GetService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream(path)
                .Select(node => node.ContentAs<T>(Mesh.JsonSerializerOptions))
                .OfType<T>()
                .Where(until))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private async Task CreateAsync(MeshNode node, CancellationToken ct)
        => await Mesh.ServiceProvider.GetService<AccessService>()
            .RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(node))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private Task CreateUserAsync(string path, CancellationToken ct)
        => CreateAsync(MeshNode.FromPath(path) with
        {
            NodeType = UserNodeType.NodeType,
            Name = path,
            State = MeshNodeState.Active,
            Content = new User { FullName = path },
        }, ct);
}
