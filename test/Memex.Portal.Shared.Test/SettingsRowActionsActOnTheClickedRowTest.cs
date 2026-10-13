using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The admin settings lists with a per-row action — Inbox (Archive), Invitations (Revoke) and the
/// service-identity tokens (Rotate, Revoke) — carry that action as a ROW-SCOPED button
/// (Doc/GUI/DataBinding → "Row-scoped actions"): ONE button declared in a template column, whose
/// click says which row it came from. Before, the first data-binding conversion had downgraded each
/// to "select the row, then press the button below the grid".
///
/// <para>Each grid's action is handed in, so this pins exactly the part that changed: which row the
/// action receives. Clicking row k acts on row k for every k; a list that changes between the render
/// and the click still acts on the row that was clicked; a click with no row does nothing (negative
/// control).</para>
/// </summary>
public class SettingsRowActionsActOnTheClickedRowTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string InboxArea = "Inbox";
    private const string InvitationsArea = "Invitations";
    private const string TokensArea = "Tokens";

    private static InboxSettingsTab.MailRow Mail(string id)
        => new($"{id} <{id}@example.com>", $"Subject {id}", "2026-10-01 10:00", "New", "…",
            $"Admin/Inbox/{id}", $"{id}@example.com", IsArchived: false);

    private static InvitationsSettingsTab.InvitationRow Invitation(string id)
        => new($"{id}@example.com", "Pending", "2026-10-01", "admin", "", "", "", "",
            $"Admin/Invitation/{id}", IsPending: true);

    private static ServiceIdentitiesSettingsTab.TokenRow Token(string id)
        => new("svc", id, "mw_ab12", "Active", "2026-10-01", "∞", "∞",
            $"Admin/ServiceIdentity/svc/_Api/{id}", IsRevoked: false, DateTimeOffset.UnixEpoch, null);

    private static readonly ImmutableList<string> Ids = ["alpha", "bravo", "charlie"];

    private readonly BehaviorSubject<IEnumerable<InboxSettingsTab.MailRow>> mails = new(Ids.Select(Mail));
    private readonly BehaviorSubject<IEnumerable<InvitationsSettingsTab.InvitationRow>> invitations = new(Ids.Select(Invitation));
    private readonly BehaviorSubject<IEnumerable<ServiceIdentitiesSettingsTab.TokenRow>> tokens = new(Ids.Select(Token));

    /// <summary>Every action an owner ran, as "action:path" — hot: each click subscribes before it submits.</summary>
    private readonly Subject<string> acted = new();

    /// <summary>The targets whose action keeps its click RUNNING until the test releases <see cref="held"/>.</summary>
    private ImmutableHashSet<string> holdOpen = [];

    /// <summary>The tracked source a held click waits on; completed in a <c>finally</c> so no click is stranded.</summary>
    private readonly Subject<ClickProgress> held = new();

    /// <summary>The action every grid is handed: records which row it acted on, and — for a held target —
    /// keeps its click session open by tracking <see cref="held"/>, exactly as an action that started
    /// longer work would (Doc/GUI/ButtonPendingState).</summary>
    private void Act(UiActionContext ctx, string action, string target)
    {
        if (holdOpen.Contains(target))
            ctx.Track(held);
        acted.OnNext($"{action}:{target}");
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(InboxSettingsTab.MailRow), typeof(InvitationsSettingsTab.InvitationRow),
                typeof(ServiceIdentitiesSettingsTab.TokenRow))
            .AddLayout(layout => layout
                .WithView(InboxArea, (host, _) => InboxSettingsTab.MailGrid(host, mails,
                    (ctx, row) => Act(ctx, "archive", row.Path)))
                .WithView(InvitationsArea, (host, _) => InvitationsSettingsTab.InvitationGrid(host, invitations,
                    (ctx, row) => Act(ctx, "revoke", row.Path)))
                .WithView(TokensArea, (host, _) => ServiceIdentitiesSettingsTab.TokenGrid(host, tokens,
                    rotate: (ctx, row) => Act(ctx, "rotate", row.NodePath),
                    revoke: (ctx, row) => Act(ctx, "revoke", row.NodePath))));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    /// <summary>The grids under test: area, data slot, the row property naming the target, and each
    /// row-scoped button as (column, action name).</summary>
    public static TheoryData<string, string, string, int, string> Buttons => new()
    {
        { InboxArea, "inboxList", "path", InboxSettingsTab.ArchiveColumn, "archive" },
        { InvitationsArea, "invitationList", "path", InvitationsSettingsTab.RevokeColumn, "revoke" },
        { TokensArea, "serviceIdentityTokens", "nodePath", ServiceIdentitiesSettingsTab.RotateColumn, "rotate" },
        { TokensArea, "serviceIdentityTokens", "nodePath", ServiceIdentitiesSettingsTab.RevokeColumn, "revoke" },
    };

    /// <summary>The button in row k acts on row k, for every k.</summary>
    [Theory]
    [MemberData(nameof(Buttons))]
    public async Task TheButtonInRowKActsOnRowK(string area, string dataId, string key, int column, string action)
    {
        var (stream, buttonArea, rowKey) = await OpenGrid(area, column);
        var rendered = await RenderedRows(stream, dataId, key, Ids.Count);

        for (var k = 0; k < Ids.Count; k++)
        {
            var target = rendered[k].GetProperty(key).GetString();
            var done = await ClickAndReadAction(stream, buttonArea, AsTheGridRendersIt(rendered[k], rowKey));
            done.Should().Be($"{action}:{target}", $"the {action} button in row {k} was clicked");
        }
    }

    /// <summary>The list changes between the render and the click — a row inserted above, the clicked
    /// one gone — and the click still acts on the row that was clicked.</summary>
    [Theory]
    [MemberData(nameof(Buttons))]
    public async Task AfterAChangeTheClickStillActsOnTheClickedRow(
        string area, string dataId, string key, int column, string action)
    {
        var (stream, buttonArea, rowKey) = await OpenGrid(area, column);
        var clickedRow = (await RenderedRows(stream, dataId, key, Ids.Count))[1]; // bravo
        var clickedTarget = clickedRow.GetProperty(key).GetString();

        string[] next = ["aardvark", "abacus", "charlie"]; // bravo gone, slot 1 is abacus
        Push(area, next);
        await RenderedRows(stream, dataId, key, next.Length, firstContains: "aardvark");

        var done = await ClickAndReadAction(stream, buttonArea, AsTheGridRendersIt(clickedRow, rowKey));
        done.Should().Be($"{action}:{clickedTarget}",
            "the person clicked bravo; the row that has since moved into its slot is not theirs to act on");
    }

    /// <summary>
    /// Two rows of one grid are two clicks. While row 0's action is still RUNNING (held open by a
    /// tracked source), the button in row 1 runs row 1's action — it is not refused as "another action
    /// on this control is still running". The grid declares its row key, the client carries the row's
    /// value of it as <see cref="RowContext.Key"/>, and the owner keys each row's click by it
    /// (Doc/GUI/ButtonPendingState → "Idempotence"). Without a declared key the rows are known only by
    /// value, and this second click is refused until the first one settles.
    /// <para>NEGATIVE CONTROL: row 0 clicked again meanwhile is the SAME click — joined, never re-run.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Buttons))]
    public async Task AClickOnAnotherRowRunsWhileTheFirstIsStillRunning(
        string area, string dataId, string key, int column, string action)
    {
        var (stream, buttonArea, rowKey) = await OpenGrid(area, column);
        var rendered = await RenderedRows(stream, dataId, key, Ids.Count);
        var first = rendered[0].GetProperty(key).GetString()!;
        var second = rendered[1].GetProperty(key).GetString()!;
        holdOpen = [first];
        try
        {
            (await ClickAndReadAction(stream, buttonArea, AsTheGridRendersIt(rendered[0], rowKey)))
                .Should().Be($"{action}:{first}", "row 0's click runs, and stays running on the held source");

            (await ClickAndReadAction(stream, buttonArea, AsTheGridRendersIt(rendered[1], rowKey)))
                .Should().Be($"{action}:{second}",
                    "row 1 is another row of the grid — its click is its own, not refused behind row 0's");

            var again = new ReplaySubject<string>(1);
            using var _ = acted.Subscribe(again);
            Submit(stream, buttonArea, AsTheGridRendersIt(rendered[0], rowKey));
            await again.Should().NotEmit(500.Milliseconds(),
                "row 0 clicked again while its click still runs is the same click — joined, never re-run",
                TestContext.Current.CancellationToken);
        }
        finally
        {
            holdOpen = [];
            held.OnCompleted();
        }
    }

    /// <summary>NEGATIVE CONTROL: the same button clicked with no row does nothing — the target comes
    /// from the row on the click and from nothing else.</summary>
    [Theory]
    [MemberData(nameof(Buttons))]
    public async Task AClickWithNoRowDoesNothing(string area, string dataId, string key, int column, string action)
    {
        var (stream, buttonArea, _) = await OpenGrid(area, column);
        await RenderedRows(stream, dataId, key, Ids.Count);

        var next = new ReplaySubject<string>(1);
        using var _ = acted.Subscribe(next);
        Submit(stream, buttonArea, row: null);

        await next.Should().NotEmit(500.Milliseconds(),
            $"with no row the {action} action cannot know which item, and must not guess",
            TestContext.Current.CancellationToken);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private void Push(string area, string[] ids)
    {
        switch (area)
        {
            case InboxArea: mails.OnNext(ids.Select(Mail)); break;
            case InvitationsArea: invitations.OnNext(ids.Select(Invitation)); break;
            default: tokens.OnNext(ids.Select(Token)); break;
        }
    }

    /// <summary>Opens the grid's area and returns its stream, the action button's area and the grid's
    /// declared row key (<see cref="DataGridControl.RowKey"/>) as the client reads it.</summary>
    private async Task<(ISynchronizationStream<JsonElement> Stream, string ButtonArea, string? RowKey)> OpenGrid(
        string area, int column)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(area));
        var grid = await stream.GetControlStream(area).Should().Within(10.Seconds()).Match(
            c => c is DataGridControl, "the area renders the grid", TestContext.Current.CancellationToken);
        var buttonArea = $"{area}/{DataGridControl.TemplateColumnArea(column)}";
        await stream.GetControlStream(buttonArea).Should().Within(10.Seconds()).Match(
            c => c is ButtonControl,
            "the action column's button is rendered into the template column's area",
            TestContext.Current.CancellationToken);
        return (stream, buttonArea, ((DataGridControl)grid!).RowKey?.ToString());
    }

    private static async Task<ImmutableArray<JsonElement>> RenderedRows(
        ISynchronizationStream<JsonElement> stream, string dataId, string key, int count, string? firstContains = null)
    {
        var all = await stream.GetDataStream<JsonElement>(
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(dataId)))
            .Should().Within(10.Seconds()).Match(
                r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == count
                     && (firstContains is null
                         || (r[0].GetProperty(key).GetString() ?? "").Contains(firstContains, StringComparison.Ordinal)),
                "the client mirror holds the rows the grid renders",
                TestContext.Current.CancellationToken);
        return [.. all.EnumerateArray().Select(r => r.Clone())];
    }

    /// <summary>The grid client's row, built exactly as the grid views build it: a JSON object with no
    /// pointer (a grid sorts and pages), keyed by the grid's declared <paramref name="rowKey"/> when it has one.</summary>
    private static RowContext AsTheGridRendersIt(JsonElement row, string? rowKey)
    {
        var value = JsonObject.Create(row)!;
        return new() { Value = value, Key = rowKey is { Length: > 0 } property ? DataGridRowSelection.KeyOf(value, property) : null };
    }

    private static void Submit(ISynchronizationStream<JsonElement> stream, string area, RowContext? row,
        Action<string>? onRefused = null)
        => stream.SubmitUserAction(new ClickedEvent(area, stream.StreamId) { Row = row },
            actingUser: null, onRefused: onRefused, onAccepted: null);

    private async Task<string> ClickAndReadAction(ISynchronizationStream<JsonElement> stream, string area, RowContext row)
    {
        var next = new ReplaySubject<string>(1);
        using var _ = acted.Subscribe(next);
        // A refused click reports itself at once, carrying the owner's reason, instead of a silent wait.
        Submit(stream, area, row, onRefused: reason => next.OnNext($"refused:{reason}"));
        var done = await next.Should().Within(10.Seconds()).Emit(
            "the row's button runs its action", TestContext.Current.CancellationToken);
        return done!;
    }
}
