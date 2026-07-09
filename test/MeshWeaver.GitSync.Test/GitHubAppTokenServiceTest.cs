#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Offline coverage for <see cref="GitHubAppTokenService"/> — the GitHub App machine identity
/// behind "server-side sync logs on AS THE APP": the RS256 App JWT must verify against the App's
/// key with the documented claims, the installation token must be fetched with that JWT (including
/// installation discovery by owner) and cached across callers, and an unconfigured App must fail
/// with an actionable error instead of a silent null.
/// </summary>
public class GitHubAppTokenServiceTest
{
    private static (string Pem, RSA Rsa) NewKey()
    {
        var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa);
    }

    private static GitHubAppTokenService NewService(
        GitHubAppOptions options, HttpMessageHandler? handler = null) =>
        new(new IoPoolRegistry(), Options.Create(options),
            httpClient: handler is null ? null : new HttpClient(handler));

    [Fact]
    public void AppJwt_SignsRs256_WithDocumentedClaims()
    {
        var (pem, rsa) = NewKey();
        using var _ = rsa;
        var service = NewService(new GitHubAppOptions { ClientId = "Iv23liTestApp", PrivateKey = pem });

        var now = DateTimeOffset.FromUnixTimeSeconds(1_752_000_000);
        var jwt = service.BuildAppJwt(now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        // Signature verifies against the App key over header.payload (RS256 / Pkcs1).
        var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        Assert.True(
            rsa.VerifyData(signed, FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            "the App JWT must be RS256-signed with the App's private key");

        using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
        Assert.Equal("Iv23liTestApp", payload.RootElement.GetProperty("iss").GetString());
        // iat is backdated a minute for clock skew; exp stays under GitHub's ten-minute cap.
        Assert.Equal(now.AddMinutes(-1).ToUnixTimeSeconds(), payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(now.AddMinutes(9).ToUnixTimeSeconds(), payload.RootElement.GetProperty("exp").GetInt64());
    }

    [Fact]
    public async Task InstallationToken_DiscoversByOwner_FetchesWithJwt_AndCaches()
    {
        var (pem, rsa) = NewKey();
        using var _ = rsa;
        var handler = new FakeGitHubAppHandler();
        var service = NewService(new GitHubAppOptions
        {
            ClientId = "Iv23liTestApp",
            PrivateKey = pem,
            InstallationOwner = "Systemorph",
        }, handler);

        var token = await service.GetInstallationToken().FirstAsync().ToTask();
        Assert.Equal("ghs_installation_token", token);

        // Discovery picked the Systemorph installation (77), not the first-listed other org.
        Assert.Contains("/app/installations/77/access_tokens", handler.TokenRequestPath);
        // Both calls authenticated with a Bearer JWT (three dot-separated segments).
        Assert.All(handler.AuthSchemes, s => Assert.Equal("Bearer", s));
        Assert.All(handler.AuthTokens, t => Assert.Equal(3, t.Split('.').Length));

        // Second call replays the cached token — no additional mint round-trip.
        var again = await service.GetInstallationToken().FirstAsync().ToTask();
        Assert.Equal(token, again);
        Assert.Equal(1, handler.TokenRequests);
    }

    [Fact]
    public async Task InstallationToken_Unconfigured_FailsActionably()
    {
        var service = NewService(new GitHubAppOptions());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetInstallationToken().FirstAsync().ToTask());
        // The error must name the missing configuration keys.
        Assert.Contains("GitHub:App:ClientId", ex.Message);
    }

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    /// <summary>GitHub App API fake: installation listing + token minting, capturing auth headers.</summary>
    private sealed class FakeGitHubAppHandler : HttpMessageHandler
    {
        public int TokenRequests;
        public string? TokenRequestPath;
        public readonly List<string> AuthSchemes = [];
        public readonly List<string> AuthTokens = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            AuthSchemes.Add(request.Headers.Authorization?.Scheme ?? "<none>");
            AuthTokens.Add(request.Headers.Authorization?.Parameter ?? "");

            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/app/installations")
                return Task.FromResult(Json("""
                    [
                      {"id": 11, "account": {"login": "SomeOtherOrg"}},
                      {"id": 77, "account": {"login": "Systemorph"}}
                    ]
                    """));
            if (request.Method == HttpMethod.Post && path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref TokenRequests);
                TokenRequestPath = path;
                var expires = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
                return Task.FromResult(Json($$"""{"token": "ghs_installation_token", "expires_at": "{{expires}}"}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected {request.Method} {path}"),
            });
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
