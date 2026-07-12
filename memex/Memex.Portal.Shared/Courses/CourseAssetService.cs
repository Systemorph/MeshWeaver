using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memex.Portal.Shared.Courses;

/// <summary>
/// Resolves a course asset's short-lived, tokenized <c>download_url</c> through the GitHub
/// contents API (<c>GET /repos/{owner}/{repo}/contents/{path}?ref={branch}</c>), authenticated
/// as the GitHub App installation (<see cref="GitHubAppTokenService"/>) so private course repos
/// resolve without any per-user credential. The endpoint 302-redirects to the returned URL —
/// the bytes are never proxied through the portal.
///
/// <para>🚨 Reactive end-to-end — the public surface is <see cref="IObservable{T}"/>; every HTTP
/// leaf runs inside the <see cref="IoPoolNames.Http"/> pool (<c>Doc/Architecture/ControlledIoPooling.md</c>).
/// Results are promise-cached per repo file for <see cref="DefaultCacheTtl"/> (30&#160;s — GitHub's tokenized
/// URLs are short-lived, so the cache must stay well inside their validity): concurrent requests
/// for the same file share one round-trip, a failed fetch invalidates itself so the next caller
/// retries. The cache is an instance field on this mesh-scoped singleton — never static
/// (<c>NoStaticState.md</c>).</para>
/// </summary>
public sealed class CourseAssetService
{
    /// <summary>How long a resolved <c>download_url</c> is reused before re-fetching.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Opportunistic prune threshold — above this entry count, stale entries are swept on access.</summary>
    private const int PruneThreshold = 512;

    private readonly HttpClient http;
    private readonly IoPoolRegistry ioPools;
    private readonly GitHubAppTokenService? appTokens;
    private readonly GitHubAppOptions options;
    private readonly ILogger? logger;
    private readonly TimeSpan cacheTtl;

    // Promise-cache (instance, never static): one eagerly-connected replay per repo file,
    // replaced when older than the TTL, removed on error so failures don't stick.
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new();

    private sealed record CacheEntry(DateTimeOffset FetchedAt, IObservable<string?> DownloadUrl);

    /// <summary>Initializes the service.</summary>
    /// <param name="ioPools">Registry the HTTP I/O pool is resolved from so every HTTP leaf runs off the hub.</param>
    /// <param name="options">The bound <see cref="GitHubAppOptions"/> (for <see cref="GitHubAppOptions.ApiBaseUrl"/>).</param>
    /// <param name="appTokens">The App installation-token service; null / unconfigured → unauthenticated
    /// requests (public repositories still resolve).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional <see cref="HttpClient"/> to reuse (tests); a default one is created when null.</param>
    /// <param name="cacheTtl">Optional cache TTL override (tests); defaults to <see cref="DefaultCacheTtl"/>.</param>
    public CourseAssetService(
        IoPoolRegistry ioPools,
        IOptions<GitHubAppOptions> options,
        GitHubAppTokenService? appTokens = null,
        ILogger<CourseAssetService>? logger = null,
        HttpClient? httpClient = null,
        TimeSpan? cacheTtl = null)
    {
        this.ioPools = ioPools;
        this.options = options.Value;
        this.appTokens = appTokens;
        this.logger = logger;
        this.cacheTtl = cacheTtl ?? DefaultCacheTtl;
        http = httpClient ?? new HttpClient();
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshWeaver-CourseAssets");
    }

    private IIoPool Http => ioPools.Get(IoPoolNames.Http);

