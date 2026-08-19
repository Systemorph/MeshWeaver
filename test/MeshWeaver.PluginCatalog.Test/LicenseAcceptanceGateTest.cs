using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the body-hash rule that makes a recorded <see cref="LicenseAcceptance"/> mean anything: an
/// acceptance is only evidence against the text that was actually shown.
///
/// <para>The hash is the part with a real trap on both sides. Too strict and every acceptance is
/// revoked by a line-ending change no reader could see; too loose and revised terms are silently
/// covered by consent given to the old ones.</para>
/// </summary>
public class LicenseAcceptanceGateTest
{
    [Fact]
    public void TheSameTextHashesTheSame()
        => Assert.Equal(
            LicenseAcceptanceGate.BodyHash("Permission is hereby granted."),
            LicenseAcceptanceGate.BodyHash("Permission is hereby granted."));

    [Fact]
    public void DifferentTermsHashDifferently()
    {
        // The case the gate exists for: terms revised after someone accepted them.
        Assert.NotEqual(
            LicenseAcceptanceGate.BodyHash("You may use this for anything."),
            LicenseAcceptanceGate.BodyHash("You may use this for non-commercial purposes only."));
    }

    [Theory]
    [InlineData("a\nb", "a\r\nb")]
    [InlineData("a\nb", "a\rb")]
    [InlineData("terms", "terms\n")]
    [InlineData("terms", "terms   ")]
    [InlineData("terms", "terms\r\n\r\n")]
    public void NormalizationIgnoresWhatARoundTripChanges(string left, string right)
    {
        // A licence body that passes through git, an editor or a copy-paste can change bytes
        // without changing terms. Hashing those raw would revoke every acceptance for a reason
        // nobody could see on screen.
        Assert.Equal(LicenseAcceptanceGate.BodyHash(left), LicenseAcceptanceGate.BodyHash(right));
    }

    [Fact]
    public void NormalizationDoesNotIgnoreLEADINGWhitespace()
    {
        // Only trailing whitespace and line endings are noise. Indentation can be meaningful in a
        // licence, so it stays part of the hash.
        Assert.NotEqual(
            LicenseAcceptanceGate.BodyHash("terms"),
            LicenseAcceptanceGate.BodyHash("   terms"));
    }

    [Fact]
    public void InternalWhitespaceIsSignificant()
        => Assert.NotEqual(
            LicenseAcceptanceGate.BodyHash("no commercial use"),
            LicenseAcceptanceGate.BodyHash("no  commercial use"));

    [Fact]
    public void NullAndEmptyHashAlike_AndDoNotThrow()
    {
        // A licence node with no body is a defect, not a crash — the gate reports it by failing to
        // match rather than by taking the install down.
        Assert.Equal(LicenseAcceptanceGate.BodyHash(null), LicenseAcceptanceGate.BodyHash(""));
        Assert.NotEmpty(LicenseAcceptanceGate.BodyHash(null));
    }

    [Fact]
    public void TheHashIsLowercaseHexSha256()
    {
        var hash = LicenseAcceptanceGate.BodyHash("terms");
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.All(hash, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void AnAcceptanceRecordCarriesTheHashItWasGivenAgainst()
    {
        // The shape the gate compares — pinned so the field cannot quietly stop being populated.
        var body = "You may use this for non-commercial purposes only.";
        var acceptance = new LicenseAcceptance
        {
            SpdxId = "Systemorph-Commercial-1.0",
            PackageId = "Publish",
            UserId = "rbuergi",
            AcceptedAt = DateTimeOffset.UtcNow,
            BodyHash = LicenseAcceptanceGate.BodyHash(body),
        };

        Assert.Equal(LicenseAcceptanceGate.BodyHash(body), acceptance.BodyHash);
        Assert.NotEqual(LicenseAcceptanceGate.BodyHash(body + " Except on Tuesdays."), acceptance.BodyHash);
    }

    [Fact]
    public void ThePlatformsOwnLicencesAskForNoAcceptance()
    {
        // Apache-2.0 and MIT ask nothing of the user, so demanding a click is friction pretending to
        // be diligence — and the gate must stay a no-op for the overwhelmingly common case.
        Assert.Equal("Apache-2.0 OR MIT", WellKnownLicenses.PlatformSpdxExpression);
        Assert.Contains("Apache-2.0", WellKnownLicenses.Shipped);
        Assert.Contains("MIT", WellKnownLicenses.Shipped);
    }
}
