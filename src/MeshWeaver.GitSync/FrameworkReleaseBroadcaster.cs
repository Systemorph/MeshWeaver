using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.GitSync;

/// <summary>
/// Broadcasts the "the platform released a new framework version" fact to the node-repo satellites
/// (Plugins, Education, Reinsurance, SocialMedia, …) as a GitHub <c>repository_dispatch</c>, so
/// each rebuilds its pre-baked bundles against the new framework <b>promptly</b> instead of waiting
/// up to a schedule interval to discover it by polling.
///
/// <para>🚨 <b>memex is the broadcast hub — this reverses the old "pull, not push" decision, and
/// resolves both objections that decision named</b> (<c>main-cd.yml</c>, the NOTIFY DEPENDENT
/// REPOS block). The old <c>notify-dependents</c> job was GitHub→GitHub and needed two things the
/// platform release path should not hold: a hand-maintained subscriber LIST and a CREDENTIAL with
/// write access to every satellite. Routing through memex removes both from the release path — the
/// platform only signs a POST to memex (the existing <c>notify-platform-update</c> pattern), and
/// memex does the fan-out with the GitHub App it <i>already</i> holds for GitSync and a subscriber
/// registry it <i>already</i> owns (the Hosting fleet). The pull/schedule remains the fallback, so
/// a lost dispatch costs at most one delayed rebake wave — REPORTER-CLASS, never a hard failure.</para>
///
/// <para>Reactive end to end: the App-token mint and each POST run inside <see cref="IIoPool"/>
/// (<see cref="GitHubAppTokenService.GetInstallationToken"/> and <see cref="IoPoolExtensions.Run{T}"/>),
/// so no <c>async</c>/<c>Task</c> escapes a public signature. Dispatches run one at a time and are
/// isolated: a failure to one repo is captured as a <see cref="RepoDispatch"/> and never aborts the
/// others or throws to the caller.</para>
/// </summary>
public sealed class FrameworkReleaseBroadcaster
{
    /// <summary>The <c>repository_dispatch</c> event type every satellite subscribes to.</summary>
    public const string DefaultEventType = "meshweaver-framework-released";

    private readonly GitHubAppTokenService tokens;
    private readonly IoPoolRegistry ioPools;
    private readonly GitHubAppOptions appOptions;
    private readonly FrameworkBroadcastOptions options;
    private readonly IConfiguration? configuration;
    private readonly HttpClient http;
    private readonly ILogger? logger;

    /// <summary>Initializes the broadcaster.</summary>
    /// <param name="tokens">Mints the GitHub App installation token the dispatches authenticate with.</param>
    /// <param name="ioPools">Registry the HTTP I/O pool is resolved from, so every POST runs off the hub.</param>
    /// <param name="appOptions">The App options — reused only for <see cref="GitHubAppOptions.ApiBaseUrl"/> (GHES parity).</param>
    /// <param name="options">The bound <see cref="FrameworkBroadcastOptions"/> (the subscriber list + event type).</param>
    /// <param name="configuration">The instance configuration — read ONLY to tell a mesh that is not
    /// the control instance (an empty subscriber list is correct there) from the control instance
    /// with nobody registered (an empty list there means the wave silently never runs). Optional;
    /// absent, an empty list is reported as the former.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional <see cref="HttpClient"/> to reuse; a default one is created when null.</param>
    public FrameworkReleaseBroadcaster(
        GitHubAppTokenService tokens,
        IoPoolRegistry ioPools,
        IOptions<GitHubAppOptions> appOptions,
        IOptions<FrameworkBroadcastOptions> options,
        IConfiguration? configuration = null,
        ILogger<FrameworkReleaseBroadcaster>? logger = null,
        HttpClient? httpClient = null)
    {
        this.tokens = tokens;
        this.ioPools = ioPools;
        this.appOptions = appOptions.Value;
        this.options = options.Value;
        this.configuration = configuration;
        this.logger = logger;
        http = httpClient ?? new HttpClient();
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshWeaver-GitSync");
    }

    /// <summary>True when the App identity is usable — without it the broadcaster is inert.</summary>
    public bool IsConfigured => tokens.IsConfigured;

    private IIoPool HttpPool => ioPools.Get(IoPoolNames.Http);

