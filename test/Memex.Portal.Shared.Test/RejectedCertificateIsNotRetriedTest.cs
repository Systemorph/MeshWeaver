using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
// The namespace and the class share a name, so the class needs an alias to be nameable.
using Defaults = Memex.Portal.ServiceDefaults.ServiceDefaults;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A CERTIFICATE THIS SIDE REJECTS IS NOT A TRANSIENT FAULT</b> — Systemorph/MeshWeaver#5910.
///
/// <para>An agent web fetch of a site whose certificate had expired (<c>NotTimeValid</c>) went
/// through the standard retry pipeline three times, each attempt ~100 ms after the last, each
/// logged at <c>Error</c> under <c>Polly</c> — and validation is deterministic, so no attempt could
/// succeed. Those Error lines become a <c>LogIncident</c> and a platform ticket about somebody
/// else's certificate.</para>
///
/// <para>The predicate is pure and asserted on the exception SHAPES the runtime produces.</para>
/// </summary>
public class RejectedCertificateIsNotRetriedTest
{
    /// <summary>
    /// The measured shape: <c>HttpRequestException("The SSL connection could not be established")</c>
    /// wrapping an <see cref="AuthenticationException"/> that <c>SslStream</c> raised from its own
    /// validation verdict — no inner exception.
    /// </summary>
    private static Exception Rejected() =>
        new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "The remote certificate is invalid because of errors in the certificate chain: NotTimeValid"));

    [Fact]
    public void ARejectedCertificateIsNeitherRetriedNorCountedByTheBreaker()
    {
        Assert.True(Defaults.CertificateIsRejected(Rejected()));
        Assert.True(Defaults.IsAboutTheUrlNotTheNetwork(Rejected()),
            "the retry and breaker predicates both read IsAboutTheUrlNotTheNetwork");
        Assert.True(Defaults.CertificateIsRejected(new InvalidOperationException("pipeline", Rejected())),
            "a handler pipeline may wrap the request exception again");
    }

    /// <summary>
    /// 🚨 THE CONTROL. A handshake the TRANSPORT broke — the peer closed the stream, a reset, a
    /// timeout — surfaces as the same <see cref="AuthenticationException"/> type, but carrying the
    /// <see cref="IOException"/> that broke it. That one is transient and must keep being retried; a
    /// predicate keyed on the exception type alone would turn a network hiccup into a hard failure.
    /// </summary>
    [Fact]
    public void AHandshakeTheTransportBrokeIsStillRetried()
    {
        var transportClosed = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "Authentication failed because the remote party has closed the transport stream.",
                new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
        Assert.False(Defaults.CertificateIsRejected(transportClosed));
        Assert.False(Defaults.IsAboutTheUrlNotTheNetwork(transportClosed));

        var reset = new HttpRequestException("reset", new SocketException((int)SocketError.ConnectionReset));
        Assert.False(Defaults.CertificateIsRejected(reset));
        Assert.False(Defaults.IsAboutTheUrlNotTheNetwork(reset));

        Assert.False(Defaults.CertificateIsRejected(null),
            "a successful outcome carries no exception");
    }

    /// <summary>The combined predicate still carries the #4613 half.</summary>
    [Fact]
    public void ANameThatDoesNotExistIsStillExcluded() =>
        Assert.True(Defaults.IsAboutTheUrlNotTheNetwork(
            new HttpRequestException("nx", new SocketException((int)SocketError.HostNotFound))));
}
