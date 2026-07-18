using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// Applies verified GitHub <c>issues</c> / <c>issue_comment</c> webhook events to the mesh so
/// synced <see cref="GitHubIssue"/> nodes stay live without polling. The event payload already
/// carries the full issue object, so this needs <b>no OAuth token</b>: it maps the payload
/// directly onto the <c>{spacePath}/_Issue/{number}</c> node of every Space configured to sync
/// that repository, merging in the new comment on a comment event and preserving comments already
/// synced. The write runs under the system identity (an infrastructure mirror update, the same
/// identity model as the instance-sync pull and <c>StaticRepoImporter</c>).
///
/// <para><c>push</c> events keep GitSync'd Spaces CURRENT without polling: every Space whose
/// sync config targets the pushed repository + branch — and, when the config scopes a
/// subdirectory, whose subdirectory the push actually touched — gets the same headless
/// "Update to latest" the GUI button and the MCP <c>git_hub_sync</c> tool run
/// (<see cref="GitHubActivityExtensions.UpdateToLatestFromGitHub"/>, <c>force: false</c> so
/// two-way conflict resolution still protects server-side edits). The mesh writes run under
/// the system identity; the GitHub pull authenticates as the sync config's CREATOR (their
/// connected credential, or the GitHub App when they have none). Register the repo webhook
/// with the <c>Pushes</c> event next to <c>Issues</c>/<c>Issue comments</c>.</para>
///
/// <para>Pull-request events are intentionally ignored: PR state is read LIVE (delegated) and
/// never materialized, so there is no node to refresh. Reactive end-to-end — no
/// <c>async</c>/<c>await</c>. Signature verification (<see cref="VerifySignature"/>) is a pure
/// static so the HTTP endpoint can reject a forged request before any work is scheduled.</para>
/// </summary>
public sealed class GitHubWebhookProcessor
{
    private readonly IMessageHub hub;
    private readonly IMeshService meshService;
    private readonly ILogger? logger;

    /// <summary>Initializes a new instance of the <see cref="GitHubWebhookProcessor"/> class.</summary>
    public GitHubWebhookProcessor(
        IMessageHub hub, IMeshService meshService, ILogger<GitHubWebhookProcessor>? logger = null)
    {
        this.hub = hub;
        this.meshService = meshService;
        this.logger = logger;
    }

    /// <summary>
    /// Verifies GitHub's <c>X-Hub-Signature-256</c> header (<c>sha256=&lt;hex&gt;</c>) is the
    /// HMAC-SHA256 of the raw request body under the shared <paramref name="secret"/>, in constant
    /// time. Returns false on any missing/misshaped input rather than throwing.
    /// </summary>
    public static bool VerifySignature(string? secret, byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || body is null || string.IsNullOrEmpty(signatureHeader))
            return false;
        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var provided = signatureHeader[prefix.Length..].ToLowerInvariant();
        var expected = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Processes one verified webhook (event name + parsed payload) and emits the number of issue
    /// nodes updated across all Spaces that sync the event's repository. Non-issue events, a
    /// payload without an issue, or an unmatched repo all emit <c>0</c>.
    /// </summary>
    public IObservable<int> Process(string eventType, JsonElement payload)
    {
        if (string.Equals(eventType, "push", StringComparison.OrdinalIgnoreCase))
            return ProcessPush(payload);
        var isIssues = string.Equals(eventType, "issues", StringComparison.OrdinalIgnoreCase);
        var isComment = string.Equals(eventType, "issue_comment", StringComparison.OrdinalIgnoreCase);
        if (!isIssues && !isComment)
            return Observable.Return(0);
        if (!payload.TryGetProperty("issue", out var issueEl) || issueEl.ValueKind != JsonValueKind.Object)
            return Observable.Return(0);
        if (!TryGetRepoUrl(payload, out var repoUrl))
            return Observable.Return(0);

        var issue = MapIssue(issueEl);
        // A comment event carries only the NEW comment (not the full list) and no token to fetch
        // the rest — so merge it into whatever comments were already synced onto the node.
        GitHubIssueComment? newComment = isComment
            && payload.TryGetProperty("comment", out var cEl) && cEl.ValueKind == JsonValueKind.Object
                ? MapComment(cEl)
                : null;

        return MatchingSpaces(repoUrl).SelectMany(spaces =>
        {
            if (spaces.Count == 0)
            {
                logger?.LogInformation("GitHub webhook for {Repo} matched no synced Space.", repoUrl);
                return Observable.Return(0);
            }
            logger?.LogInformation("GitHub webhook ({Event}) → refreshing issue #{Number} in {Count} Space(s).",
                eventType, issue.Number, spaces.Count);
            return spaces
                .Select(space => UpsertFromWebhook(space, issue, newComment))
                .Merge(4)
                .ToList()
                .Select(list => list.Count);
        });
    }

