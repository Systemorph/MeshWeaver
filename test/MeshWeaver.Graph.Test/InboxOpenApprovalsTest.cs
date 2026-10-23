using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// What the inbox shows by default and what it keeps: an approval request is the APPROVAL (listed only while it
/// awaits a decision, opening it goes to the approval), the Kind filter is the person's own and persists, a
/// source is listed only when the person configured it, and a multi-account source is filtered by ADDRESS.
/// Owner, 2026-10-23: "something announcing i had an approval?! wtf?! bring me to approvals … i should just see
/// approvals which are not approved yet … default only open approvals and important notifications … i should not
/// see those, only if they are really configured … for mail etc they should be by address."
/// </summary>
public class InboxOpenApprovalsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ActionType = "TestAction";
    private const string GridArea = InboxLayoutArea.AreaName + "/Main/Grid";

    /// <summary>The shape of an approval-gated action: undecided until an approver is stamped (an instance action's).</summary>
    public sealed record TestActionContent
    {
        /// <summary>Who approved it; absent while it awaits an approver.</summary>
        public string? ApprovedBy { get; init; }
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode("Approval", "Approvals") { HubConfiguration = config => config.WithContentType<Approval>() },
                new MeshNode(ActionType) { HubConfiguration = config => config.WithContentType<TestActionContent>() })
            // A provider that declared no connection and is not always on: the "forgetful" leg of requirement 4.
            .ConfigureServices(services => services.AddSingleton(Forgetful));

    private static readonly InboxQueryLeg Forgetful = new()
    {
        Source = "Forgetful",
        Query = $"namespace:{InboxQueries.ViewerToken}/_Forgetful nodeType:{EmailNodeType.NodeType} sort:LastModified-desc {InboxQueries.RowProjection}",
    };

    // ── 1 + 2: the approval is the row, and only while it awaits ───────────────────────────────

    /// <summary>
    /// The request for an approval is listed while the approval awaits its approver and leaves the list once the
    /// approval is decided — wherever it was decided, the notification is never touched. A request whose target
    /// is gone has nothing left to decide. The open one is the negative control: a list that drops everything
    /// would pass the other halves.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AnApprovalRequest_IsListedWhileItsTargetAwaits_AndDropsOutOnceDecided()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-approvals";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("open", "TestActions") { NodeType = ActionType, Name = "open", Content = new TestActionContent() }, ct);
        await CreateAsync(new MeshNode("decided", "TestActions") { NodeType = ActionType, Name = "decided", Content = new TestActionContent { ApprovedBy = "someone-else" } }, ct);
        await CreateAsync(new MeshNode("record", "TestApprovals") { NodeType = InboxQueries.ApprovalNodeType, Name = "record",
            Content = new Approval { Approver = owner, Status = ApprovalStatus.Approved, ApprovalDate = DateTimeOffset.UtcNow } }, ct);
        var openRequest = await Notify(owner, "n-open", NotificationType.ApprovalRequired, "TestActions/open", ct);
        await Notify(owner, "n-decided", NotificationType.ApprovalRequired, "TestActions/decided", ct);
        await Notify(owner, "n-record", NotificationType.ApprovalRequired, "TestApprovals/record", ct);
        await Notify(owner, "n-gone", NotificationType.ApprovalRequired, "TestActions/gone", ct);
        // Every kind and state: this test is about which requests survive, not about the defaults.
        await InboxPrefs.SetAsync(Mesh, owner, p => p, ct);

        var rows = Open(owner).GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxRows.GridId)));
        var paths = await rows.Select(r => r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.GetProperty("path").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray() : [])
            .Where(p => p.Length > 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        // The decided Approval RECORD is the ledger's own row (Recent): it is listed as the approval it is, once —
        // and the notification that announced it is not.
        paths.Should().Equal(new[] { openRequest, "TestApprovals/record" }.OrderBy(x => x, StringComparer.Ordinal),
            "only the request whose approval still awaits is listed — not a decided one, not one whose approval is gone");
        var row = await rows.Where(r => r.ValueKind == JsonValueKind.Array && r.EnumerateArray().Any(x => x.GetProperty("path").GetString() == openRequest))
            .Select(r => r.EnumerateArray().First(x => x.GetProperty("path").GetString() == openRequest))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        row.GetProperty("target").GetString().Should().Be("TestActions/open", "the row knows the approval it announces");
    }

    /// <summary>
    /// A notification raised for the approvals feature is never a row of the Notifications source, whatever its
    /// type: what became of an approval is the ledger's row. Here a decided "approval given" notification (a
    /// stamped type the old source listed) stays out even with every kind and the quiet notifications shown.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AnApprovalNotification_IsNeverARowOfTheNotificationsSource()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-approval-notice";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("decided", "TestActions") { NodeType = ActionType, Name = "decided", Content = new TestActionContent { ApprovedBy = "someone-else" } }, ct);
        await Notify(owner, "given", NotificationType.ApprovalGiven, "TestActions/decided", ct);
        await Notify(owner, "stamped-general", NotificationType.General, "TestActions/decided", ct, feature: NotificationFeatures.Approvals);
        var plain = await Notify(owner, "plain", NotificationType.System, null, ct);
        await InboxPrefs.SetAsync(Mesh, owner, p => p with { ShowIgnorable = true }, ct);

        var paths = await Open(owner).GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxRows.GridId)))
            .Select(r => r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.GetProperty("path").GetString()!).ToArray() : [])
            .Where(p => p.Length > 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        paths.Should().Equal([plain], "an ordinary notification is the control; the two approval notifications are not rows");

        var legs = InboxQueries.Resolve(InboxQueries.BuiltIn, owner).Where(l => l.Source == InboxQueries.NotificationsSource).ToArray();
        legs.Should().OnlyContain(l => l.Query.Contains("-content.feature:approvals") && !l.Query.Contains(nameof(NotificationType.ApprovalGiven)));
    }

    /// <summary>The decided/open rule itself: a status or a stamped approver/decision time decides; nothing stamped awaits.</summary>
    [Theory]
    [InlineData("""{"status":"Approved"}""", false)]
    [InlineData("""{"status":"Rejected"}""", false)]
    [InlineData("""{"status":"Expired"}""", false)]
    [InlineData("""{"approvedBy":"jane"}""", false)]
    [InlineData("""{"approvedAt":"2026-10-21T06:22:13+00:00"}""", false)]
    [InlineData("""{"approvalDate":"2026-10-21T06:22:13+00:00"}""", false)]
    [InlineData("""{"rejectedBy":"jane"}""", false)]
    [InlineData("""{"status":"Pending"}""", true)]
    [InlineData("""{"title":"roll","approvedBy":""}""", true)]
    [InlineData("""{}""", true)]
    public void ATarget_AwaitsUnlessItsContentSaysItWasDecided(string content, bool awaits)
    {
        var node = new MeshNode("x", "T") { Content = JsonDocument.Parse(content).RootElement.Clone() };
        InboxApprovalTargets.IsPending(node, null).Should().Be(awaits);
    }

    // ── 1: opening it goes to the approval ────────────────────────────────────────────────────

    /// <summary>The click on an approval request opens the approval; every other row opens in the reading view.</summary>
    [Fact]
    public void AClickOnAnApprovalRequest_OpensTheApproval_NotTheNotification()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = new InboxRow { Path = "alice/_Notification/n1", NodeType = "Notification", Target = "Ops/Actions/roll" };
        InboxLayoutArea.OpenTarget(request, options, "alice", _ => false).Should().Be(("/Ops/Actions/roll", false));
        InboxLayoutArea.OpenTarget(request with { Target = string.Empty }, options, "alice", _ => false)!.Value.Url
            .Should().Be(InboxReadingView.Url("alice", "alice/_Notification/n1"), "a row that announces nothing opens in the reading view");
    }

    /// <summary>
    /// The inbox address of an approval request's notification (<c>/{owner}/Inbox/{id}</c>, what a link in an old
    /// mail or a bookmark holds) redirects to its target; an ordinary notification still shows its own page.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TheInboxAddressOfAnApprovalRequest_RedirectsToItsTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-redirect";
        await CreateUserAsync(owner, ct);
        var request = await Notify(owner, "req", NotificationType.ApprovalRequired, "TestActions/open", ct);
        var plain = await Notify(owner, "plain", NotificationType.General, "TestActions/open", ct);
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = owner, Name = owner, Email = $"{owner}@meshweaver.io" });

        ISynchronizationStream<JsonElement> Item(string path) => GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(owner), new LayoutAreaReference(InboxLayoutArea.AreaName) { Id = InboxReadingView.ItemId(path) });

        var redirect = await Item(request).GetControlStream(InboxLayoutArea.AreaName).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is RedirectControl, "the approval request redirects", ct);
        ((RedirectControl)redirect!).Href.Should().Be("/TestActions/open");
        // The negative control: the same target on a notification that is NOT an approval request is not followed.
        var own = await Item(plain).GetControlStream($"{InboxLayoutArea.AreaName}/Item").Should().Within(TestTimeouts.Convergence)
            .Match(c => c is LayoutAreaControl, "an ordinary notification shows its own page", ct);
        own.Should().NotBeNull();
    }

    /// <summary>
    /// A decision taken on the approval's OWN page never touches the notification — and an inbox that is already
    /// open drops the request all the same: the target is observed live through its own stream, not read once.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ADecisionTakenElsewhere_DropsTheRequestFromAnOpenInbox()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-live";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("live", "TestActions") { NodeType = ActionType, Name = "live", Content = new TestActionContent() }, ct);
        var request = await Notify(owner, "n-live", NotificationType.ApprovalRequired, "TestActions/live", ct);
        await InboxPrefs.SetAsync(Mesh, owner, p => p, ct);
        var rows = RowPaths(Open(owner));

        (await rows.Where(p => p.Length > 0).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Equal([request], "listed while it awaits");

        // The approval is decided on its own page: only the TARGET changes.
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream("TestActions/live")
                .Update(node => node with { Content = new TestActionContent { ApprovedBy = "someone-else" } }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        (await rows.Where(p => p.Length == 0).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().BeEmpty("the open inbox follows the approval, with the notification untouched");
    }

    /// <summary>
    /// An approval deleted while its request is listed leaves the list, and one RECREATED afterwards — awaiting again — brings
    /// the request back: the observation follows the node's whole lifecycle and a deletion ends nothing for good.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AnApprovalDeletedAndRecreated_DropsTheRequest_ThenBringsItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-recreate";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("again", "TestActions") { NodeType = ActionType, Name = "again", Content = new TestActionContent() }, ct);
        var request = await Notify(owner, "n-again", NotificationType.ApprovalRequired, "TestActions/again", ct);
        await InboxPrefs.SetAsync(Mesh, owner, p => p, ct);
        var rows = RowPaths(Open(owner));
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        (await rows.Where(p => p.Length > 0).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Equal([request]);
        await access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().DeleteNode("TestActions/again"))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        (await rows.Where(p => p.Length == 0).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeEmpty("nothing is left to decide");
        await CreateAsync(new MeshNode("again", "TestActions") { NodeType = ActionType, Name = "again", Content = new TestActionContent() }, ct);
        (await rows.Where(p => p.Length > 0).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([request], "the recreated approval awaits again, and the request is back");
    }

    /// <summary>
    /// The person's preferences node deleted while the inbox is open does not end the feed: the defaults apply while it is
    /// gone, and a node created again — with another choice — is followed.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task APreferencesNodeDeletedAndRecreated_NeverEndsTheFeed()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-prefs-recreate";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("open", "TestActions") { NodeType = ActionType, Name = "open", Content = new TestActionContent() }, ct);
        var approval = await Notify(owner, "need", NotificationType.ApprovalRequired, "TestActions/open", ct);
        var mail = $"{owner}/{EmailNodeType.UserEmailSegment}/m1";
        await CreateAsync(Mail("m1", $"{owner}/{EmailNodeType.UserEmailSegment}", owner, new Email { Id = "m1", Subject = "Hello" }), ct);
        var approvalsKey = InboxRows.SourceKey("Approvals");
        var mailKey = InboxRows.SourceKey("Mail");
        Task Choose() => InboxPrefs.SetAsync(Mesh, owner, p => p with { Kinds = p.Kinds.SetItem(mailKey, true).SetItem(approvalsKey, false) }, ct);
        await Choose();
        var rows = RowPaths(Open(owner));

        (await rows.Where(p => p.Length > 0).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Equal([mail], "the chosen kinds");
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().DeleteNode(InboxPreferencePaths.PathFor(owner)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        (await rows.Where(p => p.SequenceEqual([approval])).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([approval], "with the node gone the defaults apply — and the feed is still alive");
        await Choose();
        (await rows.Where(p => p.SequenceEqual([mail])).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([mail], "the recreated node is followed");
    }

    /// <summary>
    /// A request written before the feature key existed — and the default type, omitted from stored JSON — is an
    /// approval request all the same: it is filtered by its target, its row names the target, and its inbox
    /// address redirects.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ALegacyApprovalRequest_WithoutAFeature_IsFilteredAndRedirectedLikeAnyOther()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-legacy";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("open", "TestActions") { NodeType = ActionType, Name = "open", Content = new TestActionContent() }, ct);
        await CreateAsync(new MeshNode("decided", "TestActions") { NodeType = ActionType, Name = "decided", Content = new TestActionContent { ApprovedBy = "someone-else" } }, ct);
        var open = await Notify(owner, "legacy-open", NotificationType.ApprovalRequired, "TestActions/open", ct, legacy: true);
        await Notify(owner, "legacy-decided", NotificationType.ApprovalRequired, "TestActions/decided", ct, legacy: true);
        await InboxPrefs.SetAsync(Mesh, owner, p => p, ct);

        (await RowPaths(Open(owner)).Where(p => p.Length > 0).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([open], "the legacy request of a decided approval is dropped, the open one kept");
        var redirect = await GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(owner), new LayoutAreaReference(InboxLayoutArea.AreaName) { Id = InboxReadingView.ItemId(open) })
            .GetControlStream(InboxLayoutArea.AreaName).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is RedirectControl, "a legacy request redirects too", ct);
        ((RedirectControl)redirect!).Href.Should().Be("/TestActions/open");
    }

    /// <summary>
    /// The "capped" notice reads what each leg FETCHED, not what the page kept: a window of decided requests that all
    /// drop out still leaves older open ones unread. (Counting the kept rows read zero here and hid the warning.)
    /// </summary>
    [Fact]
    public void TheCappedNotice_ReadsWhatTheLegFetched_NotWhatThePageKept()
    {
        var query = "namespace:alice/_Notification nodeType:Notification";
        InboxLayoutArea.Capped([(query, InboxQueries.LegLimit)]).Should().BeTrue("a full window may hide older rows, whatever is kept of it");
        InboxLayoutArea.Capped([(query, InboxQueries.LegLimit - 1)]).Should().BeFalse("a window that was not full hides nothing");
        InboxLayoutArea.Capped([(query, 0), (query + " limit:3", 3)]).Should().BeTrue("each leg against ITS OWN bound");
    }

    /// <summary>A comma inside a quoted display name is not a recipient separator; a bare token or garbage still resolves or yields nothing.</summary>
    [Theory]
    [InlineData("\"Roe, Jane\" <Jane.Roe@Example.com>", "jane.roe@example.com")]
    [InlineData("\"Roe, Jane\" <jane.roe@example.com>, Max <max@example.com>", "jane.roe@example.com")]
    [InlineData("max@example.com; other@example.com", "max@example.com")]
    [InlineData("  MAX@example.com ", "max@example.com")]
    [InlineData("not an address", "")]
    [InlineData("", "")]
    public void AnAddress_IsReadAsAMailbox_NotSplitAtAQuotedComma(string recipient, string expected)
        => InboxRows.AddressOf(recipient).Should().Be(expected);

    /// <summary>
    /// A silent source reads as the fallback ONCE; a real answer after it is forwarded; and a source that answers in
    /// time is never followed by the fallback — the saved choice is never replaced by defaults.
    /// </summary>
    [Fact]
    public async Task TheFallback_IsEmittedOnce_AndNeverAfterARealAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var wait = TimeSpan.FromMilliseconds(150);

        var timely = new System.Reactive.Subjects.Subject<string>();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var first = InboxLayoutArea.FirstAnswerWithin(timely, wait, "fallback").Subscribe(seen.Enqueue);
        timely.OnNext("saved");
        await Task.Delay(wait * 3, ct);
        seen.Should().Equal(["saved"], "an answer in time suppresses the fallback for good");

        var silent = new System.Reactive.Subjects.Subject<string>();
        var late = new System.Reactive.Subjects.ReplaySubject<string>();
        using var second = InboxLayoutArea.FirstAnswerWithin(silent, wait, "fallback").Subscribe(late);
        (await late.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be("fallback", "a silent source reads as the fallback, once");
        silent.OnNext("saved");
        (await late.Take(2).ToList().FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal(["fallback", "saved"], "a late real answer still replaces it");
    }

    /// <summary>A source that COMPLETES without an answer reads as the fallback, once, and ends — the consumer never waits on a silence.</summary>
    [Fact]
    public async Task ASourceThatCompletesSilently_ReadsAsTheFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        (await InboxLayoutArea.FirstAnswerWithin(Observable.Empty<string>(), TimeSpan.FromMinutes(5), "fallback").ToList()
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Equal(["fallback"]);
        (await InboxLayoutArea.FirstAnswerWithin(Observable.Return("saved"), TimeSpan.FromMinutes(5), "fallback").ToList()
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Equal(["saved"], "an answered source is not followed by the fallback");
    }

    /// <summary>
    /// "N items needing you are hidden" counts only what Show all can reveal: a mail hidden by its switched-off SOURCE
    /// is counted while its kind is ticked; with its kind unticked it stays hidden after the action, so it is not counted.
    /// </summary>
    [Fact]
    public void TheHiddenNotice_CountsOnlyWhatShowAllCanReveal()
    {
        var mail = new InboxRow { Path = "m", Category = "Mail", SourceId = "Mail", Rank = 0, Stamp = "2026-10-21T06:22:13.0000000Z" };
        var off = new InboxFilter { Sources = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.SourceKey("Mail"), false) };
        InboxRows.HiddenObligations([mail], off, needsYouRank: 0).Should().Be(1, "no kind restricts: switching the source on shows it");
        InboxRows.HiddenObligations([mail], off with { Kinds = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.SourceKey("Mail"), true) }, needsYouRank: 0)
            .Should().Be(1, "its kind is ticked: switching the source on shows it");
        InboxRows.HiddenObligations([mail], off with { Kinds = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.SourceKey("Approvals"), true) }, needsYouRank: 0)
            .Should().Be(0, "its kind is not ticked: Show all would not reveal it, so the notice does not advertise it");
    }

    /// <summary>
    /// Presence is read from the listing's answers and never conflated: a matching row is present, a COMPLETE empty answer
    /// is absent, and an INCOMPLETE empty one (a silent provider, an unfinished storage read) says nothing — so no node
    /// stream is opened on it.
    /// </summary>
    [Fact]
    public void Presence_KeepsAnIncompleteEmptyAnswer_ApartFromAConfirmedOne()
    {
        QueryResultChange<MeshNode> Change(QueryChangeType type, bool silent = false, bool incomplete = false, params MeshNode[] items)
            => new() { ChangeType = type, Items = [.. items], SilentProviders = silent ? ["Postgres"] : null, SnapshotIncomplete = incomplete };
        var node = new MeshNode("n", "x");
        InboxApprovalTargets.Fold(null, Change(QueryChangeType.Initial, items: node)).Should().BeTrue("a matching row was observed");
        InboxApprovalTargets.Fold(null, Change(QueryChangeType.Initial)).Should().BeFalse("a complete empty answer: absent");
        InboxApprovalTargets.Fold(false, Change(QueryChangeType.Initial, silent: true)).Should().BeNull("a silent provider says nothing");
        InboxApprovalTargets.Fold(false, Change(QueryChangeType.Initial, incomplete: true)).Should().BeNull("an unfinished read says nothing");
        InboxApprovalTargets.Fold(null, Change(QueryChangeType.Added, items: node)).Should().BeTrue();
        InboxApprovalTargets.Fold(true, Change(QueryChangeType.Removed, items: node)).Should().BeFalse("deleted while observed");
    }

    /// <summary>
    /// A presence snapshot that COMPLETES (a finite provider listing) must not end the content read it opened for a
    /// `true`: the node's later state still arrives. Here presence is one finite `true` and the approval is decided
    /// afterwards — the observation says "awaiting", then "decided", and only then would it have ended.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AFinitePresenceSnapshot_DoesNotEndTheContentRead()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-finite";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("finite", "TestActions") { NodeType = ActionType, Name = "finite", Content = new TestActionContent() }, ct);
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = owner, Name = owner, Email = $"{owner}@meshweaver.io" });

        var seen = new System.Reactive.Subjects.ReplaySubject<bool>();
        using var subscription = InboxApprovalTargets.ObserveNode(Mesh, null, "TestActions/finite", Observable.Return<bool?>(true),
            node => node is not null && InboxApprovalTargets.IsPending(node, Mesh.JsonSerializerOptions), absent: false).Subscribe(seen);
        (await seen.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeTrue("awaiting");

        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream("TestActions/finite")
                .Update(node => node with { Content = new TestActionContent { ApprovedBy = "someone-else" } }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        (await seen.Where(pending => !pending).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().BeFalse("the later state arrives although the presence snapshot is long complete");
    }

    /// <summary>Only a node's OWN definitive "gone" signal reads as absence; a failure carrying similar text is a fault and propagates.</summary>
    [Fact]
    public void OnlyTheNodesOwnDeletionSignal_ReadsAsAbsence()
    {
        const string path = "Ops/Actions/roll";
        InboxApprovalTargets.IsGone(new MeshNodeReadEndedWithDeleteException(path), path).Should().BeTrue("the read ended by the node's deletion");
        InboxApprovalTargets.IsGone(new MeshNodeReadEndedWithDeleteException("Ops/Actions/other"), path).Should().BeFalse("another node's deletion");
        var routingMiss = new DeliveryFailureException(new DeliveryFailure(null!, $"No node found at '{path}'. Closest ancestor is 'Ops'")
        {
            ErrorType = ErrorType.NotFound,
        });
        InboxApprovalTargets.IsGone(routingMiss, path).Should().BeTrue("the router's NotFound for exactly this path");
        InboxApprovalTargets.IsGone(routingMiss, path + "x").Should().BeFalse("a different path");
        InboxApprovalTargets.IsGone(new DeliveryFailureException(new DeliveryFailure(null!, $"No node found at '{path}'")
        {
            ErrorType = ErrorType.Failed,
        }), path).Should().BeFalse("a failed route with similar text is not an authoritative NotFound");
        InboxApprovalTargets.IsGone(new InvalidOperationException($"No node found at '{path}'"), path).Should().BeFalse("an unrelated exception is a fault");
    }

    /// <summary>A path with a space or a query metacharacter is ONE path in the existence listing, never a path and a free-text term.</summary>
    [Theory]
    [InlineData("Ops/My Request")]
    [InlineData("Ops/Actions/roll-pearl-3-1-11315")]
    [InlineData("Ops/a:b (c)")]
    public void TheExistenceListing_QuotesThePath(string path)
    {
        var parsed = new QueryParser().Parse(InboxApprovalTargets.ExistsQuery(path));
        parsed.Path.Should().Be(path, "the whole path is the anchor");
        parsed.TextSearch.Should().BeNullOrEmpty("nothing of the path leaks into the free-text search");
    }

    // ── 3: the default, and the persisted choice ──────────────────────────────────────────────

    /// <summary>
    /// A person who never chose gets the open approvals and the important notifications: the approval kind and
    /// the notification kind ticked, every other kind unticked, only the open state group, every source shown.
    /// </summary>
    [Fact]
    public void TheDefault_TicksOpenApprovalsAndImportantNotificationsOnly()
    {
        var sections = InboxLayoutArea.Sections(InboxQueries.BuiltIn, "alice");
        var defaults = InboxPreferencesNodeType.Defaults(sections);
        defaults.Kinds[InboxRows.SourceKey("Approvals")].Should().BeTrue();
        defaults.Kinds[InboxRows.SourceKey("Notifications")].Should().BeTrue();
        defaults.Kinds[InboxRows.SourceKey("Mail")].Should().BeFalse("a provider's kind is one tick away");
        defaults.Kinds[InboxRows.SourceKey("Activities")].Should().BeFalse();
        defaults.States[InboxRows.OpenKey].Should().BeTrue("only what is still open");
        defaults.States[InboxRows.DoneKey].Should().BeFalse();
        defaults.Sources.Values.Should().OnlyContain(on => on, "every source is shown; the kinds and states narrow");
        defaults.ShowIgnorable.Should().BeFalse();
    }

    /// <summary>What the default lists: the awaiting approval and the important notification — not a decided approval, a quiet notification, or mail.</summary>
    [Fact]
    public void TheDefaultFilter_ListsOpenApprovals_AndImportantNotifications_Only()
    {
        var sections = InboxLayoutArea.Sections(InboxQueries.BuiltIn, "alice");
        var filter = InboxLayoutArea.WithPreferences(new InboxFilter(), InboxPreferencesNodeType.Defaults(sections));
        InboxRow Row(string path, string category, string state, string importance = "", bool unread = false) => new()
        {
            Path = path, Category = category, SourceId = category, State = state, Importance = importance, Unread = unread,
            Stamp = "2026-10-21T06:22:13.0000000Z",
        };
        var rows = new[]
        {
            Row("awaiting", "Approvals", InboxState.Awaiting),
            Row("decided", "Approvals", InboxState.Approved),
            Row("important", "Notifications", InboxState.Unread, InboxImportance.Important, unread: true),
            Row("quiet", "Notifications", InboxState.Unread, InboxImportance.Ignorable, unread: true),
            Row("mail", "Mail", InboxState.Unread, unread: true),
            Row("failed-run", "Activities", InboxState.Failed),
        };
        InboxRows.Apply(rows, filter).Select(r => r.Path).OrderBy(x => x, StringComparer.Ordinal).Should().Equal("awaiting", "important");
    }

    /// <summary>
    /// A notification is important when it is a platform/system event and quiet otherwise — and a triage's own
    /// judgement wins either way. Any other source is judged by its own triage, or not at all.
    /// </summary>
    [Fact]
    public void ANotification_IsImportantWhenItIsASystemEvent_AndQuietOtherwise()
    {
        var section = new InboxSection(InboxBand.NeedsYou, InboxQueries.NotificationsSource, 10, "q") { TimeField = "createdAt" };
        InboxRow Of(object content, InboxSection? s = null) => InboxRows.Of(new MeshNode("n", "alice/_Notification") { Name = "n", Content = JsonSerializer.SerializeToElement(content) }, s ?? section, null, null);
        Of(new { notificationType = "System" }).Importance.Should().Be(InboxImportance.Important);
        Of(new { notificationType = "General" }).Importance.Should().Be(InboxImportance.Ignorable);
        Of(new { notificationType = "ChatReady" }).Importance.Should().Be(InboxImportance.Ignorable);
        Of(new { notificationType = "General", importance = "important" }).Importance.Should().Be(InboxImportance.Important, "a triage's judgement wins");
        Of(new { notificationType = "General" }, section with { Source = "Mail" }).Importance.Should().BeEmpty("only notifications are judged by their type");
    }

    /// <summary>
    /// The first open of a person who never chose creates their preferences node holding the defaults, and the
    /// default view lists exactly the open approval and the important notification.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TheFirstOpen_StoresTheDefaults_AndListsOnlyWhatMatters()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-default";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("open", "TestActions") { NodeType = ActionType, Name = "open", Content = new TestActionContent() }, ct);
        var approval = await Notify(owner, "need", NotificationType.ApprovalRequired, "TestActions/open", ct);
        var important = await Notify(owner, "alarm", NotificationType.System, null, ct);
        await Notify(owner, "chatter", NotificationType.General, null, ct);
        await CreateAsync(Mail("m1", $"{owner}/{EmailNodeType.UserEmailSegment}", owner, new Email { Id = "m1", Subject = "Hello", To = "jane.roe@example.com" }), ct);

        var rows = Open(owner).GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxRows.GridId)));
        var paths = await rows.Select(r => r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.GetProperty("path").GetString()!).OrderBy(x => x).ToArray() : [])
            .Where(p => p.Length > 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        paths.Should().Equal(new[] { approval, important }.OrderBy(x => x, StringComparer.Ordinal), "the open approval and the important notification — not the chatter, not the mail");

        var stored = await InboxPrefs.ReadAsync(Mesh, owner, p => p.Kinds.Count > 0, ct);
        stored.Kinds[InboxRows.SourceKey("Approvals")].Should().BeTrue();
        stored.Kinds[InboxRows.SourceKey("Mail")].Should().BeFalse();
        stored.States[InboxRows.OpenKey].Should().BeTrue();
    }

    /// <summary>
    /// The choice outlives the page: a person who ticked other kinds finds them ticked in a NEW session — the page
    /// reads the node, it does not reset it — and the very same open session follows a change to the node live.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task APersistedChoice_IsReappliedByANewSession_AndFollowedLive()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-persist";
        await CreateUserAsync(owner, ct);
        await CreateAsync(new MeshNode("open", "TestActions") { NodeType = ActionType, Name = "open", Content = new TestActionContent() }, ct);
        var approval = await Notify(owner, "need", NotificationType.ApprovalRequired, "TestActions/open", ct);
        var mail = $"{owner}/{EmailNodeType.UserEmailSegment}/m1";
        await CreateAsync(Mail("m1", $"{owner}/{EmailNodeType.UserEmailSegment}", owner, new Email { Id = "m1", Subject = "Hello" }), ct);
        var approvalsKey = InboxRows.SourceKey("Approvals");
        var mailKey = InboxRows.SourceKey("Mail");

        // The person's earlier choice: mail yes, approvals no, done too.
        await InboxPrefs.SetAsync(Mesh, owner, p => p with
        {
            Kinds = p.Kinds.SetItem(mailKey, true).SetItem(approvalsKey, false),
            States = p.States.SetItem(InboxRows.OpenKey, true).SetItem(InboxRows.DoneKey, true),
        }, ct);

        var first = RowPaths(Open(owner));
        (await first.Where(p => p.Length > 0).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([mail], "the stored choice is applied on open: mail ticked, approvals not");
        (await InboxPrefs.ReadAsync(Mesh, owner, p => p.Kinds.Count > 0, ct)).Kinds[mailKey].Should().BeTrue("opening never resets the choice");

        // Live: the same session follows the node.
        await InboxPrefs.SetAsync(Mesh, owner, p => p with { Kinds = p.Kinds.SetItem(approvalsKey, true).SetItem(mailKey, false) }, ct);
        (await first.Where(p => p.SequenceEqual([approval])).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([approval], "a change to the node re-feeds the grid with no re-render");
    }

    // ── 4: only configured sources ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every leg core brings is either the platform's own (always on) or names the query that finds its
    /// connection — the declaration is the leg's, so no source can be listed for being forgotten.
    /// </summary>
    [Fact]
    public void EveryBuiltInLeg_IsAlwaysOn_OrNamesItsConnection()
    {
        InboxQueries.BuiltIn.Should().OnlyContain(l => l.AlwaysOn || !string.IsNullOrWhiteSpace(l.ConnectedWhen));
        InboxQueries.BuiltIn.Where(l => l.AlwaysOn).Select(l => l.Source).Distinct().OrderBy(x => x, StringComparer.Ordinal)
            .Should().Equal(["Activities", "Approvals", "Notifications"], "the platform's own sources need no connection");
        InboxQueries.BuiltIn.Single(l => l.Source == "Mail" && l.Unread).ConnectedWhen.Should().NotBeNullOrEmpty("mail is a provider");
    }

    /// <summary>
    /// A source is connected when it is always on or its connection query finds a node. A source that declares
    /// NEITHER is not connected — the old reading ("no query means listed") showed it for everyone. The
    /// always-on and the found connection are the negative controls.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ASourceWithNoConnectionDeclared_IsNotConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-connected";
        await CreateUserAsync(owner, ct);
        await CreateAsync(Mail("m1", $"{owner}/{EmailNodeType.UserEmailSegment}", owner, new Email { Id = "m1", Subject = "Hello" }), ct);
        InboxSection Section(string source, string? connectedWhen, bool alwaysOn = false)
            => new(InboxBand.NeedsYou, source, 1, "q") { ConnectedWhen = connectedWhen, AlwaysOn = alwaysOn };
        var sections = ImmutableArray.Create(
            Section("Own", null, alwaysOn: true),
            Section("Forgot", null),
            Section("Nothing", $"namespace:{owner}/_Nowhere nodeType:{EmailNodeType.NodeType} {InboxQueries.ConnectionProbe}"),
            Section("Found", $"namespace:{owner}/{EmailNodeType.UserEmailSegment} nodeType:{EmailNodeType.NodeType} {InboxQueries.ConnectionProbe}"));

        var connected = await InboxLayoutArea.Connected(Mesh, sections, owner).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        connected.OrderBy(x => x, StringComparer.Ordinal).Should().Equal(["Found", "Own"]);
        InboxLayoutArea.Unprobed(sections).Should().Equal(["Own"], "only the platform's own sources are listed before any read answers");
    }

    /// <summary>
    /// End to end: a provider's leg that names no connection is not offered — no switch, no Only entry, and its
    /// rows are not shown even though nodes match its query. The notification source is the control.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AForgetfulProvider_IsNeitherOfferedNorShown()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-forgetful";
        await CreateUserAsync(owner, ct);
        await CreateAsync(Mail("f1", $"{owner}/_Forgetful", owner, new Email { Id = "f1", Subject = "From a forgetful provider" }), ct);
        var important = await Notify(owner, "alarm", NotificationType.System, null, ct);
        await InboxPrefs.SetAsync(Mesh, owner, p => p, ct);
        var stream = Open(owner);

        (await RowPaths(stream).Where(p => p.Length > 0).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([important], "the forgetful provider's row is not listed");
        var styles = await stream.GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxLayoutArea.SourceStylesId)))
            .Should().Within(TestTimeouts.Convergence).Match(o => o.ValueKind == JsonValueKind.Object
                && o.TryGetProperty(InboxRows.SourceKey("Notifications"), out var n) && n.GetString() == string.Empty
                && o.TryGetProperty(InboxRows.SourceKey("Forgetful"), out var f) && f.GetString() == InboxLayoutArea.HiddenStyle,
                "its switch is hidden, the platform's own is shown", ct);
        styles.Should().NotBeNull();
    }

    // ── 5: mail by address ────────────────────────────────────────────────────────────────────

    /// <summary>A stored mail names the address it reached (its recipient); a live mailbox row names its stamped account.</summary>
    [Fact]
    public void ARow_NamesTheAddressItReached()
    {
        var section = new InboxSection(InboxBand.NeedsYou, "Mail", 20, "q") { TimeField = "receivedAt" };
        InboxRow Of(object content) => InboxRows.Of(new MeshNode("m", "alice/_Email") { Name = "m", Content = JsonSerializer.SerializeToElement(content) }, section, null, null);
        Of(new { to = "Jane Roe <Jane.Roe@Example.com>" }).Account.Should().Be("jane.roe@example.com");
        Of(new { to = "max@example.com, other@example.com" }).Account.Should().Be("max@example.com", "the first recipient");
        Of(new { to = "no address here" }).Account.Should().BeEmpty();
        Of(new { account = "Work Mailbox", to = "jane.roe@example.com" }).Account.Should().Be("Work Mailbox", "a stamped account wins");
    }

    /// <summary>One option per configured ADDRESS — each once, whatever its spelling — and picking one lists only what reached it.</summary>
    [Fact]
    public void TheAccountFilter_OffersOneOptionPerAddress_AndNarrowsToIt()
    {
        InboxRow Mail(string path, string account) => new() { Path = path, Category = "Mail", SourceId = "Mail", Account = account, Stamp = "2026-10-21T06:22:13.0000000Z" };
        var rows = new[]
        {
            Mail("a1", "jane.roe@example.com"), Mail("a2", "Jane.Roe@Example.com"), Mail("b1", "max@example.com"),
            new InboxRow { Path = "chat", Category = "Conversations", SourceId = "Chat", Stamp = "2026-10-21T06:22:13.0000000Z" },
        };
        var accounts = InboxLayoutArea.AccountsOf(rows);
        accounts.Select(a => a.Address.ToLowerInvariant()).Should().Equal("jane.roe@example.com", "max@example.com");
        InboxLayoutArea.AccountOptions("All accounts", accounts).Select(o => ((Option<string>)o).Item)
            .Should().Equal(string.Empty, InboxRows.AccountKey("jane.roe@example.com"), InboxRows.AccountKey("max@example.com"));

        InboxRows.Apply(rows, new InboxFilter { Account = InboxRows.AccountKey("JANE.roe@example.com") }).Select(r => r.Path)
            .OrderBy(x => x, StringComparer.Ordinal).Should().Equal(["a1", "a2"], "one address, whatever its case; the chat reached no address");
        InboxRows.Apply(rows, new InboxFilter()).Should().HaveCount(4, "no address picked lists everything");
    }

    /// <summary>End to end: stored mail to two addresses yields two options in the page's Account select, and the preference narrows the grid.</summary>
    [Fact(Timeout = 120000)]
    public async Task TwoMailboxes_GiveTwoAddressOptions_AndThePreferenceNarrowsTheList()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-open-address";
        await CreateUserAsync(owner, ct);
        var ns = $"{owner}/{EmailNodeType.UserEmailSegment}";
        await CreateAsync(Mail("w1", ns, owner, new Email { Id = "w1", Subject = "Work", To = "jane.roe@example.com" }), ct);
        await CreateAsync(Mail("p1", ns, owner, new Email { Id = "p1", Subject = "Private", To = "jane@home.example" }), ct);
        await InboxPrefs.SetAsync(Mesh, owner, p => p, ct);
        var stream = Open(owner);

        var options = await stream.GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxLayoutArea.AccountOptionsId)))
            .Should().Within(TestTimeouts.Convergence).Match(o => o.ValueKind == JsonValueKind.Array && o.GetArrayLength() == 3, "all, then one per address", ct);
        options.EnumerateArray().Select(o => o.GetProperty("text").GetString()).Should().Equal("All accounts", "jane.roe@example.com", "jane@home.example");

        await InboxPrefs.SetAsync(Mesh, owner, p => p with { Account = InboxRows.AccountKey("jane@home.example") }, ct);
        (await RowPaths(stream).Where(p => p.SequenceEqual([$"{ns}/p1"])).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([$"{ns}/p1"], "only the mail that reached the picked address");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private ISynchronizationStream<JsonElement> Open(string owner)
    {
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = owner, Name = owner, Email = $"{owner}@meshweaver.io" });
        return GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(owner), new LayoutAreaReference(InboxLayoutArea.AreaName));
    }

    private static IObservable<string[]> RowPaths(ISynchronizationStream<JsonElement> stream)
        => stream.GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxRows.GridId)))
            .Select(r => r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.GetProperty("path").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray() : []);

    private async Task<string> Notify(string owner, string id, NotificationType type, string? target, CancellationToken ct, string? feature = null, bool legacy = false)
    {
        var ns = $"{owner}/{NotificationService.SatelliteSegment}";
        await CreateAsync(new MeshNode(id, ns)
        {
            NodeType = NotificationNodeType.NodeType, Name = id, MainNode = owner, State = MeshNodeState.Active,
            Content = new Notification
            {
                Id = id, Title = id, Recipient = owner, NotificationType = type, TargetNodePath = target,
                // A legacy notification predates the feature key: none is stored.
                Feature = legacy ? null : feature ?? (type is NotificationType.ApprovalRequired or NotificationType.ApprovalGiven or NotificationType.ApprovalRejected
                    ? NotificationFeatures.Approvals : null),
            },
        }, ct);
        return $"{ns}/{id}";
    }

    private static MeshNode Mail(string id, string ns, string owner, Email mail) => new(id, ns)
    {
        NodeType = EmailNodeType.NodeType, Name = mail.Subject, MainNode = owner, State = MeshNodeState.Active, Content = mail,
    };

    private async Task CreateAsync(MeshNode node, CancellationToken ct)
        => await Mesh.ServiceProvider.GetService<AccessService>()
            .RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(node))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>Creates the user's partition root as onboarding does — as System.</summary>
    private Task CreateUserAsync(string path, CancellationToken ct)
        => CreateAsync(MeshNode.FromPath(path) with
        {
            NodeType = UserNodeType.NodeType, Name = path, State = MeshNodeState.Active,
            Content = new User { FullName = path },
        }, ct);
}
