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
