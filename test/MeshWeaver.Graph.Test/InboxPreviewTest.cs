using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The inbox row's preview line, its short time and the grid's column set (maintainer, 2026-11-07:
/// "state and status not useful. i'd prefer to have first line of text of email"). Pure functions —
/// each source's preview arrives in the shape its own listing hands over, and none is fetched.
/// </summary>
public class InboxPreviewTest
{
    // ---- the preview, per source ------------------------------------------------------------

    /// <summary>Graph's bodyPreview is already one line; the Outlook reply header sits mid-line and is cut there.</summary>
    [Fact]
    public void Outlook_BodyPreview_IsCutAtTheInlineReplyHeader()
        => InboxPreview.Of("Thanks, that works for me. Best, Max ________________________________ From: Jane Roe <jane@example.com> Sent: Monday")
            .Should().Be("Thanks, that works for me. Best, Max");

    /// <summary>The Outlook header without the underscore rule still ends the new text.</summary>
    [Fact]
    public void Outlook_BodyPreview_FromSentHeader_EndsTheText()
        => InboxPreview.Of("Agreed. From: Jane Roe <jane@example.com> Sent: Monday, 3 November 2026 10:02 To: Max")
            .Should().Be("Agreed.");

    /// <summary>A Gmail snippet carries "On … wrote:" inline; the quote after it is dropped.</summary>
    [Fact]
    public void Gmail_Snippet_DropsTheQuotedReply()
        => InboxPreview.Of("See you at ten. On Mon, 3 Nov 2026 at 10:02, Jane Roe <jane@example.com> wrote: &gt; Shall we meet")
            .Should().Be("See you at ten.");

    /// <summary>A multi-line text (an iMessage) is collapsed to one line; quoted lines and the signature go.</summary>
    [Fact]
    public void MultiLineText_IsCollapsed_QuotesAndSignatureRemoved()
        => InboxPreview.Of("Hi Max,\r\n\r\n  the report   is attached.\n> earlier message\n-- \nJane Roe\nAcme")
            .Should().Be("Hi Max, the report is attached.");

    /// <summary>A German reply header is recognised too.</summary>
    [Fact]
    public void GermanQuoteHeader_EndsTheText()
        => InboxPreview.Of("Passt.\nAm 03.11.2026 um 10:02 schrieb Jane Roe <jane@example.com>:\n> Termin?")
            .Should().Be("Passt.");

    /// <summary>A preview that is ALL quote (a bare forward) still says something rather than nothing.</summary>
    [Fact]
    public void AllQuote_FallsBackToTheQuotedText()
        => InboxPreview.Of("> the forwarded text").Should().Be("the forwarded text");

    /// <summary>Zero-width characters and the attachment placeholder never reach the line.</summary>
    [Fact]
    public void InvisibleCharacters_AreDropped()
        => InboxPreview.Of("​Hello￼ ͏­there").Should().Be("Hello there");

    /// <summary>Zero-width joiners hold an emoji (or a word) together and stay.</summary>
    [Fact]
    public void Joiners_AreKept()
        => InboxPreview.Of("Pair \U0001F469\u200D\U0001F4BB and \u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645")
            .Should().Be("Pair \U0001F469\u200D\U0001F4BB and \u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645");

    /// <summary>A hard cut never splits an emoji outside the BMP.</summary>
    [Fact]
    public void HardCut_KeepsSurrogatePairsWhole()
    {
        var preview = InboxPreview.Of(string.Concat(Enumerable.Repeat("\U0001F600", 100)));
        var body = preview.TrimEnd('…');
        char.IsHighSurrogate(body[^1]).Should().BeFalse("the cut must not leave a lone high surrogate");
        for (var i = 0; i < body.Length; i++)
            if (char.IsHighSurrogate(body[i]))
                char.IsLowSurrogate(body[i + 1]).Should().BeTrue();
    }

    /// <summary>No preview from the source is no preview line — never a placeholder.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void NothingGiven_IsEmpty(string? text) => InboxPreview.Of(text).Should().BeEmpty();

    /// <summary>A long text is cut at a word to the bound and marked.</summary>
    [Fact]
    public void LongText_IsCutAtAWord()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 100));
        var preview = InboxPreview.Of(text);
        preview.Length.Should().BeLessThanOrEqualTo(InboxPreview.MaxLength);
        preview.Should().EndWith("word…");
    }

    /// <summary>A sentence that merely starts with "From:" is not mistaken for a reply header.</summary>
    [Fact]
    public void FromInProse_IsKept()
        => InboxPreview.Of("From: our side everything is ready.").Should().Be("From: our side everything is ready.");

    // ---- the row reads the source's preview ------------------------------------------------

    /// <summary>The row takes its preview from the content's <c>preview</c> (the field every channel writes) and hides the line without one.</summary>
    [Fact]
    public void Row_ReadsThePreview_AndHidesTheLineWithoutOne()
    {
        var section = new InboxSection(InboxBand.Recent, "Outlook", 0, "q") { TimeField = "receivedAt" };
        var with = Row(new { receivedAt = "2026-11-07T08:00:00Z", preview = "Hello\n\nthere", from = "Jane Roe" }, section);
        with.Preview.Should().Be("Hello there");
        with.PreviewStyle.Should().NotBe(InboxLayoutArea.HiddenStyle);

        var without = Row(new { receivedAt = "2026-11-07T08:00:00Z", from = "Jane Roe" }, section);
        without.Preview.Should().BeEmpty();
        without.PreviewStyle.Should().Be(InboxLayoutArea.HiddenStyle);
    }

    private static InboxRow Row(object content, InboxSection section)
        => InboxRows.Of(new MeshNode("m1", "rbuergi/_Inbox") { Name = "Subject", Content = JsonSerializer.SerializeToElement(content) },
            section, zone: null, blockedReason: null);

    // ---- the short time --------------------------------------------------------------------

    /// <summary>Today in the viewer's zone shows the time only; an older day shows the date.</summary>
    [Fact]
    public void ShortTime_TodayIsTime_OlderIsDate()
    {
        var culture = CultureInfo.GetCultureInfo("de-CH");
        var now = new DateTimeOffset(2026, 11, 7, 15, 0, 0, TimeSpan.Zero);
        InboxPreview.ShortTime(now.AddHours(-2), "Europe/Zurich", culture, now).Should().Be("14:00");
        InboxPreview.ShortTime(now.AddDays(-1), "Europe/Zurich", culture, now).Should().Be("06.11.2026");
        // The viewer's culture writes it: US English puts the month first.
        InboxPreview.ShortTime(now.AddDays(-1), "Europe/Zurich", CultureInfo.GetCultureInfo("en-US"), now).Should().Be("11/6/2026");
    }

    /// <summary>"Today" is the VIEWER's day: 23:30 UTC is already tomorrow in Zurich, and still today in New York.</summary>
    [Fact]
    public void ShortTime_TodayIsTheViewersDay()
    {
        var culture = CultureInfo.GetCultureInfo("de-CH");
        var now = new DateTimeOffset(2026, 11, 7, 23, 30, 0, TimeSpan.Zero); // 00:30 on the 8th in Zurich
        var earlier = new DateTimeOffset(2026, 11, 7, 22, 0, 0, TimeSpan.Zero); // 23:00 on the 7th in Zurich
        InboxPreview.ShortTime(earlier, "Europe/Zurich", culture, now).Should().Be("07.11.2026");
        InboxPreview.ShortTime(earlier, "America/New_York", culture, now).Should().Be("17:00");
    }

    /// <summary>Across a DST change the time is the local one on each side.</summary>
    [Fact]
    public void ShortTime_AcrossDst_IsLocal()
    {
        var culture = CultureInfo.GetCultureInfo("de-CH");
        // 2026-10-25: Zurich falls back from CEST (+2) to CET (+1) at 03:00 local.
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        InboxPreview.ShortTime(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), "Europe/Zurich", culture, now).Should().Be("02:30");
        InboxPreview.ShortTime(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.Zero), "Europe/Zurich", culture, now).Should().Be("03:30");
    }

    // ---- the column set --------------------------------------------------------------------

    /// <summary>No State or Status column; Item is the one flexible column; the time is narrow; the phone layout is declared.</summary>
    [Fact]
    public void Columns_AreSourceItemFromAccountTagsWhen()
    {
        InboxLayoutArea.Columns.Select(c => c.Key).Should().Equal(
            "inbox.column.channel", "inbox.column.item", "inbox.column.source", "inbox.column.account", "inbox.column.tags", "inbox.column.when");
        InboxLayoutArea.Columns.Select(c => c.Key).Should().NotContain(["inbox.column.state", "inbox.column.status"]);
        InboxLayoutArea.Columns.Where(c => c.Width.EndsWith("fr", StringComparison.Ordinal)).Select(c => c.Key)
            .Should().Equal(["inbox.column.item"], "only Item flexes; every other column has a fixed width");
        InboxLayoutArea.Columns.Single(c => c.Key == "inbox.column.when").Width.Should().Be("96px");
        InboxLayoutArea.PropertyOf("inbox.column.when").Should().Be("shortWhen");
        // Phone: symbol and sender lead, the time trails, the item is the body; account and tags stay off.
        InboxLayoutArea.Columns.Select(c => c.Compact).Should().Equal(
            ColumnCompactPlacement.Lead, ColumnCompactPlacement.Body, ColumnCompactPlacement.Lead,
            ColumnCompactPlacement.Hidden, ColumnCompactPlacement.Hidden, ColumnCompactPlacement.Trail);
    }

    /// <summary>The text columns' properties are the row's serialized property names.</summary>
    [Fact]
    public void TextColumns_NameRealRowProperties()
    {
        var names = typeof(InboxRow).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToImmutableHashSet();
        foreach (var key in new[] { "inbox.column.source", "inbox.column.account", "inbox.column.tags", "inbox.column.when" })
            names.Should().Contain(InboxLayoutArea.PropertyOf(key));
        names.Should().Contain([InboxRows.TitleProperty, InboxRows.PreviewProperty, InboxRows.PreviewStyleProperty]);
    }

    /// <summary>The Account column shows only when the rows reached more than one address.</summary>
    [Fact]
    public void AccountColumn_OnlyWithSeveralAccounts()
    {
        InboxLayoutArea.AccountColumnShown([new InboxRow { Account = "max@example.com" }, new InboxRow { Account = "MAX@example.com " }])
            .Should().BeFalse("one address, however spelled, is one account");
        InboxLayoutArea.AccountColumnShown([new InboxRow { Account = "max@example.com" }, new InboxRow { Account = "max@fabrikam.example" }])
            .Should().BeTrue();
        InboxLayoutArea.AccountColumnShown([]).Should().BeFalse();
    }
}
