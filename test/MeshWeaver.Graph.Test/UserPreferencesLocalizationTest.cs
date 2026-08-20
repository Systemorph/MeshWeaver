using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The User → Settings → <b>Preferences</b> tab must be localized in FULL.
///
/// <para>🚨 It is the one tab where half-localization is easiest to ship and hardest to notice: the
/// language picker beside it went through <c>Localize("settings.language")</c> from the day it was
/// written, so the tab LOOKS translated to anyone glancing at the code, while the time-zone label
/// and the paragraph explaining the whole feature were English literals. A German viewer read
/// "Display time zone (IANA)" under a heading that was correctly "Einstellungen".</para>
///
/// <para>The builder takes its localizer as a <c>Func&lt;string,string&gt;</c> for exactly the reason
/// <see cref="Memex.Portal.Shared.SelfUpdate.PlatformUpdateChip.Describe"/> does — so the WORDING is
/// assertable without a hub, a circuit or a rendered layout area. Here that matters twice over: the
/// bug is invisible to <c>LocalizationTest</c> (which only checks that shipped languages cover the
/// English key list — a string that never became a key is not a missing key, it is an absent one)
/// and invisible to any render test that happens to run under an English viewer.</para>
/// </summary>
public class UserPreferencesLocalizationTest
{
    /// <summary>
    /// Every label the tab renders must come back from the localizer. With an echo localizer the
    /// labels ARE the keys, so any English literal left in the builder stands out as prose among
    /// dotted keys — and this fails whether the literal is the time zone's, the language's, or one
    /// added later.
    /// </summary>
    [Fact]
    public void EveryFieldLabel_ComesFromTheCatalog()
    {
        var fields = UserNodeType.PreferenceFields(key => key);

        fields.Should().NotBeEmpty("the tab exists to carry the display-zone and language pickers");
        foreach (var field in fields)
            field.Label.Should().StartWith("settings.",
                "an echo localizer returns the key, so a label that is not a key is a hard-coded "
                + "literal that renders English to every viewer regardless of their language");
    }

    /// <summary>
    /// The paragraph above the pickers is user-visible prose — the longest English string on the
    /// tab, and the one a German reader notices first.
    /// </summary>
    [Fact]
    public void TheExplanatoryText_ComesFromTheCatalog()
        => UserNodeType.PreferencesDescriptionKeys
            .Should().OnlyContain(k => k.StartsWith("settings."),
                "the tab's explanatory prose must be catalog keys, not literals");

    /// <summary>
    /// End to end against the REAL catalog: a German viewer must not be shown the English label.
    /// The echo test above proves the value went through the localizer; this proves the catalog
    /// actually has German behind it, which is the half a key without a translation would miss.
    /// </summary>
    [Fact]
    public void AGermanViewer_SeesGermanNotEnglish()
    {
        var german = UserNodeType.PreferenceFields(key => LocalizationCatalog.Get(key, "de"));

        var zone = german.Single(f => f.Key.Equals("timeZoneId", System.StringComparison.Ordinal));
        zone.Label.Should().NotBe("Display time zone (IANA)",
            "this is the exact literal that shipped, and it read as English to every German viewer");
        zone.Label.Should().NotStartWith("settings.",
            "a raw key on screen means the German catalog has no entry for it");
    }

    /// <summary>
    /// Both shipped languages must carry every key the tab uses. <c>LocalizationTest</c> enforces
    /// coverage across the catalog as a whole; this names THESE keys, so deleting one is a failure
    /// here rather than a raw token appearing on the settings page.
    /// </summary>
    [Fact]
    public void EveryKeyTheTabUses_ExistsInBothShippedLanguages()
    {
        var keys = UserNodeType.PreferenceFields(key => key).Select(f => f.Label)
            .Concat(UserNodeType.PreferencesDescriptionKeys)
            .Distinct();

        foreach (var key in keys)
        foreach (var locale in Locales.Supported)
            LocalizationCatalog.Get(key, locale).Should().NotBe(key,
                "'{0}' is missing from strings.{1}.json — the catalog falls back to the key "
                + "itself, so the settings page would render the raw token", key, locale);
    }
}
