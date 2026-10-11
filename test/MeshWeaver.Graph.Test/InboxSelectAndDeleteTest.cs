using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Data;
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
/// The unified inbox's select-many-then-act half: what Delete and Undo do to the two item types core
/// owns, executed against a real mesh and read back THROUGH THE LEG — the leg no longer selecting a
/// put-away item is the behaviour, the flag on the node is only how it is achieved. Plus the pure
/// rules the page is built from: which rows may be ticked, how legs merge, what the outcome line says.
/// </summary>
public class InboxSelectAndDeleteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string DeleteArea = InboxLayoutArea.AreaName + "/Actions/Delete";
    private const string UndoArea = InboxLayoutArea.AreaName + "/Actions/Undo";
    private const string NeedsYouGrid = InboxLayoutArea.AreaName + "/Grid" + InboxBand.NeedsYou;

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>
    /// The page end to end, as its owner: the band's grid lists both notifications, the viewer ticks
    /// ONE, Delete puts exactly that one away and says so, and Undo brings it back. The write is
    /// issued by the page under the clicker's identity — nothing here runs as System.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ThePage_DeletesTheTickedRow_AndUndoBringsItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-page";
        await CreateUserAsync(owner, ct);
        var ns = $"{owner}/{NotificationService.SatelliteSegment}";
        foreach (var id in new[] { "keep", "bin" })
            await CreateAsync(new MeshNode(id, ns)
            {
                NodeType = NotificationNodeType.NodeType, Name = id, MainNode = owner, State = MeshNodeState.Active,
                Content = new Notification { Id = id, Title = id, Recipient = owner },
            }, ct);
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = owner, Name = owner, Email = $"{owner}@meshweaver.io" });

        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(owner), new LayoutAreaReference(InboxLayoutArea.AreaName));
        var grid = (DataGridControl)(await stream.GetControlStream(NeedsYouGrid).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is DataGridControl, "the band renders as a grid", ct))!;
        grid.SelectionMode.Should().Be(DataGridRowSelection.Multiple, "the inbox is select-many");
        grid.SelectionKey.Should().Be(InboxRows.KeyProperty);
        var rows = stream.GetDataStream<JsonElement>(
            new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxRows.GridId(InboxBand.NeedsYou))));
        Task Listed(string because, params string[] ids) => rows.Should().Within(TestTimeouts.Convergence).Match(
            r => r.ValueKind == JsonValueKind.Array
                 && r.EnumerateArray().Select(x => x.GetProperty("path").GetString()).OrderBy(x => x)
                     .SequenceEqual(ids.Select(id => $"{ns}/{id}").OrderBy(x => x)),
            because, ct);
        await Listed("both notifications are listed", "keep", "bin");

        var pointer = grid.SelectedItems.Should().BeOfType<JsonPointerReference>().Subject.Pointer;
        stream.UpdatePointer(new JsonArray(JsonValue.Create($"{ns}/bin")), pointer, new JsonPointerReference("keys"));
        await stream.GetDataStream<DataGridSelectionState>(new JsonPointerReference(pointer))
            .Should().Within(TestTimeouts.Convergence).Match(s => s is { Keys.Count: 1 }, "the tick reached the owner", ct);

        await Click(stream, DeleteArea, "1 moved out of the inbox. Undo brings them back.", ct);
        await Listed("only the ticked row left", "keep");
        (await Content<Notification>($"{ns}/bin", ct)).Dismissed.Should().BeTrue();
        (await Content<Notification>($"{ns}/keep", ct)).Dismissed.Should().BeFalse("an unticked row is never written");

        await Click(stream, UndoArea, "1 back in the inbox.", ct);
        await Listed("Undo brought it back", "keep", "bin");
    }

    private static async Task Click(ISynchronizationStream<JsonElement> stream, string area, string summary, CancellationToken ct)
    {
        var answer = new ReplaySubject<string?>(1);
        stream.SubmitUserAction(new ClickedEvent(area, stream.StreamId), actingUser: null,
            onRefused: answer.OnNext, onAccepted: () => answer.OnNext(null));
        (await answer.Should().Within(TestTimeouts.Convergence).Emit("the click is answered", ct)).Should().BeNull();
        await stream.GetDataStream<ClickProgress>(new JsonPointerReference(ClickProgress.PointerFor(area)))
            .Should().Within(TestTimeouts.Convergence).Match(p => p is { Running: false } && p.Summary == summary,
                $"the click settles saying '{summary}'", ct);
    }

    /// <summary>
    /// A notification put away leaves its leg and Undo returns it — and the node is never removed.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Delete_PutsANotificationAway_AndUndoBringsItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-delete-bell";
        await CreateUserAsync(owner, ct);
        var path = $"{owner}/{NotificationService.SatelliteSegment}/n1";
        await CreateAsync(new MeshNode("n1", $"{owner}/{NotificationService.SatelliteSegment}")
        {
            NodeType = NotificationNodeType.NodeType,
            Name = "Please look at this",
            MainNode = owner,
            State = MeshNodeState.Active,
            Content = new Notification { Id = "n1", Title = "Please look at this", Recipient = owner },
        }, ct);
        var leg = Leg(owner, "Notifications");
        var handler = Handler(NotificationNodeType.NodeType);

        (await Ids(leg, ids => ids.Contains("n1"), ct)).Should().Contain("n1", "the control: the leg selects it before Delete");

        (await Run(() => handler.Dismiss(Mesh, path), ct)).Should().Be(new InboxItemOutcome(path, true));
        (await Ids(leg, ids => !ids.Contains("n1"), ct)).Should().NotContain("n1", "a put-away notification leaves the list");
        (await Content<Notification>(path, ct)).Dismissed.Should().BeTrue("Delete is soft: the node stays, flagged");

        (await Run(() => handler.Restore(Mesh, path), ct)).Done.Should().BeTrue();
        (await Ids(leg, ids => ids.Contains("n1"), ct)).Should().Contain("n1", "Undo brings it back");
    }

    /// <summary>
    /// A stored inbound mail is archived by Delete and returned as Read by Undo; reading it never
    /// removed it (New and Read are both in the leg).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Delete_ArchivesAnInboundMail_AndUndoReturnsItAsRead()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-delete-mail";
        await CreateUserAsync(owner, ct);
        var ns = $"{owner}/{EmailNodeType.UserEmailSegment}";
        await CreateAsync(Mail("m1", ns, owner, new Email { Id = "m1", From = "a.buyer@acme.example", Subject = "Workshop follow-up" }), ct);
        await CreateAsync(Mail("out1", ns, owner, new Email
        {
            Id = "out1", Direction = EmailDirection.Outbound, Status = EmailStatus.Sending, Subject = "Queued reply",
        }), ct);
        var leg = Leg(owner, "Mail");
        var handler = Handler(EmailNodeType.NodeType);

        (await Ids(leg, ids => ids.Contains("m1"), ct)).Should().Equal(["m1"], "inbound mail is listed; a queued outbound one is not an inbox item");

        (await Run(() => handler.Dismiss(Mesh, $"{ns}/m1"), ct)).Done.Should().BeTrue();
        (await Ids(leg, ids => !ids.Contains("m1"), ct)).Should().BeEmpty("an archived mail leaves the list");
        (await Content<Email>($"{ns}/m1", ct)).Status.Should().Be(EmailStatus.Archived);

        (await Run(() => handler.Restore(Mesh, $"{ns}/m1"), ct)).Done.Should().BeTrue();
        (await Ids(leg, ids => ids.Contains("m1"), ct)).Should().Equal("m1");
        (await Content<Email>($"{ns}/m1", ct)).Status.Should().Be(EmailStatus.Read, "the viewer has seen it by the time they undo");

        // An outbound mail's status is its SEND state: Delete must not stop a queued mail leaving.
        (await Run(() => handler.Dismiss(Mesh, $"{ns}/out1"), ct)).Done.Should().BeTrue();
        (await Content<Email>($"{ns}/out1", ct)).Status.Should().Be(EmailStatus.Sending);
    }

    /// <summary>Only an inbound mail that is New or Read may be filed from the inbox.</summary>
    [Theory]
    [InlineData(EmailDirection.Inbound, EmailStatus.New, true)]
    [InlineData(EmailDirection.Inbound, EmailStatus.Read, true)]
    [InlineData(EmailDirection.Inbound, EmailStatus.Archived, false)]
    [InlineData(EmailDirection.Outbound, EmailStatus.Sending, false)]
    [InlineData(EmailDirection.Outbound, EmailStatus.Sent, false)]
    [InlineData(EmailDirection.Outbound, EmailStatus.Failed, false)]
    public void OnlyAnOpenInboundMail_MayBeFiled(EmailDirection direction, EmailStatus status, bool expected)
        => EmailInboxItemHandler.CanFile(new Email { Direction = direction, Status = status }).Should().Be(expected);

    /// <summary>Core brings exactly its own two handlers; every other type's arrives with its package.</summary>
    [Fact]
    public void CoreRegisters_TheHandlersOfItsOwnTwoTypes()
        => Mesh.ServiceProvider.GetServices<IInboxItemHandler>().Select(h => h.NodeType)
            .Should().Contain([NotificationNodeType.NodeType, EmailNodeType.NodeType]);

    /// <summary>A leg a package registered as a service is run beside core's own.</summary>
    [Fact]
    public void ALegRegisteredAsAService_JoinsTheBuiltInOnes()
    {
        var brought = new InboxQueryLeg { Source = "Threads", Band = InboxBand.NeedsYou, Order = 30, Query = "namespace:{viewer}/_Thread nodeType:Thread" };
        using var services = new ServiceCollection().AddSingleton(brought).BuildServiceProvider();

        var all = InboxQueries.All(services);

        all.Should().Contain(InboxQueries.BuiltIn).And.Contain(brought);
        InboxLayoutArea.Sections(all, "alice").Should().Contain(s => s.Source == "Threads" && s.Query == "namespace:alice/_Thread nodeType:Thread");
    }

    /// <summary>
    /// A running item is not the viewer's to put away, and a type nobody brought a handler for is
    /// listed but never written — each says why in its disabled checkbox.
    /// </summary>
    [Fact]
    public void ARow_IsSelectable_OnlyOutsideRunning_AndOnlyWithAHandler()
    {
        var handler = new NotificationInboxItemHandler();

        InboxLayoutArea.BlockedReason(InboxBand.NeedsYou, handler, "running", "none").Should().BeNull();
        InboxLayoutArea.BlockedReason(InboxBand.Recent, handler, "running", "none").Should().BeNull();
        InboxLayoutArea.BlockedReason(InboxBand.Running, handler, "running", "none").Should().Be("running");
        InboxLayoutArea.BlockedReason(InboxBand.NeedsYou, null, "running", "none").Should().Be("none");
        InboxItemHandlers.For([handler], "notification").Should().BeNull("a node type is an identifier: ordinal, never case-folded");
    }

    /// <summary>A bulk act touches only rows that are still there and still selectable.</summary>
    [Fact]
    public void ABulkAct_ReChecksTheSelection_AgainstTheRowsAsTheyStand()
    {
        ImmutableList<InboxRow> rows =
        [
            new() { Path = "alice/_Notification/a", NodeType = "Notification" },
            new() { Path = "alice/_Activity/b", NodeType = "Activity", BlockedReason = "running" },
        ];

        InboxRows.Targets(rows, ["alice/_Notification/a", "alice/_Activity/b", "alice/_Notification/gone"])
            .Select(r => r.Path).Should().Equal("alice/_Notification/a");
    }

    /// <summary>Legs merge into one band: each path once (first leg wins), newest first.</summary>
    [Fact]
    public void LegsMergeIntoOneBand_EachPathOnce_NewestFirst()
    {
        var mail = new InboxSection(InboxBand.NeedsYou, "Mail", 20, "q1");
        var bell = new InboxSection(InboxBand.NeedsYou, "Notifications", 10, "q2");
        MeshNode Node(string id, int minute) => new(id, "alice/_X")
        {
            Name = id, NodeType = "X", LastModified = new DateTimeOffset(2026, 10, 11, 9, minute, 0, TimeSpan.Zero),
        };

        var merged = InboxRows.Merge(
        [
            [InboxRows.Of(Node("old", 1), bell, null, null), InboxRows.Of(Node("both", 2), bell, null, null)],
            [InboxRows.Of(Node("both", 2), mail, null, null), InboxRows.Of(Node("new", 3), mail, null, null)],
        ]);

        merged.Select(r => r.Title).Should().Equal("new", "both", "old");
        merged.Single(r => r.Title == "both").Source.Should().Be("Notifications");
    }

    /// <summary>
    /// A stored instant is shown in the VIEWER's zone — across the date line, and on both sides of
    /// a daylight-saving change — while the row ORDER stays on the UTC instant.
    /// </summary>
    [Theory]
    [InlineData("2026-01-15T23:30:00Z", "Europe/Zurich", "2026-01-16 00:30")]
    [InlineData("2026-07-15T23:30:00Z", "Europe/Zurich", "2026-07-16 01:30")]
    [InlineData("2026-07-15T02:30:00Z", "America/New_York", "2026-07-14 22:30")]
    [InlineData("2026-07-15T02:30:00Z", null, "2026-07-15 02:30")]
    public void ARowsTime_IsTheViewers_NotTheContainers(string utc, string? zone, string expected)
    {
        var node = new MeshNode("n", "alice/_X") { LastModified = DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture) };

        var row = InboxRows.Of(node, new InboxSection(InboxBand.NeedsYou, "Mail", 20, "q"), zone, null);

        row.When.Should().Be(expected);
        row.Stamp.Should().StartWith(utc[..19], "the sort key is the UTC instant, whatever the viewer's zone");
    }

    /// <summary>
    /// A band a provider named itself still yields data ids a client write can reach — no slash, no
    /// space — and two bands never share one.
    /// </summary>
    [Fact]
    public void ABandsDataIds_CarryNoCharacterAClientWriteCannotReach()
    {
        InboxRows.SelectionId("Ops / Approvals").Should().Be("inboxSelectionOpsApprovals");
        InboxRows.GridId(InboxBand.NeedsYou).Should().NotBe(InboxRows.GridId(InboxBand.Recent));
        InboxRows.QueryId(new InboxSection("Needs you", "Mail/Work", 20, "q")).Should().Be("Inbox.Needsyou.MailWork.20");
    }

    private IInboxItemHandler Handler(string nodeType)
        => InboxItemHandlers.For(Mesh.ServiceProvider.GetServices<IInboxItemHandler>(), nodeType)
           ?? throw new InvalidOperationException($"no inbox handler registered for {nodeType}");

    private static string Leg(string owner, string source)
        => InboxQueries.Resolve(InboxQueries.BuiltIn, owner).Single(l => l.Source == source && l.Band == InboxBand.NeedsYou).Query;

    private static MeshNode Mail(string id, string ns, string owner, Email mail) => new(id, ns)
    {
        NodeType = EmailNodeType.NodeType,
        Name = mail.Subject,
        MainNode = owner,
        State = MeshNodeState.Active,
        Content = mail,
    };

    /// <summary>
    /// The ids the leg selects, once they satisfy <paramref name="until"/>. Read through the SNAPSHOT
    /// stream the page itself binds to: a raw live query answers a later change as a delta (a
    /// put-away row arrives as <c>Removed [row]</c>), which is not "what the leg selects now" — and a
    /// read that raced the write would then wait forever on an initial set that still held the row.
    /// </summary>
    private async Task<string[]> Ids(string query, Func<string[], bool> until, CancellationToken ct)
        => await Mesh.GetQuery($"test:{Guid.NewGuid():N}", query)
            .Select(nodes => nodes.Select(n => n.Id).ToArray())
            .Where(until)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private async Task<T> Content<T>(string path, CancellationToken ct) where T : class
        => (await Mesh.ServiceProvider.GetService<AccessService>()
                .RunAsSystem(() => Mesh.GetMeshNodeStream(path).Take(1))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .ContentAs<T>(Mesh.JsonSerializerOptions)
           ?? throw new InvalidOperationException($"{path} does not carry {typeof(T).Name}");

    private async Task<InboxItemOutcome> Run(Func<IObservable<InboxItemOutcome>> act, CancellationToken ct)
        => await Mesh.ServiceProvider.GetService<AccessService>().RunAsSystem(act)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private async Task CreateAsync(MeshNode node, CancellationToken ct)
        => await Mesh.ServiceProvider.GetService<AccessService>()
            .RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(node))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>Creates the user's partition root as onboarding does — as System.</summary>
    private Task CreateUserAsync(string path, CancellationToken ct)
        => CreateAsync(MeshNode.FromPath(path) with
        {
            NodeType = UserNodeType.NodeType,
            Name = path,
            State = MeshNodeState.Active,
            Content = new User { FullName = path },
        }, ct);
}
