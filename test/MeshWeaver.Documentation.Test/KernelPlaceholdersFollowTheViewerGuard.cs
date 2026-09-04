using System.Net;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The core half of clause 2's enumerated in-flow set:
/// <see href="/Doc/Architecture/ChromeAndContentLanguage">Chrome and content language</see> names
/// five surfaces that render platform chrome INSIDE an author's flow, and two of them —
/// <c>MarkdownViewLogic</c>'s kernel placeholders — are declared in THIS repo. The other three
/// (the two code-cell toolbars and <c>CodeBlock.razor</c>) are declared in MeshWeaver.Plugins and
/// are guarded there by <c>InFlowChromeClause2Guard</c>; a core test cannot see a Plugins
/// component, which is why the set is guarded in two halves rather than one.
///
/// <para><b>What this pins, and why it is clause 1 rather than clause 2.</b> These two notices
/// replace the executable block's result area, so they render in the middle of somebody's lesson.
/// They are the platform's own words about the platform's own state, which makes them
/// platform-OWNED text: under clause 1 they follow the VIEWER, and a bare English literal compiled
/// into the view is the "unowned string" that clause calls a bug. Clause 2 — drop the translated
/// visible label where a glyph says the same thing — does not reach them, because a sentence
/// explaining why execution is unavailable has no glyph equivalent. So the requirement here is
/// exactly: the text comes out of the catalog, in the language the CALLER passed.</para>
///
/// <para>🚨 <b>This guard exists because the page it defends used to say it did not.</b>
/// <c>ChromeAndContentLanguage</c>'s Enforcement section read <i>"NOT YET BUILT — do not read this
/// paragraph as a guard that exists"</i> for as long as that was true, and the same page documents
/// what happens when prose outlives the code it describes (<c>AnonymousCircuitLocaleSeedTest</c>
/// was cited as coverage for months after it was lost in a repo move). A guard is the only thing
/// that keeps the paragraph honest in both directions.</para>
///
/// <para><b>Reading a failure.</b> Every assertion below fails by naming the defect rather than a
/// byte mismatch: a hard-coded literal fails <c>DE</c> while <c>EN</c> still passes; a typo'd
/// catalog key makes both languages resolve to the raw key, which the last test catches
/// independently of what either catalog happens to contain.</para>
/// </summary>
public class KernelPlaceholdersFollowTheViewerGuard
{
    private const string DisabledKey = "code.kernelDisabledNotice";
    private const string StartingKey = "code.kernelStarting";

    /// <summary>
    /// One executable block's rendered result area, in the shape
    /// <c>LayoutAreaMarkdownRenderer.GetLayoutAreaDiv</c> emits and the placeholder substitution
    /// matches. Built from the renderer's OWN constant so a change to the marker moves the guard
    /// with it instead of leaving it matching a string nothing produces any more.
    /// </summary>
    private static string RenderedKernelHtml() =>
        "<p>lesson prose</p>"
        + $"<div class='layout-area' data-address='{ExecutableCodeBlockRenderer.KernelAddressPlaceholder}'"
        + " data-area='markdown-1'></div>";

    /// <summary>The catalog text as it appears once spliced into the notice markup.</summary>
    private static string Rendered(string key, string locale) =>
        WebUtility.HtmlEncode(LocalizationCatalog.Get(key, locale));

    [Fact]
    public void DisabledNotice_RendersInTheViewersLanguage_NotAPinnedEnglishLiteral()
    {
        var html = RenderedKernelHtml();

        MarkdownViewLogic.DisableKernelPlaceholder(html, "de")
            .Should().Contain(Rendered(DisabledKey, "de"),
                $"the 'execution unavailable' notice renders inside the cell frame, so it is "
                + $"platform-owned text and follows the VIEWER — it must come from {DisabledKey} in "
                + "the caller's language, not from a literal compiled into MarkdownViewLogic");

        MarkdownViewLogic.DisableKernelPlaceholder(html, "de")
            .Should().NotContain(Rendered(DisabledKey, "en"),
                "a German viewer must not be served the English sentence — if this fails while the "
                + "assertion above passes, the notice is concatenating both");

        MarkdownViewLogic.DisableKernelPlaceholder(html, "en")
            .Should().Contain(Rendered(DisabledKey, "en"),
                "the English path must keep working — a guard that only proves German would go "
                + "green on a notice hard-coded in German");
    }

