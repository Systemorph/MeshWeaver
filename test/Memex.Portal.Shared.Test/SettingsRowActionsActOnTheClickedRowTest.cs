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

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(InboxSettingsTab.MailRow), typeof(InvitationsSettingsTab.InvitationRow),
                typeof(ServiceIdentitiesSettingsTab.TokenRow))
            .AddLayout(layout => layout
                .WithView(InboxArea, (host, _) => InboxSettingsTab.MailGrid(host, mails,
                    (_, row) => acted.OnNext($"archive:{row.Path}")))
                .WithView(InvitationsArea, (host, _) => InvitationsSettingsTab.InvitationGrid(host, invitations,
                    (_, row) => acted.OnNext($"revoke:{row.Path}")))
                .WithView(TokensArea, (host, _) => ServiceIdentitiesSettingsTab.TokenGrid(host, tokens,
                    rotate: (_, row) => acted.OnNext($"rotate:{row.NodePath}"),
                    revoke: (_, row) => acted.OnNext($"revoke:{row.NodePath}"))));

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
        var (stream, buttonArea) = await OpenGrid(area, column);
        var rendered = await RenderedRows(stream, dataId, key, Ids.Count);

        for (var k = 0; k < Ids.Count; k++)
        {
            var target = rendered[k].GetProperty(key).GetString();
            var done = await ClickAndReadAction(stream, buttonArea, AsTheGridRendersIt(rendered[k]));
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
        var (stream, buttonArea) = await OpenGrid(area, column);
        var clickedRow = (await RenderedRows(stream, dataId, key, Ids.Count))[1]; // bravo
        var clickedTarget = clickedRow.GetProperty(key).GetString();

        string[] next = ["aardvark", "abacus", "charlie"]; // bravo gone, slot 1 is abacus
        Push(area, next);
        await RenderedRows(stream, dataId, key, next.Length, firstContains: "aardvark");

        var done = await ClickAndReadAction(stream, buttonArea, AsTheGridRendersIt(clickedRow));
        done.Should().Be($"{action}:{clickedTarget}",
            "the person clicked bravo; the row that has since moved into its slot is not theirs to act on");
    }

    /// <summary>NEGATIVE CONTROL: the same button clicked with no row does nothing — the target comes
    /// from the row on the click and from nothing else.</summary>
    [Theory]
    [MemberData(nameof(Buttons))]
    public async Task AClickWithNoRowDoesNothing(string area, string dataId, string key, int column, string action)
    {
        var (stream, buttonArea) = await OpenGrid(area, column);
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

    private async Task<(ISynchronizationStream<JsonElement> Stream, string ButtonArea)> OpenGrid(string area, int column)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(area));
        var buttonArea = $"{area}/{DataGridControl.TemplateColumnArea(column)}";
        await stream.GetControlStream(buttonArea).Should().Within(10.Seconds()).Match(
            c => c is ButtonControl,
            "the action column's button is rendered into the template column's area",
            TestContext.Current.CancellationToken);
        return (stream, buttonArea);
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

    /// <summary>The grid client's row: a JSON object with no pointer (a grid sorts and pages).</summary>
    private static RowContext AsTheGridRendersIt(JsonElement row) => new() { Value = JsonObject.Create(row) };

    private static void Submit(ISynchronizationStream<JsonElement> stream, string area, RowContext? row)
        => stream.SubmitUserAction(new ClickedEvent(area, stream.StreamId) { Row = row },
            actingUser: null, onRefused: null, onAccepted: null);

    private async Task<string> ClickAndReadAction(ISynchronizationStream<JsonElement> stream, string area, RowContext row)
    {
        var next = new ReplaySubject<string>(1);
        using var _ = acted.Subscribe(next);
        Submit(stream, area, row);
        var done = await next.Should().Within(10.Seconds()).Emit(
            "the row's button runs its action", TestContext.Current.CancellationToken);
        return done!;
    }
}