    // ── push → auto-update ───────────────────────────────────────────────────

    /// <summary>
    /// One parsed <c>push</c> event: the branch and the union of file paths the push touched.
    /// <see cref="ChangedPaths"/> is <see langword="null"/> when the change set is UNKNOWN
    /// (GitHub caps the <c>commits</c> array at 20 — a larger push must sync every candidate
    /// rather than silently skipping a subdirectory it can't see).
    /// </summary>
    internal sealed record PushEvent(string Branch, IReadOnlyList<string>? ChangedPaths);

    /// <summary>A Space sync source to update: the Space path, the source id (null = primary),
    /// and the user whose GitHub credential authenticates the pull — the sync config's CREATOR
    /// (the human who set the sync up; the activity-owner model), falling back to the system
    /// identity, which <see cref="GitHubSyncService"/> resolves to the GitHub App.</summary>
    internal sealed record PushTarget(string SpacePath, string? SourceId, string UserId);

    /// <summary>
    /// Parses a <c>push</c> payload. False for non-branch refs (tag pushes) and branch
    /// deletions — there is nothing to import from either.
    /// </summary>
    internal static bool TryParsePush(JsonElement payload, out PushEvent push)
    {
        push = null!;
        const string headsPrefix = "refs/heads/";
        var @ref = GetString(payload, "ref");
        if (@ref is null || !@ref.StartsWith(headsPrefix, StringComparison.Ordinal))
            return false;
        if (payload.TryGetProperty("deleted", out var del) && del.ValueKind == JsonValueKind.True)
            return false;

        IReadOnlyList<string>? changed = null;
        if (payload.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Array)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var commitCount = 0;
            foreach (var commit in commits.EnumerateArray())
            {
                commitCount++;
                set.UnionWith(GetArray(commit, "added", Self));
                set.UnionWith(GetArray(commit, "modified", Self));
                set.UnionWith(GetArray(commit, "removed", Self));
            }
            // payload.size = commits in the push; the commits array is capped at 20.
            var size = GetInt(payload, "size");
            changed = size > commitCount ? null : set.ToList();
        }
        push = new PushEvent(@ref[headsPrefix.Length..], changed);
        return true;

