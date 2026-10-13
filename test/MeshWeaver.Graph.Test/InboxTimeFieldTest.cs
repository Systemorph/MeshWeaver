using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A source can time its rows by a CONTENT property (<see cref="InboxQueryLeg.TimeField"/>) instead
/// of the node's <c>LastModified</c>: a write that is not news — opening a conversation clears its
/// unread mark — refreshes <c>LastModified</c>, and timing by it moved the item ahead of genuinely
/// newer ones. The row reads the field (falling back to <c>LastModified</c>), and the page keeps
/// <c>content</c> in such a leg's projection.
/// </summary>
public class InboxTimeFieldTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly DateTimeOffset Earlier = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The row time is the content field in every shape content arrives in, else LastModified.</summary>
    [Fact]
    public void ARowIsTimedByItsSourcesField_InEveryContentShape_ElseByLastModified()
    {
        var modified = new DateTimeOffset(2026, 10, 13, 7, 0, 0, TimeSpan.Zero);
        var typed = new MeshNode("n", "alice/_X") { LastModified = modified, Content = new Notification { CreatedAt = Earlier } };
        InboxRows.TimeOf(typed, "createdAt").Should().Be(Earlier, "typed content, read by its camelCase name");
        InboxRows.TimeOf(typed with { Content = JsonSerializer.SerializeToElement(new { createdAt = Earlier }) }, "createdAt")
            .Should().Be(Earlier, "JSON content");
        InboxRows.TimeOf(typed with { Content = JsonSerializer.SerializeToElement(new { createdAt = "2026-10-01T08:00:00" }) }, "createdAt")
            .Should().Be(Earlier, "an instant stored without an offset is UTC, never the container's zone");
        InboxRows.TimeOf(typed, null).Should().Be(modified, "negative control: no time field, LastModified");
        InboxRows.TimeOf(typed, "lastReplyAt").Should().Be(modified, "a field the content lacks falls back to LastModified");
        InboxRows.TimeOf(typed with { Content = null }, "createdAt").Should().Be(modified, "no content falls back to LastModified");

        var row = InboxRows.Of(typed, new InboxSection(InboxBand.NeedsYou, "Notifications", 10, "q") { TimeField = "createdAt" }, null, null);
        row.Stamp.Should().Be(Earlier.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            "the stamp the newest-first order reads is the source's time");
    }

    /// <summary>
    /// The built-in sources whose items a READ rewrites are timed by when they arrived — notifications
    /// by <c>createdAt</c>, mail by <c>receivedAt</c> — on the real page path (<see cref="InboxLayoutArea.Sections"/>),
    /// and their queries already project <c>content</c>, so the page adds no second projection.
    /// </summary>
    [Fact]
    public void TheBuiltInReadableSources_AreTimedByArrival_OnThePagesOwnSections()
    {
        var sections = InboxLayoutArea.Sections(InboxQueries.BuiltIn, "alice");
        sections.Where(s => s.Source is "Notifications" or InboxQueries.ApprovalsSource).Should().OnlyContain(s => s.TimeField == "createdAt");
        sections.Where(s => s.Source == "Mail").Should().OnlyContain(s => s.TimeField == "receivedAt");
        sections.Where(s => s.Source is "Notifications" or "Mail" or InboxQueries.ApprovalsSource).Should().HaveCount(6, "an unread and a read leg each");
        sections.Where(s => s.TimeField != null).Should().OnlyContain(s =>
            InboxQueries.Bounded(s.Query, s.TimeField) == $"{s.Query} limit:{InboxQueries.LegLimit}",
            "a timed built-in leg is already projected for its time");
    }

    /// <summary>Typed content no serializer can convert is unreadable — the row falls back, the inbox does not fault.</summary>
    [Fact]
    public void UnconvertibleContent_FallsBackToLastModified()
    {
        var modified = new DateTimeOffset(2026, 10, 13, 7, 0, 0, TimeSpan.Zero);
        var node = new MeshNode("n", "alice/_X") { LastModified = modified, Content = new Unconvertible() };
        InboxRows.TimeOf(node, "createdAt").Should().Be(modified);
    }

    private sealed class Unconvertible
    {
        public IntPtr Handle { get; init; } = new(1);
    }

    /// <summary>
    /// <c>before:</c> / <c>after:</c> in the search narrow the rows by their time — so everything
    /// before a day can be listed, ticked and put away in one Delete — and the rest of the text still
    /// goes to the sources. A bound that is not a date stays text.
    /// </summary>
    [Fact]
    public void ADateBoundInTheSearch_NarrowsTheRowsByTheirTime()
    {
        InboxRows.SplitDates("invoice before:2026-10-01 after:2026-09-01").Should()
            .Be(("invoice", (DateTimeOffset?)new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), (DateTimeOffset?)new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
        InboxRows.SplitDates("before:yesterday").Should().Be(("before:yesterday", (DateTimeOffset?)null, (DateTimeOffset?)null),
            "a bound that is not a date is searched for as text, never silently dropped");

        MeshNode At(string id, DateTimeOffset when) => new(id, "alice/_X") { Name = id, LastModified = when };
        var section = new InboxSection(InboxBand.NeedsYou, "Mail", 20, "q");
        var rows = new[] { At("sept", Earlier.AddDays(-20)), At("oct1", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)), At("oct2", Later) }
            .Select(n => InboxRows.Of(n, section, null, null)).ToList();
        InboxRows.Apply(rows, new InboxFilter { Search = "before:2026-10-01" }).Select(r => r.Title).Should().Equal(["sept"],
            "strictly before the day");
        InboxRows.Apply(rows, new InboxFilter { Search = "after:2026-10-01" }).Select(r => r.Title).Should().Equal(["oct2", "oct1"],
            "the day itself and later, newest first");
        InboxRows.Apply(rows, new InboxFilter()).Should().HaveCount(3, "negative control: no bound, nothing narrowed");
    }

    /// <summary>
    /// The side menu's categories: a category shows every row filed under it — even a source switched
    /// off by default — and All (none) leaves the switches in charge. Counts are read before any of it.
    /// </summary>
    [Fact]
    public void ACategory_ShowsItsRows_EvenFromASourceSwitchedOff()
    {
        MeshNode At(string id, int day) => new(id, "alice/_X") { Name = id, LastModified = Earlier.AddDays(day) };
        var chat = new InboxSection(InboxBand.NeedsYou, "WhatsApp", 30, "q1") { Category = "Conversations" };
        var teams = new InboxSection(InboxBand.NeedsYou, "Teams", 32, "q2") { Category = "Conversations", Unread = true };
        var activities = new InboxSection(InboxBand.Running, "Activities", 10, "q3");
        var rows = new[]
        {
            InboxRows.Of(At("w", 1), chat, null, null), InboxRows.Of(At("t", 2), teams, null, null),
            InboxRows.Of(At("a", 3), activities, null, null),
        };
        var switchedOff = new InboxFilter { Sources = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.SourceKey("Activities"), false) };

        InboxRows.Apply(rows, switchedOff, "Conversations").Select(r => r.Title).Should().Equal(["t", "w"],
            "two sources filed under one category, newest first");
        InboxRows.Apply(rows, switchedOff, "Activities").Select(r => r.Title).Should().Equal(["a"],
            "a source switched off is reached by its category");
        InboxRows.Apply(rows, switchedOff, category: null).Select(r => r.Title).Should().Equal(["t", "w"],
            "negative control: with All, the switches apply");
        rows[2].Category.Should().Be("Activities", "a leg with no category is filed under its source");

        var counts = InboxRows.CategoryCounts(rows);
        counts["Conversations"].Should().Be((2, 1), "two rows, one unread");
        counts["Activities"].Should().Be((1, 0));

        // The side menu's options: All first, then each category — its unread count when it has
        // unread rows, else all its rows.
        var options = InboxLayoutArea.CategoryOptions("All",
            [("Conversations", "Conversations", "💬"), ("Activities", "Activities", "⚙️")], counts);
        options.Select(o => ((Option<string>)o).Item).Should().Equal([string.Empty, "Conversations", "Activities"]);
        options.Select(o => o.Text).Should().Equal(["📥 All  1", "💬 Conversations  1", "⚙️ Activities  1"],
            "All and Conversations show their one unread row; Activities, with none unread, its one row");
    }

    /// <summary>A timed leg keeps <c>content</c> in its projection; an untimed one does not.</summary>
    [Fact]
    public void ATimedLeg_KeepsContentInItsProjection()
    {
        var parser = new QueryParser();
        const string leg = "namespace:alice/_Thread nodeType:Thread sort:content.lastReplyAt-desc";
        parser.Parse(InboxQueries.Bounded(leg, "lastReplyAt")).Select.Should().Contain("content");
        parser.Parse(InboxQueries.Bounded(leg)).Select.Should().NotContain("content", "negative control: an untimed leg reads no bodies");
        var timed = $"{leg} {InboxQueries.TimedRowProjection}";
        InboxQueries.Bounded(timed, "lastReplyAt").Should().Be($"{timed} limit:{InboxQueries.LegLimit}",
            "a leg already projected for its time is not re-projected");
    }

    /// <summary>
    /// 🚨 LIVE: an item that was only READ (its LastModified refreshed) stays behind a newer one when
    /// its source is timed by a content field — and, as the negative control, jumps ahead of it when
    /// the same leg is timed by LastModified.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ReadingAnOlderItem_DoesNotMoveItAhead_WhenItsSourceIsTimedByContent()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-time-field";
        await CreateUserAsync(owner, ct);
        var ns = $"{owner}/{NotificationService.SatelliteSegment}";
        await CreateAsync(Notice(ns, owner, "older", Earlier), ct);
        await CreateAsync(Notice(ns, owner, "newer", Later), ct);
        // Reading the older one: a write that is not news, refreshing its LastModified.
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream($"{ns}/older").Update(n =>
                n.ContentAs<Notification>(Mesh.JsonSerializerOptions) is { IsRead: false } open ? n with { Content = open with { IsRead = true } } : n))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var query = $"namespace:{ns} nodeType:{NotificationNodeType.NodeType} sort:content.createdAt-desc";
        var timed = new InboxSection(InboxBand.NeedsYou, "Notifications", 10, query) { TimeField = "createdAt" };
        (await Newest(timed, ct)).Should().Equal(["newer", "older"], "timed by when it arrived, reading it moves nothing");

        var untimed = new InboxSection(InboxBand.NeedsYou, "Notifications", 10, query);
        (await Newest(untimed, ct)).Should().Equal(["older", "newer"],
            "negative control: timed by LastModified, the read item jumps ahead — the defect this field exists for");
    }

    /// <summary>The leg's rows as the page reads them, newest first by the rows' own stamps.</summary>
    private async Task<string[]> Newest(InboxSection section, CancellationToken ct)
        => await InboxLayoutArea.Nodes(Mesh, section, string.Empty, "inbox-time-field")
            .Select(nodes => nodes.ToArray())
            .Where(nodes => nodes.Length == 2)
            .Select(nodes => nodes
                .Select(node => (node.Id, Stamp: InboxRows.Of(node, section, null, null).Stamp))
                .OrderByDescending(r => r.Stamp, StringComparer.Ordinal)
                .Select(r => r.Id)
                .ToArray())
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private static MeshNode Notice(string ns, string owner, string id, DateTimeOffset createdAt) => new(id, ns)
    {
        NodeType = NotificationNodeType.NodeType, Name = id, MainNode = owner, State = MeshNodeState.Active,
        Content = new Notification { Id = id, Title = id, Recipient = owner, NotificationType = NotificationType.General, CreatedAt = createdAt },
    };

    private async Task CreateAsync(MeshNode node, CancellationToken ct)
        => await Mesh.ServiceProvider.GetRequiredService<AccessService>()
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
