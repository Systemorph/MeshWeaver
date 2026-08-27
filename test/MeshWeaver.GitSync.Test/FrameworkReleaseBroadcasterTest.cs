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
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        // ONE options instance for the token service AND the broadcaster — they are the same
        // configuration in production (GitHubSyncConfiguration resolves the same IOptions), and a
        // split here would let the broadcaster silently build dispatch URLs against the default
        // base while the token service uses the configured one (the GHES case).
        var appOptions = new GitHubAppOptions { ClientId = "Iv23liTest", PrivateKey = pem, InstallationId = 1 };
        var broadcaster = new FrameworkReleaseBroadcaster(
            NewTokenService(appOptions),
            new IoPoolRegistry(),
            Options.Create(appOptions),
            Options.Create(new FrameworkBroadcastOptions()));

        var outcome = await broadcaster.Broadcast(Array.Empty<string>())
            .Timeout(TimeSpan.FromSeconds(5)).ToTask();

        Assert.Empty(outcome.Results);
    }

    // ── behavior: the CONFIG SEAM — the only source of the subscriber set that exists ────────

    /// <summary>
    /// 🚨 #2235: <see cref="FrameworkReleaseBroadcaster.BroadcastToConfigured"/> had NO test and NO
    /// caller, and the key it reads (<c>FrameworkBroadcast:Subscribers</c>) was rendered by no
    /// chart in either repo — so the one path that turns a release into a wave was unexercised at
    /// every level at once. This pins that a configured subscriber list actually reaches GitHub.
    /// </summary>
    [Fact]
    public async Task BroadcastToConfigured_DispatchesToEverySubscriberInConfiguration()
    {
        var (pem, rsa) = NewKey();
        using var _ = rsa;
        var appOptions = new GitHubAppOptions { ClientId = "Iv23liTest", PrivateKey = pem, InstallationId = 7 };

        var dispatched = new List<Uri>();
        var broadcaster = new FrameworkReleaseBroadcaster(
            new GitHubAppTokenService(new IoPoolRegistry(), Options.Create(appOptions),
                httpClient: new HttpClient(TokenHandler())),
            new IoPoolRegistry(),
            Options.Create(appOptions),
            // Exactly what FrameworkBroadcast__Subscribers__0/__1 binds to.
            Options.Create(new FrameworkBroadcastOptions
            {
                Subscribers = ["Systemorph/MeshWeaver.Plugins", "Systemorph/MeshWeaver.Education"],
            }),
            httpClient: new HttpClient(new StubHandler(req =>
            {
                dispatched.Add(req.RequestUri!);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            })));

        var outcome = await broadcaster.BroadcastToConfigured("3.0.0-rc8.ci.5083")
            .Timeout(TimeSpan.FromSeconds(10)).ToTask();

        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(
            new[]
            {
                "https://api.github.com/repos/Systemorph/MeshWeaver.Plugins/dispatches",
                "https://api.github.com/repos/Systemorph/MeshWeaver.Education/dispatches",
            },
            dispatched.Select(u => u.AbsoluteUri).ToArray());
    }

    /// <summary>
    /// 🚨 THE STATE THAT LOOKED LIKE SUCCESS. An empty subscriber set produces the same outcome
    /// (0 dispatched, 0 failed) whether this mesh simply is not the control instance or IS the
    /// control instance with nobody registered — and only the second is a defect. The broadcaster
    /// is the one place that can see both facts, so it must say which one happened: an instance
    /// whose webhook inbox accepts platform-build deliveries and has no subscribers WARNS, naming
    /// the key to set.
    /// </summary>
    [Fact]
    public async Task Broadcast_NoSubscribers_OnAnInstanceThatReceivesReleaseEvents_Warns()
    {
        var log = new CapturingLogger();
        var broadcaster = NewInertBroadcaster(log, Configuration(
            (WebhookInbox.TargetsConfigSection + ":0", FrameworkBroadcastOptions.PlatformBuildsTarget)));

        await broadcaster.Broadcast([]).Timeout(TimeSpan.FromSeconds(5)).ToTask();

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(FrameworkBroadcastOptions.SubscribersEnvKeyPrefix, warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half, and the reason the level is a judgement rather than a blanket warning: on
    /// every mesh that does NOT receive release events an empty subscriber set is the correct,
    /// permanent state. Warning there would train people to ignore the line that matters.
    /// </summary>
    [Fact]
    public async Task Broadcast_NoSubscribers_OnAnInstanceThatDoesNot_IsInformationOnly()
    {
        var log = new CapturingLogger();
        var broadcaster = NewInertBroadcaster(log, Configuration(
            (WebhookInbox.TargetsConfigSection + ":0", "Store/Payments")));

        await broadcaster.Broadcast([]).Timeout(TimeSpan.FromSeconds(5)).ToTask();

        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information);
    }

    // ── behavior: per-repo isolation — one failure does not abort the others ──

    [Fact]
    public async Task Broadcast_IsolatesPerRepoFailures_AndDispatchesTheRest()
    {
        var (pem, rsa) = NewKey();
        using var _ = rsa;
        // A NON-default ApiBaseUrl (the GHES shape): the broadcaster must build its dispatch URLs
        // from the SAME options the token service authenticates against — a default-base URL here
        // would mean a GHES deployment dispatches into the wrong GitHub.
        var appOptions = new GitHubAppOptions
        {
            ClientId = "Iv23liTest", PrivateKey = pem, InstallationId = 42,
            ApiBaseUrl = "https://ghes.example/api/v3",
        };

        // Token endpoint → a valid installation token. Dispatches: repo "ok" → 204,
        // repo "gone" → 404, repo "boom" → the handler throws (network-class failure).
        var dispatched = new List<Uri>();
        var tokenHandler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)
                ? Json(HttpStatusCode.Created,
                    $"{{\"token\":\"ghs_test\",\"expires_at\":\"{DateTimeOffset.UtcNow.AddMinutes(50):o}\"}}")
                : throw new InvalidOperationException($"unexpected token call {req.RequestUri}"));
        var dispatchHandler = new StubHandler(req =>
        {
            dispatched.Add(req.RequestUri!);              // …/repos/{owner}/{name}/dispatches
            var path = req.RequestUri!.AbsolutePath;
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

        // Every dispatch was built against the CONFIGURED base (GHES parity), never the default.
        Assert.All(dispatched, uri => Assert.StartsWith(
            "https://ghes.example/api/v3/repos/Systemorph/", uri.AbsoluteUri, StringComparison.Ordinal));

        Assert.True(outcome.Results.Single(r => r.Repo == "Systemorph/ok").Ok);
        Assert.Contains("404", outcome.Results.Single(r => r.Repo == "Systemorph/gone").Error);
        Assert.Contains("connection reset", outcome.Results.Single(r => r.Repo == "Systemorph/boom").Error);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A broadcaster with a usable App identity whose only variable is configuration —
    /// used by the two empty-subscriber-set tests, which never reach an HTTP call.</summary>
    private static FrameworkReleaseBroadcaster NewInertBroadcaster(
        ILogger<FrameworkReleaseBroadcaster> logger, IConfiguration configuration)
    {
        var (pem, rsa) = NewKey();
        rsa.Dispose();
        var appOptions = new GitHubAppOptions { ClientId = "Iv23liTest", PrivateKey = pem, InstallationId = 1 };
        return new FrameworkReleaseBroadcaster(
            NewTokenService(appOptions), new IoPoolRegistry(), Options.Create(appOptions),
            Options.Create(new FrameworkBroadcastOptions()), configuration, logger);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static StubHandler TokenHandler() => new(req =>
        req.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Created,
                $"{{\"token\":\"ghs_test\",\"expires_at\":\"{DateTimeOffset.UtcNow.AddMinutes(50):o}\"}}")
            : throw new InvalidOperationException($"unexpected token call {req.RequestUri}"));

    /// <summary>Captures what the broadcaster reported — the level IS the assertion here.</summary>
    private sealed class CapturingLogger : ILogger<FrameworkReleaseBroadcaster>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

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
