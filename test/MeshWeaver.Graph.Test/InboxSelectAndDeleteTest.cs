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
    private const string GridArea = InboxLayoutArea.AreaName + "/Grid";

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
        var grid = (DataGridControl)(await stream.GetControlStream(GridArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is DataGridControl, "the inbox renders as one grid", ct))!;
        grid.SelectionMode.Should().Be(DataGridRowSelection.Multiple, "the inbox is select-many");
        grid.SelectionKey.Should().Be(InboxRows.KeyProperty);
        var rows = stream.GetDataStream<JsonElement>(
            new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxRows.GridId)));
        Task Listed(string because, params string[] ids) => rows.Should().Within(TestTimeouts.Convergence).Match(
            r => r.ValueKind == JsonValueKind.Array
                 && r.EnumerateArray().Select(x => x.GetProperty("path").GetString()).OrderBy(x => x)
                     .SequenceEqual(ids.Select(id => $"{ns}/{id}").OrderBy(x => x)),
            because, ct);
        await Listed("both notifications are listed", "keep", "bin");

        var pointer = grid.SelectedItems.Should().BeOfType<JsonPointerReference>().Subject.Pointer;
        // The selection is the client's claim. Beside the real tick it names a node of the owner's
        // that NO leg lists (an already put-away notification): a key outside the owner's own feed
        // is never a target, whatever the client says it ticked.
        await CreateAsync(new MeshNode("away", ns)
        {
            NodeType = NotificationNodeType.NodeType, Name = "away", MainNode = owner, State = MeshNodeState.Active,
            Content = new Notification { Id = "away", Title = "away", Recipient = owner, Dismissed = true },
        }, ct);
        stream.UpdatePointer(new JsonArray(JsonValue.Create($"{ns}/bin"), JsonValue.Create($"{ns}/away")),
            pointer, new JsonPointerReference("keys"));
        await stream.GetDataStream<DataGridSelectionState>(new JsonPointerReference(pointer))
            .Should().Within(TestTimeouts.Convergence).Match(s => s is { Keys.Count: 2 }, "the ticks reached the owner", ct);

        await Click(stream, DeleteArea, "1 moved out of the inbox. Undo brings them back.", ct);
        await Listed("only the ticked row left", "keep");
        (await Content<Notification>($"{ns}/bin", n => n.Dismissed, ct)).Dismissed.Should().BeTrue();
        (await Content<Notification>($"{ns}/keep", _ => true, ct)).Dismissed.Should().BeFalse("an unticked row is never written");

        await Click(stream, UndoArea, "1 back in the inbox.", ct);
        await Listed("Undo brought it back — and only what Delete put away: the unlisted node stays away", "keep", "bin");
        (await Content<Notification>($"{ns}/away", _ => true, ct)).Dismissed.Should().BeTrue();
    }

    /// <summary>
    /// Everything above the grid is bound to ONE filter in the page's data section — writing a field
    /// there (what the search box, the order select and a source switch do) re-feeds the grid, with
    /// no click handler and no re-render. The search goes to the QUERY ENGINE with each leg: a term
    /// lists only what the mesh finds for it.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TheSearch_TheOrder_AndTheSourceSwitches_AreBoundToTheGrid()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-bound";
        await CreateUserAsync(owner, ct);
        var bell = $"{owner}/{NotificationService.SatelliteSegment}";
        var mail = $"{owner}/{EmailNodeType.UserEmailSegment}";
        await CreateAsync(new MeshNode("alpha", bell)
        {
            NodeType = NotificationNodeType.NodeType, Name = "Alpha invoice", MainNode = owner, State = MeshNodeState.Active,
            Content = new Notification { Id = "alpha", Title = "Alpha invoice", Recipient = owner },
        }, ct);
        await CreateAsync(Mail("zulu", mail, owner, new Email { Id = "zulu", From = "a.buyer@acme.example", Subject = "Zulu workshop" }), ct);
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = owner, Name = owner, Email = $"{owner}@meshweaver.io" });

        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(owner), new LayoutAreaReference(InboxLayoutArea.AreaName));
        var rows = stream.GetDataStream<JsonElement>(
            new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxRows.GridId)));
        Task Titles(string because, params string[] titles) => rows.Should().Within(TestTimeouts.Convergence).Match(
            r => r.ValueKind == JsonValueKind.Array
                 && r.EnumerateArray().Select(x => x.GetProperty("title").GetString()).SequenceEqual(titles),
            because, ct);
        var filter = LayoutAreaReference.GetDataPointer(InboxRows.FilterId);
        void Set(string field, JsonNode value) => stream.UpdatePointer(value, filter, new JsonPointerReference(field));

        // Both are in "Needs you"; the mail was created last, so it is the newer one.
        await Titles("the default order lists the newest first within a band", "Zulu workshop", "Alpha invoice");

        // The page must settle: its own row writes re-emit the data section, and a page that read
        // those as filter changes would rewrite the rows without end (dozens of writes in this window).
        (await rows.Buffer(TimeSpan.FromMilliseconds(600)).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Count.Should().BeLessThanOrEqualTo(2, "an idle inbox writes nothing");

        Set("order", JsonValue.Create(InboxOrder.Title));
        await Titles("the order select re-orders the grid", "Alpha invoice", "Zulu workshop");

        Set($"sources/{InboxRows.SourceKey("Mail")}", JsonValue.Create(false));
        await Titles("a source switched off leaves the grid", "Alpha invoice");
        Set($"sources/{InboxRows.SourceKey("Mail")}", JsonValue.Create(true));
        await Titles("and returns when switched on", "Alpha invoice", "Zulu workshop");

        Set("search", JsonValue.Create("name:*workshop*"));
        await Titles("a term is evaluated by the query engine, per leg", "Zulu workshop");
        Set("search", JsonValue.Create(""));
        await Titles("clearing the box restores the list", "Alpha invoice", "Zulu workshop");
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
        (await Content<Notification>(path, n => n.Dismissed, ct)).Dismissed.Should().BeTrue("Delete is soft: the node stays, flagged");

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
        (await Content<Email>($"{ns}/m1", m => m.Status == EmailStatus.Archived, ct)).Status.Should().Be(EmailStatus.Archived);

        (await Run(() => handler.Restore(Mesh, $"{ns}/m1"), ct)).Done.Should().BeTrue();
        (await Ids(leg, ids => ids.Contains("m1"), ct)).Should().Equal("m1");
        (await Content<Email>($"{ns}/m1", m => m.Status == EmailStatus.Read, ct)).Status.Should().Be(EmailStatus.Read, "the viewer has seen it by the time they undo");

        // An outbound mail's status is its SEND state: Delete must not stop a queued mail leaving —
        // and a write that changed nothing is NOT a delete: it answers "not done", so the mail
        // never enters the Undo batch.
        (await Run(() => handler.Dismiss(Mesh, $"{ns}/out1"), ct)).Should().Be(new InboxItemOutcome($"{ns}/out1", false));
        (await Run(() => handler.Restore(Mesh, $"{ns}/out1"), ct)).Done.Should().BeFalse("nothing was put away, so nothing came back");
        (await Content<Email>($"{ns}/out1", _ => true, ct)).Status.Should().Be(EmailStatus.Sending);
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

        InboxRows.Targets(rows, ["alice/_Notification/a", "alice/_Activity/b", "alice/_Notification/gone"], "alice")
            .Select(r => r.Path).Should().Equal("alice/_Notification/a");
    }

    /// <summary>
    /// A search may reach beyond the owner's partition (it runs with the viewer's rights); a bulk
    /// act never does — a ticked row elsewhere is not a target, and a partition that merely
    /// STARTS with the owner's name is elsewhere.
    /// </summary>
    [Fact]
    public void ABulkAct_NeverLeavesTheOwnersPartition()
    {
        ImmutableList<InboxRow> rows =
        [
            new() { Path = "alice/_Notification/a", NodeType = "Notification" },
            new() { Path = "alicia/_Notification/b", NodeType = "Notification" },
            new() { Path = "bob/_Notification/c", NodeType = "Notification" },
        ];

        InboxRows.Targets(rows, rows.Select(r => r.Path), "alice").Select(r => r.Path).Should().Equal("alice/_Notification/a");
    }

    /// <summary>Legs merge into one list: each path once, under the first leg that brought it.</summary>
    [Fact]
    public void LegsMergeIntoOneList_EachPathOnce_UnderTheFirstLegThatBroughtIt()
    {
        var mail = new InboxSection(InboxBand.NeedsYou, "Mail", 20, "q1");
        var bell = new InboxSection(InboxBand.NeedsYou, "Notifications", 10, "q2");

        var merged = InboxRows.Merge(
        [
            [InboxRows.Of(Node("old", 1), bell, null, null), InboxRows.Of(Node("both", 2), bell, null, null)],
            [InboxRows.Of(Node("both", 2), mail, null, null), InboxRows.Of(Node("new", 3), mail, null, null)],
        ]);

        merged.Select(r => r.Title).OrderBy(t => t, StringComparer.Ordinal).Should().Equal("both", "new", "old");
        merged.Single(r => r.Title == "both").Source.Should().Be("Notifications");
    }

    /// <summary>
    /// Every ordering the select offers — and an order the page does not know falls back to
    /// importance, so a stale stored value can never bury what needs the viewer.
    /// </summary>
    [Theory]
    [InlineData(InboxOrder.Importance, "urgentNew,urgentOld,doneNewest")]
    [InlineData(InboxOrder.Newest, "doneNewest,urgentNew,urgentOld")]
    [InlineData(InboxOrder.Oldest, "urgentOld,urgentNew,doneNewest")]
    [InlineData(InboxOrder.Title, "doneNewest,urgentNew,urgentOld")]
    [InlineData(InboxOrder.Source, "doneNewest,urgentNew,urgentOld")]
    [InlineData("SomethingElse", "urgentNew,urgentOld,doneNewest")]
    public void TheChosenOrder_ArrangesTheRows(string order, string expected)
    {
        var needs = new InboxSection(InboxBand.NeedsYou, "Mail", 20, "q1");
        var recent = new InboxSection(InboxBand.Recent, "Activities", 20, "q2");
        ImmutableList<InboxRow> rows =
        [
            InboxRows.Of(Node("urgentOld", 1), needs, null, null, "Needs you", 0),
            InboxRows.Of(Node("doneNewest", 9), recent, null, null, "Recent", 2),
            InboxRows.Of(Node("urgentNew", 5), needs, null, null, "Needs you", 0),
        ];

        InboxRows.Apply(rows, new InboxFilter { Order = order }).Select(r => r.Title)
            .Should().Equal(expected.Split(','));
    }

    /// <summary>
    /// A source switched off is not listed; a source with no entry IS — a leg a package brings
    /// later must not arrive switched off.
    /// </summary>
    [Fact]
    public void ASourceSwitchedOff_IsNotListed_AndAnUnknownSourceIs()
    {
        ImmutableList<InboxRow> rows =
        [
            InboxRows.Of(Node("mail", 1), new InboxSection(InboxBand.NeedsYou, "Mail", 20, "q1"), null, null),
            InboxRows.Of(Node("wa", 2), new InboxSection(InboxBand.NeedsYou, "WhatsApp", 30, "q2"), null, null),
        ];
        var filter = new InboxFilter { Sources = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.SourceKey("Mail"), false) };

        InboxRows.Apply(rows, filter).Select(r => r.Title).Should().Equal("wa");
    }

    /// <summary>
    /// The page tells a change the viewer made from a re-emission of the same state BY VALUE: two
    /// separately built filters with the same content are the same, and every field moves it.
    /// Without this the grid's own row writes read as filter changes and the page feeds on itself.
    /// </summary>
    [Fact]
    public void AFiltersFingerprint_IsItsValue_NotItsInstance()
    {
        InboxFilter Build() => new()
        {
            Search = "acme", Order = InboxOrder.Title,
            Sources = ImmutableDictionary<string, bool>.Empty.Add("mail", true).Add("chat", false),
        };
        var one = InboxRows.Fingerprint(Build());

        InboxRows.Fingerprint(Build()).Should().Be(one);
        Build().Should().NotBe(Build(), "the control: the record's own equality compares the dictionary by reference");
        InboxRows.Fingerprint(Build() with { Search = "globex" }).Should().NotBe(one);
        InboxRows.Fingerprint(Build() with { Order = InboxOrder.Newest }).Should().NotBe(one);
        InboxRows.Fingerprint(Build() with { Sources = Build().Sources.SetItem("chat", true) }).Should().NotBe(one);
    }

    /// <summary>
    /// A live read answers its first set whole and every later change as a delta; folded, it is
    /// "what the leg selects now" — a removed row leaves, an updated one is replaced, not doubled.
    /// </summary>
    [Fact]
    public void ALiveReadsDeltas_FoldIntoWhatTheLegSelectsNow()
    {
        static QueryResultChange<MeshNode> Change(QueryChangeType type, params MeshNode[] items)
            => new() { ChangeType = type, Items = items };
        var set = ImmutableDictionary<string, MeshNode>.Empty;

        set = InboxLayoutArea.Fold(set, Change(QueryChangeType.Initial, Node("a", 1), Node("b", 1)));
        set = InboxLayoutArea.Fold(set, Change(QueryChangeType.Added, Node("c", 2)));
        set = InboxLayoutArea.Fold(set, Change(QueryChangeType.Updated, Node("a", 3)));
        set = InboxLayoutArea.Fold(set, Change(QueryChangeType.Removed, Node("b", 1)));

        set.Keys.OrderBy(k => k).Should().Equal("alice/_X/a", "alice/_X/c");
        set["alice/_X/a"].LastModified.Minute.Should().Be(3, "an update replaces the row");
        InboxLayoutArea.Fold(set, Change(QueryChangeType.Initial, Node("z", 1))).Keys.Should().Equal("alice/_X/z");
    }

    private static MeshNode Node(string id, int minute) => new(id, "alice/_X")
    {
        Name = id, NodeType = "X", LastModified = new DateTimeOffset(2026, 10, 11, 9, minute, 0, TimeSpan.Zero),
    };

    /// <summary>
    /// A stored instant is shown in the VIEWER's zone and the viewer's date format — across the
    /// date line, and on both sides of a daylight-saving change — while the row ORDER stays on the
    /// UTC instant.
    /// </summary>
    [Theory]
    [InlineData("2026-01-15T23:30:00Z", "Europe/Zurich", "de-CH", "16.01.2026 00:30")]
    [InlineData("2026-07-15T23:30:00Z", "Europe/Zurich", "de-CH", "16.07.2026 01:30")]
    [InlineData("2026-07-15T02:30:00Z", "America/New_York", "de-CH", "14.07.2026 22:30")]
    [InlineData("2026-07-15T02:30:00Z", null, null, "07/15/2026 02:30")]
    public void ARowsTime_IsTheViewers_NotTheContainers(string utc, string? zone, string? locale, string expected)
    {
        var node = new MeshNode("n", "alice/_X") { LastModified = DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture) };

        var row = InboxRows.Of(node, new InboxSection(InboxBand.NeedsYou, "Mail", 20, "q"), zone, null,
            culture: InboxLayoutArea.ViewerCulture(locale));

        row.When.Should().Be(expected);
        row.Stamp.Should().StartWith(utc[..19], "the sort key is the UTC instant, whatever the viewer's zone or language");
    }

    /// <summary>A language tag the runtime does not know must never fault a render.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a language tag !!")]
    public void AnUnreadableLocale_FallsBackToInvariant(string? locale)
        => InboxLayoutArea.ViewerCulture(locale).Should().Be(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A source's switch key is unique per source id BY CONSTRUCTION — not merely unlikely to
    /// collide — and carries nothing a pointer segment cannot. The second pair is one a 32-bit
    /// hash of the id maps to the same value.
    /// </summary>
    [Theory]
    [InlineData("Ops / Approvals", "Ops Approvals")]
    [InlineData("OpsApprov_al sL_o-n/g", "OpsAppro va/l-sL-o-ng")]
    [InlineData("Mail", "mail")]
    public void ASourcesSwitchKey_IsUniquePerSource_AndPointerSafe(string one, string other)
    {
        InboxRows.SourceKey(one).Should().NotBe(InboxRows.SourceKey(other));
        InboxRows.SourceKey(one).Should().MatchRegex("^[a-z0-9]+$", "no slash, space or capital");
        InboxRows.SourceKey(one).Should().Be(InboxRows.SourceKey(one), "stable: it is stored in the page's data");
    }

    /// <summary>
    /// The row shows the source's DISPLAY name and keeps its id apart: the switch and the by-source
    /// order key on the id, so a translated label can never detach a row from its switch. Every leg
    /// core brings has a catalog key.
    /// </summary>
    [Fact]
    public void ASourcesLabel_IsShown_AndItsIdKeysTheSwitch()
    {
        var mail = new InboxSection(InboxBand.NeedsYou, "Mail", 20, "q");
        var row = InboxRows.Of(Node("m", 1), mail, null, null, sourceLabel: "E-Mail");

        row.Source.Should().Be("E-Mail");
        row.SourceId.Should().Be("Mail");
        InboxRows.Apply([row], new InboxFilter { Sources = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.SourceKey("Mail"), false) })
            .Should().BeEmpty("the switch is keyed on the id, not on what the cell shows");
        InboxQueries.BuiltIn.Should().OnlyContain(leg => !string.IsNullOrEmpty(leg.SourceLabelKey));
        InboxLayoutArea.Sections(InboxQueries.BuiltIn, "alice").Should().OnlyContain(s => !string.IsNullOrEmpty(s.SourceLabelKey));
    }

    /// <summary>
    /// A write that faults answers for its item with NO reason: an outcome's reason is shown to the
    /// viewer verbatim, and a typed-write failure can quote the content it could not read. The cause
    /// is logged; the page says only that the item could not be changed.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AFaultedWrite_AnswersForItsItem_WithoutQuotingItsCause()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-fault";
        await CreateUserAsync(owner, ct);
        var ns = $"{owner}/{EmailNodeType.UserEmailSegment}";
        // A node the notification handler cannot read AS a notification: content of another shape.
        await CreateAsync(new MeshNode("odd", ns)
        {
            NodeType = EmailNodeType.NodeType, Name = "odd", MainNode = owner, State = MeshNodeState.Active,
            Content = "this is not a mail record",
        }, ct);

        var outcome = await Run(() => Handler(EmailNodeType.NodeType).Dismiss(Mesh, $"{ns}/odd"), ct);

        outcome.Should().Be(new InboxItemOutcome($"{ns}/odd", false), "no reason: the cause is logged, never shown");
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

    /// <summary>
    /// The node's content once it satisfies <paramref name="until"/>. The first emission of a node
    /// read can still be the value from before a write that has already been answered, so content
    /// asserted right after a write is WAITED for, never sampled.
    /// </summary>
    private async Task<T> Content<T>(string path, Func<T, bool> until, CancellationToken ct) where T : class
        => await Mesh.ServiceProvider.GetService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream(path)
                .Select(node => node.ContentAs<T>(Mesh.JsonSerializerOptions))
                .OfType<T>()
                .Where(until))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

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
