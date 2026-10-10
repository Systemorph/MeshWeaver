using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Defaults = Memex.Portal.ServiceDefaults.ServiceDefaults;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A REJECTED CERTIFICATE IS NOT A TRANSIENT FAULT</b> — Systemorph/MeshWeaver#5910.
///
/// <para>An agent web fetch of a site whose certificate had expired (<c>NotTimeValid</c>) was
/// attempted three times by the defaults pipeline (<c>Standard-Retry</c>), each logged at Error.
/// The first fix keyed on "an <see cref="AuthenticationException"/> with no inner exception" and was
/// withdrawn: <c>SslStream</c> raises other, transient handshake failures in that same shape. The
/// marker is therefore raised where the verdict is made — the validation callback — as a typed
/// <see cref="RemoteCertificateRejectedException"/>.</para>
///
/// <para>Both directions run through the REAL defaults (<see cref="Defaults.AddHttpClientResilienceDefaults"/>)
/// against a real TLS listener on loopback, counting the connections it accepts: a server whose
/// certificate has expired is contacted ONCE, and — the control — a server that drops the
/// connection mid-handshake is still retried.</para>
/// </summary>
public class RejectedCertificateIsNotRetriedTest
{
    private const string Client = "cert-probe";

    [Fact]
    public async Task AnExpiredCertificate_IsContactedOnce_AndReportedAsTyped()
    {
        using var certificate = ExpiredSelfSigned();
        using var server = new LoopbackServer(certificate);
        using var provider = Provider();
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(Client);

        var fault = await Record(() => http.GetAsync(server.Url, TestContext.Current.CancellationToken));

        fault.Should().BeOfType<HttpRequestException>();
        var rejected = fault!.InnerException.Should().BeOfType<RemoteCertificateRejectedException>(
            "the defaults' validation callback reports the rejection as the typed marker").Subject;
        rejected.ChainStatus.Should().Contain(nameof(X509ChainStatusFlags.NotTimeValid),
            "the marker names what was wrong, as the runtime's own message did");
        server.Accepted.Should().Be(1,
            "a certificate that failed validation cannot pass on a retry — the pipeline gives up after one attempt");
    }

    /// <summary>
    /// 🚨 THE CONTROL — the case the withdrawn fix broke. A server that closes the connection during
    /// the handshake produces an <see cref="AuthenticationException"/> too, and that one IS transient:
    /// it must still be retried.
    /// </summary>
    [Fact]
    public async Task AHandshakeTheServerDrops_IsStillRetried()
    {
        using var server = new LoopbackServer(certificate: null);
        using var provider = Provider();
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(Client);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var request = http.GetAsync(server.Url, cts.Token);
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => server.Accepted >= 2)
            .Take(1)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        cts.Cancel();
        await Record(() => request);

        server.Accepted.Should().BeGreaterThanOrEqualTo(2,
            "a dropped handshake is transient, and the defaults pipeline retries it");
    }

    [Fact]
    public void TheVerdictIsTheDefaultOne_AndOnlyTheMarkerIsDeterministic()
    {
        Defaults.ValidateServerCertificate(SslPolicyErrors.None, chain: null).Should().BeTrue(
            "a certificate the runtime found valid is accepted exactly as before");
        var rejected = Assert.Throws<RemoteCertificateRejectedException>(() =>
            Defaults.ValidateServerCertificate(SslPolicyErrors.RemoteCertificateNameMismatch, chain: null));
        rejected.PolicyErrors.Should().Be(SslPolicyErrors.RemoteCertificateNameMismatch,
            "any non-empty policy error is a rejection, reported with what was wrong");

        Defaults.IsDeterministicTransportFailure(new HttpRequestException("tls",
                new RemoteCertificateRejectedException(SslPolicyErrors.RemoteCertificateChainErrors, "NotTimeValid")))
            .Should().BeTrue("the typed marker, wrapped as HttpClient wraps it, is not retried");
        Defaults.IsDeterministicTransportFailure(new HttpRequestException("tls",
                new AuthenticationException("Authentication failed because the remote party has closed the transport stream.")))
            .Should().BeFalse("a bare AuthenticationException is not the marker — it may be transient");
        Defaults.IsDeterministicTransportFailure(null).Should().BeFalse();
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Defaults.AddHttpClientResilienceDefaults(services);
        services.AddHttpClient(Client);
        return services.BuildServiceProvider();
    }

    private static async Task<Exception?> Record(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static X509Certificate2 ExpiredSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
        // Round-trip through PFX so the server side holds a usable private key on every platform.
        return X509CertificateLoader.LoadPkcs12(expired.Export(X509ContentType.Pfx), password: null);
    }

    /// <summary>
    /// A loopback listener that counts accepted connections. With a certificate it completes the
    /// server side of the handshake; without one it closes each connection at once, which the client
    /// sees as a dropped handshake.
    /// </summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2? certificate;
        private readonly CancellationTokenSource stop = new();
        private int accepted;

        public LoopbackServer(X509Certificate2? certificate)
        {
            this.certificate = certificate;
            listener.Start();
            _ = AcceptLoop();
        }

        public int Accepted => Volatile.Read(ref accepted);

        public Uri Url => new($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

        private async Task AcceptLoop()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception) { return; }
                Interlocked.Increment(ref accepted);
                _ = Serve(tcp);
            }
        }

        private async Task Serve(TcpClient tcp)
        {
            using (tcp)
            {
                if (certificate is null)
                    return;
                using var ssl = new SslStream(tcp.GetStream());
                try { await ssl.AuthenticateAsServerAsync(certificate, false, false); }
                catch (Exception) { /* the client rejects the certificate: the handshake ends here */ }
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            stop.Dispose();
        }
    }
}
