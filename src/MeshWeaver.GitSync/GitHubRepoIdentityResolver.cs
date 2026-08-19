using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// Answers "which repository is this url ACTUALLY pointing at right now" — GitHub's own
/// <c>full_name</c> for the repository, which FOLLOWS a rename — and remembers the answer for a
/// bounded time so asking is never a per-delivery network call.
///
/// <para>🚨 <b>Why a stored url stops matching.</b> A GitHub repository can be renamed and every
/// stored url keeps working: the old name 301-redirects, so git, <c>gh</c>, the REST API and the
/// sync itself all carry on. What breaks is EQUALITY. A webhook payload always carries the
/// repository's CURRENT name, so a config that stored the old one can never string-match it again —
/// <c>education</c> vs <c>MeshWeaver.Education</c> is not a casing difference any comparer can
/// bridge. That is issue #1856: ten Spaces stopped importing for four days while every delivery
/// reported success.</para>
///
/// <para><b>This is a FALLBACK, never the hot path.</b> The caller compares stored strings first
/// (free, and correct for every repository that was never renamed) and only asks here when NOTHING
/// matched — the rare case that is either a rename or a genuinely foreign repository. The answer is
/// then cached, so even a hook that matches nothing at all costs one lookup per repository per
/// <see cref="Ttl"/>, not one per delivery.</para>
///
/// <para><b>The cache EXPIRES, deliberately.</b> A canonical name is a fact with a shelf life: a
/// repository can be renamed AGAIN, and a resolution that failed once (a token that could not see a
/// private repository, a transport blip) must not become the permanent answer. Every entry — a
/// resolved name just as much as an "unknown" — is therefore re-resolved once it is older than
/// <see cref="Ttl"/>. The timestamp lives INSIDE the cached value, so it can never disagree with the
/// answer it stamps.</para>
///
/// <para>Reactive end to end: the GitHub read is an <see cref="IIoPool"/>-bridged leaf inside
/// <see cref="IGitHubRepoClient.GetCanonicalRepository"/>; nothing here awaits anything.</para>
/// </summary>
/// <param name="repoClient">The GitHub seam that performs the lookup.</param>
/// <param name="credentials">Per-user GitHub credentials — the identity that already syncs the repository.</param>
/// <param name="appTokens">The GitHub App installation identity, used when the user has no credential.</param>
/// <param name="timeProvider">Clock behind the TTL (injected so expiry is testable without waiting).</param>
/// <param name="logger">Optional logger.</param>
public sealed class GitHubRepoIdentityResolver(
    IGitHubRepoClient repoClient,
    GitHubCredentialService credentials,
    GitHubAppTokenService? appTokens = null,
    TimeProvider? timeProvider = null,
    ILogger<GitHubRepoIdentityResolver>? logger = null)
{
    /// <summary>
    /// How long a resolved identity is trusted before it is looked up again. Long enough that a
    /// chatty hook costs one call an hour per repository, short enough that a second rename — or a
    /// credential that has since been connected — heals on its own without a restart.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    /// <summary>One cached answer plus WHEN it was established — stored together so the TTL check
    /// and the value it governs are always the same fact.</summary>
    private sealed record Resolution(DateTimeOffset At, RepoIdentity? Identity);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly PromiseCache<string, Resolution> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many GitHub lookups this resolver has actually performed. Diagnostics only — a test uses
    /// it to prove the cache serves repeat callers without a second call, and that an EXPIRED entry
    /// costs a fresh one.
    /// </summary>
    public int LookupCount => lookups;

    private int lookups;

    /// <summary>
    /// The canonical identity of <paramref name="repositoryUrl"/>, or <see langword="null"/> when it
    /// cannot be established (unreachable with the identities available, or the url is unparseable).
    ///
    /// <para><paramref name="userId"/> is the identity to read with — the sync config's creator, i.e.
    /// the account that already syncs this repository and can therefore see it even when it is
    /// private. It falls back to the GitHub App installation, then to anonymous. Only the FIRST
    /// caller within a TTL window supplies it: the answer is a property of the repository, not of the
    /// asker, so it is cached per repository. A resolution that failed under a weak identity is
    /// retried after the TTL, which is when a better one gets its turn.</para>
    /// </summary>
    /// <param name="repositoryUrl">The url as stored (any name the repository has ever had).</param>
    /// <param name="userId">Identity to read with; null/empty = App, then anonymous.</param>
    /// <returns>The canonical identity, or null when unknown. Never faults.</returns>
    public IObservable<RepoIdentity?> Resolve(string repositoryUrl, string? userId)
    {
        if (Parse(repositoryUrl) is not { } stored)
            return Observable.Return<RepoIdentity?>(null);

        var key = stored.ToString();
        return Observable.Defer(() => cache.GetOrAdd(key, _ => Lookup(repositoryUrl, userId)))
            .SelectMany(cached => clock.GetUtcNow() - cached.At < Ttl
                ? Observable.Return(cached.Identity)
                // Expired. Forget it and build a genuinely NEW attempt. Two callers can race here
                // and both invalidate; the loser's cost is one duplicate lookup, which is the
                // acceptable outcome — a permanently stale name is not.
                : Observable.Defer(() =>
                {
                    cache.Invalidate(key);
                    return cache.GetOrAdd(key, _ => Lookup(repositoryUrl, userId));
                }).Select(fresh => fresh.Identity))
            .Take(1);
    }

    /// <summary>One real GitHub lookup, stamped with the time it settled and shared by every
    /// concurrent caller (<c>Replay(1).AutoConnect(1)</c> — the promise runs once).</summary>
    private IObservable<Resolution> Lookup(string repositoryUrl, string? userId) =>
        ResolveToken(userId)
            .Do(_ => Interlocked.Increment(ref lookups))
            .SelectMany(token => repoClient.GetCanonicalRepository(repositoryUrl, token))
            .Select(id => id is { IsComplete: true } ? id : null)
            .Catch((Exception ex) =>
            {
                // 🚨 Not a swallow: this resolution is a diagnostic FALLBACK, and its failure is
                // reported by the caller's zero-match warning, which names both sides. Faulting here
                // would turn "we could not confirm a rename" into a failed webhook delivery, and
                // GitHub would redeliver it — a retry storm over a lookup that will fail
                // identically. The unknown is cached for one TTL, then retried.
                logger?.LogInformation(ex,
                    "Could not resolve the canonical identity of {Repo} (as {User}). A rename of this "
                    + "repository cannot be detected until this succeeds.",
                    repositoryUrl, string.IsNullOrEmpty(userId) ? "(app/anonymous)" : userId);
                return Observable.Return<RepoIdentity?>(null);
            })
            .Select(id => new Resolution(clock.GetUtcNow(), id))
            .Replay(1)
            .AutoConnect(1);

    /// <summary>
    /// Parses a stored repository url to its <c>owner/repo</c> identity, or null when it cannot be
    /// parsed at all (an empty or malformed configuration value).
    /// </summary>
    /// <param name="repositoryUrl">The url to parse.</param>
    /// <returns>The identity, or null.</returns>
    public static RepoIdentity? Parse(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return null;
        try
        {
            var (owner, repo) = OctokitGitHubRepoClient.ParseRepoUrl(repositoryUrl);
            var identity = new RepoIdentity(owner, repo);
            return identity.IsComplete ? identity : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The token to read with: the user's connected credential, else the GitHub App installation,
    /// else anonymous (empty). Mirrors <c>GitHubSyncService.ResolveAuth</c> except that "no identity
    /// at all" is not an error here — an anonymous read still resolves a PUBLIC repository, and a
    /// private one degrades to the loud zero-match the caller already reports.
    /// </summary>
    private IObservable<string> ResolveToken(string? userId)
    {
        var user = string.IsNullOrEmpty(userId) || userId == WellKnownUsers.System ? null : userId;
        var fromUser = user is null
            ? Observable.Return<GitHubCredential?>(null)
            : credentials.Get(user).Take(1);
        return fromUser.SelectMany(cred =>
            cred?.AccessToken is { Length: > 0 } token
                ? Observable.Return(token)
                : appTokens is { IsConfigured: true }
                    ? appTokens.GetInstallationToken()
                    : Observable.Return(string.Empty));
    }
}
