using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Three faults a fresh install showed on the inbox (2026-10-30), pinned:
/// <list type="bullet">
///   <item>one source that never answers held the whole list ("mails load forever") — now each source has a bound on
///     its first answer, the others are listed, and the silent one is NAMED; an action's preflight waits for real
///     answers and never acts on a stand-in;</item>
///   <item>the Kind filter showed a full-colour WhatsApp logo for Conversations — the chrome shows monochrome glyphs
///     only, brand marks identify a source in its own column;</item>
///   <item>a package had no way onto the inbox — the <see cref="UiContribution.InboxContext"/> lane embeds its area,
///     gated like every embedding lane.</item>
/// </list>
/// Every timer runs on a virtual scheduler: the verdicts never depend on thread-pool timing.
/// </summary>
public class InboxSlowSourcesAndChromeTest
{
    private static readonly ImmutableList<InboxRow> OneRow = [new InboxRow { Path = "jroe/_Email/1", Title = "Hello" }];
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static List<ImmutableList<(ImmutableList<InboxRow> Rows, string? Unread, int Fetched, string? Slow)>> Record(
        IObservable<ImmutableList<(ImmutableList<InboxRow> Rows, string? Unread, int Fetched, string? Slow)>> answers)
    {
        var seen = new List<ImmutableList<(ImmutableList<InboxRow> Rows, string? Unread, int Fetched, string? Slow)>>();
        answers.Subscribe(seen.Add);
        return seen;
    }

    [Fact]
    public void ASourceThatNeverAnswers_DoesNotHoldTheOthers_AndIsNamed()
    {
        var scheduler = new TestScheduler();
        var seen = Record(InboxLayoutArea.FanIn(
            [
                ("Outlook", Observable.Return((OneRow, (string?)null, 1))),
                ("Gmail", Observable.Never<(ImmutableList<InboxRow>, string?, int)>()),
            ],
            Bound, scheduler));

        seen.Should().BeEmpty("before the bound, the list waits for the silent source");
        scheduler.AdvanceBy(Bound.Ticks);

        var first = seen.Should().ContainSingle().Subject;
        first[0].Rows.Should().Equal(OneRow, "the source that answered is listed");
        first[0].Slow.Should().BeNull();
        first[1].Rows.Should().BeEmpty();
        first[1].Slow.Should().Be("Gmail", "the silent source is named, never left to spin");
    }

    [Fact]
    public void ASlowSource_ReplacesItsStandIn_WhenItAnswers()
    {
        var scheduler = new TestScheduler();
        var gmail = new Subject<(ImmutableList<InboxRow>, string?, int)>();
        var seen = Record(InboxLayoutArea.FanIn([("Gmail", gmail)], Bound, scheduler));

        scheduler.AdvanceBy(Bound.Ticks);
        gmail.OnNext((OneRow, null, 1));

        seen.Should().HaveCount(2);
        seen[0][0].Slow.Should().Be("Gmail");
        seen[1][0].Slow.Should().BeNull("the late answer replaces the stand-in");
        seen[1][0].Rows.Should().Equal(OneRow);
    }

    [Fact]
    public void AnAnswerBeforeTheBound_IsNeverFollowedByAStandIn()
    {
        var scheduler = new TestScheduler();
        var outlook = new Subject<(ImmutableList<InboxRow>, string?, int)>();
        var seen = Record(InboxLayoutArea.FanIn([("Outlook", outlook)], Bound, scheduler));

        outlook.OnNext((OneRow, null, 1));
        scheduler.AdvanceBy(Bound.Ticks * 2);

        seen.Should().ContainSingle().Which[0].Slow.Should().BeNull("the arbitration is serialized: a deadline after the answer emits nothing");
    }

