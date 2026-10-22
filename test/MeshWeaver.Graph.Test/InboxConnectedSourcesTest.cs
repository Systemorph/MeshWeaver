using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The inbox lists only the sources a viewer has CONNECTED (<see cref="InboxQueryLeg.ConnectedWhen"/>)
/// — owner, 2026-10-15: "i see a lot of stuff that's not even configured ==> must get only configured
/// sources" — and says which of the viewer's accounts an item reached (<see cref="InboxRow.Account"/>):
/// "we have many emails => show which mail account it is".
/// </summary>
public class InboxConnectedSourcesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SourcesArea = InboxLayoutArea.AreaName + "/Filters/Source/Content";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>
    /// The stored-mail source is connected once mail is stored for the viewer. A viewer without any
    /// gets no Mail switch and no Mail entry in the Only select — while the sources that name no
    /// connection (approvals, notifications) are listed for everyone. A viewer WITH stored mail gets
    /// the switch: the negative control, so the first half cannot pass on a page that lists nothing.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AnUnconnectedSource_IsHiddenAndNotOffered_AndAConnectedOneIsShown()
    {
        var ct = TestContext.Current.CancellationToken;
        const string without = "inbox-no-mail";
        const string with = "inbox-has-mail";
        await CreateUserAsync(without, ct);
        await CreateUserAsync(with, ct);
        await CreateAsync(new MeshNode("m1", $"{with}/{EmailNodeType.UserEmailSegment}")
        {
            NodeType = EmailNodeType.NodeType, Name = "Hello", MainNode = with, State = MeshNodeState.Active,
            Content = new Email { Id = "m1", Subject = "Hello" },
        }, ct);

        var mail = InboxRows.SourceKey("Mail");
        var notifications = InboxRows.SourceKey("Notifications");

        // The page is a TEMPLATE: every source's toggle is declared at once, its style BOUND to the
        // connection the rows feed publishes — never built after a wait.
        var toggle = (StackControl)(await Open(without).GetControlStream($"{SourcesArea}/Source{mail}")
            .Should().Within(TestTimeouts.Convergence).Match(c => c is StackControl, "the Mail source row is in the template", ct))!;
        toggle.Style.Should().BeOfType<JsonPointerReference>()
            .Which.Pointer.Should().Be(InboxLayoutArea.SourceStylePointer(mail), "its visibility is bound, not baked");

        var bare = await Answered(without, ct);
        bare.Styles[notifications].Should().BeEmpty("a source that names no connection is listed for everyone");
        bare.Styles[mail].Should().Be(InboxLayoutArea.HiddenStyle, "nobody stored mail for this viewer — the mailbox is not connected");
        bare.Only.Should().Contain(notifications).And.NotContain(mail, "the Only select lists connected sources only");

        var connected = await Answered(with, ct);
        connected.Styles[mail].Should().BeEmpty("stored mail connects the Mail source — the probe answered and showed it");
        connected.Only.Should().Contain([notifications, mail]);
    }

    /// <summary>
    /// A connection query is held to the leg's own anchoring rule: one that reads beyond the viewer's
    /// partition is refused — the leg is never run, and it is reported for the page to log — exactly
    /// as an unanchored leg query is. The anchored form of the same leg is resolved, with the viewer
    /// substituted into the connection query too.
    /// </summary>
    [Fact]
    public void AConnectionQuery_IsAnchoredLikeTheLeg_AndResolvedForTheViewer()
    {
        var leg = new InboxQueryLeg
        {
            Source = "Probe",
            Query = $"namespace:{InboxQueries.ViewerToken}/_Probe nodeType:Probe {InboxQueries.RowProjection}",
            ConnectedWhen = $"namespace:{InboxQueries.ViewerToken}/_ProbeAccount {InboxQueries.ConnectionProbe}",
        };
        var resolved = InboxQueries.Resolve([leg], "alice").Should().ContainSingle().Subject;
        resolved.ConnectedWhen.Should().Be($"namespace:alice/_ProbeAccount {InboxQueries.ConnectionProbe}");
        InboxQueries.Refused([leg], "alice").Should().BeEmpty();
        InboxLayoutArea.Sections([leg], "alice").Should().ContainSingle()
            .Which.ConnectedWhen.Should().Be(resolved.ConnectedWhen, "the page probes the resolved query");

        var wide = leg with { ConnectedWhen = $"nodeType:ProbeAccount partitions:all {InboxQueries.ConnectionProbe}" };
        InboxQueries.Resolve([wide], "alice").Should().BeEmpty("a connection read beyond the viewer's partition is never run");
        InboxQueries.Refused([wide], "alice").Should().ContainSingle("and it is reported, never silently dropped");
    }

    /// <summary>
    /// A row says which account the item reached when its source stamps one (the content's
    /// <c>account</c>), in whatever shape the content arrives; a row whose source does not is empty.
    /// </summary>
    [Fact]
    public void ARow_NamesTheAccountItsSourceStamped()
    {
        var section = new InboxSection(InboxBand.NeedsYou, "Gmail", 23, "q") { TimeField = "receivedAt" };
        var node = new MeshNode("g1", "alice/_Mailbox/gmail")
        {
            Name = "Hi",
            Content = JsonSerializer.SerializeToElement(new { account = " jane.roe@example.com ", from = "max@example.com" }),
        };
        InboxRows.Of(node, section, null, null).Account.Should().Be("jane.roe@example.com");
        InboxRows.Of(node with { Content = JsonSerializer.SerializeToElement(new { from = "max@example.com" }) }, section, null, null)
            .Account.Should().BeEmpty("a source that stamps no account leaves the column empty");
    }

    /// <summary>
    /// The page is BUILT on the turn the connection reads answer on, not on the render's own turn —
    /// and it is still built in the VIEWER's language: a German viewer reads German column titles.
    /// The English viewer is the negative control, so the German half cannot pass on a catalog that
    /// happens to say the same in both languages.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task APageBuiltAfterTheConnectionReads_SpeaksTheViewersLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        const string german = "inbox-de";
        const string english = "inbox-en";
        await CreateUserAsync(german, ct);
        await CreateUserAsync(english, ct);

        (await ColumnTitles(german, "de", ct)).Should().Contain("Konto",
            "the account column is titled in the viewer's language, whichever turn the connection read answered on");
        (await ColumnTitles(english, "en", ct)).Should().Contain("Account").And.NotContain("Konto");
    }

    /// <summary>
    /// A connection read that could not see everything — a provider that never answered, a storage
    /// read that did not finish — is NOT "not connected": the source stays listed. A complete empty
    /// answer is the negative control: that, and only that, hides it.
    /// </summary>
    [Fact]
    public void AnIncompleteEmptyAnswer_ListsTheSource_ACompleteOneDoesNot()
    {
        InboxLayoutArea.FoundConnection(new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial })
            .Should().BeFalse("a complete empty answer: nothing is connected");
        InboxLayoutArea.FoundConnection(new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial, SilentProviders = ["Postgres"] })
            .Should().BeTrue("a provider that never answered cannot say the source is unconnected");
        InboxLayoutArea.FoundConnection(new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial, SnapshotIncomplete = true })
            .Should().BeTrue("an incomplete storage read cannot say the source is unconnected");
        InboxLayoutArea.FoundConnection(new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial, Items = [new MeshNode("a", "x")] })
            .Should().BeTrue("a found connection");
    }

    /// <summary>
    /// The page is built on the turn the connection reads answer on — here a thread-pool turn that
    /// carries no identity, as a database provider's or the wait's timer does — and is built in the
    /// viewer's language there. The same answer read WITHOUT the restore is the negative control: on
    /// that turn the ambient language is not the viewer's.
    /// </summary>
    [Fact]
    public async Task ThePageIsBuiltInTheViewersIdentity_OnAnAnswerFromAnotherTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var viewer = new AccessContext { ObjectId = "de-viewer", Name = "de-viewer", Locale = "de" };
        var offTurn = Observable.Return(Unit.Default, System.Reactive.Concurrency.TaskPoolScheduler.Default);

        (await InboxLayoutArea.InViewerContext(offTurn, Mesh.ServiceProvider, viewer, _ => access.Localize("inbox.column.account"))
                .Should().Within(TestTimeouts.Convergence).Emit("the page", ct))
            .Should().Be("Konto", "the build runs inside the viewer's identity");
        (await offTurn.Select(_ => access.Localize("inbox.column.account"))
                .Should().Within(TestTimeouts.Convergence).Emit("the unrestored build", ct))
            .Should().Be("Account", "without the restore the off-turn build reads no viewer and falls back to English");
    }

    /// <summary>
    /// A connection query is run for the path alone, whatever projection its provider wrote — none,
    /// or a wider one — so asking whether a connection exists never reads what it holds.
    /// </summary>
    [Fact]
    public void AConnectionQuery_ReadsThePathOnly()
    {
        const string q = "namespace:alice/_Account nodeType:Account";
        new QueryParser().Parse(InboxQueries.ConnectionQuery(q)).Select.Should().Equal(["path"]);
        new QueryParser().Parse(InboxQueries.ConnectionQuery(q + " select:content")).Select.Should().Equal(["path"]);
        InboxQueries.ConnectionQuery(q + " select:path").Should().Be(q + " select:path", "an already narrow query is left as written");
    }

    private async Task<IReadOnlyList<string>> ColumnTitles(string owner, string locale, CancellationToken ct)
    {
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = owner, Name = owner, Email = $"{owner}@meshweaver.io", Locale = locale });
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(owner), new LayoutAreaReference(InboxLayoutArea.AreaName));
        var grid = (DataGridControl)(await stream.GetControlStream(InboxLayoutArea.AreaName + "/Main/Grid")
            .Should().Within(TestTimeouts.Convergence).Match(c => c is DataGridControl, "the grid", ct))!;
        return [.. grid.Columns.OfType<PropertyColumnControl>().Select(c => c.Title?.ToString() ?? string.Empty)];
    }

    /// <summary>
    /// The page's source styles and Only options once the rows feed has answered — the grid no longer
    /// loading means the connection reads answered first (the rows are read only for connected sources).
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string> Styles, IReadOnlyList<string> Only)> Answered(string owner, CancellationToken ct)
    {
        var stream = Open(owner);
        await stream.GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxRows.GridId + "_loading")))
            .Should().Within(TestTimeouts.Convergence).Match(l => l.ValueKind == JsonValueKind.False, "the rows feed answered", ct);
        var styles = await stream.GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxLayoutArea.SourceStylesId)))
            .Should().Within(TestTimeouts.Convergence).Match(o => o.ValueKind == JsonValueKind.Object, "the source styles", ct);
        return (styles.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty),
            await OnlyOptions(stream, ct));
    }

    private static async Task<IReadOnlyList<string>> OnlyOptions(ISynchronizationStream<JsonElement> stream, CancellationToken ct)
    {
        var options = await stream.GetDataStream<JsonElement>(
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxLayoutArea.SourceOptionsId)))
            .Should().Within(TestTimeouts.Convergence).Match(o => o.ValueKind == JsonValueKind.Array, "the Only options", ct);
        return [.. options.EnumerateArray().Select(o => o.GetProperty("item").GetString() ?? string.Empty)];
    }

    private ISynchronizationStream<JsonElement> Open(string owner)
    {
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = owner, Name = owner, Email = $"{owner}@meshweaver.io" });
        return GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(owner), new LayoutAreaReference(InboxLayoutArea.AreaName));
    }

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
