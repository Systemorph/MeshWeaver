using System.Collections.Concurrent;
using System.Collections.Immutable;
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
        => base.ConfigureMesh(builder).AddMeshNodes(
            // The approval RECORD's type, as the Approvals package registers it: the ledger reads its nodes.
            new MeshNode("Approval", "Approvals") { HubConfiguration = config => config.WithContentType<Approval>() },
            new MeshNode(ApprovalType)
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

    /// <summary>
    /// One click decides at most what ONE step-up covers (the endpoint refuses more), in page order —
    /// a full page of requests is never held beyond what the approver can confirm.
    /// </summary>
    [Fact]
    public void OneClick_DecidesAtMostWhatOneStepUpCovers()
    {
        var page = Enumerable.Range(0, InboxQueries.LegLimit + 20).ToArray();
        InboxLayoutArea.DecideBatch(page).Should().Equal(page.Take(StepUpPaths.MaxTargets), "the first ones, in page order");
        InboxLayoutArea.DecideBatch(page[..3]).Should().Equal(page[..3], "a small selection is decided whole");
        StepUpPaths.MaxTargets.Should().BeLessThan(InboxQueries.LegLimit, "otherwise the cap would never bite and this test proves nothing");
    }

    private const string LedgerType = InboxQueries.ApprovalNodeType;

    /// <summary>
    /// The approvals ledger legs resolved for <paramref name="owner"/>: refused only when they are not
    /// core's own viewer-keyed shape, and brought by the Approvals source in the bands after Needs you.
    /// </summary>
    [Fact]
    public void TheLedgerLegs_AreViewerKeyed_BoundedByTheWindow_AndAfterWaitingOnMe()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var sections = InboxQueries.Resolve(InboxQueries.BuiltIn, "alice", now);
        var ledger = sections.Where(l => InboxQueries.LedgerKindOf(l.Query) is not null).ToArray();

        ledger.Should().HaveCount(3);
        ledger.Should().OnlyContain(l => l.Source == InboxQueries.ApprovalsSource && l.ShownByDefault);
        ledger.Count(l => InboxQueries.LedgerKindOf(l.Query) == ApprovalLedgerKind.Requested).Should().Be(1);
        ledger.Where(l => InboxQueries.LedgerKindOf(l.Query) == ApprovalLedgerKind.Decided)
            .Should().OnlyContain(l => l.Query.Contains("content.approvalDate:>=2026-10-04 "), "14 days before the clock");
        // As the page orders them: what waits on the viewer first, then what they are waiting for, then what was decided.
        InboxLayoutArea.Sections(InboxQueries.BuiltIn, "alice").Where(s => s.Source == InboxQueries.ApprovalsSource)
            .Select(s => s.Band).Should().Equal(
                InboxBand.NeedsYou, InboxBand.NeedsYou, InboxBand.Running, InboxBand.Recent, InboxBand.Recent);
    }

    /// <summary>
    /// Only core's exact ledger shapes may read across partitions: a contributed leg keyed to someone
    /// else, unkeyed, or for another type is refused, as is any unanchored leg.
    /// </summary>
    [Theory]
    [InlineData("nodeType:Approvals/Approval content.requester:bob partitions:all")]
    [InlineData("nodeType:Approvals/Approval partitions:all")]
    [InlineData("nodeType:Approvals/Approval content.requester:alice content.approver:alice partitions:all")]
    [InlineData("nodeType:Approvals/Approval content.requester:alice namespace:bob partitions:all")]
    [InlineData("nodeType:Approvals/Approval content.requester:alice")]
    public void AContributedCrossPartitionLeg_IsRefused(string query)
    {
        var leg = new InboxQueryLeg { Source = "Mine", Query = query.Replace("alice", InboxQueries.ViewerToken) };
        InboxQueries.Resolve([leg], "alice").Should().BeEmpty();
        InboxQueries.Refused([leg], "alice").Should().ContainSingle();
    }

    /// <summary>
    /// "Requested by me" holds only the viewer's still-pending requests, wherever the document lives:
    /// not a decided one, and not another person's.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task RequestedByMe_ListsOnlyTheViewersPendingRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLedger(ct, "ledger-req");
        var now = DateTimeOffset.UtcNow;
        await Approve("ledger-req-doc-a", "r1", "ledger-req-alice", "ledger-req-bob", ApprovalStatus.Pending, null, ct);
        await Approve("ledger-req-doc-b", "r2", "ledger-req-alice", "ledger-req-carol", ApprovalStatus.Pending, null, ct);
        await Approve("ledger-req-doc-a", "r3", "ledger-req-alice", "ledger-req-bob", ApprovalStatus.Approved, now.AddDays(-1), ct);
        await Approve("ledger-req-doc-b", "r4", "ledger-req-bob", "ledger-req-alice", ApprovalStatus.Pending, null, ct);

        var kind = ApprovalLedgerKind.Requested;
        (await LedgerIds("ledger-req-alice", kind, ids => ids.Length >= 2, ct)).Should().Equal(["r1", "r2"]);
        (await LedgerIds("ledger-req-bob", kind, ids => ids.Length >= 1, ct)).Should().Equal(["r4"],
            "the other person's request is theirs, not the approver's to see here");
        (await LedgerIds("ledger-req-carol", kind, ids => true, ct)).Should().BeEmpty("carol requested nothing");
    }

    /// <summary>
    /// "Decided" shows the approvals the viewer asked OR was asked, decided inside the window, with the
    /// outcome and the approver; an older decision and a stranger's are not listed.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Decided_ShowsOutcomeAndApprover_WithinTheWindow_ForRequesterAndApprover()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLedger(ct, "ledger-dec");
        var now = DateTimeOffset.UtcNow;
        await Approve("ledger-dec-doc-a", "d1", "ledger-dec-alice", "ledger-dec-bob", ApprovalStatus.Approved, now.AddDays(-2), ct);
        await Approve("ledger-dec-doc-b", "d2", "ledger-dec-carol", "ledger-dec-alice", ApprovalStatus.Rejected, now.AddDays(-3), ct);
        await Approve("ledger-dec-doc-a", "old", "ledger-dec-alice", "ledger-dec-bob", ApprovalStatus.Approved, now.AddDays(-30), ct);
        await Approve("ledger-dec-doc-b", "other", "ledger-dec-carol", "ledger-dec-bob", ApprovalStatus.Approved, now.AddDays(-1), ct);
        await Approve("ledger-dec-doc-b", "waiting", "ledger-dec-alice", "ledger-dec-bob", ApprovalStatus.Pending, null, ct);

        (await LedgerIds("ledger-dec-alice", ApprovalLedgerKind.Decided, ids => ids.Length >= 2, ct)).Should().Equal(["d1", "d2"]);

        var rows = await LedgerRows("ledger-dec-alice", ApprovalLedgerKind.Decided, "UTC", ct);
        rows.Single(r => r.Path.EndsWith("/d1")).From.Should().Be("Approved by ledger-dec-bob");
        rows.Single(r => r.Path.EndsWith("/d2")).From.Should().Be("Rejected by ledger-dec-alice");
        rows.Should().OnlyContain(r => r.Band == "Recent");
    }

    /// <summary>The decided time is shown in the VIEWER's zone, not the container's UTC.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheDecidedTime_IsShownInTheViewersZone()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLedger(ct, "ledger-tz");
        var decided = DateTimeOffset.UtcNow.AddDays(-1).Date.AddHours(23).AddMinutes(30);
        var at = new DateTimeOffset(decided, TimeSpan.Zero);
        await Approve("ledger-tz-doc-a", "z1", "ledger-tz-alice", "ledger-tz-bob", ApprovalStatus.Approved, at, ct);

        var utc = (await LedgerRows("ledger-tz-alice", ApprovalLedgerKind.Decided, "UTC", ct)).Single().When;
        var auckland = (await LedgerRows("ledger-tz-alice", ApprovalLedgerKind.Decided, "Pacific/Auckland", ct)).Single().When;
        var la = (await LedgerRows("ledger-tz-alice", ApprovalLedgerKind.Decided, "America/Los_Angeles", ct)).Single().When;

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        utc.Should().Be(DisplayTimeExtensions.ToDisplayTime(at, "UTC").ToString("g", culture));
        auckland.Should().Be(DisplayTimeExtensions.ToDisplayTime(at, "Pacific/Auckland").ToString("g", culture))
            .And.NotBe(utc, "Auckland is 12-13 hours ahead of UTC and crosses the date");
        la.Should().Be(DisplayTimeExtensions.ToDisplayTime(at, "America/Los_Angeles").ToString("g", culture)).And.NotBe(utc);
    }

    /// <summary>A pending request says who it waits on.</summary>
    [Fact(Timeout = 60000)]
    public async Task APendingRequest_SaysWhoItWaitsOn()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLedger(ct, "ledger-who");
        await Approve("ledger-who-doc-a", "w1", "ledger-who-alice", "ledger-who-bob", ApprovalStatus.Pending, null, ct);
        var rows = await LedgerRows("ledger-who-alice", ApprovalLedgerKind.Requested, "UTC", ct);
        rows.Single().From.Should().Be("Requested from ledger-who-bob");
        rows.Single().Band.Should().Be("Running");
    }

    private async Task SeedLedger(CancellationToken ct, string prefix)
    {
        foreach (var user in new[] { "alice", "bob", "carol" })
            await CreateUserAsync($"{prefix}-{user}", ct);
        foreach (var doc in new[] { "doc-a", "doc-b" })
            await CreateAsync(new MeshNode($"{prefix}-{doc}") { NodeType = ApprovalType, Name = doc }, ct);
    }

    private Task Approve(string doc, string id, string requester, string approver, ApprovalStatus status, DateTimeOffset? at, CancellationToken ct)
        => CreateAsync(new MeshNode(id, $"{doc}/_Approval")
        {
            NodeType = LedgerType, Name = $"Approval: {id}", MainNode = doc, State = MeshNodeState.Active,
            Content = new Approval
            {
                Id = id, PrimaryNodePath = doc, Requester = requester, Approver = approver, Purpose = id,
                Status = status, ApprovalDate = at, CreatedAt = (at ?? DateTimeOffset.UtcNow).AddHours(-1),
            },
        }, ct);

    private ImmutableArray<InboxSection> LedgerSections(string owner, ApprovalLedgerKind kind)
        => [.. InboxLayoutArea.Sections(InboxQueries.BuiltIn, owner).Where(s => s.Ledger == kind)];

    private async Task<string[]> LedgerIds(string owner, ApprovalLedgerKind kind, Func<string[], bool> until, CancellationToken ct)
        => await LedgerSections(owner, kind)
            .Select(section => InboxLayoutArea.Nodes(Mesh, section, string.Empty, owner))
            .CombineLatest()
            .Select(legs => legs.SelectMany(nodes => nodes.Select(n => n.Id)).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray())
            .Where(until)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private async Task<InboxRow[]> LedgerRows(string owner, ApprovalLedgerKind kind, string zone, CancellationToken ct)
    {
        var sections = LedgerSections(owner, kind);
        var texts = InboxApprovalLedger.Texts.Of("Requested from {0}", "Approved by {0}", "Rejected by {0}");
        await LedgerIds(owner, kind, ids => ids.Length > 0, ct);
        return await sections
            .Select(section => InboxLayoutArea.Nodes(Mesh, section, string.Empty, owner)
                .Select(nodes => nodes.Select(node => InboxApprovalLedger.Decorate(
                    InboxRows.Of(node, section, zone, null, section.Band, 0, "Approvals", System.Globalization.CultureInfo.InvariantCulture, Mesh.JsonSerializerOptions),
                    node, section.Ledger, texts, Mesh.JsonSerializerOptions)).ToArray()))
            .CombineLatest()
            .Select(legs => legs.SelectMany(rows => rows).GroupBy(r => r.Path).Select(g => g.First()).ToArray())
            .Where(rows => rows.Length > 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
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
