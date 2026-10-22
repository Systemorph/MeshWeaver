using System.Collections.Immutable;
using MeshWeaver.Mesh;
using System.Xml.Linq;
using MeshWeaver.Domain;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The inbox's first column shows the MEDIUM an item came through, and the toolbar offers an act
/// only where there is something to act on.
/// </summary>
public class InboxSourceIconsTest
{
    /// <summary>Every source id a built-in or channel leg registers, today.</summary>
    private static readonly string[] Sources =
        ["Approvals", "Notifications", "Activities", "Mail", "Chat", "Conversations", "Outlook", "Gmail", "iMessage", "WhatsApp", "Teams"];

    /// <summary>Every known source has its own, renderable symbol — none is a bare emoji any more.</summary>
    [Fact]
    public void EveryKnownSource_HasItsOwnRenderableSymbol()
    {
        InboxSourceIcons.KnownSources.Should().Contain(Sources, "the symbol is keyed by the leg's source id");
        foreach (var source in Sources)
        {
            var icon = InboxSourceIcons.Resolve(source, "✅");
            icon.Should().NotBe("✅", $"{source}: the table wins over a leg's emoji");
            Icon.Parse(icon)!.Provider.Should().BeOneOf([Icon.FluentProvider, Icon.InlineSvgProvider], $"{source} is a Fluent symbol or an inline mark");
        }
        Sources.Select(s => InboxSourceIcons.Resolve(s, null)).Should().OnlyHaveUniqueItems("one source, one look");
    }

    /// <summary>The inline marks are well-formed SVG documents.</summary>
    [Fact]
    public void TheInlineMarks_AreWellFormedSvg()
    {
        foreach (var source in new[] { "Outlook", "Gmail", "iMessage", "WhatsApp", "Teams" })
            XDocument.Parse(InboxSourceIcons.Resolve(source, null)).Root!.Name.LocalName.Should().Be("svg", source);
    }

    /// <summary>Lookup ignores case; an unknown source keeps its leg's symbol, and with none gets the fallback.</summary>
    [Fact]
    public void AnUnknownSource_KeepsItsDeclaredSymbol_ElseTheFallback()
    {
        InboxSourceIcons.Resolve("WHATSAPP", null).Should().Be(InboxSourceIcons.Resolve("WhatsApp", null));
        InboxSourceIcons.Resolve("Telegram", "📣").Should().Be("📣");
        InboxSourceIcons.Resolve("Telegram", null).Should().Be(InboxSourceIcons.Fallback);
        InboxSourceIcons.Resolve("Telegram", "  ").Should().Be(InboxSourceIcons.Fallback);
        InboxSourceIcons.Resolve(null, null).Should().Be(InboxSourceIcons.Fallback);
    }

    /// <summary>Only a text glyph may be spliced into a label.</summary>
    [Fact]
    public void OnlyAGlyph_IsUsableAsATextPrefix()
    {
        InboxSourceIcons.Glyph("📣").Should().Be("📣");
        InboxSourceIcons.Glyph(InboxSourceIcons.Resolve("Teams", null)).Should().BeEmpty();
        InboxSourceIcons.Glyph("Mail").Should().BeEmpty();
        InboxSourceIcons.Glyph(null).Should().BeEmpty();
    }

    /// <summary>The page resolves a leg's symbol through the table.</summary>
    [Fact]
    public void ASection_CarriesTheResolvedSymbol()
    {
        var leg = new MeshWeaver.Mesh.InboxQueryLeg
        {
            Source = "WhatsApp", SourceIcon = "🟢",
            Query = $"namespace:{InboxQueries.ViewerToken}/_Thread {InboxQueries.RowProjection}",
        };
        InboxLayoutArea.Sections([leg], "alice").Should().ContainSingle()
            .Which.SourceIcon.Should().Be(InboxSourceIcons.Resolve("WhatsApp", null));
    }

    private const int NeedsYou = 0;

    private static InboxRow Row(string source, string name, int rank = NeedsYou)
        => new() { Path = "alice/" + name, Title = name, SourceId = source, Rank = rank };

