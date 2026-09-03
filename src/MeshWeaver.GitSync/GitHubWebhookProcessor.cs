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
    private readonly GitHubRepoIdentityResolver? identities;
    private readonly ILogger? logger;

    /// <summary>Initializes a new instance of the <see cref="GitHubWebhookProcessor"/> class.</summary>
    /// <param name="hub">The hub this processor issues its reads and writes from.</param>
    /// <param name="meshService">Mesh query surface for the sync-config fan-out.</param>
    /// <param name="identities">
    /// Canonical repository identity, for matching a config that stores a repository's OLD name
    /// after a rename (#1856). Null degrades to stored-string matching alone — which is exactly what
    /// the zero-match warning then says.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public GitHubWebhookProcessor(
        IMessageHub hub,
        IMeshService meshService,
        GitHubRepoIdentityResolver? identities = null,
        ILogger<GitHubWebhookProcessor>? logger = null)
    {
        this.hub = hub;
        this.meshService = meshService;
        this.identities = identities;
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
        if (GitHubRepoIdentityResolver.Parse(repoUrl) is not { } target)
        {
            logger?.LogWarning(
                "GitHub webhook ({Event}) carried a repository url that cannot be parsed to owner/repo: '{Repo}'.",
                eventType, repoUrl);
            return Observable.Return(0);
        }

        var issue = MapIssue(issueEl);
        // A comment event carries only the NEW comment (not the full list) and no token to fetch
        // the rest — so merge it into whatever comments were already synced onto the node.
        GitHubIssueComment? newComment = isComment
            && payload.TryGetProperty("comment", out var cEl) && cEl.ValueKind == JsonValueKind.Object
                ? MapComment(cEl)
                : null;

        return MatchingSpaces(target, eventType).SelectMany(spaces =>
        {
            // A zero match is reported by ConfigsTargeting — at Warning, naming BOTH sides. Nothing
            // to add here; a second line would only split the diagnosis across two records.
            if (spaces.Count == 0)
                return Observable.Return(0);
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
    private IObservable<int> TriggerSyncForGreenBuild(RepoIdentity repo, string branch, string headSha)
        => MatchingBuildTargets(repo, branch, headSha).Select(targets =>
        {
            if (targets.Count == 0)
            {
                // "No source NEEDS updating" (already at this commit, wrong branch, export-only) is a
                // NORMAL outcome and stays at Information. "No source TARGETS this repository at all"
                // is a different animal and is reported at Warning by ConfigsTargeting — the two must
                // not read alike, which is exactly how #1856 hid for four days.
                logger?.LogInformation(
                    "Green build of {Repo}@{Branch} ({Sha}) matched no sync source that needs updating.",
                    repo, branch, headSha);
                return 0;
            }
            logger?.LogInformation(
                "Green build of {Repo}@{Branch} ({Sha}) → updating {Count} sync source(s) to latest.",
                repo, branch, headSha, targets.Count);
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
    /// <remarks>Expressed as "there is no reason to skip it" so the predicate and the log line
    /// (<see cref="SkipReason"/>) can never disagree about why a Space was left behind.</remarks>
    internal static bool ConfigMatchesBuild(GitHubSyncConfig? cfg, string branch, string headSha)
        => SkipReason(cfg, branch, headSha) is null;

    /// <summary>The distinct sync sources whose config targets <paramref name="repo"/> AND
    /// matches the green build's branch, minus those already at <paramref name="headSha"/>.</summary>
    private IObservable<IReadOnlyList<PushTarget>> MatchingBuildTargets(
        RepoIdentity repo, string branch, string headSha)
        => ConfigsTargeting(repo, $"green build of {branch}")
            .Select(match =>
            {
                // 🚨 Classify EVERY candidate and say what happened to it. A fan-out that reports
                // only its winners cannot be audited: "updated 34" and "updated 34 of 43" look
                // identical in the log, which is how #1326 stayed invisible for days. One line per
                // skipped config, naming the reason, is what makes a future omission findable.
                //
                // 🚨 …and the candidates that never REACHED this classification are the other half.
                // A config that targets a different repository is dropped one level up, silently and
                // correctly — but when that silence swallows EVERY config, this line used to read
                // "43 sync config(s) in the mesh, 0 selected, 0 skipped": a healthy-looking count,
                // no skips, and no clue that nothing was ever even a candidate. Hence {Targeting}
                // here, and the Warning ConfigsTargeting raises (#1856).
                var picked = new List<PushTarget>();
                var skipped = new List<string>();
                foreach (var node in match.Configs)
                {
                    var cfg = node.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger);
                    if (SkipReason(cfg, branch, headSha) is { } reason)
                    {
                        skipped.Add($"{node.Path} ({reason})");
                        continue;
                    }
                    if (ToPushTarget(node) is not { } pushTarget)
                    {
                        skipped.Add($"{node.Path} (path carries no '{GitHubSyncService.ConfigId}' segment)");
                        continue;
                    }
                    picked.Add(pushTarget);
                }

                var targets = (IReadOnlyList<PushTarget>)picked
                    .DistinctBy(t => (t.SpacePath, t.SourceId))
                    .ToList();

                logger?.LogInformation(
                    "Green build of {Repo}@{Branch} ({Sha}): {Candidates} sync config(s) in the mesh, "
                    + "{Targeting} targeting this repository, {Selected} selected, {Skipped} skipped{SkipDetail}.",
                    repo, branch, headSha, match.Candidates, match.Configs.Count, targets.Count,
                    skipped.Count, skipped.Count == 0 ? string.Empty : " — " + string.Join("; ", skipped));

                return targets;
            });

    /// <summary>
    /// Why a config that DOES target this repo is not being updated, or <c>null</c> when it is.
    /// The reason strings are log copy — a skipped Space must be traceable to the exact predicate
    /// that dropped it, never inferred from its absence.
    /// </summary>
    private static string? SkipReason(GitHubSyncConfig? cfg, string branch, string headSha)
        => cfg is null ? "config content could not be read"
            : cfg.Direction == SyncDirection.ExportOnly ? "direction is ExportOnly"
            : !string.Equals(cfg.Branch, branch, StringComparison.OrdinalIgnoreCase)
                ? $"branch '{cfg.Branch}' != built branch '{branch}'"
            : string.Equals(cfg.LastSyncCommitSha, headSha, StringComparison.OrdinalIgnoreCase)
                ? "already at this commit"
            : null;

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
    /// <remarks>
    /// 🚨 TWO silent-drop guards, both of which this read shipped without and both of which cost
    /// spaces their updates (#1326: 9 of 43 permanently stale while every webhook reported success).
    ///
    /// <para><b>Complete(), not a page.</b> The query carries no <c>path:</c> and no
    /// <c>namespace:</c>, so on Postgres it is the UNPINNED shape served by the cross-schema
    /// fan-out — which answers a request that states no limit with the 50 most recently modified
    /// rows. This is a fan-out over EVERY configured sync source: a config that falls out of that
    /// window is not "missing from a list", it is a Space that never syncs again. Worse, it is
    /// self-reinforcing — a successful sync rewrites the config node (<c>RecordSeenCommit</c>), so
    /// the spaces that DID update stay in the window and the stragglers sink further out of it,
    /// which is exactly the "the same 9 every time, each a different number of commits behind"
    /// signature. Same defect as #1216 one caller over.</para>
    ///
    /// <para><b>Initial, not just the first emission.</b> A bare <c>Take(1)</c> can capture a
    /// pre-Initial emission and export the fan-out as an EMPTY candidate set — observed on a live
    /// instance and already guarded this way in <c>GitHubSyncService</c>. Filtering on
    /// <see cref="QueryChangeType.Initial"/> waits for the snapshot the fan-out is asking for.</para>
    /// </remarks>
    private IObservable<QueryResultChange<MeshNode>> QueryConfigNodesAsSystem()
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        // RunAsSystem, never `Observable.Using(() => ImpersonateAsSystem(), …)` — store and restore
        // of the identity must land on the same thread (AGENTS.md; #1790).
        return accessService.RunAsSystem(() => meshService
            .Query<MeshNode>(MeshQueryRequest
                // Every {Space}/_GitSync config — the webhook cannot know which space a push
                // belongs to until it has read them all, so mesh-wide by nature (#3202).
                .FromQuery(MeshWideQuery.OfType(GitHubSyncService.ConfigNodeType))
                .Complete())
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1));
    }

    // ── workflow_run → build-completion record ───────────────────────────────

    /// <summary>
    /// 🚨 <b>The <c>workflow_run</c> triggers that mean "a completed build of THE DEFAULT BRANCH'S
    /// OWN TREE" — an ALLOW-LIST, so an event name nobody has considered is REFUSED rather than
    /// admitted.</b> A deny-list here fails open: the next trigger GitHub invents would publish.
    ///
    /// <para><b>Why each one is admitted.</b>
    /// <list type="bullet">
    /// <item><c>push</c> — the branch moved and its CI ran. The original case.</item>
    /// <item><c>repository_dispatch</c> — GitHub only ever runs a dispatched workflow from the
    /// DEFAULT branch, and the run's <c>head_sha</c> is that branch's tip. This is how a platform
    /// release re-verifies every satellite repo: no commit to push, the same tree, a genuine green
    /// verdict on it.</item>
    /// <item><c>schedule</c> — same reason: a cron run only ever exists on the default branch.</item>
    /// <item><c>workflow_dispatch</c> — may target ANY ref, so it is admitted here and
    /// DISCRIMINATED by the head_branch check. Aimed at the default branch it is a manual
    /// re-verification of that tree — and the only recovery lever when a merge burst cancelled the
    /// push-triggered run.</item>
    /// </list></para>
    ///
    /// <para><b>Deliberately NOT admitted.</b> <c>pull_request</c> / <c>pull_request_target</c> are
    /// green UNMERGED code; <c>dynamic</c> is GitHub's Copilot reviewer, which completes green on the
    /// default branch and is not a build at all; anything unknown fails closed. <c>merge_group</c> is
    /// absent ON PURPOSE — a queue run's <c>head_branch</c> is the temporary
    /// <c>gh-readonly-queue/{base}/pr-{n}-{sha}</c> ref (measured on this repository's own queue,
    /// 2026-09-02), so the head_branch guard already rejects it and an entry here would be a line no
    /// test could reach.</para>
    ///
    /// <para>🚨 <b>The measurement that widened this (2026-09-02,
    /// <c>Systemorph/MeshWeaver.Plugins#1194</c>).</b> The test was <c>event == "push"</c> alone, and
    /// it DISCARDED REAL PUBLISH SIGNALS. <c>Systemorph/MeshWeaver.Reinsurance</c>'s <c>main</c> built
    /// green three times at <c>636ebd5</c> — 11:17, 12:27 and 12:55Z — every one of them
    /// <c>event=repository_dispatch</c> (the release-follow lane, which rebuilds every module against
    /// a new platform pin without a commit to push). All three were dropped here, and
    /// <c>Underwriting/_GitSync</c> on <c>memex.systemorph.com</c> sat <b>38 h</b> behind a merged
    /// main while the webhook was armed and healthy and every delivery answered 200 OK — no error, no
    /// warning, nothing to grep. (The other half of that incident was a genuinely red push lane,
    /// where this gate behaved correctly and is meant to.)</para>
    ///
    /// <para><b>Widening this cannot cause churn.</b> A sync source already sitting on the built sha
    /// is skipped by <see cref="SkipReason"/> ("already at this commit"), so a scheduled or dispatched
    /// re-verification of an unchanged default branch triggers no import at all.</para>
    /// </summary>
    private static readonly ImmutableHashSet<string> PublishSignalTriggers =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "push", "repository_dispatch", "schedule", "workflow_dispatch");

    /// <summary>The admitted triggers as log copy — ordered so the line is stable, built once so the
    /// rejection log cannot drift from the set it is reporting.</summary>
    private static readonly string PublishSignalTriggerList =
        string.Join(", ", PublishSignalTriggers.OrderBy(t => t, StringComparer.Ordinal));

    /// <summary>
    /// Whether a <c>workflow_run</c> trigger means "a build of the default branch's own tree".
    /// Fail-closed: an empty, missing or unrecognised event name is NOT a publish signal.
    /// </summary>
    /// <remarks>See <see cref="PublishSignalTriggers"/> for why each admitted trigger is admitted,
    /// and for the 2026-09-02 measurement that replaced the single-value <c>== "push"</c> test.</remarks>
    internal static bool IsPublishSignalTrigger(string? runEvent)
        => runEvent is { Length: > 0 } && PublishSignalTriggers.Contains(runEvent);

    /// <summary>
    /// Whether a run's <c>head_branch</c> IS the repository's default branch — the second, independent
    /// guard, and the one that discriminates the admitted triggers that can target any ref. Fail-closed:
    /// a payload whose branch or default branch cannot be read is not publishable.
    /// </summary>
    internal static bool IsDefaultBranchBuild(string? headBranch, string? defaultBranch)
        => headBranch is { Length: > 0 } && defaultBranch is { Length: > 0 }
           && string.Equals(headBranch, defaultBranch, StringComparison.OrdinalIgnoreCase);

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
    /// <para><b>Two independent guards decide "is this a publish signal".</b> The run's TRIGGER must
    /// be one of <see cref="PublishSignalTriggers"/> (an allow-list — unknown events fail closed),
    /// AND its <c>head_branch</c> must be the repository's default branch
    /// (<see cref="IsDefaultBranchBuild"/>). Neither subsumes the other: the trigger check states the
    /// requirement, the branch check discriminates the triggers that can target any ref.</para>
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

        // 🚨 The run must be a build of THE DEFAULT BRANCH'S OWN TREE, expressed as an ALLOW-LIST of
        // triggers (see PublishSignalTriggers) — never a deny-list. `workflow_run` fires for far more
        // than the repo's content CI: measured on the education hook, GitHub's own Copilot reviewer
        // arrives as action=completed / conclusion=success with event="dynamic", and every
        // `pull_request` run of the content workflow arrives green too. Those are green builds of code
        // the default branch has not accepted; importing on one would publish a feature branch. The
        // branch check below catches most of them, but only because a PR's head_branch is its source
        // branch — filtering on the trigger states the actual requirement instead of relying on that
        // coincidence.
        var runEvent = GetString(run, "event") ?? "";
        if (!IsPublishSignalTrigger(runEvent))
        {
            logger?.LogDebug(
                "workflow_run webhook: green '{Workflow}' run was triggered by '{Event}', which is not a "
                + "build of the default branch's own tree (admitted: {Admitted}) — not a publish signal.",
                GetString(run, "name"), runEvent, PublishSignalTriggerList);
            return Observable.Return(0);
        }

        if (!TryGetRepoUrl(payload, out var repoUrl))
            return Observable.Return(0);

        // 🚨 Only the DEFAULT branch's green builds are publishable — the SECOND, INDEPENDENT guard,
        // deliberately not folded into the trigger check above. A PR-branch run is green UNMERGED
        // code — recording it would make the plugin-update watcher offer (or, for an opted-in record,
        // unattended-install) content the default branch never accepted, at that branch's sha
        // (Copilot catch). Fail closed: no branch match, no record — a payload whose branch we cannot
        // read must not become an update either. This is also what discriminates the one admitted
        // trigger that can target any ref (`workflow_dispatch`), and what already rejects a
        // merge-queue run, whose head_branch is the temporary `gh-readonly-queue/…` ref.
        var headBranch = GetString(run, "head_branch") ?? "";
        var defaultBranch = payload.TryGetProperty("repository", out var repoElement)
                            && repoElement.ValueKind == JsonValueKind.Object
            ? GetString(repoElement, "default_branch") ?? ""
            : "";
        if (!IsDefaultBranchBuild(headBranch, defaultBranch))
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

        if (GitHubRepoIdentityResolver.Parse(repoUrl) is not { } target)
        {
            logger?.LogWarning(
                "workflow_run webhook carried a repository url that cannot be parsed to owner/repo: '{Repo}'.",
                repoUrl);
            return Observable.Return(0);
        }
        var (owner, repo) = (target.Owner, target.Repo);

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
        // Off-router issuing: the webhook processor holds the DI root mesh hub — a target-less
        // CreateOrUpdateNodeRequest posted there runs on the router (ROUTER_TRAFFIC).
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.NodeOperationIssuingHub()
                    .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node)).FirstAsync())
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
                return TriggerSyncForGreenBuild(target, completion.Branch, headSha)
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

    /// <summary>The distinct Space paths whose GitHub sync config targets <paramref name="repo"/>.</summary>
    private IObservable<IReadOnlyList<string>> MatchingSpaces(RepoIdentity repo, string context)
        => ConfigsTargeting(repo, context)
            .Select(match => (IReadOnlyList<string>)match.Configs
                .Select(n => n.Path.Split('/', 2)[0])
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList());

    // ── which configs target this repository (rename-tolerant) ───────────────

    /// <summary>
    /// One delivery's fan-out, CLASSIFIED: how many sync configs exist at all, which of them target
    /// the incoming repository, and which of those only matched because the repository was RENAMED
    /// (their stored url still names it by an old name).
    /// </summary>
    /// <param name="Candidates">Every sync config in the mesh — the denominator.</param>
    /// <param name="Configs">The configs that target this repository.</param>
    /// <param name="Renamed">The subset matched only via canonical identity — a stale stored url.</param>
    internal sealed record RepoMatch(
        int Candidates, IReadOnlyList<MeshNode> Configs, IReadOnlyList<MeshNode> Renamed);

    /// <summary>
    /// Every sync config that targets <paramref name="incoming"/> — the ONE matching seam every
    /// webhook path funnels through, so the rename tolerance and the zero-match report cannot drift
    /// apart between the issue fan-out and the green-build fan-out.
    ///
    /// <para><b>Stored strings first.</b> A stored <c>owner/repo</c> that already equals the incoming
    /// one is a match, for free, with no network call — which is every repository that was never
    /// renamed, i.e. essentially all of them. Only when NOTHING matched does the canonical lookup
    /// run, and it is cached per repository (<see cref="GitHubRepoIdentityResolver.Ttl"/>), so even a
    /// hook whose repository this mesh does not sync at all costs one lookup an hour rather than one
    /// per delivery.</para>
    ///
    /// <para>🚨 <b>A stored url that stops matching is INVISIBLE without this.</b> GitHub redirects a
    /// renamed repository's old url, so git, the REST API and every manual sync keep working —
    /// nothing errors, nothing 404s. Only equality breaks, and only in this one comparison
    /// (#1856).</para>
    /// </summary>
    /// <param name="incoming">The repository the delivery is FOR (payload → always the current name).</param>
    /// <param name="context">Human copy for the log line: the event, or "green build of main".</param>
    /// <returns>The classified match. Never faults — a resolution failure degrades to "no match".</returns>
    private IObservable<RepoMatch> ConfigsTargeting(RepoIdentity incoming, string context)
        => QueryConfigNodesAsSystem().SelectMany(c =>
        {
            var stored = c.Items
                .Select(node => (
                    Node: node,
                    Url: node.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger)?.RepositoryUrl))
                .Select(x => (x.Node, x.Url, Id: GitHubRepoIdentityResolver.Parse(x.Url)))
                .ToList();

            var direct = stored.Where(x => incoming.Matches(x.Id)).Select(x => x.Node).ToList();
            if (direct.Count > 0)
                return Observable.Return(new RepoMatch(stored.Count, direct, []));

            // Nothing matched by stored string. Either this repository is genuinely foreign, or one
            // of the stored urls is an OLD NAME of it. Ask GitHub what each stored url resolves to
            // today — grouped, so a repository configured by ten Spaces costs ONE lookup.
            var groups = stored
                .Where(x => x.Id is not null && x.Url is { Length: > 0 })
                .GroupBy(x => x.Id!.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (identities is null || groups.Count == 0)
                return Observable.Return(Report(
                    new RepoMatch(stored.Count, [], []),
                    incoming, context, groups.Select(g => (g.Key, (RepoIdentity?)null)).ToList()));

            return groups
                .Select(g => identities
                    .Resolve(g.First().Url!, g.First().Node.CreatedBy)
                    .Select(canonical => (Group: g, Canonical: canonical)))
                .Merge(4)
                .ToList()
                .Select(resolved =>
                {
                    var renamed = resolved
                        .Where(r => incoming.Matches(r.Canonical))
                        .SelectMany(r => r.Group.Select(x => x.Node))
                        .ToList();
                    if (renamed.Count > 0)
                        RepointToCanonical(resolved
                            .Where(r => incoming.Matches(r.Canonical))
                            .SelectMany(r => r.Group.Select(x => (x.Node, x.Url!)))
                            .ToList(), incoming);
                    return Report(
                        new RepoMatch(stored.Count, renamed, renamed),
                        incoming, context,
                        resolved.Select(r => (r.Group.Key, r.Canonical)).ToList());
                });
        });

    /// <summary>
    /// 🚨 <b>A delivery that matches NOTHING is the loudest thing this processor can say.</b> It
    /// means every Space that syncs the repository has just been skipped, silently, and will stay
    /// skipped on every future delivery until someone notices — which is precisely what took four
    /// days in #1856. Warning, naming BOTH sides: the repository the payload is for, and every
    /// repository the mesh compared it against (with what each resolves to today, when known).
    ///
    /// <para>Information is the wrong level for it: an unmatched delivery is not a routine outcome
    /// of a healthy mesh, it is a hook pointing at a repository nothing syncs — a stale config, a
    /// rename, or a hook installed on the wrong repository. Each of the three wants a human.</para>
    /// </summary>
    private RepoMatch Report(
        RepoMatch match, RepoIdentity incoming, string context,
        IReadOnlyList<(string Stored, RepoIdentity? Canonical)> compared)
    {
        if (match.Configs.Count > 0)
        {
            if (match.Renamed.Count > 0)
                logger?.LogWarning(
                    "GitHub webhook ({Context}) for {Repo} matched {Count} sync config(s) only by "
                    + "CANONICAL identity — the repository was RENAMED and their stored url still "
                    + "names it {Stored}. Repointing them: {Configs}.",
                    context, incoming, match.Renamed.Count,
                    string.Join(", ", compared
                        .Where(x => incoming.Matches(x.Canonical))
                        .Select(x => x.Stored)
                        .Distinct(StringComparer.OrdinalIgnoreCase)),
                    string.Join(", ", match.Renamed.Select(n => n.Path)));
            return match;
        }

        const int cap = 25;
        var listed = compared
            .Select(x => x.Canonical is null || string.Equals(x.Stored, x.Canonical.ToString(), StringComparison.OrdinalIgnoreCase)
                ? x.Stored
                : $"{x.Stored} → {x.Canonical}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shown = listed.Count <= cap
            ? string.Join(", ", listed)
            : string.Join(", ", listed.Take(cap)) + $", (+{listed.Count - cap} more)";

        logger?.LogWarning(
            "GitHub webhook ({Context}) for {Repo} matched NONE of the {Candidates} sync config(s) in "
            + "the mesh — nothing will sync for this delivery. Compared against: {Compared}. If this "
            + "repository was RENAMED, the stored url is stale: GitHub redirects the old name so "
            + "every other operation keeps working, while the payload always carries the CURRENT "
            + "name and can never string-match the old one. Otherwise the hook is installed on a "
            + "repository this mesh does not sync.",
            context, incoming, match.Candidates, shown.Length == 0 ? "(no config carries a parseable url)" : shown);
        return match;
    }

    /// <summary>
    /// Records the repository's CURRENT url on a config that only matched by canonical identity, so
    /// the drift is repaired instead of merely tolerated: the next delivery matches on the free path,
    /// the Space's GitHub settings stop showing a name the repository no longer has, and the
    /// resolver is not asked again.
    ///
    /// <para>Fire-and-forget under the SYSTEM identity (a webhook request is anonymous — its
    /// authorization is the verified HMAC), and never allowed to fail the delivery: a failed repair
    /// leaves the canonical matching doing its job, whereas a non-2xx would make GitHub redeliver.
    /// The write touches ONLY <c>RepositoryUrl</c>, so the RFC 7396 merge patch cannot clobber a
    /// concurrent <c>LastSyncCommitSha</c> from the import this same delivery is about to trigger.</para>
    /// </summary>
    private void RepointToCanonical(
        IReadOnlyList<(MeshNode Node, string Url)> configs, RepoIdentity canonical)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var workspace = hub.GetWorkspace();
        foreach (var (node, url) in configs)
        {
            var repointed = RepointUrl(url, canonical);
            if (string.Equals(repointed, url, StringComparison.Ordinal))
                continue;
            // 🚨 RunAsSystem, never Observable.Using (#1790): the Using shape opens the AsyncLocal
            // scope on the SUBSCRIBING thread and disposes it wherever the work terminates, leaving
            // the subscriber running as System.
            accessService
                .RunAsSystem(() => workspace.GetMeshNodeStream(node.Path).Update(current =>
                {
                    var cfg = current.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger)
                              ?? new GitHubSyncConfig();
                    return current with { Content = cfg with { RepositoryUrl = repointed } };
                }))
                .Subscribe(
                    _ => logger?.LogInformation(
                        "Repointed {Config} from '{Old}' to '{New}' — the repository was renamed.",
                        node.Path, url, repointed),
                    exception => logger?.LogWarning(exception,
                        "Could not repoint {Config} to '{New}'; canonical matching still covers it.",
                        node.Path, repointed));
        }
    }

    /// <summary>
    /// The stored url with its owner/repo replaced by <paramref name="canonical"/>, KEEPING the
    /// original scheme and host — a GitHub Enterprise config must not be silently repointed at
    /// github.com. Falls back to the canonical github.com url when the stored value is not an
    /// absolute uri (the <c>owner/repo</c> shorthand <c>ParseRepoUrl</c> also accepts).
    /// </summary>
    /// <param name="storedUrl">The url currently on the config.</param>
    /// <param name="canonical">The repository's current identity.</param>
    /// <returns>The url to store.</returns>
    internal static string RepointUrl(string storedUrl, RepoIdentity canonical)
        => Uri.TryCreate(storedUrl.Trim(), UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}/{canonical.Owner}/{canonical.Repo}"
            : canonical.Url;

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
            // Off-router issuing — same reason as RecordBuildCompletion above.
            return Observable.Using(
                    () => accessService.ImpersonateAsSystem(),
                    _ => hub.NodeOperationIssuingHub()
                        .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node)).FirstAsync())
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
