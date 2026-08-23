#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Offline coverage for <see cref="FrameworkReleaseBroadcaster"/> — the memex-side broadcast that
/// turns a platform release into a <c>repository_dispatch</c> per subscriber. The pure request
/// shaping and subscriber parsing must be exact; a per-repo failure must be isolated (never abort
/// the others); an unconfigured App must report every subscriber un-notified rather than throw.
/// </summary>
public class FrameworkReleaseBroadcasterTest
{
    // ── pure: subscriber normalization ───────────────────────────────────────

    [Fact]
    public void NormalizeSubscribers_TrimsStripsDedupesAndDropsInvalid()
    {
        var repos = FrameworkReleaseBroadcaster.NormalizeSubscribers(new[]
        {
            "  Systemorph/MeshWeaver.Plugins  ",             // trimmed
            "https://github.com/Systemorph/MeshWeaver.Education.git", // prefix + .git stripped
            "systemorph/meshweaver.plugins",                 // case-insensitive dup of the first
            "",                                              // blank dropped
            "not-a-repo",                                    // no slash → dropped
            "too/many/slashes",                              // dropped
            "Systemorph/MeshWeaver.Reinsurance",
        });

        Assert.Equal(
            new[] { "Systemorph/MeshWeaver.Plugins", "Systemorph/MeshWeaver.Education", "Systemorph/MeshWeaver.Reinsurance" },
            repos.ToArray());
    }

    [Fact]
    public void NormalizeSubscribers_NullIsEmpty() =>
        Assert.Empty(FrameworkReleaseBroadcaster.NormalizeSubscribers(null));

    // ── pure: dispatch request shape ─────────────────────────────────────────

    [Fact]
    public void BuildDispatch_TargetsTheDispatchesEndpoint_WithEventAndPayload()
    {
        var (url, body) = FrameworkReleaseBroadcaster.BuildDispatch(
            "https://api.github.com/", "Systemorph/MeshWeaver.Plugins",
            FrameworkReleaseBroadcaster.DefaultEventType, "3.0.0-rc8.ci.5083");

        Assert.Equal("https://api.github.com/repos/Systemorph/MeshWeaver.Plugins/dispatches", url);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("meshweaver-framework-released", doc.RootElement.GetProperty("event_type").GetString());
        var payload = doc.RootElement.GetProperty("client_payload");
        Assert.Equal("3.0.0-rc8.ci.5083", payload.GetProperty("version").GetString());
        Assert.Equal("memex", payload.GetProperty("source").GetString());
    }

    // ── behavior: unconfigured App is inert, not fatal ───────────────────────

    [Fact]
    public async Task Broadcast_WhenAppNotConfigured_ReportsEverySubscriberUnnotified()
    {
        var broadcaster = new FrameworkReleaseBroadcaster(
            NewTokenService(new GitHubAppOptions()),            // no client id / key ⇒ not configured
            new IoPoolRegistry(),
            Options.Create(new GitHubAppOptions()),
            Options.Create(new FrameworkBroadcastOptions()));
        Assert.False(broadcaster.IsConfigured);

        var outcome = await broadcaster
            .Broadcast(new[] { "Systemorph/A", "Systemorph/B" })
            .Timeout(TimeSpan.FromSeconds(5)).ToTask();

        Assert.Equal(0, outcome.Succeeded);
        Assert.Equal(2, outcome.Failed);
        Assert.All(outcome.Results, r => Assert.Contains("not configured", r.Error, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Broadcast_NoSubscribers_IsANoOp()
    {
        var (pem, rsa) = NewKey();
        using var _ = rsa;
        var broadcaster = new FrameworkReleaseBroadcaster(
            NewTokenService(new GitHubAppOptions { ClientId = "Iv23liTest", PrivateKey = pem, InstallationId = 1 }),
            new IoPoolRegistry(),
            Options.Create(new GitHubAppOptions()),
            Options.Create(new FrameworkBroadcastOptions()));

        var outcome = await broadcaster.Broadcast(Array.Empty<string>())
            .Timeout(TimeSpan.FromSeconds(5)).ToTask();

        Assert.Empty(outcome.Results);
    }

    // ── behavior: per-repo isolation — one failure does not abort the others ──

    [Fact]
    public async Task Broadcast_IsolatesPerRepoFailures_AndDispatchesTheRest()
    {
        var (pem, rsa) = NewKey();
        using var _ = rsa;
        var appOptions = new GitHubAppOptions { ClientId = "Iv23liTest", PrivateKey = pem, InstallationId = 42 };

        // Token endpoint → a valid installation token. Dispatches: repo "ok" → 204,
        // repo "gone" → 404, repo "boom" → the handler throws (network-class failure).
        var dispatched = new List<string>();
        var tokenHandler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)
                ? Json(HttpStatusCode.Created,
                    $"{{\"token\":\"ghs_test\",\"expires_at\":\"{DateTimeOffset.UtcNow.AddMinutes(50):o}\"}}")
                : throw new InvalidOperationException($"unexpected token call {req.RequestUri}"));
        var dispatchHandler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;      // /repos/{owner}/{name}/dispatches
            dispatched.Add(path);
            if (path.Contains("/boom/", StringComparison.Ordinal))
                throw new HttpRequestException("connection reset");
            return new HttpResponseMessage(
                path.Contains("/gone/", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.NoContent);
        });

        var broadcaster = new FrameworkReleaseBroadcaster(
            new GitHubAppTokenService(new IoPoolRegistry(), Options.Create(appOptions),
                httpClient: new HttpClient(tokenHandler)),
            new IoPoolRegistry(),
            Options.Create(appOptions),
            Options.Create(new FrameworkBroadcastOptions()),
            httpClient: new HttpClient(dispatchHandler));

        var outcome = await broadcaster
            .Broadcast(new[] { "Systemorph/ok", "Systemorph/gone", "Systemorph/boom" }, "3.0.0-rc8.ci.5083")
            .Timeout(TimeSpan.FromSeconds(10)).ToTask();

        // All three were attempted despite the middle two failing.
        Assert.Equal(3, outcome.Results.Length);
        Assert.Equal(3, dispatched.Count);
        Assert.Equal(1, outcome.Succeeded);
        Assert.Equal(2, outcome.Failed);

        Assert.True(outcome.Results.Single(r => r.Repo == "Systemorph/ok").Ok);
        Assert.Contains("404", outcome.Results.Single(r => r.Repo == "Systemorph/gone").Error);
        Assert.Contains("connection reset", outcome.Results.Single(r => r.Repo == "Systemorph/boom").Error);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static GitHubAppTokenService NewTokenService(GitHubAppOptions options) =>
        new(new IoPoolRegistry(), Options.Create(options));

    private static (string Pem, RSA Rsa) NewKey()
    {
        var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