    [Fact]
    public void WithoutABound_ThePreflightWaitsForEveryRealAnswer()
    {
        var scheduler = new TestScheduler();
        var gmail = new Subject<(ImmutableList<InboxRow>, string?, int)>();
        var seen = Record(InboxLayoutArea.FanIn([("Outlook", Observable.Return((OneRow, (string?)null, 1))), ("Gmail", gmail)], bound: null, scheduler));

        scheduler.AdvanceBy(Bound.Ticks * 6);
        seen.Should().BeEmpty("an action never acts on a stand-in: it waits for the slow source's real answer");
        gmail.OnNext((OneRow, null, 1));
        seen.Should().ContainSingle().Which.Should().OnlyContain(leg => leg.Slow == null);
    }

    // ── the chrome: glyphs only ────────────────────────────────────────────

    [Fact]
    public void TheKindFilter_ShowsGlyphsOnly_NeverABrandMark()
    {
        // Conversations is listed with WhatsApp FIRST — the order that put its logo in the filter.
        var sources = new (string Category, string Source)[]
        {
            ("Approvals", "Approvals"), ("Notifications", "Notifications"), ("Mail", "Mail"), ("Mail", "Outlook"), ("Mail", "Gmail"),
            ("Conversations", "WhatsApp"), ("Conversations", "Teams"), ("Conversations", "iMessage"), ("Activities", "Activities"),
        };
        foreach (var category in sources.GroupBy(s => s.Category))
        {
            var icon = InboxSourceIcons.CategoryIcon(category.Key, category.Select(s => InboxSourceIcons.Resolve(s.Source, null)));
            InboxSourceIcons.IsGlyph(icon).Should().BeTrue($"{category.Key}: the Kind filter shows a monochrome glyph, got {icon[..Math.Min(40, icon.Length)]}");
            Icon.Parse(icon)!.Provider.Should().Be(Icon.FluentProvider);
        }
        InboxSourceIcons.CategoryIcon("Conversations", [InboxSourceIcons.Resolve("WhatsApp", null)])
            .Should().Be("ChatMultiple", "Conversations covers Teams, WhatsApp and iMessage: a neutral chat bubble");
        InboxSourceIcons.CategoryIcon("SomeProviderCategory", [InboxSourceIcons.Resolve("Teams", null)])
            .Should().Be(InboxSourceIcons.Fallback, "a category made only of brand marks still gets a glyph");
        InboxSourceIcons.IsGlyph(InboxSourceIcons.Resolve("Teams", null)).Should().BeFalse("the source column keeps the brand mark");
    }

    // ── the Inbox contribution lane ────────────────────────────────────────

    private static (MeshNode, UiContribution) C(string path, UiContribution content) => (MeshNode.FromPath(path), content);

    private static readonly MeshNode UserHome = MeshNode.FromPath("jroe") with { NodeType = UserNodeType.NodeType };

    [Fact]
    public void AnInboxContribution_IsEmbedded_OnlyFromItsOwnPartition_InOrder_WithAUniqueId()
    {
        var contributions = new[]
        {
            C("Mail/Inbox/Status", new UiContribution { Context = UiContribution.InboxContext, Area = "InboxAccounts", Order = 10 }),
            C("Other/Inbox/Status", new UiContribution { Context = UiContribution.InboxContext, Area = "Early", Order = 1 }),
            C("Other/Inbox/Foreign", new UiContribution { Context = UiContribution.InboxContext, Area = "X", Address = "Mail" }),
            C("Other/Inbox/Admin", new UiContribution { Context = UiContribution.InboxContext, Area = "Y", Gates = new UiContributionGates { AdminOnly = true } }),
            C("Mail/Profile/Z", new UiContribution { Context = UiContribution.ProfileContext, Area = "Z" }),
        };

        var areas = UiContributionProjection.ProjectInboxAreas(contributions, "jroe", UserHome, isAdmin: false, viewerId: "jroe");

        areas.Select(a => (a.Area, a.Address)).Should().Equal([("Early", null), ("InboxAccounts", null)],
            "in order; an address outside the contributor's partition, an admin-only entry for a non-admin and another context are dropped");
        areas.Select(a => a.Id).Should().OnlyHaveUniqueItems("two packages' Inbox/Status must never share a layout slot");
        UiContributionSeedValidation.PlatformContexts.Should().Contain(UiContribution.InboxContext, "a seed naming it is not 'declared by nobody'");
    }

