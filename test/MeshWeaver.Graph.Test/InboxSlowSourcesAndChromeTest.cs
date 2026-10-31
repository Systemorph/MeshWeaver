using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Three faults a fresh install showed on the inbox (2026-10-30), pinned:
/// <list type="bullet">
///   <item>one source that never answers held the whole list ("mails load forever") — now each source has a bound on
///     its first answer, the others are listed, and the silent one is NAMED;</item>
///   <item>the Kind filter showed a full-colour WhatsApp logo for Conversations — the chrome shows monochrome glyphs
///     only, brand marks identify a source in its own column;</item>
///   <item>a package had no way onto the inbox — the <see cref="UiContribution.InboxContext"/> lane embeds its area.</item>
/// </list>
/// </summary>
public class InboxSlowSourcesAndChromeTest
{
    private static readonly ImmutableList<InboxRow> OneRow = [new InboxRow { Path = "jroe/_Email/1", Title = "Hello" }];

    [Fact(Timeout = 20_000)]
    public async Task ASourceThatNeverAnswers_DoesNotHoldTheOthers_AndIsNamed()
    {
        var answers = InboxLayoutArea.FanIn(
            [
                ("Outlook", Observable.Return((OneRow, (string?)null, 1))),
                ("Gmail", Observable.Never<(ImmutableList<InboxRow>, string?, int)>()),
            ],
            TimeSpan.FromMilliseconds(200));

        var first = await answers.FirstAsync().Timeout(TimeSpan.FromSeconds(5)).Await(TestContext.Current.CancellationToken);

        first.Should().HaveCount(2);
        first[0].Rows.Should().Equal(OneRow, "the source that answered is listed");
        first[0].Slow.Should().BeNull();
        first[1].Rows.Should().BeEmpty();
        first[1].Slow.Should().Be("Gmail", "the silent source is named, never left to spin");
    }

    [Fact(Timeout = 20_000)]
    public async Task ASlowSource_ReplacesItsStandIn_WhenItAnswers()
    {
        var late = Observable.Timer(TimeSpan.FromMilliseconds(400)).Select(_ => (OneRow, (string?)null, 1));
        var answers = InboxLayoutArea.FanIn([("Gmail", late)], TimeSpan.FromMilliseconds(100));

        var seen = await answers.Take(2).ToList().Timeout(TimeSpan.FromSeconds(5)).Await(TestContext.Current.CancellationToken);

        seen[0][0].Slow.Should().Be("Gmail");
        seen[1][0].Slow.Should().BeNull("the late answer replaces the stand-in");
        seen[1][0].Rows.Should().Equal(OneRow);
    }

    [Fact(Timeout = 20_000)]
    public async Task ASourceThatAnswersInTime_GetsNoStandIn()
    {
        var answers = InboxLayoutArea.FanIn([("Outlook", Observable.Return((OneRow, (string?)null, 1)))], TimeSpan.FromMilliseconds(100));
        var all = await answers.Take(TimeSpan.FromMilliseconds(500)).ToList().Timeout(TimeSpan.FromSeconds(5)).Await(TestContext.Current.CancellationToken);
        all.Should().ContainSingle().Which[0].Slow.Should().BeNull("no stand-in once the source answered");
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

    [Fact]
    public void AnInboxContribution_IsEmbedded_OnlyFromItsOwnPartition_InOrder()
    {
        static (MeshNode, UiContribution) C(string path, UiContribution content) => (MeshNode.FromPath(path), content);
        var contributions = new[]
        {
            C("Mail/Inbox/Accounts", new UiContribution { Context = UiContribution.InboxContext, Area = "InboxAccounts", Order = 10 }),
            C("Other/Inbox/First", new UiContribution { Context = UiContribution.InboxContext, Area = "Early", Order = 1 }),
            C("Other/Inbox/Foreign", new UiContribution { Context = UiContribution.InboxContext, Area = "X", Address = "Mail" }),
            C("Other/Inbox/Admin", new UiContribution { Context = UiContribution.InboxContext, Area = "Y", Gates = new UiContributionGates { AdminOnly = true } }),
            C("Mail/Profile/Z", new UiContribution { Context = UiContribution.ProfileContext, Area = "Z" }),
        };

        var areas = UiContributionProjection.ProjectInboxAreas(contributions, "jroe", isAdmin: false, viewerId: "jroe");

        areas.Select(a => (a.Area, a.Address)).Should().Equal([("Early", null), ("InboxAccounts", null)],
            "in order; an address outside the contributor's partition, an admin-only entry for a non-admin and another context are dropped");
        UiContributionSeedValidation.PlatformContexts.Should().Contain(UiContribution.InboxContext, "a seed naming it is not 'declared by nobody'");
    }
}