    /// <summary>Approve / Reject show for approvals the viewer decides — never for a mailbox.</summary>
    [Fact]
    public void ApproveAndReject_ShowOnlyWhereThereIsAnApprovalToDecide()
    {
        var approval = Row(InboxQueries.ApprovalsSource, "a1");
        var mail = Row("Outlook", "m1");
        var decided = Row(InboxQueries.ApprovalsSource, "a2", rank: 2);

        InboxLayoutArea.ToolbarOf([approval, mail], [], "alice", NeedsYou).Decide.Should().BeTrue("an approval is listed");
        InboxLayoutArea.ToolbarOf([mail, Row("Gmail", "m2")], [], "alice", NeedsYou).Decide.Should().BeFalse("a mail-only view");
        InboxLayoutArea.ToolbarOf([], [], "alice", NeedsYou).Decide.Should().BeFalse("an empty list");
        InboxLayoutArea.ToolbarOf([mail], [], "alice", null).Decide.Should().BeFalse("a viewer who decides no approvals has no such band");
        InboxLayoutArea.ToolbarOf([approval], [], "alice", null).Decide.Should().BeFalse("no obligations band, nothing to decide");
        InboxLayoutArea.ToolbarOf([decided, mail], [], "alice", NeedsYou).Decide.Should().BeFalse("a decided approval in the ledger is not a request");
    }

    /// <summary>With rows ticked, only those count.</summary>
    [Fact]
    public void ASelection_NarrowsTheToolbarToTheTickedRows()
    {
        var approval = Row(InboxQueries.ApprovalsSource, "a1");
        var mail = Row("Outlook", "m1");
        InboxLayoutArea.ToolbarOf([approval, mail], [mail.Path], "alice", NeedsYou).Decide.Should().BeFalse("only the mail is ticked");
        InboxLayoutArea.ToolbarOf([approval, mail], [approval.Path], "alice", NeedsYou).Decide.Should().BeTrue("the approval is ticked");
        InboxLayoutArea.ToolbarOf([approval, mail], ["alice/gone"], "alice", NeedsYou).Decide.Should().BeFalse("a ticked key that names no row counts for nothing");
        InboxLayoutArea.ToolbarOf([approval with { Path = "bob/a1" }], ["bob/a1"], "alice", NeedsYou).Decide.Should().BeFalse("outside the owner's partition");
    }
}

/// <summary>The lifecycle state each kind of item shows, and the multi-select kind / tag filters.</summary>
public class InboxStateAndFilterTest
{
    /// <summary>Approvals wait, then are approved or rejected; only done states carry a check.</summary>
    [Fact]
    public void AnApproval_IsAwaiting_UntilItIsDecided()
    {
        InboxState.Of("Approvals", InboxBand.NeedsYou, false, false, null).Should().Be(InboxState.Awaiting);
        InboxState.Of("Approvals", InboxBand.Running, true, false, null).Should().Be(InboxState.Awaiting, "asked by me, still pending");
        InboxState.Of("Approvals", InboxBand.Recent, false, false, "Approved").Should().Be(InboxState.Approved);
        InboxState.Of("Approvals", InboxBand.Recent, false, false, "Rejected").Should().Be(InboxState.Rejected);
        InboxState.Of("Approvals", InboxBand.Recent, false, false, "Expired").Should().Be(InboxState.Expired);
        InboxState.Of("Approvals", InboxBand.Recent, false, false, null).Should().BeEmpty("nothing says what became of it");
        InboxState.Mark(InboxState.Awaiting).Should().BeEmpty("a waiting request must not look finished");
        InboxState.Mark(InboxState.Approved).Should().Be("✓ ");
        InboxState.Mark(InboxState.Done).Should().Be("✓ ");
        InboxState.Mark(InboxState.Failed).Should().Be("✕ ");
        InboxState.All.Where(s => InboxState.Mark(s).Contains('✓')).Should().Equal(InboxState.Approved, InboxState.Done);
    }

    /// <summary>Activities show their real state.</summary>
    [Fact]
    public void AnActivity_ShowsItsRealState()
    {
        InboxState.Of("Activities", InboxBand.Running, false, false, null).Should().Be(InboxState.Running);
        InboxState.Of("Activities", InboxBand.NeedsYou, false, false, null).Should().Be(InboxState.Failed);
        InboxState.Of("Activities", InboxBand.Recent, false, false, "Succeeded").Should().Be(InboxState.Done);
        InboxState.Of("Activities", InboxBand.Recent, false, false, "Warning").Should().Be(InboxState.Done);
        InboxState.Of("Activities", InboxBand.Recent, false, false, "Cancelled").Should().Be(InboxState.Cancelled);
        InboxState.Of("Activities", InboxBand.Recent, false, false, "Requested").Should().Be(InboxState.Requested);
        InboxState.Of("Activities", InboxBand.Recent, false, false, "Failed").Should().Be(InboxState.Failed);
    }

