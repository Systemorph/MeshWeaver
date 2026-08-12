using System.Linq;
using HtmlAgilityPack;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Pdf;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// The page-furniture contract for the content-faithful PDF.
///
/// <para>Since #1230 the PDF is printed from HTML + CSS Paged Media rather than drawn by a PDF
/// document model, and everything the old renderer promised — cover page, table of contents,
/// running header, running footer with page numbers, page-break rules — is now a decision made
/// HERE, in a pure function. So the whole parity contract is pinned by ordinary unit tests that
/// need no browser installed; the browser tests then check that a real Chromium honours it.</para>
/// </summary>
public class DocumentPrintComposerTests
{
    private const string SampleMarkdown = """
        # Executive Summary

        A paragraph with **bold**, *italic* and `code`.

        ## Details

        | Column A | Column B |
        | --- | --- |
        | a1 | b1 |
        """;

    private static readonly BrandingOptions Brand = BrandingOptions.Default with
    {
        Name = "Systemorph AG",
        Tagline = "Data that explains itself",
        Website = "https://systemorph.com",
        HeaderText = "Quarterly Report",
        FooterText = "© 2026 Systemorph AG",
    };

    private static string Compose(
        DocumentExportOptions? options = null,
        BrandingOptions? branding = null,
        string? markdown = null)
        => DocumentPrintComposer.Compose(new DocumentBuilder().Build(
            "Quarterly Report 2026",
            markdown ?? SampleMarkdown,
            options ?? new DocumentExportOptions(),
            branding ?? Brand));

    private static HtmlDocument Parse(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document;
    }

    // ── Cover page ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cover_page_carries_the_brand_and_is_its_own_named_page()
    {
        var html = Compose();

        var cover = Parse(html).DocumentNode.SelectSingleNode("//section[@class='mw-cover']");
        cover.Should().NotBeNull("the cover is the first thing the document model produced");
        cover.InnerText.Should().Contain("Quarterly Report 2026").And.Contain("Systemorph AG");
        cover.InnerText.Should().Contain("Data that explains itself").And.Contain("https://systemorph.com");

        // The cover is bare of running furniture, and it is keyed on BEING the cover rather
        // than on being page one — with the cover switched off the contents page is page one
        // and must keep its header.
        html.Should().Contain("@page mw-cover")
            .And.Contain("page: mw-cover");
    }

    [Fact]
    public void Cover_page_is_omitted_when_the_option_is_off()
        => Parse(Compose(new DocumentExportOptions { CoverPage = false }))
            .DocumentNode.SelectSingleNode("//section[@class='mw-cover']")
            .Should().BeNull();

    [Fact]
    public void Cover_page_is_omitted_when_the_brand_has_neither_logo_nor_name()
    {
        // Same gate the document model applied: a "cover" carrying only the title is not one.
        var html = Compose(branding: BrandingOptions.Default with { Name = "", Logo = null });

        Parse(html).DocumentNode.SelectSingleNode("//section[@class='mw-cover']").Should().BeNull();
    }

    [Fact]
    public void Cover_logo_is_embedded_as_a_data_uri_because_the_print_document_fetches_nothing()
    {
        var html = Compose(branding: Brand with { Logo = new LogoImage([1, 2, 3, 4], "image/png") });

        var img = Parse(html).DocumentNode.SelectSingleNode("//img[@class='mw-cover-logo']");
        img.Should().NotBeNull();
        img.GetAttributeValue("src", "").Should().StartWith("data:image/png;base64,");
    }

    // ── Table of contents ───────────────────────────────────────────────────────────────

    [Fact]
    public void Contents_entries_link_to_anchors_that_the_headings_actually_carry()
    {
        var document = Parse(Compose());

        var hrefs = document.DocumentNode
            .SelectNodes("//section[@class='mw-toc']//a")
            .Select(a => a.GetAttributeValue("href", "").TrimStart('#'))
            .ToArray();
        hrefs.Should().Equal("executive-summary", "details");

        // The pairing is the whole feature: an entry whose target id is missing is a link that
        // navigates nowhere, and nothing else in the pipeline would notice.
        var ids = document.DocumentNode
            .SelectNodes("//main//*[@id]")
            .Select(n => n.Id)
            .ToArray();
        ids.Should().Contain(hrefs);
    }