    /// <summary>
    /// Broadcasts the release to the subscriber set the caller derived from the mesh — the Hosting
    /// module's <c>PlatformBuildInboxWatcher</c> passes the registry-source repositories of every
    /// <c>Hosting/Deployment</c> record on this instance (there is no configured list; the former
    /// <c>BroadcastToConfigured</c> config seam was retired 2026-09-03, see
    /// <see cref="FrameworkBroadcastOptions"/>). Mints one installation token, then POSTs a
    /// <c>repository_dispatch</c> to each
    /// repo in turn. Never throws: an unconfigured App, an empty set, or a per-repo failure all
    /// resolve to a <see cref="BroadcastOutcome"/> the caller can log. Cold — nothing runs until
    /// subscribed.
    /// </summary>
    /// <param name="subscribers">Repositories as <c>owner/name</c> (normalized by <see cref="NormalizeSubscribers"/>).</param>
    /// <param name="version">The released version, carried in the dispatch <c>client_payload</c> for the logs; optional.</param>
    /// <param name="eventType">Override the dispatch event type; defaults to <see cref="FrameworkBroadcastOptions.EventType"/>.</param>
    /// <param name="payload">Further <c>client_payload</c> fields the registered record carries — for a
    /// bundle publication (<c>meshweaver-upstream-published</c>) the publishing <c>source</c>, its
    /// <c>identity</c>, <c>image</c> and <c>platform_image</c>. Merged over the defaults
    /// (<c>version</c>, <c>source = "memex"</c>), so a caller may name the real source.</param>
    public IObservable<BroadcastOutcome> Broadcast(
        IEnumerable<string>? subscribers, string? version = null, string? eventType = null,
        IReadOnlyDictionary<string, string>? payload = null)
    {
        var repos = NormalizeSubscribers(subscribers);
        var evt = string.IsNullOrWhiteSpace(eventType) ? options.EventType : eventType!.Trim();

        if (!IsConfigured)
        {
            logger?.LogWarning(
                "Framework-release broadcast skipped: the GitHub App is not configured "
                + "(GitHub:App:ClientId + GitHub:App:PrivateKey). {Count} subscriber(s) not notified — "
                + "they will rebake on their own schedule.", repos.Length);
            return Observable.Return(BroadcastOutcome.NotConfigured(repos));
        }
        if (repos.Length == 0)
        {
            // 🚨 #2235 — THE STATE THAT LOOKED LIKE SUCCESS FOR THREE DAYS. "Nobody to dispatch
            // to" has two causes that are IDENTICAL in the outcome (0 dispatched, 0 failed), and
            // telling them apart is the whole defect: on a mesh that does not receive
            // platform-build deliveries an empty list is the correct, permanent state; on the
            // CONTROL instance it is a misconfiguration whose only symptom is a wave that never
            // runs — the release event verifies, the broadcast "succeeds" with zero dispatches,
            // and nothing anywhere is red. This is the one place in the chain that can see both
            // facts (the subscriber list AND whether this instance accepts release events), so
            // it is the one place that can say WHICH happened. The level difference is the
            // point, not verbosity tuning: it fires once per release, never in a loop.
            if (AcceptsPlatformBuildDeliveries())
                logger?.LogWarning(
                    "Framework-release broadcast: this instance ACCEPTS platform-build deliveries "
                    + "({Target} is allowlisted in {TargetsKey}) but has NO subscribers, so the "
                    + "release wave dispatches to nobody and every node repo falls back to its "
                    + "schedule poll. The subscriber set is DATA IN THE MESH: every repository a "
                    + "Hosting/Deployment record on this instance serves as a registry source "
                    + "(pluginRepos[].isRegistrySource) — no record here names one.",
                    FrameworkBroadcastOptions.PlatformBuildsTarget,
                    WebhookInbox.TargetsConfigSection);
            else
                logger?.LogInformation(
                    "Framework-release broadcast: no subscribers configured and this instance does "
                    + "not receive platform-build deliveries — nothing to dispatch (satellites "
                    + "rebake on their own schedule).");
            return Observable.Return(new BroadcastOutcome([]));
        }

        return tokens.GetInstallationToken().SelectMany(token =>
                Observable
                    .Concat(repos.Select(repo => DispatchOne(token, repo, evt, version, payload)))
                    .ToList()
                    .Select(results =>
                    {
                        var outcome = new BroadcastOutcome([.. results]);
                        logger?.LogInformation(
                            "Framework-release broadcast ({Event}, version {Version}): {Ok}/{Total} dispatched{Failed}.",
                            evt, version ?? "(none)", outcome.Succeeded, repos.Length,
                            outcome.Failed == 0 ? "" : $" — {outcome.Failed} failed: {outcome.FailureSummary}");
                        return outcome;
                    }))
            // A failure BEFORE the per-repo isolation (token mint) is still reporter-class: log it
            // and report every subscriber as un-notified rather than throwing to the caller.
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Framework-release broadcast could not mint a GitHub App token — {Count} subscriber(s) "
                    + "not notified; they will rebake on their own schedule.", repos.Length);
                return Observable.Return(new BroadcastOutcome(
                    [.. repos.Select(r => new RepoDispatch(r, false, $"token: {ex.Message}"))]));
            });
    }

    /// <summary>
    /// Whether THIS instance is the control instance — i.e. its webhook inbox allowlist accepts
    /// <see cref="FrameworkBroadcastOptions.PlatformBuildsTarget"/>, which is what makes it the
    /// mesh that receives release events and therefore the one that must have subscribers.
    /// </summary>
    private bool AcceptsPlatformBuildDeliveries() =>
        configuration?.GetSection(WebhookInbox.TargetsConfigSection).GetChildren()
            .Any(c => string.Equals(
                WebhookInbox.NormalizeTarget(c.Value),
                FrameworkBroadcastOptions.PlatformBuildsTarget,
                StringComparison.OrdinalIgnoreCase)) == true;

    // One repo's dispatch, already isolated: a non-2xx status OR a thrown exception both become a
    // RepoDispatch(ok:false), so Concat never short-circuits on the first failure. Invoke, not
    // Run: Run is the eager promise-cache shape — it starts the POST at call time, decoupled from
    // the subscription, so an unsubscribe (timeout, shutdown) would leave the dispatch running and
    // holding an HTTP pool slot. Invoke stays cold and propagates cancellation into the leaf's ct.
    private IObservable<RepoDispatch> DispatchOne(
        string token, string repo, string eventType, string? version, IReadOnlyDictionary<string, string>? payload) =>
        HttpPool.Invoke(ct => PostDispatchAsync(token, repo, eventType, version, payload, ct))
            .Catch((Exception ex) => Observable.Return(new RepoDispatch(repo, false, ex.Message)));

    private async Task<RepoDispatch> PostDispatchAsync(
        string token, string repo, string eventType, string? version,
        IReadOnlyDictionary<string, string>? payload, CancellationToken ct)
    {
        var (url, body) = BuildDispatch(appOptions.ApiBaseUrl, repo, eventType, version, payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)   // repository_dispatch answers 204 No Content
        {
            logger?.LogInformation("Dispatched {Event} to {Repo}.", eventType, repo);
            return new RepoDispatch(repo, true, null);
        }
        var detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new RepoDispatch(repo, false, $"{(int)resp.StatusCode}: {Truncate(detail)}");
    }

    // ── pure helpers (offline-testable) ──────────────────────────────────────

    /// <summary>
    /// Normalizes a raw subscriber list to distinct <c>owner/name</c> repos: trims, strips a
    /// leading <c>https://github.com/</c> and a trailing <c>.git</c>, drops anything that is not
    /// exactly one <c>owner/name</c> pair, and de-duplicates case-insensitively while preserving
    /// first-seen order. Pure. Null → empty.
    /// </summary>
    internal static ImmutableArray<string> NormalizeSubscribers(IEnumerable<string>? raw)
    {
        if (raw is null)
            return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var repo = entry.Trim();
            const string prefix = "https://github.com/";
            if (repo.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                repo = repo[prefix.Length..];
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                repo = repo[..^4];
            repo = repo.Trim('/');
            var slash = repo.IndexOf('/');
            if (slash <= 0 || slash != repo.LastIndexOf('/') || slash == repo.Length - 1)
                continue;   // not exactly owner/name
            if (seen.Add(repo))
                result.Add(repo);
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Builds the <c>repository_dispatch</c> request for one repo: the endpoint URL and the JSON
    /// body carrying the <c>event_type</c> and a <c>client_payload</c> (the version + a
    /// <c>source</c> breadcrumb). Pure. Internal for the offline shape test.
    /// </summary>
    internal static (string Url, string Body) BuildDispatch(
        string apiBaseUrl, string repo, string eventType, string? version,
        IReadOnlyDictionary<string, string>? payload = null)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/repos/{repo}/dispatches";
        var fields = ImmutableSortedDictionary<string, string?>.Empty
            .Add("version", version)
            .Add("source", "memex");
        if (payload is not null)
            foreach (var (key, value) in payload)
                fields = fields.SetItem(key, value);
        var body = JsonSerializer.Serialize(new { event_type = eventType, client_payload = fields });
        return (url, body);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}

/// <summary>The result of dispatching to ONE subscriber repository.</summary>
/// <param name="Repo">The <c>owner/name</c> the dispatch targeted.</param>
/// <param name="Ok">Whether GitHub accepted the dispatch (HTTP 2xx).</param>
/// <param name="Error">The failure detail when <paramref name="Ok"/> is false; otherwise null.</param>
public sealed record RepoDispatch(string Repo, bool Ok, string? Error);

/// <summary>
/// The outcome of one broadcast — the per-repo results, plus convenience roll-ups. Reporter-class:
/// even a total failure is an outcome to read, never an exception.
/// </summary>
/// <param name="Results">One entry per subscriber the broadcast attempted.</param>
public sealed record BroadcastOutcome(ImmutableArray<RepoDispatch> Results)
{
    /// <summary>How many dispatches GitHub accepted.</summary>
    public int Succeeded => Results.Count(r => r.Ok);

    /// <summary>How many dispatches failed (or could not be attempted).</summary>
    public int Failed => Results.Count(r => !r.Ok);

    /// <summary>A one-line "repo (error); …" summary of the failures, for a log line.</summary>
    public string FailureSummary =>
        string.Join("; ", Results.Where(r => !r.Ok).Select(r => $"{r.Repo} ({r.Error})"));

    /// <summary>The outcome when the App identity is not configured: every repo reported un-notified.</summary>
    public static BroadcastOutcome NotConfigured(ImmutableArray<string> repos) =>
        new([.. repos.Select(r => new RepoDispatch(r, false, "GitHub App not configured"))]);
}
