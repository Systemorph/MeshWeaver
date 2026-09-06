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
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The installation-token cache of <see cref="GitHubAppTokenService"/> as a LONG-LIVED consumer
/// sees it: one <see cref="GitHubAppTokenService.GetInstallationToken"/> observable held for the
/// life of a feed and subscribed once per poll pass — the shape of the Store's git poll loop.
///
/// <para>🚨 <b>The failure this file makes impossible (Systemorph/Memex#165):</b> the promise was
/// captured when the observable was BUILT, so the refresh guard matched it exactly once. The first
/// expiry minted a new token; every later expiry compared against the stale capture, found the
/// cache already replaced, and handed the expired token back. Two token lifetimes after boot the
/// portal presented an expired installation token on every pass, and GitHub reported that as
/// <c>401 Bad credentials</c> on every private source until the process restarted — measured on
/// memex-cloud as the first failure landing 2 h 01 min after the container started.</para>
///
/// <para>Time is injected, so each assertion names the minute at which the held observable is
/// subscribed and the token it must answer with; the third refresh is the one the defect skipped.</para>
/// </summary>
public class GitHubAppTokenRefreshTest
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

    // Every await is bounded: a wedged token stream must surface as a timeout, never a stuck run.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OneHeldObservable_RefreshesOnEveryExpiry_NotOnlyTheFirst()
    {
        using var rsa = RSA.Create(2048);
        var clock = T0;
        var handler = new MintingHandler(() => clock);
        var service = new GitHubAppTokenService(
            new IoPoolRegistry(),
            Options.Create(new GitHubAppOptions
            {
                ClientId = "Iv23liTestApp",
                PrivateKey = rsa.ExportRSAPrivateKeyPem(),
                InstallationId = 77,
            }),
            httpClient: new HttpClient(handler),
            clock: () => clock);

        // ONE observable, held — the Store feed's shape — subscribed per pass below.
        var held = service.GetInstallationToken();

        Assert.Equal("ghs_1", await held.FirstAsync().Timeout(Budget).Await());
        Assert.Equal(1, handler.Mints);

        // Well inside the lifetime: replayed, no mint.
        clock = T0.AddMinutes(30);
        Assert.Equal("ghs_1", await held.FirstAsync().Timeout(Budget).Await());
        Assert.Equal(1, handler.Mints);

        // Inside the five-minute refresh window: the FIRST refresh — this one always worked.
        clock = T0.AddMinutes(56);
        Assert.Equal("ghs_2", await held.FirstAsync().Timeout(Budget).Await());
        Assert.Equal(2, handler.Mints);

        // Token 2 expires at T0+116 min. The SECOND refresh is the one the defect skipped: before
        // the fix this pass answered ghs_2, four minutes from expiry, and every later pass answered
        // it too — long after GitHub had stopped accepting it.
        clock = T0.AddMinutes(112);
        Assert.Equal("ghs_3", await held.FirstAsync().Timeout(Budget).Await());
        Assert.Equal(3, handler.Mints);

        // And the third, to show it is every expiry rather than the first two.
        clock = T0.AddMinutes(168);
        Assert.Equal("ghs_4", await held.FirstAsync().Timeout(Budget).Await());
        Assert.Equal(4, handler.Mints);

        // A pass while the token is fresh still replays: the fix did not turn the cache into a mint-per-pass.
        clock = T0.AddMinutes(170);
        Assert.Equal("ghs_4", await held.FirstAsync().Timeout(Budget).Await());
        Assert.Equal(4, handler.Mints);
    }

    [Fact]
    public async Task ConcurrentPassesOnAnExpiredToken_ShareOneRefresh()
    {
        using var rsa = RSA.Create(2048);
        var clock = T0;
        var handler = new MintingHandler(() => clock);
        var service = new GitHubAppTokenService(
            new IoPoolRegistry(),
            Options.Create(new GitHubAppOptions
            {
                ClientId = "Iv23liTestApp",
                PrivateKey = rsa.ExportRSAPrivateKeyPem(),
                InstallationId = 77,
            }),
            httpClient: new HttpClient(handler),
            clock: () => clock);

        var held = service.GetInstallationToken();
        Assert.Equal("ghs_1", await held.FirstAsync().Timeout(Budget).Await());

        // Five sources poll on the same timer; when the token is stale they all refresh at once
        // and must share the single new promise rather than mint five tokens.
        clock = T0.AddMinutes(58);
        var tokens = await Observable.Range(0, 5)
            .SelectMany(_ => held.Take(1))
            .ToList()
            .FirstAsync()
            .Timeout(Budget)
            .Await();
        Assert.All(tokens, t => Assert.Equal("ghs_2", t));
        Assert.Equal(2, handler.Mints);
    }

    /// <summary>GitHub App token endpoint fake: every mint answers the next token, valid one hour from the injected clock.</summary>
    private sealed class MintingHandler(Func<DateTimeOffset> clock) : HttpMessageHandler
    {
        private int mints;
        public int Mints => Volatile.Read(ref mints);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/app/installations/77/access_tokens")
            {
                var n = Interlocked.Increment(ref mints);
                var expires = clock().AddHours(1).ToString("o");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        $$"""{"token": "ghs_{{n}}", "expires_at": "{{expires}}"}""",
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
