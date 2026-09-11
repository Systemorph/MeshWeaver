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
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// Space whose sync config targets that repository + branch a headless import
/// <b>at that build's commit</b>
/// (<see cref="GitHubActivityExtensions.UpdateToProvenCommitFromGitHub"/>, <c>force: false</c> so
/// two-way conflict resolution still protects server-side edits). The mesh writes run under
/// the system identity; the GitHub pull authenticates as the sync config's CREATOR (their
/// connected credential, or the GitHub App when they have none).</para>
///
/// <para>🚨 <b>AT THAT BUILD'S COMMIT, not "latest".</b> The GUI button's
/// <see cref="GitHubActivityExtensions.UpdateToLatestFromGitHub"/> resolves the branch when it
/// fetches, which is right for a human asking for latest and wrong for every machine trigger: the
/// branch can move between the build completing and the fetch happening, and what lands is then a
/// tree no build ever proved. That is Systemorph/MeshWeaver.Plugins#1430 — a ~5 h outage on two
/// production portals. The contract is in <c>Doc/Architecture/SyncRefContract</c>.</para>
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
    private readonly IReadOnlyList<GitHubContentWorkflow> contentWorkflows;

    /// <summary>Initializes a new instance of the <see cref="GitHubWebhookProcessor"/> class.</summary>
    /// <param name="hub">The hub this processor issues its reads and writes from.</param>
    /// <param name="meshService">Mesh query surface for the sync-config fan-out.</param>
    /// <param name="identities">
    /// Canonical repository identity, for matching a config that stores a repository's OLD name
    /// after a rename (#1856). Null degrades to stored-string matching alone — which is exactly what
    /// the zero-match warning then says.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="contentWorkflowOptions">Repository-level content-workflow overrides.</param>
    public GitHubWebhookProcessor(
        IMessageHub hub,
        IMeshService meshService,
        GitHubRepoIdentityResolver? identities = null,
        ILogger<GitHubWebhookProcessor>? logger = null,
        IOptions<GitHubContentWorkflowOptions>? contentWorkflowOptions = null)
    {
        this.hub = hub;
        this.meshService = meshService;
        this.identities = identities;
        this.logger = logger;
        contentWorkflows = contentWorkflowOptions?.Value.Repositories.ToArray() ?? [];
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
    /// A verified GREEN build of the default branch → a headless import AT THAT BUILD'S COMMIT for
    /// every sync source that targets this repo + branch and is not already sitting on it. TRIGGERS
    /// the updates (each its own activity, fire-and-forget with error logging) and emits the number
    /// triggered — it does NOT await the imports, so the webhook response returns within GitHub's
    /// delivery timeout.
    ///
    /// <para>🚨 <b><paramref name="headSha"/>, never the branch — this is the #1430 fix.</b> The
    /// candidate filter has always been the built commit while the import itself said "bring the
    /// Space to latest", and those are the same tree only when nothing merges in between. When
    /// something does, the mesh receives a tree NO build proved: a MeshWeaver.Plugins <c>main</c> run
    /// for <c>8d4920c93</c> finished at 2026-09-06 22:38:18Z with <c>main</c> already past #1413, and
    /// both production portals imported #1413's <c>Store/*</c> sources — against a platform carrying
    /// neither <c>IPaymentProvider</c> nor the Payments module — leaving four <c>Store</c> NodeTypes
    /// in compile <c>Error</c> for ~5 h. The publication and the sources it was compiled from are one
    /// artefact; resolving the ref a second time at fetch time is what split them. The sibling
    /// consumer of the same fact, <c>PluginUpdateWatcher</c>, already read
    /// <c>BuildCompletion.HeadSha</c>; this makes GitSync agree with it. See
    /// <c>Doc/Architecture/SyncRefContract</c>.</para>
    ///
    /// <para>Scoping differs from the old push path in one way that matters: a <c>workflow_run</c>
    /// payload carries no file list, so a source's <c>Subdirectory</c> cannot be used to skip it.
    /// Every source of the repo is brought to the built commit; an unchanged subdirectory imports as
    /// a no-op. The <c>lastSyncCommitSha</c> check below is what keeps that cheap — it makes a re-run
    /// of an already-imported commit (a flake re-run, a manual re-dispatch) trigger nothing at all,
    /// and now compares like with like: what the source RECORDS is the commit it was told to fetch.
    /// 🚨 …and for a source whose import does NOT converge, that check never fires by construction,
    /// which is why <see cref="SkipReason"/> carries a second, weaker one (#3945).</para>
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
                "Green build of {Repo}@{Branch} ({Sha}) → importing {Count} sync source(s) AT THAT COMMIT.",
                repo, branch, headSha, targets.Count);
            var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
            foreach (var t in targets)
                // 🚨 RunAsSystem, never `Observable.Using(() => ImpersonateAsSystem(), …)` — #1790.
                // Rx disposes a Using's resource on whichever thread the INNER observable
                // terminates on, so the subscribing thread stays latched as system-security while
                // the restore lands somewhere else entirely. RunAsSystem opens the scope, composes
                // AND subscribes the work inside it, and leaves it on the same thread — one
                // synchronous frame it owns. Ratcheted down here (Copilot review) because this is
                // the very expression this change rewrites; the file's three remaining sites are
                // untouched work, still on the inventory.
                accessService.RunAsSystem(
                        // 🚨 headSha, NOT "latest" — see the remarks. The candidates were selected
                        // against this commit; importing anything else means the selection and the
                        // import disagree about which tree this build proved (#1430).
                        () => hub.UpdateToProvenCommitFromGitHub(
                            t.SpacePath, t.UserId, headSha, sourceId: t.SourceId))
                    .Subscribe(
                        activity => logger?.LogInformation(
                            "Build-triggered import of {Space} at {Sha} completed ({Activity}).",
                            t.SpacePath, headSha, activity),
                        exception => logger?.LogWarning(exception,
                            "Build-triggered import of {Space} at {Sha} (source {Source}) failed.",
                            t.SpacePath, headSha, t.SourceId ?? "(primary)"));
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
                // 🚨 What THIS instance's identity has sealed (MeshWeaver.Plugins#1430) — read once
                // per delivery. A module-bearing repository's sources advance only to the commit
                // sealed for this instance; the green build alone proved the tree compiles
                // somewhere, not here. Repositories the instance runs no publication of are
                // unaffected (SealedSyncGate: no attributable seal ⇒ today's behaviour).
                var identity = PrebuiltAssemblySeeder.LiveFrameworkMvid;
                var sealedForThisIdentity = SealedPublicationIndex.ReadFor(
                    hub.ServiceProvider.GetService<IConfiguration>()?[ShippedPrebuiltBundles.PublishedRootConfigKey],
                    identity, logger);
                var held = 0;
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
                    if (SealedSyncGate.Decide(repo, headSha, cfg?.LastSyncCommitSha, sealedForThisIdentity, identity)
                        is { Proceed: false } hold)
                    {
                        held++;
                        skipped.Add($"{node.Path} ({hold.HoldReason})");
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
                if (held > 0)
                    // Its own line, at Warning: a source held back by the seal is content that
                    // quietly stops arriving until the registry seals this commit for this identity
                    // — an operator must be able to find it without reading the skip detail.
                    logger?.LogWarning(
                        "Green build of {Repo}@{Branch} ({Sha}): {Held} sync source(s) HELD — the build is "
                        + "not sealed for this instance's framework identity {Identity}; they advance when it is "
                        + "(MeshWeaver.Plugins#1430).",
                        repo, branch, headSha, held, identity);

                return targets;
            });

    /// <summary>
    /// Why a config that DOES target this repo is not being updated, or <c>null</c> when it is.
    /// The reason strings are log copy — a skipped Space must be traceable to the exact predicate
    /// that dropped it, never inferred from its absence.
    ///
    /// <para>🚨 <b>Issue #3945 — the last arm is the one that stops a non-converging source
    /// re-cloning its whole repository on every green build.</b> Until it existed, exactly ONE
    /// reason made a delivery free — <c>lastSyncCommitSha == headSha</c> — and that same field is
    /// deliberately HELD by <c>GitHubSyncService.MayAdvanceBaseline</c> whenever an import did not
    /// converge (#675 / #677 / #2229 item C). So the cheapness gate and the convergence guard were
    /// one field, and a source that could never converge paid a full <c>git fetch --depth 1</c> of
    /// its ENTIRE repository plus a full parse on every delivery, at the SOURCE repository's CI
    /// cadence: measured 2026-09-10, <c>Essentials/_GitSync</c> on memex.meshweaver.cloud had been
    /// doing that against MeshWeaver.Plugins since 2026-08-10 — 31 days, 436 commits — while its 33
    /// siblings on the same repository, same webhook, same schedule, were skipped for free.</para>
    ///
    /// <para>🚨 <b>And why it is TWO conditions, not one.</b> Today's re-clone-on-every-delivery is
    /// also, accidentally, the retry loop for the failures <c>StaticRepoImporter.IsContentVerdict</c>
    /// deliberately refuses to call final — an unreachable store, an owner that did not answer, a
    /// hub that went down mid-import. Skipping on "same commit, already attempted" ALONE would make
    /// every one of those wait for the next commit, which on a quiet repository is never: a fix that
    /// strands every self-healing source. So the skip also requires the recorded verdict to be FINAL
    /// at that commit (<see cref="GitHubSyncConfig.LastAttemptWasFinal"/> ←
    /// <see cref="StaticRepoImportResult.VerdictIsFinal"/>) — a preserved node or an
    /// all-content-verdict refusal settles; <c>ImportedWithErrors</c>, a whole-import <c>Failed</c>
    /// and a truncated listing do not.</para>
    ///
    /// <para>The baseline stays untouched, so nothing here weakens the guards above it: the next
    /// NEW commit re-attempts the whole source unscoped, and the GUI's own "Update to latest"
    /// (<c>GitHubSyncService.ReimportAtCommit</c>) never consults this predicate at all.</para>
    /// </summary>
    private static string? SkipReason(GitHubSyncConfig? cfg, string branch, string headSha)
        => cfg is null ? "config content could not be read"
            : cfg.Direction == SyncDirection.ExportOnly ? "direction is ExportOnly"
            : !string.Equals(cfg.Branch, branch, StringComparison.OrdinalIgnoreCase)
                ? $"branch '{cfg.Branch}' != built branch '{branch}'"
            : string.Equals(cfg.LastSyncCommitSha, headSha, StringComparison.OrdinalIgnoreCase)
                ? "already at this commit"
            // 🚨 Ordered AFTER "already at this commit" on purpose: a converged source keeps
            // reporting the reason it has always reported, so this arm's appearance in a log is
            // itself the signal that a source is settled-but-not-converged.
            : cfg.LastAttemptWasFinal
              && string.Equals(cfg.LastAttemptedCommitSha, headSha, StringComparison.OrdinalIgnoreCase)
                ? $"already attempted at this commit with a final verdict ('{cfg.LastSyncOutcome}') — "
                  + "re-reading the same bytes re-derives it; the next new commit re-attempts"
            : null;

    /// <summary>Maps a config node path (<c>{space}/_GitSync</c> or <c>{space}/_GitSync/{sourceId}</c>)
    /// to the Space + source id it configures.</summary>
    internal static PushTarget? ToPushTarget(MeshNode configNode)
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
    /// 🚨 <b>The <c>workflow_run</c> triggers under which the repository's CONTENT CI is eligible to
    /// publish — an ALLOW-LIST, so an event name nobody has considered is REFUSED rather than
    /// admitted.</b> This is HOW the workflow started, never evidence of WHAT it checked; the
    /// independent <see cref="IsRepositoryContentWorkflow"/> guard supplies that identity. A
    /// deny-list here fails open: the next trigger GitHub invents would publish.
    ///
    /// <para><b>Why each one is admitted.</b>
    /// <list type="bullet">
    /// <item><c>push</c> — the branch moved and its content CI ran. The original case.</item>
    /// <item><c>repository_dispatch</c> — GitHub only ever runs a dispatched workflow from the
    /// DEFAULT branch, and the run's <c>head_sha</c> is that branch's tip. This is how the CONTENT
    /// CI re-verifies every satellite after a platform release: no commit to push, the same tree, a
    /// genuine green verdict on it.</item>
    /// <item><c>schedule</c> — the content CI's scheduled self-check; a cron run only exists on the
    /// default branch. An unrelated scheduled workflow is rejected by workflow identity.</item>
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
    /// <para>🚨 Before #3978 this allow-list and the branch check were the WHOLE decision. A green
    /// scheduled PR updater therefore published a red repository tree twenty times, and a green
    /// push-triggered chart check could do the same. The workflow-path guard is deliberately not
    /// folded into this trigger predicate: HOW and WHAT are independent payload facts and each must
    /// fail closed.</para>
    ///
    /// <para><b>An accepted re-verification of unchanged content must not cause churn.</b> A source
    /// already sitting on the built sha is skipped by <see cref="SkipReason"/>. 🚨 That claim was
    /// FALSE for two years for a source whose import did not converge, because
    /// <c>lastSyncCommitSha</c> is deliberately held back there. #3945 supplies the separate
    /// "already attempted these exact bytes with a final verdict" arm. Dropping <c>schedule</c> or
    /// <c>repository_dispatch</c> would lose genuine content-CI signals; the trigger set remains
    /// broad, while #3978 independently proves which workflow may carry the verdict.</para>
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
    /// Whether a <c>workflow_run</c> trigger is eligible to carry a content-CI verdict.
    /// Fail-closed: an empty, missing or unrecognised event name is NOT eligible.
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

    /// <summary>The conventional content-CI path every GitSync repository uses. The node-repo
    /// scaffold and the shared gate both own this filename, so it is a repository contract rather
    /// than a display name a maintainer can casually edit.</summary>
    internal const string StandardContentWorkflowPath = ".github/workflows/ci.yml";

    /// <summary>Core predates the node-repo convention and is one of the platform exceptions.</summary>
    internal const string CoreContentWorkflowPath = ".github/workflows/dotnet-test.yml";

    /// <summary><c>Systemorph/Memex</c> — the deployment-configuration repository whose
    /// <c>mesh/Deployments</c> tree two portals sync — predates the convention exactly as core does,
    /// and its content CI is <c>Memex Build</c>.</summary>
    internal const string MemexContentWorkflowPath = ".github/workflows/build.yml";

    /// <summary>
    /// 🚨 <b>The platform's OWN repositories whose content CI predates the node-repo convention.</b>
    /// These are DECLARATIONS, not special cases: the fleet ships the platform, so the fact "this
    /// repository's content CI is at P" travels with the platform rather than waiting for every
    /// portal's configuration to be edited. An override under
    /// <see cref="GitHubContentWorkflowOptions"/> still wins, for a repository the platform does not
    /// know about.
    ///
    /// <para><b>Measured 2026-09-11, with the denominator.</b> Nine repositories hold an
    /// <c>Admin/_Build/{owner}.{repo}</c> record — the same nine on memex.meshweaver.cloud and on
    /// memex.systemorph.com, both listings <c>truncated:false</c>. Eight satisfy the convention or
    /// core's exception. The ninth, <c>Systemorph/Memex</c>, has NO <c>.github/workflows/ci.yml</c>
    /// at all (its workflows are <c>build.yml</c>, <c>config-key-coverage.yml</c>,
    /// <c>deploy-drift.yml</c>, <c>helm-release.yml</c>, <c>image-pins.yml</c>, <c>smoke.yml</c>) —
    /// so on the convention alone every one of its deliveries would be refused and
    /// <c>Deployments/_GitSync</c> would freeze on BOTH portals, silently, which is precisely the
    /// MeshWeaver.Plugins#1194 shape #3978 exists to avoid recreating.</para>
    /// </summary>
    private static readonly ImmutableArray<(RepoIdentity Repository, string Path)> PlatformContentWorkflows =
    [
        (new RepoIdentity("Systemorph", "MeshWeaver"), CoreContentWorkflowPath),
        (new RepoIdentity("Systemorph", "Memex"), MemexContentWorkflowPath),
    ];

    /// <summary>Where a repository's content-CI path came from — the third state made nameable.</summary>
    internal enum ContentWorkflowSource
    {
        /// <summary>A deployment declared it under <c>GitHub:ContentWorkflows:Repositories</c>.</summary>
        Configured,

        /// <summary>The platform declares it for one of its own repositories
        /// (<see cref="PlatformContentWorkflows"/>).</summary>
        Platform,

        /// <summary>Nobody declared anything: the node-repo CONVENTION is being presumed. Correct for
        /// every repository whose CI calls the shared <c>node-repo-validate</c> lane, because that
        /// lane's <c>check-content-ci-path.py</c> reds if the file moves — and a PRESUMPTION for
        /// anything else, which is why a refusal under this source is reported differently.</summary>
        Convention,
    }

    /// <summary>One repository's content-CI path and where that path came from.</summary>
    internal sealed record ContentWorkflowDeclaration(string Path, ContentWorkflowSource Source);

    /// <summary>
    /// The one workflow whose green verdict proves a repository's CONTENT, <b>and where that claim
    /// comes from</b>. A trigger says why a workflow ran; it does not say what that workflow
    /// checked. GitHub sends the stable workflow file path in every <c>workflow_run</c> payload, so
    /// identity is keyed on that path rather than on the mutable display name.
    ///
    /// <para>🚨 <b>Returning the SOURCE, not just the path, is the point.</b> A bare path collapses
    /// "this deployment declared <c>ci.yml</c>" into "nobody declared anything and <c>ci.yml</c> was
    /// presumed" — the same two-answers-in-one shape as the trigger proxy #3978 removed, one level
    /// down. They must stay apart because they have OPPOSITE failure modes: a refusal under a
    /// declaration is routine (some other workflow finished green), while a refusal under a
    /// presumption may be a repository whose content CI is somewhere else entirely and whose every
    /// Space is therefore frozen. <see cref="ReportRefusedWorkflow"/> is what acts on the
    /// difference.</para>
    ///
    /// <para>Fail-closed by construction: a repository with no workflow at its expected path has no
    /// automatic publish signal — exactly like a repository with no content CI at all. An unrelated
    /// green workflow can never stand in for it.</para>
    /// </summary>
    internal static ContentWorkflowDeclaration ContentWorkflowFor(
        RepoIdentity repository,
        IEnumerable<GitHubContentWorkflow>? overrides = null)
    {
        var configured = overrides?.FirstOrDefault(entry =>
            GitHubRepoIdentityResolver.Parse(entry.Repository)?.Matches(repository) == true
            && !string.IsNullOrWhiteSpace(entry.Path));
        if (configured is not null)
            return new ContentWorkflowDeclaration(configured.Path.Trim(), ContentWorkflowSource.Configured);
        foreach (var (declared, path) in PlatformContentWorkflows)
            if (declared.Matches(repository))
                return new ContentWorkflowDeclaration(path, ContentWorkflowSource.Platform);
        return new ContentWorkflowDeclaration(
            StandardContentWorkflowPath, ContentWorkflowSource.Convention);
    }

    /// <summary>The path <see cref="ContentWorkflowFor"/> resolves, without its source.</summary>
    internal static string ExpectedContentWorkflowPath(
        RepoIdentity repository,
        IEnumerable<GitHubContentWorkflow>? overrides = null)
        => ContentWorkflowFor(repository, overrides).Path;

    /// <summary>Whether this run is the repository's declared-by-convention content CI. Paths are
    /// Git paths and therefore compared case-sensitively; a missing path means nothing was proved.</summary>
    internal static bool IsRepositoryContentWorkflow(
        RepoIdentity repository,
        string? workflowPath,
        IEnumerable<GitHubContentWorkflow>? overrides = null)
        => workflowPath is { Length: > 0 }
           && string.Equals(
               workflowPath,
               ExpectedContentWorkflowPath(repository, overrides),
               StringComparison.Ordinal);

    /// <summary>
    /// A verified <c>workflow_run</c> → the repository's <see cref="BuildCompletion"/> node.
    ///
    /// <para>GitSync records a FACT and stops there: "repo X built green at sha Y". It does not know
    /// or care who is listening. Consumers — today the plugin catalog — SUBSCRIBE to the node's
    /// stream and decide for themselves whether anything they care about actually changed. That
    /// keeps the two sides decoupled at compile time (the node's content type lives in
    /// MeshWeaver.Graph, which both reference) and means a second consumer costs nothing here.</para>
    ///
    /// <para><b>Only a completed, successful CONTENT-CI run is recorded.</b> A failed or cancelled run may still
    /// have produced artifacts; writing that as a build completion is how a broken build reaches
    /// consumers. Every green run of that one workflow rewrites the node — including doc-only
    /// commits, reverts, and re-runs of an unchanged tree — because deciding "did anything change"
    /// needs content identity, which is the consumer's business, not the webhook's. Green probes,
    /// deploys, chart checks and PR updaters prove no content and are ignored.</para>
    ///
    /// <para><b>Three independent guards decide "is this a publish signal".</b> The run's TRIGGER must
    /// be one of <see cref="PublishSignalTriggers"/> (an allow-list — unknown events fail closed),
    /// its <c>head_branch</c> must be the repository's default branch
    /// (<see cref="IsDefaultBranchBuild"/>), AND the run's stable workflow file path must be that
    /// repository's content CI (<see cref="IsRepositoryContentWorkflow"/>). None subsumes another:
    /// trigger is HOW it ran, branch is WHICH tree, and workflow path is WHAT it proved.</para>
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

        // 🚨 A trigger is only HOW the workflow started. It says nothing about WHAT the workflow
        // proved. A scheduled PR updater and a push-triggered chart check both used to pass the two
        // guards above and overwrite BuildCompletion even while the repository's content CI was
        // red (#3978). The workflow FILE is the repository-level identity: display names are mutable,
        // while the fleet's content-CI path is part of the node-repo contract.
        var workflowPath = GetString(run, "path");
        var declaration = ContentWorkflowFor(target, contentWorkflows);
        if (!IsRepositoryContentWorkflow(target, workflowPath, contentWorkflows))
        {
            if (string.IsNullOrWhiteSpace(workflowPath))
            {
                // "The payload did not say WHAT ran" is its own answer and always deserves a human:
                // it is not a refusal on the evidence, it is the absence of evidence.
                logger?.LogWarning(
                    "workflow_run webhook for {Repo}: green '{Workflow}' run carried no workflow path, "
                    + "so it cannot be verified as the repository content CI at '{Expected}' — no build record.",
                    repoUrl, GetString(run, "name"), declaration.Path);
                return Observable.Return(0);
            }
            return ReportRefusedWorkflow(
                    target, repoUrl, GetString(run, "name"), workflowPath, declaration)
                .Select(_ => 0);
        }

        var completion = new BuildCompletion
        {
            RepositoryUrl = repoUrl,
            Branch = GetString(run, "head_branch") ?? "",
            HeadSha = headSha,
            WorkflowName = GetString(run, "name"),
            // The path the run was ADMITTED on, not the name it happens to display. This is what
            // lets a later refusal tell "this repository's content CI has never been seen" from
            // "some other workflow finished green" — see ReportRefusedWorkflow.
            WorkflowPath = workflowPath,
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
                    //
                    // 🚨 The line NAMES WHAT THE DROP COST, because nothing else will (#3374). The
                    // comment below states the invariant this path breaks — the record and the sync
                    // "hang off this one green-build event so they cannot disagree about what
                    // shipped" — and here they disagree: neither happened, GitHub is answered 200 so
                    // it will not redeliver, and no other lane retries this. A reader who finds only
                    // "recording … failed" has to derive all of that from the source; 154 of these
                    // were logged on memex-cloud in one week and every one of them cost that
                    // derivation. The sibling failure path below already states its outcome
                    // ("recorded, but triggering the sync failed"); this one now meets the same bar.
                    logger?.LogWarning(
                        "Recording build completion at {Path} failed: {Error}. The green build of "
                        + "{Repo} at {Sha} is NOT recorded AND the sync it authorises did NOT run. "
                        + "GitHub is answered 200, so there is no redelivery, and nothing retries "
                        + "this elsewhere — the build fact is lost unless someone replays it.",
                        path, d.Message.Error, repoUrl, headSha);
                    // 🚨 …and now the fact is KEPT rather than only mourned (#3374). The warning
                    // above is the floor, not the record: on memex-cloud the ATTEMPT is logged at
                    // Information and Information is not emitted, so 154 of these in one week had
                    // no denominator at all. The node is queryable, survives the pod, and carries
                    // the payload verbatim, so the lost build can be REPLAYED.
                    //
                    // Still returns 0 and still answers GitHub 200 — recording the miss must not
                    // change the delivery's outcome, and a non-2xx is the redelivery storm the
                    // comment above exists to avoid.
                    return RecordMissedBuildFact(target, completion, d.Message.Error)
                        .Select(_ => 0);
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

    /// <summary>
    /// Keeps a green build the mesh refused to record, at
    /// <c>Admin/_MissedBuild/{owner}.{repo}</c> (Systemorph/MeshWeaver#3374).
    ///
    /// <para><b>Never fails the delivery.</b> Every outcome — a refusal, a throw — is logged and
    /// swallowed. This runs on a path that has ALREADY lost the build fact; turning that into a
    /// non-2xx would trade a silent loss for the redelivery storm the caller's comment exists to
    /// avoid, and turning it into a throw would lose the caller's own warning too.</para>
    ///
    /// <para>🚨 <b>It shares a failure domain with the write that just failed, and says so.</b>
    /// This is a different node with a different owning hub, so it survives the per-node faults
    /// actually observed (an owner returning no verdict; initial state not arriving) — but a
    /// cluster-wide fault takes both. When it does, the log line is all that is left, which is why
    /// the refusal below is logged at Warning naming what is now unrecorded, rather than counted as
    /// a success.</para>
    /// </summary>
    private IObservable<System.Reactive.Unit> RecordMissedBuildFact(
        RepoIdentity target, BuildCompletion completion, string? error)
    {
        var path = MissedBuildFact.PathFor(target.Owner, target.Repo);
        var slash = path.LastIndexOf('/');
        var node = new MeshNode(path[(slash + 1)..], path[..slash])
        {
            NodeType = MissedBuildFact.NodeType,
            Name = $"{target.Owner}/{target.Repo} missed build",
            State = MeshNodeState.Active,
            Content = MissedBuildFact.For(completion, error, DateTimeOffset.UtcNow),
        };

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.NodeOperationIssuingHub()
                    .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
                    .FirstAsync())
            .Select(r =>
            {
                if (r.Message.Success)
                    logger?.LogInformation(
                        "Missed build fact for {Repo} at {Sha} recorded at {Path} — replay it from "
                        + "there, or let the next green build of the same workflow supersede it.",
                        completion.RepositoryUrl, completion.HeadSha, path);
                else
                    logger?.LogWarning(
                        "Could not record the missed build fact at {Path} either: {Error}. The "
                        + "green build of {Repo} at {Sha} is now recorded NOWHERE — this log line "
                        + "is the only remaining witness.",
                        path, r.Message.Error, completion.RepositoryUrl, completion.HeadSha);
                return System.Reactive.Unit.Default;
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Could not record the missed build fact at {Path} either. The green build of "
                    + "{Repo} at {Sha} is now recorded NOWHERE — this log line is the only "
                    + "remaining witness.",
                    path, completion.RepositoryUrl, completion.HeadSha);
                return Observable.Return(System.Reactive.Unit.Default);
            });
    }

    /// <summary>
    /// 🚨 <b>The positive signal for the one failure mode this gate can cause: a SILENT freeze.</b>
    ///
    /// <para>#3978 made the repository's own content CI the publish signal, which is correct and
    /// which has exactly one way to go wrong — the platform expects a path the repository does not
    /// use. Then every delivery is refused, GitHub is answered 200, every Space that syncs the
    /// repository stops advancing, and nothing says so. That is MeshWeaver.Plugins#1194 verbatim,
    /// whose ENTIRE cost was that 38 h of deliveries all reported success while nothing arrived. A
    /// gate whose failure is indistinguishable from its success is not a gate.</para>
    ///
    /// <para><b>The report is CONDITIONED, because an unconditional one would be noise and noise is
    /// how a real line gets missed.</b> Refusing a green run is the NORMAL case: core refuses ~200 a
    /// day (Chart Gate, Hosting Operator, the synthetic probe), and the <c>*/10</c> PR-updater cron
    /// alone is 144 a day in three satellites. Two facts narrow it to the shape that is actually an
    /// outage:</para>
    /// <list type="number">
    ///   <item><b>Does anything sync this repository?</b> No sync config ⇒ no Space can be frozen,
    ///   so the refusal cost nothing. Matched on the STORED url only — no canonical resolution, and
    ///   deliberately not <see cref="ConfigsTargeting"/>, whose zero-match Warning would then fire
    ///   on every refused run of every repository this mesh does not sync.</item>
    ///   <item><b>Has the expected content CI EVER been accepted for it?</b> Read off
    ///   <see cref="BuildCompletion.WorkflowPath"/> — the path the last accepted run was admitted
    ///   on. If it equals what is expected today, the content CI exists and is arriving, and this
    ///   refusal is some other workflow finishing green. If it does not — or there is no record at
    ///   all — the mesh has never once identified this repository's content CI.</item>
    /// </list>
    ///
    /// <para>So the Warning fires exactly when a repository the mesh SYNCS has had a publishable
    /// green run refused while no run has ever been accepted at the expected path, and it SELF-
    /// CLEARS the moment one is: the repository's next genuine content-CI build writes the path and
    /// every later refusal drops back to Debug. It is the same read the gate itself makes, so the
    /// two cannot disagree.</para>
    ///
    /// <para><b>The residual, stated rather than implied.</b> A repository that has already
    /// published, and THEN moves its content CI, keeps a record whose path still equals the expected
    /// one, so this stays quiet. That case is covered a layer up instead: the shared
    /// <c>node-repo-validate</c> lane runs <c>check-content-ci-path.py</c> against every satellite's
    /// tree, so moving the file reds that repository's own CI, and
    /// <c>CoreContentCiRemainsAtThePublishSignalPath</c> does the same for core. Neither covers a
    /// repository that calls neither — which is why <see cref="PlatformContentWorkflows"/> declares
    /// those rather than leaving them to the convention.</para>
    ///
    /// <para>Reactive end-to-end and never faulting: this runs on a delivery that has already
    /// decided its outcome, so a failure to REPORT must not change that outcome (and must not turn
    /// a 200 into the redelivery storm a non-2xx would cause).</para>
    /// </summary>
    /// <param name="target">The repository the refused run belongs to.</param>
    /// <param name="repoUrl">Its url, as the log lines name it.</param>
    /// <param name="workflowName">The refused run's display name — copy for the log line only.</param>
    /// <param name="actualPath">The refused run's stable workflow path.</param>
    /// <param name="declaration">The path expected for this repository, and where that came from.</param>
    /// <returns>Always one <c>Unit</c>; the refusal itself is the caller's answer.</returns>
    private IObservable<System.Reactive.Unit> ReportRefusedWorkflow(
        RepoIdentity target,
        string repoUrl,
        string? workflowName,
        string actualPath,
        ContentWorkflowDeclaration declaration)
        => SpacesSyncingByStoredUrl(target)
            .SelectMany(spaces => spaces.Count == 0
                ? Observable.Return(System.Reactive.Unit.Default).Do(_ => logger?.LogDebug(
                    "workflow_run webhook for {Repo}: green '{Workflow}' is '{Actual}', not the "
                    + "repository content CI at '{Expected}' — not a publish signal. No sync "
                    + "config targets this repository, so nothing was withheld.",
                    repoUrl, workflowName, actualPath, declaration.Path))
                : ContentCiHasEverBeenAccepted(target, declaration.Path).Select(seen =>
                {
                    if (seen)
                        logger?.LogDebug(
                            "workflow_run webhook for {Repo}: green '{Workflow}' is '{Actual}', not "
                            + "the repository content CI at '{Expected}' — not a publish signal. "
                            + "That content CI has published before, so this is a routine refusal.",
                            repoUrl, workflowName, actualPath, declaration.Path);
                    else
                        logger?.LogWarning(
                            "workflow_run webhook for {Repo}: I COULD NOT DETERMINE THIS "
                            + "REPOSITORY'S CONTENT CI. A green '{Workflow}' ('{Actual}') on the "
                            + "default branch was refused because the content CI is expected at "
                            + "'{Expected}' ({Source}), and NO run has ever been accepted at that "
                            + "path. {SpaceCount} Space(s) sync this repository ({Spaces}) and every "
                            + "one of them is FROZEN until a green run arrives from '{Expected}' — "
                            + "GitHub is answered 200, so nothing else will report this. Either the "
                            + "repository's content CI is elsewhere, in which case declare it under "
                            + "'{Section}:Repositories' as {{ Repository: '{Repo}', Path: '<its "
                            + "path>' }}, or it has no content CI, in which case it has no automatic "
                            + "publish signal.",
                            repoUrl, workflowName, actualPath, declaration.Path, declaration.Source,
                            spaces.Count, string.Join(", ", spaces), declaration.Path,
                            GitHubContentWorkflowOptions.ConfigSection, repoUrl);
                    return System.Reactive.Unit.Default;
                }))
            .Catch((Exception ex) =>
            {
                // The refusal already stands; failing to CLASSIFY it must not change the delivery's
                // outcome. Said at Warning rather than swallowed, because the thing that just failed
                // is the freeze detector itself.
                logger?.LogWarning(ex,
                    "workflow_run webhook for {Repo}: green '{Workflow}' ('{Actual}') was refused as "
                    + "not the content CI at '{Expected}', but whether that refusal freezes a synced "
                    + "Space could not be determined.",
                    repoUrl, workflowName, actualPath, declaration.Path);
                return Observable.Return(System.Reactive.Unit.Default);
            });

    /// <summary>
    /// The distinct Space paths whose sync config names <paramref name="incoming"/> by its STORED
    /// url — no canonical resolution and, deliberately, no zero-match report.
    ///
    /// <para>This is the cheap half of <see cref="ConfigsTargeting"/> and it is used only to decide
    /// whether a REFUSAL cost anything. Using the full matcher here would fire its zero-match
    /// Warning on every refused run of every repository the mesh does not sync — hundreds a day —
    /// and burying that Warning is exactly the outcome it exists to prevent. The cost of the
    /// narrower match is that a repository whose configs still store an OLD name reads as unsynced
    /// and is reported at Debug; its first accepted delivery repoints those urls, after which the
    /// stored match succeeds.</para>
    /// </summary>
    private IObservable<IReadOnlyList<string>> SpacesSyncingByStoredUrl(RepoIdentity incoming)
        => QueryConfigNodesAsSystem().Select(c => (IReadOnlyList<string>)c.Items
            .Select(node => GitHubRepoIdentityResolver.Parse(
                node.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger)?.RepositoryUrl) is { } id
                && incoming.Matches(id)
                    ? node.Path.Split('/', 2)[0]
                    : null)
            .Where(s => s is { Length: > 0 })
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray());

    /// <summary>
    /// Whether a run at <paramref name="expectedPath"/> has ever been ACCEPTED for this repository —
    /// read off the build record's own <see cref="BuildCompletion.WorkflowPath"/>.
    ///
    /// <para>🚨 A <c>scope:children</c> LISTING of <c>Admin/_Build</c>, never a point read of
    /// <c>Admin/_Build/{owner}.{repo}</c>. The whole question here is about a repository that may
    /// have NO record, and a point read of an absent node makes its owner answer a routing NotFound
    /// that terminates the stream and opens the storm-breaker on that path — which would then
    /// fast-fail the WRITES the next genuine build needs. The listing answers existence and content
    /// in one query, which is what the CQRS rule asks for.</para>
    ///
    /// <para>The listing is path-scoped because the Admin partition is invisible to an unscoped
    /// query — <see cref="BuildCompletion.WatchQuery"/> carries that reasoning and its measurement,
    /// and is reused here rather than restated.</para>
    /// </summary>
    private IObservable<bool> ContentCiHasEverBeenAccepted(RepoIdentity target, string expectedPath)
    {
        var recordPath = BuildCompletion.PathFor(target.Owner, target.Repo);
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        // RunAsSystem, never Observable.Using(ImpersonateAsSystem): store and restore of the
        // identity must land on the same thread (AGENTS.md; #1790). The webhook request is
        // anonymous, so there is no ambient identity that could read the Admin partition.
        return accessService.RunAsSystem(() => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery(BuildCompletion.WatchQuery).Complete())
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Take(1))
            .Select(c => c.Items
                .Where(n => string.Equals(n.Path, recordPath, StringComparison.Ordinal))
                .Select(n => n.ContentAs<BuildCompletion>(hub.JsonSerializerOptions, logger))
                .Any(b => b is not null
                          && string.Equals(b.WorkflowPath, expectedPath, StringComparison.Ordinal)));
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
    internal IObservable<RepoMatch> ConfigsTargeting(RepoIdentity incoming, string context)
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
                // 🚨 The TYPED overload (#3623). The untyped shape read the config through
                // `ContentAs<GitHubSyncConfig>(…) ?? new GitHubSyncConfig()`, which answers the same
                // empty record for "no content yet" and for "content is present and this build
                // cannot read it" — and the write then persisted that empty record, erasing the
                // token reference, the branch, the path mapping and `LastSyncCommitSha` on a repo
                // whose only sin was being renamed. Here `null` means ABSENT and only absent;
                // unreadable content faults, the write does NOT happen, and the onError arm below
                // says so — which is the right outcome, because canonical matching already covers
                // this delivery and a repair is never worth the record.
                .RunAsSystem(() => workspace.GetMeshNodeStream(node.Path)
                    .Update<GitHubSyncConfig>((current, cfg) => current with
                    {
                        Content = (cfg ?? new GitHubSyncConfig()) with { RepositoryUrl = repointed }
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
