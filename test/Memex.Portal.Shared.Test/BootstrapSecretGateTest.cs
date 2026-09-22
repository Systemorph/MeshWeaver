using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The <c>Bootstrap:Secret</c> door, in both directions.
///
/// <para>This endpoint is anonymous-reachable by design — there is no administrator yet to
/// authorise it — and what it grants is platform admin on an instance nobody administers. So the
/// two properties worth pinning are that it stays SHUT (absent, wrong, or not configured at all)
/// and that the one accepted case is the configured secret, presented in either form.</para>
/// </summary>
public class BootstrapSecretGateTest
{
    [Fact]
    public void WithNoSecretConfiguredTheEndpointDoesNotExist()
    {
        // Disabled must not be confused with Missing: the controller answers 404 for the first and
        // 401 for the second, so an unconfigured instance never admits that the door is there.
        Assert.Equal(BootstrapAuthResult.Disabled, BootstrapSecretGate.Decide(null, "anything"));
        Assert.Equal(BootstrapAuthResult.Disabled, BootstrapSecretGate.Decide("", "anything"));
        Assert.Equal(BootstrapAuthResult.Disabled, BootstrapSecretGate.Decide("   ", "anything"));
    }

    [Fact]
    public void AConfiguredSecretAdmitsOnlyItself()
    {
        Assert.Equal(BootstrapAuthResult.Accepted, BootstrapSecretGate.Decide("s3cret", "s3cret"));
        Assert.Equal(BootstrapAuthResult.Invalid, BootstrapSecretGate.Decide("s3cret", "s3crey"));
        Assert.Equal(BootstrapAuthResult.Invalid, BootstrapSecretGate.Decide("s3cret", "s3cret "));
        Assert.Equal(BootstrapAuthResult.Invalid, BootstrapSecretGate.Decide("s3cret", "S3CRET"));
        Assert.Equal(BootstrapAuthResult.Missing, BootstrapSecretGate.Decide("s3cret", null));
        Assert.Equal(BootstrapAuthResult.Missing, BootstrapSecretGate.Decide("s3cret", ""));
    }

    [Fact]
    public void ALengthMismatchIsRefusedRatherThanThrowing()
    {
        // FixedTimeEquals over byte arrays of different lengths must answer false, not throw:
        // the prefix of a correct secret is the most likely wrong answer an attacker sends.
        Assert.Equal(BootstrapAuthResult.Invalid, BootstrapSecretGate.Decide("s3cret", "s3"));
        Assert.Equal(BootstrapAuthResult.Invalid, BootstrapSecretGate.Decide("s3cret", "s3cretatlength"));
        Assert.False(BootstrapSecretGate.FixedTimeEquals("s3cret", "s3"));
        Assert.True(BootstrapSecretGate.FixedTimeEquals("s3cret", "s3cret"));
    }

    [Fact]
    public void TheHeaderWinsAndOnlyTheQueryFormIsReportedAsSuch()
    {
        // The warning is about the transport, not the value — a caller that has moved to the header
        // must not be scolded because a stale script still appends the query parameter.
        Assert.Equal(("hdr", false), BootstrapSecretGate.Present("hdr", "qry"));
        Assert.Equal(("qry", true), BootstrapSecretGate.Present(null, "qry"));
        Assert.Equal(("qry", true), BootstrapSecretGate.Present("", "qry"));
        Assert.Equal(("hdr", false), BootstrapSecretGate.Present("hdr", null));

        // Nothing presented at all is not "from the query": there is no transport to complain about.
        Assert.Equal((null, false), BootstrapSecretGate.Present(null, null));
    }

    [Fact]
    public void AUnicodeSecretComparesByItsBytes()
    {
        // The gate encodes UTF-8 before comparing, so a non-ASCII secret is neither truncated nor
        // silently equal to a different string with the same length in chars.
        Assert.Equal(BootstrapAuthResult.Accepted, BootstrapSecretGate.Decide("pässwörd", "pässwörd"));
        Assert.Equal(BootstrapAuthResult.Invalid, BootstrapSecretGate.Decide("pässwörd", "passwoerd"));
    }
}