    [Fact]
    public void Contents_indents_by_heading_level()
    {
        var document = Parse(Compose());

        var entries = document.DocumentNode.SelectNodes("//p[contains(@class,'mw-toc-entry')]");
        entries[0].GetAttributeValue("class", "").Should().Contain("mw-toc-1");
        entries[1].GetAttributeValue("class", "").Should().Contain("mw-toc-2");
    }

    [Fact]
    public void Contents_is_omitted_when_the_option_is_off()
        => Parse(Compose(new DocumentExportOptions { TableOfContents = false }))
            .DocumentNode.SelectSingleNode("//section[@class='mw-toc']")
            .Should().BeNull();

    // ── Running header and footer ───────────────────────────────────────────────────────

    [Fact]
    public void Running_header_and_footer_are_page_margin_boxes_with_a_page_counter()
    {
        var html = Compose();

        html.Should().Contain("@top-center").And.Contain("\"Quarterly Report\"");
        html.Should().Contain("@bottom-left").And.Contain("\"© 2026 Systemorph AG\"");
        // Numbering is the one part of the furniture with no interpolation at all.
        html.Should().Contain("counter(page) \" / \" counter(pages)");
    }

    [Fact]
    public void Header_and_footer_overrides_beat_the_brand()
    {
        var html = Compose(new DocumentExportOptions
        {
            HeaderOverride = "Board Pack",
            FooterOverride = "Internal only",
        });

        html.Should().Contain("\"Board Pack\"").And.Contain("\"Internal only\"");
        html.Should().NotContain("\"Quarterly Report\"");
    }

    [Fact]
    public void No_header_text_and_no_logo_generates_no_header_band_at_all()
    {
        var html = Compose(branding: Brand with { HeaderText = "", Logo = null });

        // `content: none` does not generate the margin box, so the band AND the rule beneath
        // it disappear together — the document model's behaviour when both were absent.
        html.Should().Contain("content: none");
        html.Should().Contain("border-bottom: none");
    }

    [Fact]
    public void The_footer_survives_an_empty_brand_because_the_page_number_belongs_on_every_page()
    {
        var html = Compose(branding: BrandingOptions.Default with { Name = "x", FooterText = "" });

        html.Should().Contain("@bottom-right")
            .And.Contain("counter(page)");
    }

    // ── Page geometry and breaks ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, "size: A4;")]
    [InlineData(true, "size: A4 landscape;")]
    public void Orientation_follows_the_landscape_option(bool landscape, string expected)
        => Compose(new DocumentExportOptions { Landscape = landscape }).Should().Contain(expected);

    [Fact]
    public void A_page_break_element_becomes_a_page_break_rule()
    {
        // PageBreakBeforeH1 makes the builder emit a break before the second H1.
        var html = Compose(
            new DocumentExportOptions { PageBreakBeforeH1 = true },
            markdown: "# One\n\ntext\n\n# Two\n\ntext");

        Parse(html).DocumentNode.SelectNodes("//div[@class='mw-page-break']")
            .Should().HaveCount(1);
        html.Should().Contain("break-before: page");
    }

    // ── Escaping into CSS: branding text is authored content ────────────────────────────

    [Fact]
    public void Header_text_cannot_break_out_of_the_style_element()
    {
        // A brand header carrying markup is untrusted input landing inside <style>, where HTML
        // encoding does not apply: `</style>` would end the element and everything after it
        // would be parsed as markup.
        var html = Compose(branding: Brand with
        {
            HeaderText = "</style><script>alert(1)</script>",
        });

        html.Should().NotContain("</style><script>");
        html.Should().NotContain("<script>");
        // Escaped as a CSS hex escape instead, so it prints as the literal text it is.
        html.Should().Contain("\\3c ");
    }