    [Fact]
    public void PendingNotice_RendersInTheViewersLanguage_NotAPinnedEnglishLiteral()
    {
        var html = RenderedKernelHtml();

        MarkdownViewLogic.PendingKernelPlaceholder(html, "de")
            .Should().Contain(Rendered(StartingKey, "de"),
                $"the 'starting kernel' notice is platform-owned text inside the cell frame — it "
                + $"must come from {StartingKey} in the caller's language");

        MarkdownViewLogic.PendingKernelPlaceholder(html, "de")
            .Should().NotContain(Rendered(StartingKey, "en"),
                "a German viewer must not be served the English sentence");

        MarkdownViewLogic.PendingKernelPlaceholder(html, "en")
            .Should().Contain(Rendered(StartingKey, "en"),
                "the English path must keep working");
    }

    /// <summary>
    /// The two Blazor views do not call the leaf helpers — they call
    /// <c>RenderKernelResultAreas</c>, which is where a dropped locale would actually happen. This
    /// is the same omission that made a Code node page render "Run" to a German viewer while a code
    /// cell rendered "Ausführen": the parameter existed and the one call site passed nothing.
    /// </summary>
    [Theory]
    [InlineData(null, false, "markdown-kernel-disabled", DisabledKey)]
    [InlineData("rbuergi/Doc", false, "markdown-kernel-pending", StartingKey)]
    public void RenderKernelResultAreas_ThreadsTheLocaleToWhicheverNoticeItResolvesTo(
        string? ownerPath, bool kernelReady, string expectedClass, string key)
    {
        var rendered = MarkdownViewLogic.RenderKernelResultAreas(
            RenderedKernelHtml(),
            ownerPath,
            kernelReady,
            new Address("rbuergi/Doc/_Activity/markdown-1"),
            locale: "de");

        rendered.Should().Contain(expectedClass,
            "this render must still resolve to the same non-subscribing notice as before");
        rendered.Should().Contain(Rendered(key, "de"),
            "RenderKernelResultAreas is what the Blazor views call, so a locale dropped HERE renders "
            + "English for every German viewer even with both leaf helpers correct — that is exactly "
            + "how CodeViews.BuildCellToolbar's locale parameter came to be unused");
        rendered.Should().NotContain(Rendered(key, "en"),
            "the viewer asked for German");
    }

    /// <summary>
    /// Independent of what either catalog says. <c>LocalizationCatalog.Get</c> falls back to the KEY
    /// when the key is in no catalog, so a typo in the key name renders a visible <c>code.…</c>
    /// token in the middle of a lesson and every value-comparing assertion above still passes —
    /// both sides resolve to the same wrong string. Two languages resolving to the SAME text is the
    /// signal, and it needs no knowledge of the translations.
    /// </summary>
    [Theory]
    [InlineData(DisabledKey)]
    [InlineData(StartingKey)]
    public void EachNoticeKey_IsActuallyInTheCatalog_InEveryShippedLanguage(string key)
    {
        var english = LocalizationCatalog.Get(key, Locales.Default);

        english.Should().NotBe(key,
            $"'{key}' resolved to its own name, which is what LocalizationCatalog does for a key it "
            + "cannot find — the notice would render that raw token to the reader");

        foreach (var locale in Locales.Supported.Where(l => l != Locales.Default))
            LocalizationCatalog.Get(key, locale)
                .Should().NotBe(english,
                    $"'{key}' is untranslated in '{locale}' — it falls through to English, so a "
                    + $"{locale} reader gets an English sentence inside the cell frame");
    }
}
