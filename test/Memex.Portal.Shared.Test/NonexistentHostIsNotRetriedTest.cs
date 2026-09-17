using System;
using System.Net.Http;
using System.Net.Sockets;
// The namespace and the class share a name, so the class needs an alias to be nameable.
using Defaults = Memex.Portal.ServiceDefaults.ServiceDefaults;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A HOSTNAME THAT DOES NOT EXIST IS NOT A TRANSIENT FAULT</b> — Systemorph/MeshWeaver#4613.
///
/// <para>The standard resilience handler's predicate treats every <see cref="HttpRequestException"/>
/// as retryable, so a request to a name that resolves nowhere was attempted three times — and no
/// retry can make a name exist. Measured on the control instance 2026-09-17: two agent web fetches
/// of <c>www.boss-software.ch</c> and <c>www.bosssw.ch</c> spent three attempts each and logged
/// every one at <c>Error</c> under category <c>Polly</c>. Those Error lines are what the fleet's log
/// watcher folds into a <c>LogIncident</c> and a ticket, so a URL in somebody's data manufactured a
/// platform defect report. All four spellings — both <c>www.</c> hosts and both apex domains —
/// answer NXDOMAIN from the public internet, so the DNS half is a fact about the data and not about
/// cluster DNS, egress or a missing route.</para>
///
/// <para>The predicate is pure, and it is asserted here rather than through a socket: a predicate
/// that can only be exercised by a real failed connection is a predicate nobody checks.</para>
/// </summary>
public class NonexistentHostIsNotRetriedTest
{
    /// <summary>The shape the failure actually arrives in: HttpClient wraps the resolver's error.</summary>
    private static Exception Wrapped(SocketError error) =>
        new HttpRequestException(
            $"Name or service not known (www.example.invalid:443)",
            new SocketException((int)error));

    [Fact]
    public void ANameThatDoesNotExistIsRecognised_ThroughTheWrapperHttpClientAdds()
    {
        Assert.True(Defaults.NameDoesNotResolve(Wrapped(SocketError.HostNotFound)),
            "an NXDOMAIN reaches the predicate wrapped in an HttpRequestException — unwrapping it "
            + "is the whole job, and a predicate that only matched the bare SocketException would "
            + "match nothing that ever happens");

        // …and through a second wrap, which a handler pipeline adds.
        Assert.True(
            Defaults.NameDoesNotResolve(
                new InvalidOperationException("pipeline", Wrapped(SocketError.HostNotFound))),
            "a handler pipeline may wrap the request exception again; the walk must reach the "
            + "socket error however deep it is");
    }

    /// <summary>
    /// 🚨 THE CONTROL, and the reason the set is exactly one error code. A nameserver that failed to
    /// answer (<c>TryAgain</c> / EAI_AGAIN) IS transient and must keep being retried — excluding it
    /// would turn a DNS hiccup into a hard failure, which is the opposite defect.
    /// </summary>
    [Fact]
    public void EveryOtherTransportFailureIsStillRetried()
    {
        Assert.False(Defaults.NameDoesNotResolve(Wrapped(SocketError.TryAgain)),
            "EAI_AGAIN is a nameserver that did not answer — genuinely transient");
        Assert.False(Defaults.NameDoesNotResolve(Wrapped(SocketError.ConnectionReset)));
        Assert.False(Defaults.NameDoesNotResolve(Wrapped(SocketError.TimedOut)));
        Assert.False(Defaults.NameDoesNotResolve(Wrapped(SocketError.ConnectionRefused)),
            "a host that exists and refuses the connection is about the SERVICE, not the name");
        Assert.False(Defaults.NameDoesNotResolve(
            new HttpRequestException("TLS handshake failed")),
            "a request failure carrying no socket error at all says nothing about the name");
        Assert.False(Defaults.NameDoesNotResolve(null),
            "a successful outcome carries no exception, and must not be read as a dead name");
    }
}