    [Fact]
    public void A_quote_or_a_newline_cannot_terminate_the_css_string()
    {
        DocumentPrintComposer.CssString("say \"hi\"").Should().Be("\"say \\\"hi\\\"\"");
        DocumentPrintComposer.CssString("a\nb").Should().Be("\"a\\a b\"");
        DocumentPrintComposer.CssString("back\\slash").Should().Be("\"back\\\\slash\"");
    }

    [Fact]
    public void A_placeholder_token_in_authored_text_is_not_itself_substituted()
    {
        // Chained string.Replace re-scans what an earlier call substituted, so a document titled
        // with a placeholder token would have the NEXT value spliced into its <title>. The
        // composer fills the template in one pass instead; this pins that.
        var html = Compose(
            branding: Brand with { HeaderText = "{{BODY}}" },
            markdown: "# Only heading\n\nUNIQUEBODYMARKER");

        var title = Parse(html).DocumentNode.SelectSingleNode("//title").InnerText;
        title.Should().Be("Quarterly Report 2026");

        // The header text prints as the literal token, and the body appears exactly once —
        // in the body, not smuggled into the stylesheet.
        html.Split("UNIQUEBODYMARKER").Length.Should().Be(2);
    }

    [Theory]
    [InlineData("#fff", "#fff")]
    [InlineData("#1d4ed8", "#1d4ed8")]
    [InlineData("#1d4ed8ff", "#1d4ed8ff")]
    // Not a colour: a CSS value cannot be quoted and escaped, so anything that is not plainly
    // one is replaced rather than interpolated. The document model's colour parser rejected
    // these too, so a brand carrying one was already broken, not newly downgraded.
    [InlineData("red; } body { display: none } .x {", BrandingOptionsDefaultPrimary)]
    [InlineData("url(http://evil)", BrandingOptionsDefaultPrimary)]
    [InlineData("", BrandingOptionsDefaultPrimary)]
    public void Only_a_hex_colour_reaches_the_stylesheet(string configured, string expected)
        => DocumentPrintComposer.CssColor(configured, BrandingOptionsDefaultPrimary)
            .Should().Be(expected);

    private const string BrandingOptionsDefaultPrimary = "#1f2937";

    // ── Body elements ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Emphasis_becomes_real_elements_rather_than_inline_styling()
    {
        // Structure, not CSS: a <strong> survives copy-paste, PDF text extraction and a reader's
        // accessibility tooling in a way a font-weight declaration does not.
        var html = Compose(markdown: "Plain **bold** and *italic* and `code`.");

        html.Should().Contain("<strong>bold</strong>")
            .And.Contain("<em>italic</em>")
            .And.Contain("<code>code</code>");
    }

    [Fact]
    public void A_table_header_row_becomes_a_thead_so_it_repeats_across_pages()
    {
        var document = Parse(Compose());

        document.DocumentNode.SelectNodes("//table/thead/tr/th").Should().HaveCount(2);
        document.DocumentNode.SelectNodes("//table/tbody/tr/td").Should().HaveCount(2);
    }

    [Fact]
    public void Text_is_html_encoded_on_the_one_path_out()
    {
        var html = Compose(markdown: "A <b>literal</b> & an ampersand");

        html.Should().Contain("&amp;");
        html.Should().NotContain("<b>literal</b>");
    }

    [Fact]
    public void The_print_document_denies_every_subresource_but_inlined_data_uris()
    {
        // The browser prints INSIDE the server's trust boundary; the policy is what stops a
        // document from making it fetch an internal service or read a local file.
        var html = Compose();

        html.Should().Contain("Content-Security-Policy");
        html.Should().Contain("default-src 'none'");
        // Must be the first element in <head> — a policy declared after content does not
        // govern that content.
        html.IndexOf("Content-Security-Policy", System.StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("<title>", System.StringComparison.Ordinal));
    }
}