        static string? Self(JsonElement el)
            => el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    /// <summary>
    /// Whether one sync source should update for <paramref name="push"/>: the branch must match,
    /// the source must be allowed to import, and — when the source scopes a subdirectory and the
    /// push's change set is known — the push must have touched that subdirectory.
    /// </summary>
    internal static bool ConfigMatchesPush(GitHubSyncConfig? cfg, PushEvent push)
    {
        if (cfg is null || cfg.Direction == SyncDirection.ExportOnly)
            return false;
        if (!string.Equals(cfg.Branch, push.Branch, StringComparison.OrdinalIgnoreCase))
            return false;
        if (push.ChangedPaths is null)
            return true;
        var sub = cfg.Subdirectory?.Trim('/') ?? "";
        if (sub.Length == 0)
            return push.ChangedPaths.Count > 0;
        return push.ChangedPaths.Any(p =>
            p.Equals(sub, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(sub + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A verified <c>push</c> → the same headless "Update to latest" for every matching sync
    /// source. TRIGGERS the updates (each runs as its own activity, fire-and-forget with error
    /// logging) and emits the number triggered — it does NOT await the imports, so the webhook
    /// response returns within GitHub's delivery timeout.
    /// </summary>
    private IObservable<int> ProcessPush(JsonElement payload)
    {
        if (!TryParsePush(payload, out var push) || !TryGetRepoUrl(payload, out var repoUrl))
            return Observable.Return(0);

        return MatchingSyncTargets(repoUrl, push).Select(targets =>
        {
            if (targets.Count == 0)
            {
                logger?.LogInformation(
                    "GitHub push webhook for {Repo}@{Branch} matched no synced Space.", repoUrl, push.Branch);
                return 0;
            }
            logger?.LogInformation(
                "GitHub push webhook ({Repo}@{Branch}) → updating {Count} sync source(s) to latest.",
                repoUrl, push.Branch, targets.Count);
            var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
            foreach (var t in targets)
                Observable.Using(
                        () => accessService.ImpersonateAsSystem(),
                        _ => hub.UpdateToLatestFromGitHub(
                            t.SpacePath, t.UserId, sourceId: t.SourceId))
                    .Subscribe(
                        activity => logger?.LogInformation(
                            "Push-triggered update of {Space} completed ({Activity}).", t.SpacePath, activity),
                        exception => logger?.LogWarning(exception,
                            "Push-triggered update of {Space} (source {Source}) failed.",
                            t.SpacePath, t.SourceId ?? "(primary)"));
            return targets.Count;
        });
    }

    /// <summary>The distinct sync sources whose config targets <paramref name="repoUrl"/> AND
    /// matches the push's branch + touched paths.</summary>
    private IObservable<IReadOnlyList<PushTarget>> MatchingSyncTargets(string repoUrl, PushEvent push)
    {
        var target = ParseSafe(repoUrl);
        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{GitHubSyncService.ConfigNodeType}"))
            .Take(1)
            .Select(c => (IReadOnlyList<PushTarget>)c.Items
                .Where(n => RepoMatches(n, target))
                .Where(n => ConfigMatchesPush(
                    n.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger), push))
                .Select(ToPushTarget)
                .Where(t => t is not null)
                .Select(t => t!)
                .DistinctBy(t => (t.SpacePath, t.SourceId))
                .ToList());
    }

    /// <summary>Maps a config node path (<c>{space}/_GitSync</c> or <c>{space}/_GitSync/{sourceId}</c>)
    /// to the Space + source id it configures.</summary>
    private static PushTarget? ToPushTarget(MeshNode configNode)
    {
        var parts = configNode.Path.Split('/');
        var idx = Array.IndexOf(parts, GitHubSyncService.ConfigId);
        if (idx <= 0)
            return null;
        var space = string.Join('/', parts[..idx]);
        var sourceId = idx == parts.Length - 1 ? null : string.Join('/', parts[(idx + 1)..]);
        var userId = configNode.CreatedBy is { Length: > 0 } creator ? creator : WellKnownUsers.System;
        return new PushTarget(space, sourceId, userId);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The distinct Space paths whose GitHub sync config targets <paramref name="repoUrl"/>.</summary>
    private IObservable<IReadOnlyList<string>> MatchingSpaces(string repoUrl)
    {
        var target = ParseSafe(repoUrl);
        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{GitHubSyncService.ConfigNodeType}"))
            .Take(1)
            .Select(c => (IReadOnlyList<string>)c.Items
                .Where(n => RepoMatches(n, target))
                .Select(n => n.Path.Split('/', 2)[0])
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList());
    }

    private bool RepoMatches(MeshNode node, (string Owner, string Repo) target)
    {
        var cfg = node.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger);
        if (cfg?.RepositoryUrl is not { Length: > 0 } url) return false;
        var (owner, repo) = ParseSafe(url);
        return owner.Length > 0
            && string.Equals(owner, target.Owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(repo, target.Repo, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Upserts the issue node for one Space, preserving already-synced comments and
    /// merging the webhook's new comment when present. Written under the system identity.</summary>
    private IObservable<MeshNode> UpsertFromWebhook(string space, GitHubIssue issue, GitHubIssueComment? newComment)
    {
        var path = IssueService.IssuePath(space, issue.Number);
        return ReadExisting(path).SelectMany(existing =>
        {
            var comments = existing?.Comments ?? ImmutableList<GitHubIssueComment>.Empty;
            if (newComment is not null)
                comments = comments.RemoveAll(c => c.Id == newComment.Id).Add(newComment);
            var merged = issue with { Comments = comments };
            var node = new MeshNode(issue.Number.ToString(), IssueService.IssueNamespace(space))
            {
                NodeType = IssueService.NodeType,
                Name = issue.Title is { Length: > 0 } t ? $"#{issue.Number} {t}" : $"Issue #{issue.Number}",
                State = MeshNodeState.Active,
                MainNode = space,
                Content = merged,
            };
            var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
            return Observable.Using(
                    () => accessService.ImpersonateAsSystem(),
                    _ => hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node)).FirstAsync())
                .SelectMany(d => d.Message.Success
                    ? Observable.Return(d.Message.Node ?? node)
                    : Observable.Throw<MeshNode>(new InvalidOperationException(
                        $"Webhook upsert of issue #{issue.Number} into {space} failed: {d.Message.Error}")));
        });
    }

    /// <summary>Tolerant read of the existing issue node's content (null on absent — never a point read).</summary>
    private IObservable<GitHubIssue?> ReadExisting(string path)
        => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
            .Take(1)
            .Select(c => c.Items.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.ContentAs<GitHubIssue>(hub.JsonSerializerOptions, logger));

    private static (string Owner, string Repo) ParseSafe(string url)
    {
        try { return OctokitGitHubRepoClient.ParseRepoUrl(url); }
        catch { return ("", ""); }
    }

    private static bool TryGetRepoUrl(JsonElement payload, out string url)
    {
        url = "";
        if (!payload.TryGetProperty("repository", out var r) || r.ValueKind != JsonValueKind.Object)
            return false;
        var full = GetString(r, "full_name");
        if (!string.IsNullOrEmpty(full)) { url = $"https://github.com/{full}"; return true; }
        var html = GetString(r, "html_url");
        if (!string.IsNullOrEmpty(html)) { url = html!; return true; }
        return false;
    }

    private static GitHubIssue MapIssue(JsonElement e) => new()
    {
        Number = GetInt(e, "number"),
        Title = GetString(e, "title"),
        Body = GetString(e, "body"),
        State = string.Equals(GetString(e, "state"), "closed", StringComparison.OrdinalIgnoreCase)
            ? GitHubIssueState.Closed : GitHubIssueState.Open,
        AuthorLogin = e.TryGetProperty("user", out var u) ? GetString(u, "login") : null,
        Labels = GetArray(e, "labels", el => GetString(el, "name")),
        Assignees = GetArray(e, "assignees", el => GetString(el, "login")),
        CommentsCount = GetInt(e, "comments"),
        Url = GetString(e, "html_url"),
        CreatedAt = GetDate(e, "created_at"),
        UpdatedAt = GetDate(e, "updated_at"),
        ClosedAt = GetDate(e, "closed_at"),
    };

    private static GitHubIssueComment MapComment(JsonElement c) =>
        new(GetLong(c, "id"),
            c.TryGetProperty("user", out var u) ? GetString(u, "login") : null,
            GetString(c, "body"),
            GetDate(c, "created_at"),
            GetString(c, "html_url"));

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static DateTimeOffset? GetDate(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var d)
            ? d : null;

    private static ImmutableList<string> GetArray(JsonElement e, string name, Func<JsonElement, string?> select)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ImmutableList<string>.Empty;
        var builder = ImmutableList.CreateBuilder<string>();
        foreach (var el in arr.EnumerateArray())
        {
            var s = select(el);
            if (!string.IsNullOrEmpty(s)) builder.Add(s!);
        }
        return builder.ToImmutable();
    }
}
