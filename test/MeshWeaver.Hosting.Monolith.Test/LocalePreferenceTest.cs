using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pure, host-independent unit tests for the write-once DECISION — the single correctness
/// property of the UI-language population feature (browser detect must NEVER clobber an existing
/// or user-chosen language). The twin of <see cref="TimeZonePreferenceTest"/>. Deterministic: no
/// mesh, no browser, no host-culture dependency.
/// </summary>
public class LocalePreferenceTest
{
    [Theory]
    [InlineData(null, "de", "de")]              // empty profile → write detected
    [InlineData("", "de", "de")]
    [InlineData("   ", "de", "de")]
    [InlineData(null, "de-CH", "de")]           // region variant normalizes to the shipped tag
    [InlineData(null, "de_DE.UTF-8", "de")]     // POSIX shape normalizes too
    [InlineData(null, "en-GB", "en")]
    public void ShouldWrite_WritesDetected_WhenProfileEmpty(string? current, string? detected, string expected)
        => LocalePreference.ShouldWrite(current, detected).Should().Be(expected);

    [Theory]
    // A non-empty existing value is NEVER overwritten — the whole point of write-once. Someone
    // who chose German keeps German when signing in from an English-configured machine.
    [InlineData("de", "en")]
    [InlineData("en", "de")]
    [InlineData("de", null)]
    [InlineData("de", "")]
    // Nothing detected → nothing to write, even on an empty profile.
    [InlineData(null, null)]
    [InlineData("", "   ")]
    public void ShouldWrite_ReturnsNull_WhenNoWriteWanted(string? current, string? detected)
        => LocalePreference.ShouldWrite(current, detected).Should().BeNull();

    /// <summary>
    /// An UNSUPPORTED browser language must leave the profile untouched rather than persisting a
    /// tag we ship no translation for. Persisting it would pin the user to a value that renders in
    /// English anyway AND would block the write-once path from ever picking up a translation
    /// shipped later — so "unsupported" has to stay distinguishable from "chose English".
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("fr-CA")]
    [InlineData("ja")]
    [InlineData("zz-ZZ")]
    public void ShouldWrite_ReturnsNull_ForUnsupportedLanguage(string detected)
        => LocalePreference.ShouldWrite(null, detected).Should().BeNull();

    [Fact]
    public void SupportedLocales_AllHaveAnEndonym()
    {
        Locales.Supported.Should().NotBeEmpty();
        Locales.Supported.Should().Contain(Locales.Default);
        foreach (var locale in Locales.Supported)
            Locales.DisplayNames.Should().ContainKey(locale,
                "the settings picker shows each language named in itself");
    }
}