    /// <summary>Mail and notifications show Unread, or nothing.</summary>
    [Fact]
    public void MailAndNotifications_AreUnreadOrNothing()
    {
        InboxState.Of("Outlook", InboxBand.NeedsYou, false, true, null).Should().Be(InboxState.Unread);
        InboxState.Of("Outlook", InboxBand.NeedsYou, false, false, null).Should().BeEmpty();
        InboxState.Of("Notifications", InboxBand.NeedsYou, false, false, "Sent").Should().BeEmpty("an unknown status is not part of the vocabulary");
        InboxState.All.Select(InboxState.Color).Should().OnlyContain(c => c.Length > 0, "every state has its own colour");
        InboxState.Color(InboxState.Awaiting).Should().NotBe(InboxState.Color(InboxState.Approved));
    }

    private static InboxRow Row(string name, string category, params string[] tags)
        => new() { Path = "alice/" + name, Title = name, SourceId = category, Category = category, Stamp = name, Tags = [.. tags] };

    /// <summary>Ticking several kinds lists their union; ticking none lists all.</summary>
    [Fact]
    public void SeveralKinds_ListTheirUnion_AndNoneListsAll()
    {
        InboxRow[] rows = [Row("a", "Approvals"), Row("b", "Notifications"), Row("c", "Conversations")];
        InboxFilter Ticked(params string[] kinds) => new()
        {
            Kinds = kinds.ToImmutableDictionary(InboxRows.SourceKey, _ => true),
        };
        InboxRows.Apply(rows, Ticked("Approvals", "Conversations")).Select(r => r.Title).OrderBy(t => t).Should().Equal("a", "c");
        InboxRows.Apply(rows, Ticked("Notifications")).Select(r => r.Title).Should().Equal("b");
        InboxRows.Apply(rows, new InboxFilter()).Should().HaveCount(3);
        InboxRows.Apply(rows, new InboxFilter { Kinds = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.SourceKey("Approvals"), false) })
            .Should().HaveCount(3, "an unticked box narrows nothing");
        InboxRows.Fingerprint(Ticked("Approvals")).Should().NotBe(InboxRows.Fingerprint(new InboxFilter()));
    }

    /// <summary>Ticked tags list the union; none ticked lists all.</summary>
    [Fact]
    public void TickedTags_ListTheirUnion_AndNoneListsAll()
    {
        InboxRow[] rows = [Row("a", "Mail", "reply"), Row("b", "Mail", "act"), Row("c", "Mail", "promo"), Row("d", "Mail")];
        InboxRows.Apply(rows, new InboxFilter { TagKeys = [InboxRows.TagKey("reply"), InboxRows.TagKey("act")] })
            .Select(r => r.Title).OrderBy(t => t).Should().Equal("a", "b");
        InboxRows.Apply(rows, new InboxFilter()).Should().HaveCount(4, "no tag ticked: all tags");
        InboxRows.Fingerprint(new InboxFilter { TagKeys = [InboxRows.TagKey("reply")] }).Should().NotBe(InboxRows.Fingerprint(new InboxFilter()));
    }

    /// <summary>The tag checkboxes keep a ticked box ticked and put Important first.</summary>
    [Fact]
    public void TheTagCheckboxes_KeepWhatWasTicked()
    {
        var counts = new[] { (InboxRows.ImportantKey, string.Empty, 2), (InboxRows.TagKey("reply"), "reply", 3) };
        var items = InboxLayoutArea.TagItems(counts, "Important", [InboxRows.TagKey("reply")]);
        items.Select(i => (i.Key, i.Checked)).Should().Equal((InboxRows.ImportantKey, false), (InboxRows.TagKey("reply"), true));
        items[0].Label.Should().Contain("Important").And.Contain("2");
        items[1].Label.Should().Be("#reply  3");
    }
}