    [Fact]
    public void AShapeGate_IsHeldBack_UntilTheUserNodeIsKnown_AndThenDecidedOnIt()
    {
        var excluded = new[]
        {
            C("Mail/Inbox/NotOnHomes", new UiContribution
            {
                Context = UiContribution.InboxContext, Area = "A", Gates = new UiContributionGates { ExcludePartitionRoot = true },
            }),
        };
        UiContributionProjection.ProjectInboxAreas(excluded, "jroe", userNode: null, isAdmin: false, viewerId: "jroe")
            .Should().BeEmpty("a gate that needs the node's shape narrows while the node is unknown, never fails open");
    }

    [Fact]
    public void AnInboxContribution_NeedsTheViewersLivePermission()
    {
        var contributions = new[]
        {
            C("Mail/Inbox/Read", new UiContribution { Context = UiContribution.InboxContext, Area = "R" }),
            C("Mail/Inbox/Sync", new UiContribution { Context = UiContribution.InboxContext, Area = "S", RequiredPermission = Permission.Sync }),
        };
        var projected = UiContributionProjection.ProjectInboxAreas(contributions, "jroe", UserHome, isAdmin: false, viewerId: "jroe");

        UiContributionProjection.FilterInboxAreas(projected, Permission.Read).Select(a => a.Area).Should().Equal(["R"],
            "an entry demanding Sync is hidden from a viewer without it");
        UiContributionProjection.FilterInboxAreas(projected, Permission.Read | Permission.Sync).Select(a => a.Area).Should().Equal(["R", "S"]);
        UiContributionProjection.FilterInboxAreas(projected, Permission.None).Should().BeEmpty("never less than Read");
    }

    [Fact]
    public void ATickedTag_StaysTicked_WhileASourceStandsIn_AndIsDroppedOnceEverySourceAnswered()
    {
        var urgent = InboxRows.TagKey("urgent");
        var reply = InboxRows.TagKey("reply");
        // The viewer ticked #urgent, whose rows sit only in a source that has not answered this read.
        var shown = InboxLayoutArea.TagItems([(urgent, "urgent", 2), (reply, "reply", 1)], "Important", [urgent]);
        var counts = new[] { (reply, "reply", 1) };

        InboxLayoutArea.TagItems(counts, "Important", [urgent], held: shown)
            .Where(item => item.Checked).Select(item => item.Key).Should().Equal([urgent],
                "while a source stands in, the rows are incomplete: dropping the box would untick it and widen the filter");
        InboxLayoutArea.TagItems(counts, "Important", [urgent], held: null)
            .Select(item => item.Key).Should().Equal([reply], "once every source answered, a tag no row carries is gone");
    }

    [Fact]
    public void ALegEndedByTheStallTerminal_KeepsTheRowsIncomplete()
    {
        // Slow, then the query's stall terminal ends it: the leg turns from a stand-in into an unreadable source.
        InboxLayoutArea.Incomplete([(null, null), (null, "Gmail")]).Should().BeTrue("a stand-in leaves the rows incomplete");
        InboxLayoutArea.Incomplete([(null, null), ("Gmail", null)]).Should().BeTrue(
            "a leg the stall terminal ended has contributed no rows either — a tag only it carries must stay ticked");
        InboxLayoutArea.Incomplete([(null, null), (null, null)]).Should().BeFalse("every source answered");
    }

    [Fact]
    public void OnlyTheDisplayFeed_PublishesTheSlowNotice()
    {
        // The action preflight has no bound, so it never carries a stand-in: were it to publish, it would clear the
        // notice while the display grid still omits the silent source.
        InboxLayoutArea.SlowSources([null, null], display: false).Should().BeNull("the preflight publishes no slow notice");
        InboxLayoutArea.SlowSources([null, "Gmail", "Gmail"], display: true).Should().Equal(["Gmail"]);
        InboxLayoutArea.SlowSources([null, null], display: true).Should().BeEmpty("the display clears it once all answered");
    }
}
