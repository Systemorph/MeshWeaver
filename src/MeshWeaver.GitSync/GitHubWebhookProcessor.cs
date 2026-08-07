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
/// <para><b><c>workflow_run</c> success keeps GitSync'd Spaces CURRENT without polling — and only
/// ever with content CI accepted.</b> A green build of a repository's DEFAULT branch gives every
/// Space whose sync config targets that repository + branch the same headless "Update to latest"
/// the GUI button and the MCP <c>git_hub_sync</c> tool run
/// (<see cref="GitHubActivityExtensions.UpdateToLatestFromGitHub"/>, <c>force: false</c> so
/// two-way conflict resolution still protects server-side edits). The mesh writes run under
/// the system identity; the GitHub pull authenticates as the sync config's CREATOR (their
/// connected credential, or the GitHub App when they have none).</para>
///
/// <para>🚨 <b>The import is gated on CI, so <c>push</c> imports nothing.</b> A push event arrives
/// BEFORE the build it starts, so importing on push shipped content to live Spaces seconds ahead of
/// the gate meant to vet it. Because a repo's own content CI is the only thing that knows whether a
/// commit is installable, the green build — not the merge — is the publish signal. A repository with
/// NO CI workflow therefore never auto-imports: give it one, or sync it by hand.</para>
///
/// <para>Register the repo webhook with <c>Workflow runs</c> (required — it is the trigger) and
/// <c>Pushes</c> (optional; logged only, and the breadcrumb that tells you a repo's pushes are
/// arriving while its green builds are not) next to <c>Issues</c>/<c>Issue comments</c>.</para>
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
        if (string.Equals(eventType, "workflow_run", StringComparison.OrdinalIgnoreCase))
            return ProcessWorkflowRun(payload);
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
    /// A verified <c>push</c> → nothing. **The import is gated on CI**, so it is triggered by the
    /// repository's GREEN build (<see cref="ProcessWorkflowRun"/>), not by the push that started it.
    ///
    /// <para>🚨 This used to import on every push, and that is precisely the hole it left: the push
    /// event arrives BEFORE the build it triggers, so a red main reached production seconds ahead
    /// of the gate meant to stop it (observed 2026-08-07 — a merge synced at 09:20:53, its CI failed
    /// at 09:52). Content repos are GitSync'd straight into live Spaces, so "imported, then found
    /// broken" is indistinguishable from shipping broken content to users.</para>
    ///
    /// <para>The push is still parsed and logged: it is the signal that a build is COMING, and the
    /// log line is what makes "the merge landed but nothing synced" diagnosable — a repo whose
    /// pushes are seen but whose green builds never arrive has no CI workflow, and will never
    /// auto-import until it gets one.</para>
    /// </summary>
    private IObservable<int> ProcessPush(JsonElement payload)
    {
        if (!TryParsePush(payload, out var push) || !TryGetRepoUrl(payload, out var repoUrl))
            return Observable.Return(0);

        logger?.LogInformation(
            "GitHub push webhook ({Repo}@{Branch}) — no import: the sync is CI-gated and waits for a "
            + "green build of this ref (workflow_run/success on the default branch).",
            repoUrl, push.Branch);
        return Observable.Return(0);
    }

    /// <summary>
    /// A verified GREEN build of the default branch → the headless "Update to latest" for every sync
    /// source that targets this repo + branch and is not already at this commit. TRIGGERS the updates
    /// (each its own activity, fire-and-forget with error logging) and emits the number triggered — it
    /// does NOT await the imports, so the webhook response returns within GitHub's delivery timeout.
    ///
    /// <para>Scoping differs from the old push path in one way that matters: a <c>workflow_run</c>
    /// payload carries no file list, so a source's <c>Subdirectory</c> cannot be used to skip it.
    /// Every source of the repo is brought to latest; an unchanged subdirectory imports as a no-op.
    /// The <c>lastSyncCommitSha</c> check below is what keeps that cheap — it makes a re-run of an
    /// already-imported commit (a flake re-run, a manual re-dispatch) trigger nothing at all.</para>
    /// </summary>
    private IObservable<int> TriggerSyncForGreenBuild(string repoUrl, string branch, string headSha)
        => MatchingBuildTargets(repoUrl, branch, headSha).Select(targets =>
        {
            if (targets.Count == 0)
            {
                logger?.LogInformation(
                    "Green build of {Repo}@{Branch} ({Sha}) matched no sync source that needs updating.",
                    repoUrl, branch, headSha);
                return 0;
            }
            logger?.LogInformation(
                "Green build of {Repo}@{Branch} ({Sha}) → updating {Count} sync source(s) to latest.",
                repoUrl, branch, headSha, targets.Count);
            var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
            foreach (var t in targets)
                Observable.Using(
                        () => accessService.ImpersonateAsSystem(),
                        _ => hub.UpdateToLatestFromGitHub(
                            t.SpacePath, t.UserId, sourceId: t.SourceId))
                    .Subscribe(
                        activity => logger?.LogInformation(
                            "Build-triggered update of {Space} completed ({Activity}).", t.SpacePath, activity),
                        exception => logger?.LogWarning(exception,
                            "Build-triggered update of {Space} (source {Source}) failed.",
                            t.SpacePath, t.SourceId ?? "(primary)"));
            return targets.Count;
        });

    /// <summary>
    /// Whether one sync source should import for a green build of <paramref name="branch"/> at
    /// <paramref name="headSha"/>: the branch must match, the source must be allowed to import, and
    /// the source must not already sit on that commit.
    /// </summary>
    internal static bool ConfigMatchesBuild(GitHubSyncConfig? cfg, string branch, string headSha)
    {
        if (cfg is null || cfg.Direction == SyncDirection.ExportOnly)
            return false;
        if (!string.Equals(cfg.Branch, branch, StringComparison.OrdinalIgnoreCase))
            return false;
        // Already imported this exact commit — a re-run of the same green build must be a no-op.
        return !string.Equals(cfg.LastSyncCommitSha, headSha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The distinct sync sources whose config targets <paramref name="repoUrl"/> AND
    /// matches the green build's branch, minus those already at <paramref name="headSha"/>.</summary>
    private IObservable<IReadOnlyList<PushTarget>> MatchingBuildTargets(
        string repoUrl, string branch, string headSha)
    {
        var target = ParseSafe(repoUrl);
        return QueryConfigNodesAsSystem()
            .Select(c => (IReadOnlyList<PushTarget>)c.Items
                .Where(n => RepoMatches(n, target))
                .Where(n => ConfigMatchesBuild(
                    n.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger), branch, headSha))
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

    /// <summary>
    /// The full <c>GitHubSyncConfig</c> node set, queried under the SYSTEM identity. The webhook
    /// HTTP request is anonymous — its authorization is the verified HMAC signature — and
    /// <c>_GitSync</c> satellites are not anonymous-readable, so an ambient-identity query
    /// silently matches nothing on an access-gated portal (the DevLogin test fallback masked
    /// exactly this). Same identity model as the write path below.
    /// </summary>
    private IObservable<QueryResultChange<MeshNode>> QueryConfigNodesAsSystem()
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{GitHubSyncService.ConfigNodeType}"))
                .Take(1));
    }

    // ── workflow_run → build-completion record ───────────────────────────────

    /// <summary>
    /// A verified <c>workflow_run</c> → the repository's <see cref="BuildCompletion"/> node.
    ///
    /// <para>GitSync records a FACT and stops there: "repo X built green at sha Y". It does not know
    /// or care who is listening. Consumers — today the plugin catalog — SUBSCRIBE to the node's
    /// stream and decide for themselves whether anything they care about actually changed. That
    /// keeps the two sides decoupled at compile time (the node's content type lives in
    /// MeshWeaver.Graph, which both reference) and means a second consumer costs nothing here.</para>
    ///
    /// <para><b>Only a completed, successful run is recorded.</b> A failed or cancelled run may still
    /// have produced artifacts; writing that as a build completion is how a broken build reaches
    /// consumers. Every green run rewrites the node — including doc-only commits, reverts, and
    /// re-runs of an unchanged tree — because deciding "did anything change" needs content identity,
    /// which is the consumer's business, not the webhook's.</para>
    ///
    /// <para>Written under the SYSTEM identity: the webhook request is anonymous (its authorization
    /// is the verified HMAC signature), so an ambient-identity write would be refused on an
    /// access-gated portal. Same identity model as the issue upsert and the push auto-update.</para>
    /// </summary>
    private IObservable<int> ProcessWorkflowRun(JsonElement payload)
    {
        if (!string.Equals(GetString(payload, "action"), "completed", StringComparison.OrdinalIgnoreCase))
            return Observable.Return(0);
        if (!payload.TryGetProperty("workflow_run", out var run) || run.ValueKind != JsonValueKind.Object)
            return Observable.Return(0);
        if (!string.Equals(GetString(run, "conclusion"), "success", StringComparison.OrdinalIgnoreCase))
            return Observable.Return(0);
        if (!TryGetRepoUrl(payload, out var repoUrl))
            return Observable.Return(0);

        // 🚨 Only the DEFAULT branch's green builds are publishable. A PR-branch run is green
        // UNMERGED code — recording it would make the plugin-update watcher offer (or, for an
        // opted-in record, unattended-install) content the default branch never accepted, at that
        // branch's sha (Copilot catch). Fail closed: no branch match, no record — a payload whose
        // branch we cannot read must not become an update either.
        var headBranch = GetString(run, "head_branch") ?? "";
        var defaultBranch = payload.TryGetProperty("repository", out var repoElement)
                            && repoElement.ValueKind == JsonValueKind.Object
            ? GetString(repoElement, "default_branch") ?? ""
            : "";
        if (headBranch.Length == 0 || defaultBranch.Length == 0
            || !string.Equals(headBranch, defaultBranch, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogDebug(
                "workflow_run webhook for {Repo}: green build on '{Branch}' is not the default branch "
                + "('{Default}') — not publishable, no build record.", repoUrl, headBranch, defaultBranch);
            return Observable.Return(0);
        }

        var headSha = GetString(run, "head_sha") ?? "";
        if (headSha.Length == 0)
        {
            logger?.LogWarning("workflow_run webhook for {Repo} carried no head_sha — ignoring.", repoUrl);
            return Observable.Return(0);
        }

        var (owner, repo) = ParseSafe(repoUrl);
        if (owner.Length == 0 || repo.Length == 0)
            return Observable.Return(0);

        var completion = new BuildCompletion
        {
            RepositoryUrl = repoUrl,
            Branch = GetString(run, "head_branch") ?? "",
            HeadSha = headSha,
            WorkflowName = GetString(run, "name"),
            RunId = GetLong(run, "id"),
            RunNumber = GetLong(run, "run_number"),
            CompletedAtUtc = GetDate(run, "updated_at"),
            Conclusion = "success",
        };

        var path = BuildCompletion.PathFor(owner, repo);
        var slash = path.LastIndexOf('/');
        var node = new MeshNode(path[(slash + 1)..], path[..slash])
        {
            NodeType = BuildCompletion.NodeType,
            Name = $"{owner}/{repo} build",
            State = MeshNodeState.Active,
            Content = completion,
        };

        logger?.LogInformation(
            "workflow_run webhook ({Repo}@{Branch} {Sha}, {Workflow} #{RunNumber}) → recording build completion at {Path}.",
            repoUrl, completion.Branch, headSha, completion.WorkflowName, completion.RunNumber, path);

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node)).FirstAsync())
            .SelectMany(d =>
            {
                if (!d.Message.Success)
                {
                    // Never throw: GitHub retries a non-2xx delivery, so a write failure would turn
                    // into a delivery storm. Surface it and report "nothing recorded".
                    logger?.LogWarning("Recording build completion at {Path} failed: {Error}", path, d.Message.Error);
                    return Observable.Return(0);
                }
                // The build record is the CI gate's verdict; the import is what the verdict authorises.
                // Both hang off this one green-build event so they cannot disagree about what shipped.
                return TriggerSyncForGreenBuild(repoUrl, completion.Branch, headSha)
                    .Select(_ => 1)
                    .Catch((Exception ex) =>
                    {
                        // A failed import must not fail the delivery — the build record is already
                        // written, and a non-2xx would make GitHub redeliver and re-import.
                        logger?.LogWarning(ex,
                            "Green build of {Repo} recorded, but triggering the sync failed.", repoUrl);
                        return Observable.Return(1);
                    });
            });
    }

    /// <summary>The distinct Space paths whose GitHub sync config targets <paramref name="repoUrl"/>.</summary>
    private IObservable<IReadOnlyList<string>> MatchingSpaces(string repoUrl)
    {
        var target = ParseSafe(repoUrl);
        return QueryConfigNodesAsSystem()
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
