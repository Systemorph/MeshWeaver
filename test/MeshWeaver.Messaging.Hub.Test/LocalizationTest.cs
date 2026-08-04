using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the per-viewer TEXT seam — the twin of <see cref="DisplayTimeTest"/>. Covers the two
/// lookup shapes (the string catalog for markup/inline literals, the <see cref="TranslationAttribute"/>
/// for declarations), the shared language-resolution rule, and — most importantly — the
/// COMPLETENESS of every shipped language against the English key list.
/// </summary>
public class LocalizationTest
{
    /// <summary>
    /// The catalogs must actually be embedded and parseable. Without this, every other assertion
    /// here would still pass on the key-fallback path while the real UI silently rendered raw
    /// keys — an embedded-resource name typo is invisible at compile time.
    /// </summary>
    [Fact]
    public void EnglishCatalog_IsLoaded()
    {
        LocalizationCatalog.Keys.Should().NotBeEmpty(
            "the English catalog must be embedded as MeshWeaver.Messaging.Localization.strings.en.json");
        LocalizationCatalog.Get("common.save", "en").Should().Be("Save");
    }

    /// <summary>
    /// THE guard for this feature: every language we ship must cover the full English key list.
    /// A key added to the UI without a translation fails here rather than surfacing to a German
    /// user as a raw <c>chat.new</c> token.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonDefaultLocales))]
    public void EveryShippedLanguage_CoversEveryEnglishKey(string locale)
    {
        var missing = LocalizationCatalog.Keys
            .Except(LocalizationCatalog.KeysFor(locale))
            .OrderBy(k => k, System.StringComparer.Ordinal)
            .ToArray();

        missing.Should().BeEmpty(
            "every key in strings.en.json needs a translation in strings.{0}.json; missing: {1}",
            locale, string.Join(", ", missing));
    }

    /// <summary>
    /// The reverse direction: a key that exists ONLY in a translation is dead weight (usually a
    /// leftover after the English string was renamed or removed), and it silently never renders.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonDefaultLocales))]
    public void NoTranslationHasOrphanKeys(string locale)
    {
        var orphans = LocalizationCatalog.KeysFor(locale)
            .Except(LocalizationCatalog.Keys)
            .OrderBy(k => k, System.StringComparer.Ordinal)
            .ToArray();

        orphans.Should().BeEmpty(
            "strings.{0}.json has keys with no English counterpart: {1}",
            locale, string.Join(", ", orphans));
    }

    /// <summary>
    /// Plural keys come in <c>.one</c>/<c>.other</c> pairs — half a pair renders the raw key for
    /// exactly one count value, which is the kind of gap that reaches production.
    /// </summary>
    [Fact]
    public void PluralKeys_ComeInCompletePairs()
    {
        var pluralRoots = LocalizationCatalog.Keys
            .Where(k => k.EndsWith(".one") || k.EndsWith(".other"))
            .Select(k => k[..k.LastIndexOf('.')])
            .Distinct()
            .ToArray();

        foreach (var root in pluralRoots)
        {
            LocalizationCatalog.Keys.Should().Contain($"{root}.one");
            LocalizationCatalog.Keys.Should().Contain($"{root}.other");
        }
    }

