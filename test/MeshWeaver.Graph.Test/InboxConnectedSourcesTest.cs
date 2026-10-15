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
/// The inbox lists only the sources a viewer has CONNECTED (<see cref="InboxQueryLeg.ConnectedWhen"/>)
/// — owner, 2026-10-15: "i see a lot of stuff that's not even configured ==> must get only configured
/// sources" — and says which of the viewer's accounts an item reached (<see cref="InboxRow.Account"/>):
/// "we have many emails => show which mail account it is".
/// </summary>
public class InboxConnectedSourcesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SourcesArea = InboxLayoutArea.AreaName + "/Main/Commands/Sources";

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
    public async Task AnUnconnectedSource_GetsNoSwitch_AndAConnectedOneDoes()
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

        var mail = "Toggle" + InboxRows.SourceKey("Mail");
        var notifications = "Toggle" + InboxRows.SourceKey("Notifications");

        var bare = await Toggles(without, ct);
        bare.Should().Contain(notifications, "a source that names no connection is listed for everyone");
        bare.Should().NotContain(mail, "nobody stored mail for this viewer — the mailbox is not connected");
        (await OnlyOptions(without, ct)).Should().NotContain(InboxRows.SourceKey("Mail"), "the Only select lists connected sources only");

        (await Toggles(with, ct)).Should().Contain([notifications, mail], "stored mail connects the Mail source");
        (await OnlyOptions(with, ct)).Should().Contain(InboxRows.SourceKey("Mail"));
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

    private async Task<IReadOnlyList<string>> Toggles(string owner, CancellationToken ct)
    {
        var stream = Open(owner);
        var stack = (StackControl)(await stream.GetControlStream(SourcesArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is StackControl, "the source toggles", ct))!;
        return [.. stack.Areas.Select(a => a.Id?.ToString() ?? string.Empty)];
    }

    private async Task<IReadOnlyList<string>> OnlyOptions(string owner, CancellationToken ct)
    {
        var stream = Open(owner);
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
