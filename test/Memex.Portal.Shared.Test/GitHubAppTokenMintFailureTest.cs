using System;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Options;
using Octokit;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The two halves of "GitHub said no", told apart by TYPE alone (#4736).
///
/// <para>🚨 <b>The failure this file makes impossible.</b> A GitHub App installation token that
/// could NOT BE MINTED and one that was minted and then REJECTED are different events with
/// different operator actions — reprovision the App or reinstall it, versus look at the
/// installation's repository permissions. They used to be indistinguishable: every mint failure
/// threw a bare <see cref="InvalidOperationException"/>, so a consumer that degrades to an
/// anonymous fetch (the Store's package feed does, deliberately — a public source must keep
/// working) could only report the fault it saw ONE LAYER LATER, which is Octokit's
/// <c>AuthorizationException: Bad credentials</c>. That sentence describes a credential GitHub
/// REJECTED; read against a mint that never happened it sends the reader to the App's repository
/// permissions, which are fine, and away from the private key or the installation, which are not.
/// </para>
///
/// <para>Each case names a stage AND proves it reached that stage — how many requests the fake
/// GitHub answered, and which. "The fault is a mint failure" would otherwise also pass on a
/// service that refused at its configuration guard and never called GitHub at all.</para>
/// </summary>
public class GitHubAppTokenMintFailureTest
{
    // In-process fakes over an I/O pool; nothing here waits on a network or on a convergence.
    private static readonly TimeSpan Budget = TestTimeouts.Quick;

    [Fact]
    public async Task AnExchangeGitHubRefuses_IsAMintFailure_NamingTheExchange()
    {
        using var rsa = RSA.Create(2048);
        var handler = new FakeGitHub { ExchangeStatus = HttpStatusCode.Unauthorized };
        var service = Service(handler, rsa.ExportRSAPrivateKeyPem());

        var failure = await Assert.ThrowsAsync<GitHubAppTokenMintException>(
            () => service.GetInstallationToken().Timeout(Budget).Await());

        Assert.Equal(GitHubAppTokenMintStage.TokenExchange, failure.Stage);
        Assert.Equal(401, failure.StatusCode);
        // 🚨 The path was actually walked: the App identity passed the configuration guard and the
        // exchange really was attempted. Without these two the assertion above would also pass on
        // a service that refused at NotConfigured without calling GitHub once.
        Assert.True(service.IsConfigured);
        Assert.Equal(1, handler.Exchanges);
        // Verdicts and statuses travel in the report; nothing that could be a secret does.
        Assert.DoesNotContain("BEGIN RSA", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMintedTokenRejectedDownstream_IsNotAMintFailure()
    {
        using var rsa = RSA.Create(2048);
        var handler = new FakeGitHub();
        var service = Service(handler, rsa.ExportRSAPrivateKeyPem());

        // The other side of the change, in the shape a Store poll pass has: the mint SUCCEEDS, the
        // token flows, and the repository read is what GitHub refuses.
        var rejected = service.GetInstallationToken()
            .SelectMany(_ => Observable.Throw<string>(new AuthorizationException()));

        var failure = await Assert.ThrowsAsync<AuthorizationException>(
            () => rejected.Timeout(Budget).Await());

        // The discriminator, with no message read on either side.
        Assert.IsNotType<GitHubAppTokenMintException>(failure);
        // A credential that WAS obtained — the whole difference from the case above.
        Assert.Equal(1, handler.Exchanges);
    }

    [Fact]
    public async Task AnUnusablePrivateKey_IsAMintFailure_AndNeverReachesGitHub()
    {
        var handler = new FakeGitHub();
        var service = Service(
            handler, "-----BEGIN RSA PRIVATE KEY-----\nnot-a-key\n-----END RSA PRIVATE KEY-----");

        var failure = await Assert.ThrowsAsync<GitHubAppTokenMintException>(
            () => service.GetInstallationToken().Timeout(Budget).Await());

        Assert.Equal(GitHubAppTokenMintStage.Signing, failure.Stage);
        Assert.Null(failure.StatusCode);
        // 🚨 It failed at SIGNING rather than at the configuration guard — the identity IS
        // configured — and GitHub was never asked, so this can never be a rejected credential.
        Assert.True(service.IsConfigured);
        Assert.Equal(0, handler.Requests);
        Assert.DoesNotContain("not-a-key", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAppWithNoInstallation_IsAMintFailure_NamingTheInstallation()
    {
        using var rsa = RSA.Create(2048);
        var handler = new FakeGitHub { DiscoveryStatus = HttpStatusCode.NotFound };
        var service = Service(handler, rsa.ExportRSAPrivateKeyPem(), installationId: null);

        var failure = await Assert.ThrowsAsync<GitHubAppTokenMintException>(
            () => service.GetInstallationToken().Timeout(Budget).Await());

        Assert.Equal(GitHubAppTokenMintStage.InstallationDiscovery, failure.Stage);
        Assert.Equal(404, failure.StatusCode);
        // Discovery was reached and the exchange never was: the App is not installed, rather than
        // installed with a credential GitHub turned down.
        Assert.Equal(1, handler.Discoveries);
        Assert.Equal(0, handler.Exchanges);
    }

    [Theory]
    [InlineData("""{"expires_at": "2026-09-18T12:00:00+00:00"}""")]          // no 'token' at all
    [InlineData("""{"token": 12345, "expires_at": "2026-09-18T12:00:00+00:00"}""")]   // present, not a string
    public async Task AnExchangeThatSucceedsWithNoUsableToken_IsAMintFailure_NamingTheResponse(string body)
    {
        using var rsa = RSA.Create(2048);
        var handler = new FakeGitHub { ExchangeBody = body };
        var service = Service(handler, rsa.ExportRSAPrivateKeyPem());

        var failure = await Assert.ThrowsAsync<GitHubAppTokenMintException>(
            () => service.GetInstallationToken().Timeout(Budget).Await());

        Assert.Equal(GitHubAppTokenMintStage.Response, failure.Stage);
        // 🚨 NULL, and the null is the assertion. StatusCode is set only where GitHub REFUSED
        // something; here the exchange SUCCEEDED, so a 201 attached to this failure would be an
        // accurate number under a misleading claim — the very confusion #4736 is about.
        Assert.Null(failure.StatusCode);
        // The exchange was really walked: this cannot pass on a service that stopped earlier.
        Assert.Equal(1, handler.Exchanges);
    }

    [Fact]
    public async Task AMintThatNeverReachesAVerdict_IsStillAMintFailure()
    {
        using var rsa = RSA.Create(2048);
        // The transport dies before any status exists. Translate ALL of it or let it through: a
        // fault with no GitHub verdict behind it is still "no credential was obtained", and a
        // caller must not need a second catch arm for the untyped remainder.
        var handler = new FakeGitHub { Transport = () => new HttpRequestException("no route to host") };
        var service = Service(handler, rsa.ExportRSAPrivateKeyPem());

        var failure = await Assert.ThrowsAsync<GitHubAppTokenMintException>(
            () => service.GetInstallationToken().Timeout(Budget).Await());

        Assert.Equal(GitHubAppTokenMintStage.Transport, failure.Stage);
        Assert.Null(failure.StatusCode);
        Assert.NotNull(failure.InnerException);
        Assert.Equal(1, handler.Requests);
    }

    /// <summary>A usable App identity, so each case fails where it claims to rather than on the
    /// configuration guard.</summary>
    private static GitHubAppTokenService Service(
        HttpMessageHandler handler, string privateKey, long? installationId = 77) =>
        new(
            new IoPoolRegistry(),
            Options.Create(new GitHubAppOptions
            {
                ClientId = "Iv23liTestApp",
                PrivateKey = privateKey,
                InstallationId = installationId,
            }),
            httpClient: new HttpClient(handler));

    /// <summary>
    /// The GitHub App endpoints, answering what each case asks for and COUNTING what it was asked —
    /// the counts are what make an assertion about a stage an assertion about a path walked.
    /// </summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private int discoveries;
        private int exchanges;

        /// <summary>Status for <c>GET /app/installations</c> — 200 with one installation by default.</summary>
        public HttpStatusCode DiscoveryStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>Status for <c>POST /app/installations/{id}/access_tokens</c>.</summary>
        public HttpStatusCode ExchangeStatus { get; init; } = HttpStatusCode.Created;

        /// <summary>When set, the body a SUCCESSFUL exchange answers with — so a 2xx carrying no
        /// usable token can be exercised without pretending GitHub refused anything.</summary>
        public string? ExchangeBody { get; init; }

        /// <summary>When set, the request dies in transport instead of answering a status.</summary>
        public Func<Exception>? Transport { get; init; }

        public int Discoveries => Volatile.Read(ref discoveries);

        public int Exchanges => Volatile.Read(ref exchanges);

        public int Requests => Discoveries + Exchanges;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/app/installations")
            {
                Interlocked.Increment(ref discoveries);
                if (Transport is { } deadDiscovery)
                    return Task.FromException<HttpResponseMessage>(deadDiscovery());
                return Task.FromResult(new HttpResponseMessage(DiscoveryStatus)
                {
                    Content = new StringContent(
                        DiscoveryStatus == HttpStatusCode.OK
                            ? """[{"id": 77, "account": {"login": "Systemorph"}}]"""
                            : """{"message": "Not Found"}""",
                        Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Post && path == "/app/installations/77/access_tokens")
            {
                Interlocked.Increment(ref exchanges);
                if (Transport is { } deadExchange)
                    return Task.FromException<HttpResponseMessage>(deadExchange());
                var expires = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
                var success = ExchangeBody
                    ?? $$"""{"token": "ghs_test", "expires_at": "{{expires}}"}""";
                return Task.FromResult(new HttpResponseMessage(ExchangeStatus)
                {
                    Content = new StringContent(
                        ExchangeStatus == HttpStatusCode.Created
                            ? success
                            : """{"message": "Bad credentials", "status": "401"}""",
                        Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected {request.Method} {path}"),
            });
        }
    }
}