    [Theory]
    // Exact matches.
    [InlineData("de", "de")]
    [InlineData("en", "en")]
    // Region variants fall back to the primary subtag — one German serves DE, AT and CH.
    [InlineData("de-CH", "de")]
    [InlineData("de-AT", "de")]
    // POSIX and weighted Accept-Language shapes both normalize.
    [InlineData("de_DE.UTF-8", "de")]
    [InlineData("de-CH;q=0.9", "de")]
    // Unsupported / empty → English, never a throw and never null.
    [InlineData("fr", "en")]
    [InlineData("zz-ZZ", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void Resolve_FallsBackThroughPrimarySubtagToEnglish(string? requested, string expected)
        => Locales.Resolve(requested).Should().Be(expected);

    /// <summary>
    /// <see cref="Locales.TryMatch"/> must keep "unsupported" distinguishable from "English" —
    /// the write-once profile population depends on that distinction so it never pins a profile
    /// to a language we would only render in English anyway.
    /// </summary>
    [Fact]
    public void TryMatch_DistinguishesUnsupportedFromEnglish()
    {
        Locales.TryMatch("fr").Should().BeNull();
        Locales.TryMatch("en").Should().Be("en");
        Locales.TryMatch("de-CH").Should().Be("de");
    }

    [Fact]
    public void MissingKey_FallsBackToEnglishThenToTheKeyItself()
    {
        // Present in English, absent from a hypothetical gap → English text.
        LocalizationCatalog.Get("common.save", "fr").Should().Be("Save");
        // Absent everywhere → the key surfaces, so the gap is visible rather than blank.
        LocalizationCatalog.Get("no.such.key.exists", "de").Should().Be("no.such.key.exists");
    }

    [Fact]
    public void Get_AppliesPositionalArguments()
        => LocalizationCatalog.Get("chat.sendFailed", "en", "boom")
            .Should().Be("Couldn't send your message: boom");

    /// <summary>
    /// Placeholder/argument mismatches must degrade to readable text, never throw — this runs on
    /// the render path, where an exception takes out the whole view. Both directions matter: a
    /// translator dropping a <c>{0}</c>, and a call site passing fewer args than the template uses.
    /// </summary>
    [Fact]
    public void Get_PlaceholderArgumentMismatch_DoesNotThrow()
    {
        Action tooManyArgs = () => LocalizationCatalog.Get("common.save", "en", "unused");
        tooManyArgs.Should().NotThrow();

        Action tooFewArgs = () => LocalizationCatalog.Get("chat.sendFailed", "en");
        tooFewArgs.Should().NotThrow();

        // Extra args are ignored; a template used with no args stays unformatted rather than
        // throwing, so the worst case is a visible "{0}" instead of a broken page.
        LocalizationCatalog.Get("common.save", "en", "unused").Should().Be("Save");
        LocalizationCatalog.Get("chat.sendFailed", "en")
            .Should().Be("Couldn't send your message: {0}");
    }

    [Theory]
    [InlineData(1, "en", "1 message")]
    [InlineData(3, "en", "3 messages")]
    [InlineData(0, "en", "0 messages")]
    [InlineData(1, "de", "1 Nachricht")]
    [InlineData(3, "de", "3 Nachrichten")]
    public void Plural_SelectsOneOrOtherPerLanguage(int count, string locale, string expected)
        => LocalizationCatalog.Plural("plural.message", count, locale).Should().Be(expected);

    [Fact]
    public void AccessServiceExtension_ResolvesLanguageFromRequestContext()
    {
        var access = new AccessService();
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "u", Locale = "de" }))
        {
            access.Localize("common.save").Should().Be("Speichern");
            access.ViewerLocale().Should().Be("de");
        }
    }

    [Fact]
    public void AccessServiceExtension_FallsBackToCircuitContext()
    {
        var access = new AccessService();
        access.SetCircuitContext(new AccessContext { ObjectId = "u", Locale = "de" });
        try
        {
            // No request-scoped context set → resolves from the circuit context.
            access.Localize("common.save").Should().Be("Speichern");
        }
        finally
        {
            access.SetCircuitContext(null);
        }
    }

    [Fact]
    public void AccessServiceExtension_NullServiceOrNoLocale_IsEnglish()
    {
        AccessService? none = null;
        none.Localize("common.save").Should().Be("Save");

        var access = new AccessService();
        access.Localize("common.save").Should().Be("Save");
    }

    // ---- [Translation] attribute lookup -------------------------------------------------

    private sealed record Sample
    {
        [Description("Display time zone (IANA)")]
        [Translation("de", "Anzeige-Zeitzone (IANA)")]
        public string? Translated { get; init; }

        [Description("English only")]
        public string? UntranslatedProperty { get; init; }

        public string? NoAttributesAtAll { get; init; }
    }

    [Theory]
    [InlineData("de", "Anzeige-Zeitzone (IANA)")]
    // A region variant picks up the primary-subtag translation.
    [InlineData("de-CH", "Anzeige-Zeitzone (IANA)")]
    // English (and any unsupported language) reads the [Description] as-is.
    [InlineData("en", "Display time zone (IANA)")]
    [InlineData("fr", "Display time zone (IANA)")]
    [InlineData(null, "Display time zone (IANA)")]
    public void LocalizedDescription_PrefersTranslationThenDescription(string? locale, string expected)
        => typeof(Sample).GetProperty(nameof(Sample.Translated))!
            .LocalizedDescription(locale).Should().Be(expected);

    [Fact]
    public void LocalizedDescription_WithoutTranslation_FallsBackToEnglishDescription()
        => typeof(Sample).GetProperty(nameof(Sample.UntranslatedProperty))!
            .LocalizedDescription("de").Should().Be("English only");

    /// <summary>
    /// No attributes at all → null, so the caller applies its own fallback chain
    /// ([Display] name, then the wordified member name) rather than getting an empty label.
    /// </summary>
    [Fact]
    public void LocalizedDescription_WithNoAttributes_IsNull()
        => typeof(Sample).GetProperty(nameof(Sample.NoAttributesAtAll))!
            .LocalizedDescription("de").Should().BeNull();

    public static IEnumerable<object[]> NonDefaultLocales()
        => Locales.Supported
            .Where(l => l != Locales.Default)
            .Select(l => new object[] { l });
}