    /// <summary>
    /// The tokenized <c>download_url</c> for one repository file — cached per file for the TTL,
    /// shared across concurrent callers. Emits <c>null</c> when the file (or the repository ref)
    /// does not exist; errors propagate (and self-invalidate the cache entry).
    /// </summary>
    /// <param name="repositoryUrl">The repo URL from the Space's <see cref="GitHubSyncConfig.RepositoryUrl"/>.</param>
    /// <param name="branch">The branch from <see cref="GitHubSyncConfig.Branch"/> (null/blank → <c>main</c>).</param>
    /// <param name="repoFilePath">The file path inside the repository.</param>
    /// <returns>A single-emission observable with the download URL, or null when not found.</returns>
    public IObservable<string?> GetDownloadUrl(string repositoryUrl, string? branch, string repoFilePath)
    {
        var (owner, repo) = OctokitGitHubRepoClient.ParseRepoUrl(repositoryUrl);
        var reference = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        var key = $"{owner}/{repo}@{reference}:{repoFilePath}";
        var now = DateTimeOffset.UtcNow;

        PruneIfOversized(now);

        var entry = cache.AddOrUpdate(
            key,
            _ => new CacheEntry(now, CreateFetch(owner, repo, reference, repoFilePath, key)),
            (_, existing) => now - existing.FetchedAt < cacheTtl
                ? existing
                : new CacheEntry(now, CreateFetch(owner, repo, reference, repoFilePath, key)));
        return entry.DownloadUrl;
    }

    /// <summary>
    /// One eager fetch: App token (when configured) → contents API on the HTTP pool →
    /// replayed to every subscriber of this cache entry. A failure removes the entry
    /// (never cache a failure) and is replayed as the error to current subscribers.
    /// </summary>
    private IObservable<string?> CreateFetch(
        string owner, string repo, string reference, string repoFilePath, string cacheKey)
    {
        var token = appTokens is { IsConfigured: true }
            ? appTokens.GetInstallationToken().Select(t => (string?)t)
            : Observable.Return<string?>(null);

        var fetch = token
            .Take(1)
            .SelectMany(t => Http.Run(ct => FetchDownloadUrlAsync(owner, repo, reference, repoFilePath, t, ct)))
            .Catch((Exception ex) =>
            {
                cache.TryRemove(cacheKey, out _); // never cache a failure — the next caller retries
                return Observable.Throw<string?>(ex);
            })
            .Replay(1);
        // Eager promise: kick the (self-terminating, single-emission) fetch off once; every
        // subscriber replays its value or error — same shape as GitHubAppTokenService's cache.
        fetch.Connect();
        return fetch;
    }

    /// <summary>Sweeps TTL-expired entries once the cache grows beyond <see cref="PruneThreshold"/>.</summary>
    private void PruneIfOversized(DateTimeOffset now)
    {
        if (cache.Count <= PruneThreshold)
            return;
        foreach (var (key, entry) in cache)
            if (now - entry.FetchedAt >= cacheTtl)
                cache.TryRemove(key, out _);
    }

    // ── HTTP leaf (runs inside the I/O pool) ─────────────────────────────────

    private async Task<string?> FetchDownloadUrlAsync(
        string owner, string repo, string reference, string repoFilePath, string? token, CancellationToken ct)
    {
        var escapedPath = string.Join('/', repoFilePath.Split('/').Select(Uri.EscapeDataString));
        var url = $"{options.ApiBaseUrl.TrimEnd('/')}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}"
                  + $"/contents/{escapedPath}?ref={Uri.EscapeDataString(reference)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // The object media type keeps download_url present for 1-100 MB files, where the
        // default media type errors (github.com/rest/repos/contents — course videos are large).
        req.Headers.Accept.ParseAdd("application/vnd.github.object+json");
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub contents request for {owner}/{repo}/{repoFilePath}@{reference} failed "
                + $"({(int)resp.StatusCode}): {Truncate(json)}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // A directory answers with an array (or type != "file") — not a servable asset.
        if (root.ValueKind != JsonValueKind.Object
            || (root.TryGetProperty("type", out var type) && type.GetString() != "file"))
            return null;
        var downloadUrl = root.TryGetProperty("download_url", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        if (downloadUrl is null)
            logger?.LogWarning("GitHub contents response for {Owner}/{Repo}/{Path}@{Ref} carried no download_url",
                owner, repo, repoFilePath, reference);
        return downloadUrl;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
