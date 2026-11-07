using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The inbox row reads like a mail client's message list: the sender (bold while unread), the state badge, the
/// compact time; the subject; the beginning of the body. A part a row lacks takes no room — no badge without a
/// state (nor for unread, which the bold says), no account when only one feeds the inbox.
/// </summary>
public class InboxMessageListRowTest
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly ImmutableDictionary<string, string> Texts = InboxState.All.ToImmutableDictionary(
        state => state, state => InboxState.Mark(state) + char.ToUpperInvariant(state[0]) + state[1..], StringComparer.Ordinal);

    /// <summary>ICU writes a narrow no-break space before AM/PM on some hosts; the assertions read it as a space.</summary>
    private static string N(string text) => text.Replace('\u202F', ' ').Replace('\u00A0', ' ');

    /// <summary>A live mailbox leg: timed by receipt, read state per row.</summary>
    private static readonly InboxSection Mailbox = new(InboxBand.NeedsYou, "Outlook", 22, "q") { TimeField = "receivedAt" };

    private static MeshNode Mail(object content, string subject = "Quarterly figures")
        => new("m1", "alice/_Mailbox/outlook") { Name = subject, NodeType = "Outlook/Message", Content = JsonSerializer.SerializeToElement(content) };

    private static InboxRow Row(object content, string subject = "Quarterly figures")
        => InboxLayoutArea.WithStateText(InboxRows.Of(Mail(content, subject), Mailbox, null, null), Texts);

    /// <summary>The provider's preview is the row's preview line, whitespace collapsed; nothing else is invented.</summary>
    [Fact]
    public void AMailsPreview_IsTheBeginningOfItsBody()
    {
        var row = Row(new { from = "Max Muster", isRead = true, receivedAt = "2026-11-07T09:00:00Z", preview = "Hi Jane,\r\n\r\n  please find the   figures attached." });
        row.Preview.Should().Be("Hi Jane, please find the figures attached.");
        row.Title.Should().Be("Quarterly figures", "the subject is carried whole — the column cuts it, never the row");
        Row(new { from = "Max Muster", isRead = true }).Preview.Should().BeEmpty("negative control: a source without a body has no preview");
    }

    /// <summary>A stored mail carries no preview but its body: the row reads its plain text, markup and styles dropped.</summary>
    [Fact]
    public void AStoredMailsHtmlBody_IsReadAsPlainText()
    {
        InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { body = "<html><head><style>p{color:red}</style></head><body><p>Dear&nbsp;Jane,</p><p>the <b>offer</b> &amp; terms.</p></body></html>" }))
            .Should().Be("Dear Jane, the offer & terms.");
        InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { body = "plain\ntext" })).Should().Be("plain text");
        InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { body = "Approve if cost < 100 and balance > 0." }))
            .Should().Be("Approve if cost < 100 and balance > 0.", "a plain text with angle brackets is not markup");
        InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { body = "<p>Please re<b>view</b> the <a href=\"x\">draft</a>.</p><p>Thanks</p>" }))
            .Should().Be("Please review the draft. Thanks", "an inline tag joins its word; a block boundary is a space");
        InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { body = "line one<br>line two" })).Should().Be("line one line two");
        InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { preview = "the provider's", body = "<p>the body</p>" }))
            .Should().Be("the provider's", "the source's own preview wins over the body");
        var cut = InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { preview = new string('x', 500) }));
        cut.Should().HaveLength(InboxRows.PreviewLength).And.EndWith("…", "a long body is cut, and says so");
        var emoji = InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { preview = new string('x', InboxRows.PreviewLength - 2) + "😀 and more text" }));
        emoji.Should().EndWith("…");
        emoji.Any(char.IsSurrogate).Should().BeFalse("the cut never splits the emoji's surrogate pair");
        InboxRows.PreviewOf(null).Should().BeEmpty();
    }

    /// <summary>An unread mail's sender and subject are bold on the row itself, so every client bolds them; a read one's are not.</summary>
    [Fact]
    public void AnUnreadMail_IsBold_AReadOneIsNot()
    {
        var unread = Row(new { from = "Max Muster", isRead = false });
        unread.Unread.Should().BeTrue();
        unread.Emphasis.Should().Be(InboxRows.EmphasisStyle);
        unread.RowClass.Should().Be(InboxRows.UnreadClass);
        var read = Row(new { from = "Max Muster", isRead = true });
        read.Emphasis.Should().Be(InboxRows.LineStyle).And.NotContain("font-weight", "negative control: a read mail is regular");
        InboxRows.PreviewStyle.Should().Contain("font-weight: 400", "the preview stays regular under a bold row");
    }

    /// <summary>
    /// Why the State and Status columns were blank for every mail: a mail's only lifecycle state is Unread — a READ
    /// mail has none — and Status repeated the badge. The state is still resolved and spelled; the badge shows only
    /// where it says something the row does not.
    /// </summary>
    [Fact]
    public void TheStateText_IsResolved_AndTheBadgeShowsOnlyWhereItSaysSomething()
    {
        var unread = Row(new { isRead = false });
        unread.State.Should().Be(InboxState.Unread);
        unread.StateText.Should().Be("Unread", "the state's label is resolved from the texts");
        unread.StateStyle.Should().Be(InboxRows.VisuallyHiddenStyle, "the bold says unread to the eye; the badge still says it to a screen reader");
        unread.StateStyle.Should().NotContain("display: none", "display:none would drop it from the accessibility tree");

        var read = Row(new { isRead = true });
        read.State.Should().BeEmpty("a read mail has no lifecycle state");
        read.StateStyle.Should().Be(InboxLayoutArea.HiddenStyle, "no state, no badge");

        var approval = InboxLayoutArea.WithStateText(InboxRows.Of(
            new MeshNode("a1", "alice/_Approvals") { Name = "Deploy", Content = JsonSerializer.SerializeToElement(new { status = "Pending" }) },
            new InboxSection(InboxBand.NeedsYou, InboxQueries.ApprovalsSource, 1, "q") { TimeField = "createdAt" }, null, null), Texts);
        approval.StateText.Should().Be("Awaiting");
        approval.StateStyle.Should().BeEmpty("an approval waiting for a decision shows its badge");
        approval.StateColor.Should().Be(InboxState.Color(InboxState.Awaiting));
    }

    /// <summary>The account is named only when more than one account feeds the inbox — the same address on every row is noise.</summary>
    [Fact]
    public void ASingleAccount_IsNotRepeatedOnEveryRow()
    {
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        var a = Row(new { account = "jane@example.com", isRead = true });
        var b = Row(new { account = "jane@fabrikam.example", isRead = true });
        InboxLayoutArea.AccountsOf([a, a]).Should().HaveCount(1);
        InboxRows.ForList([a, a], 1, now, null, English).Should().OnlyContain(r => r.AccountLabel.Length == 0, "one account: not named");
        InboxRows.ForList([a, b], InboxLayoutArea.AccountsOf([a, b]).Count, now, null, English).Select(r => r.AccountLabel)
            .Should().Equal(["jane@example.com", "jane@fabrikam.example"], "several accounts: each row names its own");
        a.Account.Should().Be("jane@example.com", "the account itself stays on the row — the Account filter reads it");
    }

    /// <summary>The third line is the preview; without one, a status that says more than the badge; else nothing.</summary>
    [Fact]
    public void TheDetailLine_IsThePreview_ElseAStatusBeyondTheBadge()
    {
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        InboxRows.ForList([Row(new { preview = "Hello there", isRead = true })], 1, now, null, English).Single().Detail.Should().Be("Hello there");
        var refused = new InboxRow { State = InboxState.Stale, StateText = "Stale", Status = "Refused · stale" };
        InboxRows.ForList([refused], 1, now, null, English).Single().Detail.Should().Be("Refused · stale", "the detail says more than the badge");
        var plain = new InboxRow { State = InboxState.Done, StateText = "✓ Done", Status = "Done" };
        InboxRows.ForList([plain], 1, now, null, English).Single().Detail.Should().BeEmpty("a status that only repeats the badge is not shown twice");
    }

    /// <summary>The time is compact, where the viewer is: today the time, this week the weekday, else the short date.</summary>
    [Fact]
    public void TheTime_IsCompact_InTheViewersZone()
    {
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero); // a Saturday
        N(InboxRows.ShortWhen(new DateTimeOffset(2026, 11, 7, 9, 5, 0, TimeSpan.Zero), now, "UTC", English)).Should().Be("9:05 AM");
        N(InboxRows.ShortWhen(new DateTimeOffset(2026, 11, 3, 15, 42, 0, TimeSpan.Zero), now, "UTC", English)).Should().Be("Tue 3:42 PM");
        N(InboxRows.ShortWhen(new DateTimeOffset(2026, 10, 2, 15, 42, 0, TimeSpan.Zero), now, "UTC", English)).Should().Be("10/2/2026");
        N(InboxRows.ShortWhen(new DateTimeOffset(2026, 11, 7, 9, 5, 0, TimeSpan.Zero), now, "UTC", CultureInfo.GetCultureInfo("de-CH")))
            .Should().Be("09:05", "the viewer's culture writes the time");
        // 23:30 UTC on the 6th is already the 7th in Zurich: "today" is the viewer's day, not the container's.
        N(InboxRows.ShortWhen(new DateTimeOffset(2026, 11, 6, 23, 30, 0, TimeSpan.Zero), now, "Europe/Zurich", English)).Should().Be("12:30 AM");
        N(InboxRows.ShortWhen(new DateTimeOffset(2026, 11, 6, 23, 30, 0, TimeSpan.Zero), now, "UTC", English))
            .Should().Be("Fri 11:30 PM", "negative control: in UTC it was yesterday");

        var row = InboxRows.ForList([Row(new { receivedAt = "2026-11-07T09:05:00Z", isRead = true })], 1, now, "UTC", English).Single();
        N(row.Received).Should().Be("9:05 AM", "the list's time comes from the row's stamp");
    }
}
