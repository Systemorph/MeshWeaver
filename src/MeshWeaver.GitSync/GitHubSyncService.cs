using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The mesh ↔ GitHub sync engine. EXPORT ("sync back") mirrors a Space's content
/// subtree into a GitHub repo as a single commit; IMPORT creates a new Space (or
/// re-imports an existing one) from a repo at a chosen branch or commit, reusing the
/// documented <see cref="StaticRepoImporter"/> pipeline.
///
/// <para>🚨 Reactive end-to-end (no <c>async</c>/<c>await</c>/<c>Task</c> in any
/// signature). Every blocking/async leaf — Octokit calls and the OAuth HTTP — runs
/// inside <see cref="IIoPool"/> per <c>Doc/Architecture/ControlledIoPooling.md</c>.
/// File-format parse/serialize is pure in-memory CPU and runs synchronously (no pool).</para>
/// </summary>
public sealed class GitHubSyncService
{
    /// <summary>The fixed node id of a Space's GitHub-sync config satellite (<c>{space}/_GitSync</c>).</summary>
    public const string ConfigId = AccessAssignmentGuard.SyncConfigId;
    /// <summary>The <see cref="MeshNode.NodeType"/> of the sync config node.</summary>
    public const string ConfigNodeType = "GitHubSyncConfig";
    /// <summary>The <see cref="MeshNode.NodeType"/> identifying a Space (the unit GitHub sync acts on).</summary>
    public const string SpaceNodeType = "Space";

    private readonly IMessageHub hub;
    private readonly IMeshService meshService;
    private readonly IGitHubRepoClient repoClient;
    private readonly GitHubCredentialService credentials;
    private readonly GitHubAppTokenService? appTokens;
    private readonly ILogger? logger;
    private readonly FileFormatParserRegistry parsers;

    /// <summary>Initializes a new instance of the <c>GitHubSyncService</c> class.</summary>
    /// <param name="hub">The message hub used for node create/update and workspace access.</param>
    /// <param name="meshService">Mesh service used for node creation and descendant queries.</param>
    /// <param name="repoClient">The GitHub repo client that performs the actual push/fetch operations.</param>
    /// <param name="credentials">Per-user GitHub credential store providing the OAuth access token.</param>
    /// <param name="logger">Optional logger.</param>
    public GitHubSyncService(
        IMessageHub hub,
        IMeshService meshService,
        IGitHubRepoClient repoClient,
        GitHubCredentialService credentials,
        ILogger<GitHubSyncService>? logger = null,
        GitHubAppTokenService? appTokens = null)
    {
        this.hub = hub;
        this.meshService = meshService;
        this.repoClient = repoClient;
        this.credentials = credentials;
        this.appTokens = appTokens;
        this.logger = logger;
        parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions, hub.ServiceProvider.GetServices<IFileFormatParser>());
    }

    /// <summary>The sync-config node path for a Space: <c>{spacePath}/_GitSync</c>.</summary>
    /// <param name="spacePath">The Space (partition root) path.</param>
    /// <returns>The config node path.</returns>
    public static string ConfigPath(string spacePath) => $"{spacePath}/{ConfigId}";

    /// <summary>
    /// The sync-config node path for one of a Space's sync sources. The PRIMARY source
    /// (null/empty <paramref name="sourceId"/>) lives at <c>{spacePath}/_GitSync</c>;
    /// every additional source is a child: <c>{spacePath}/_GitSync/{sourceId}</c>. All
    /// sources carry the same <see cref="GitHubSyncConfig"/> content (repo, branch,
    /// <see cref="GitHubSyncConfig.Direction"/>, last-sync state).
    /// </summary>
    /// <param name="spacePath">The Space (partition root) path.</param>
    /// <param name="sourceId">The source id, or null/empty for the primary source.</param>
    /// <returns>The config node path for that source.</returns>
    public static string ConfigPath(string spacePath, string? sourceId) =>
        string.IsNullOrEmpty(sourceId) ? ConfigPath(spacePath) : $"{spacePath}/{ConfigId}/{sourceId}";

    // ══════════════════════════════════════════════════════════════════════════
    //  EXPORT — mesh → GitHub (the "sync back")
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mirrors the Space subtree into the configured GitHub repo of one sync source as a
    /// single commit, authenticated as <paramref name="userId"/>, and stores the resulting
    /// commit SHA on that source's <see cref="GitHubSyncConfig"/>. Rejected when the
    /// source's <see cref="GitHubSyncConfig.Direction"/> is
    /// <see cref="SyncDirection.ImportOnly"/>. Emits the push result.
    /// <paramref name="progress"/> (when the caller runs as an activity: <c>ctx.Log</c>)
    /// receives per-node problems — e.g. a node skipped from the export — so they land on
    /// the activity log instead of only in the server log.
    /// </summary>
    public IObservable<GitHubPushResult> SyncToGitHub(
        string spacePath, string userId, string? sourceId = null,
        Action<string, LogLevel>? progress = null)
    {
        return ReadConfig(spacePath, sourceId).Take(1).SelectMany(config =>
        {
            if (config?.RepositoryUrl is not { Length: > 0 } repoUrl)
                return Observable.Throw<GitHubPushResult>(new InvalidOperationException(
                    "No repository URL is set for this Space yet. In the Repository section above, enter a " +
                    "URL like https://github.com/owner/repo (the repo is created automatically if it doesn't " +
                    "exist), then Sync."));

            if (config.Direction == SyncDirection.ImportOnly)
                return Observable.Throw<GitHubPushResult>(new InvalidOperationException(
                    $"This sync source is import-only (repo → mesh): exporting to {repoUrl} is not allowed. " +
                    "Change the source's Sync direction to Bidirectional or Export-only to commit."));

            return ResolveAuth(userId).SelectMany(auth =>
            {
                var token = auth.Token;
                return SnapshotNodes(spacePath, SyncIgnore.For(config)).SelectMany(nodes =>
                    SerializeAll(nodes, spacePath, progress).SelectMany(files =>
                    {
                        // App-identity exports author as the bot (no personal credential involved).
                        var (name, email) = auth.Credential is null
                            ? ("meshweaver-app[bot]", "meshweaver-app[bot]@users.noreply.github.com")
                            : AuthorIdentity(auth.Credential);
                        var request = new GitHubPushRequest
                        {
                            RepositoryUrl = repoUrl,
                            Branch = config.Branch,
                            Subdirectory = config.Subdirectory,
                            Files = files.ToImmutableList(),
                            CommitMessage = $"Sync {spacePath} from MeshWeaver",
                            AuthorName = name,
                            AuthorEmail = email,
                            AccessToken = token,
                            CreatePrivateIfMissing = config.CreateRepoIfMissing,
                            CreateBranchIfMissing = config.CreateBranchIfMissing,
                        };
                        logger?.LogInformation(
                            "Exporting {Count} node(s) of {Space} → {Repo}@{Branch}",
                            files.Count, spacePath, repoUrl, config.Branch);
                        // Record the commit by MERGING only the last-sync fields atop the latest
                        // node content (stream.Update read-modify-write) — never a full-content write,
                        // so a concurrent repo-field edit in the GUI editor is not clobbered.
                        // 🚨 The attempt pair (#3945) is left to its default — i.e. CLEARED. An
                        // export advances the conflict horizon, and the horizon is what decides
                        // which live nodes an import preserves, so any "final at commit X" verdict
                        // an earlier import recorded is stale the moment this lands: the next green
                        // build must attempt again rather than skip on it.
                        return repoClient.Push(request).SelectMany(result =>
                            RecordSyncResult(spacePath, CommittedOutcome, result.CommitSha,
                                    advanceHorizon: true, sourceId)
                                .Select(_ => result));
                    }));
            });
        });
    }

    /// <summary>Root + descendants of the Space, filtered to exportable content nodes.</summary>
    private IObservable<IReadOnlyList<MeshNode>> SnapshotNodes(string spacePath, SyncIgnore ignore)
    {
        var root = hub.GetWorkspace().GetMeshNodeStream(spacePath)
            .Where(n => n is not null).Take(1).Timeout(TimeSpan.FromSeconds(30));
        // Wait for the query's INITIAL snapshot — a bare Take(1) can capture an empty pre-Initial
        // emission, silently exporting the Space as root-only (observed on a live instance: a
        // commit that wrote just index.json + README.md for a Space with ~50 descendants). Same
        // pattern as PartitionSyncAdminLayoutArea.DiscoverPartitions.
        var descendants = meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{spacePath} scope:descendants"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1).Timeout(TimeSpan.FromSeconds(30))
            .Select(c => (IEnumerable<MeshNode>)c.Items);
        return root.CombineLatest(descendants, (r, desc) =>
        {
            var all = new List<MeshNode>();
            if (r is not null) all.Add(r);
            all.AddRange(desc);
            return Filter(all, spacePath, ignore);
        });
    }

    /// <summary>
    /// Keeps content nodes only: drops satellite/governance subtrees (a <c>_</c>-prefixed
    /// segment after the partition root — <c>_GitSync</c>, <c>_Access</c>, <c>_Activity</c>,
    /// threads, notifications, …), honours <see cref="SyncBehavior"/>, and applies the Space's
    /// gitignore-style <paramref name="ignore"/> rules (<see cref="GitHubSyncConfig.Ignore"/> /
    /// <see cref="SyncIgnore.Default"/>) to the path relative to the partition root.
    /// </summary>
    internal static IReadOnlyList<MeshNode> Filter(List<MeshNode> all, string partition, SyncIgnore ignore)
    {
        var excludedRoots = all
            .Where(n => n.SyncBehavior == SyncBehavior.ExcludeThisAndChildren)
            .Select(n => n.Path)
            .ToArray();
        bool underExcluded(string p) =>
            excludedRoots.Any(r => p.StartsWith(r + "/", StringComparison.Ordinal));
        string relative(string p) =>
            p.StartsWith(partition + "/", StringComparison.Ordinal) ? p[(partition.Length + 1)..] : "";

        return all
            .Where(n => !string.IsNullOrEmpty(n.Path)
                        && !n.Segments.Skip(1).Any(s => s.StartsWith('_'))
                        && n.SyncBehavior == SyncBehavior.Include
                        && !underExcluded(n.Path)
                        && !ignore.IsIgnored(relative(n.Path)))
            .GroupBy(n => n.Path, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private IObservable<IReadOnlyList<RepoFile>> SerializeAll(
        IReadOnlyList<MeshNode> nodes, string partition, Action<string, LogLevel>? progress = null)
    {
        if (nodes.Count == 0)
            return Observable.Return((IReadOnlyList<RepoFile>)Array.Empty<RepoFile>());
        var allPaths = nodes.Select(n => n.Path).ToArray();
        return nodes
            .Select(n => SerializeOne(n, partition, allPaths, progress))
            .Merge(8)
            .Where(f => f is not null).Select(f => f!)
            .ToList()
            .Select(list => AppendReadme(list, nodes, partition));
    }

    /// <summary>
    /// Adds a top-level <c>README.md</c> rendered from the Space root's body so the GitHub
    /// repo page shows a landing page. The authoritative root remains <c>index.json</c>;
    /// import skips an undeclared display <c>README.md</c> so it never becomes a stray node.
    /// An authored README already exported as a node takes precedence over generated text.
    /// </summary>
    private IReadOnlyList<RepoFile> AppendReadme(IList<RepoFile> files, IReadOnlyList<MeshNode> nodes, string partition)
    {
        var list = files.ToList();
        if (list.Any(f => string.Equals(f.Path, "README.md", StringComparison.OrdinalIgnoreCase)))
            return list;
        var root = nodes.FirstOrDefault(n => string.Equals(n.Path, partition, StringComparison.Ordinal));
        var readme = root is null ? null : BuildReadme(root);
        if (!string.IsNullOrEmpty(readme))
            list.Add(new RepoFile("README.md", readme));
        return list;
    }

    private static string? BuildReadme(MeshNode root)
    {
        var body = root.Content switch
        {
            Space s => string.IsNullOrWhiteSpace(s.Body) ? null : s.Body,
            MarkdownContent mc => mc.Content,
            string str => str,
            System.Text.Json.JsonElement je => JsonString(je, "body") ?? JsonString(je, "content"),
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(body)) return body;
        var name = root.Name ?? root.Id;
        return $"# {name}\n";
    }

    private static string? JsonString(System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object
        && e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() : null;

    private IObservable<RepoFile?> SerializeOne(
        MeshNode node, string partition, string[] allPaths, Action<string, LogLevel>? progress = null)
    {
        // A repo file must not carry a compile verdict: the operational members on a NodeType node
        // (status, assembly pointers, source-version maps) are MESH-owned bookkeeping that would
        // otherwise ride into git and — being stale the moment the next compile runs — stamp a
        // stale green back onto any mesh that later imports the file. Export the authored
        // definition only. (Export-only seam on purpose: the file parsers are shared with the
        // filesystem persistence backend, which must keep round-tripping the full node.)
        node = NodeTypeOperationalContent.StripOperational(node, hub.JsonSerializerOptions);
        var serializer = parsers.GetSerializerFor(node);
        if (serializer is null)
        {
            // Every node MUST round-trip to the mirror. The registry always includes the universal
            // JSON serializer (JsonFileParser.CanSerialize => true — any content type, the exact
            // inverse of the JSON import), so this is unreachable in practice. If a future change
            // ever left the registry without that fallback, FAIL LOUD rather than silently dropping
            // the node: a dropped node is pruned by the next import — silent data loss, the very
            // failure mode this guard exists to make impossible.
            var message =
                $"No serializer for node '{node.Path}' (type {node.NodeType}). Export must serialize " +
                "every node; the universal JSON fallback is missing from the parser registry.";
            logger?.LogError(message);
            progress?.Invoke(message, LogLevel.Error);
            throw new InvalidOperationException(message);
        }
        var ext = serializer.SupportedExtensions.FirstOrDefault() ?? ".json";
        var repoPath = NodeFileMapper.ToRepoPath(node.Path, partition, ext, NodeFileMapper.HasChildren(node.Path, allPaths));
        // Serialize is pure in-memory work — no pool, just project the value into the chain.
        return Observable.Return<RepoFile?>(new RepoFile(repoPath, serializer.Serialize(node)));
    }

    private (string Name, string Email) AuthorIdentity(GitHubCredential cred)
    {
        var ctx = hub.ServiceProvider.GetService<AccessService>()?.Context;
        var login = cred.GitHubLogin;
        var name = !string.IsNullOrEmpty(ctx?.Name) ? ctx!.Name : (login ?? "MeshWeaver");
        var email = !string.IsNullOrEmpty(ctx?.Email) ? ctx!.Email!
            : !string.IsNullOrEmpty(login) ? $"{login}@users.noreply.github.com"
            : "noreply@meshweaver.cloud";
        return (name, email);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  IMPORT — GitHub → new Space, and re-import an existing Space at any commit
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new Space (provisioning its partition + granting the user admin) and
    /// imports all nodes from the repo at <paramref name="commitish"/> (branch or SHA).
    /// <paramref name="progress"/> receives per-file problems (a repo file that failed to
    /// parse and was therefore dropped) so an owning activity can surface them.
    /// </summary>
    public IObservable<StaticRepoImportResult> ImportFromGitHub(
        string repositoryUrl, string commitish, string newSpaceId, string newSpaceName,
        string? subdirectory, string userId,
        Action<string, LogLevel>? progress = null)
    {
        // Capture identity synchronously BEFORE the async credentials.Get hop — the SelectMany
        // continuation runs without the AsyncLocal AccessContext, and the Space create below must run
        // as the USER (so they become its admin). Re-assert it on the create's subscribe so
        // meshService.CreateNode's own at-call capture picks it up. (Same async-boundary fix as
        // UpdateConfig / EnsureConfigNode — the GitSync CI access-context flake.)
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var ctx = accessService?.Context ?? accessService?.CircuitContext;
        return ResolveAuth(userId).SelectMany(auth =>
        {
            var token = auth.Token;
            // Pre-create the Space under the USER so they become its admin and the
            // partition is provisioned, THEN import the content (under System, per-write).
            var spaceNode = new MeshNode(newSpaceId)
            {
                NodeType = SpaceNodeType,
                Name = newSpaceName,
                State = MeshNodeState.Active,
                Content = new Space(),
            };
            logger?.LogInformation("Importing {Repo}@{Ref} into new Space {Space}", repositoryUrl, commitish, newSpaceId);
            var createSpace = accessService is null || ctx is null
                ? meshService.CreateNode(spaceNode)
                : Observable.Using(() => accessService.SwitchAccessContext(ctx), _ => meshService.CreateNode(spaceNode));
            return createSpace
                .SelectMany(_ => FetchAndImport(repositoryUrl, commitish, subdirectory, token, newSpaceId,
                    SyncIgnore.For(null), progress))
                .Select(x => x.Result);
        });
    }

    /// <summary>
    /// Re-imports an existing Space to the state of <paramref name="commitish"/> (a
    /// branch or commit SHA) from one sync source, mirroring the repo into the partition
    /// (add/update/prune), and records the resolved commit SHA on that source's config.
    /// Rejected when the source's <see cref="GitHubSyncConfig.Direction"/> is
    /// <see cref="SyncDirection.ExportOnly"/>. This is the "change the commit and
    /// re-import to that state" operation.
    /// <paramref name="progress"/> (when the caller runs as an activity: <c>ctx.Log</c>)
    /// receives per-file problems — a repo file that failed to parse and was therefore
    /// dropped from the import — so they land on the activity log, not only in Loki.
    /// <paramref name="force"/> ignores the source's two-way setting and overwrites/prunes
    /// from the repo regardless (the deliberate-discard escape hatch).
    /// </summary>
    public IObservable<StaticRepoImportResult> ReimportAtCommit(
        string spacePath, string commitish, string userId, string? sourceId = null,
        Action<string, LogLevel>? progress = null, bool force = false)
        => ReimportAtCommitCore(spacePath, commitish, userId, sourceId, progress, force, reconcile: false);

    /// <summary>
    /// <see cref="ReimportAtCommit"/> with the RECONCILE switch
    /// (<see cref="ImportConflictPolicy.Reconcile"/>): the import re-evaluates the partition even
    /// when its content fingerprint matches a prior import, while every conflict protection stays
    /// armed. For the sealed-publication reconciler, which has measured that the live sources
    /// disagree with the bytes baked from this very commit. A DISTINCT name, not an overload: an
    /// added overload makes every dependent's <c>cref</c> to the original ambiguous (CS0419 under
    /// warnings-as-errors — the shape that bit MeshWeaver.SocialMedia on 2026-09-04).
    /// </summary>
    public IObservable<StaticRepoImportResult> ReconcileAtCommit(
        string spacePath, string commitish, string userId, string? sourceId = null,
        Action<string, LogLevel>? progress = null)
        => ReimportAtCommitCore(spacePath, commitish, userId, sourceId, progress, force: false, reconcile: true);

    private IObservable<StaticRepoImportResult> ReimportAtCommitCore(
        string spacePath, string commitish, string userId, string? sourceId,
        Action<string, LogLevel>? progress, bool force, bool reconcile)
    {
        return ReadConfig(spacePath, sourceId).Take(1).SelectMany(config =>
        {
            if (config?.RepositoryUrl is not { Length: > 0 } repoUrl)
                return Observable.Throw<StaticRepoImportResult>(new InvalidOperationException(
                    "No GitHub repository configured for this Space."));
            if (config.Direction == SyncDirection.ExportOnly)
                return Observable.Throw<StaticRepoImportResult>(new InvalidOperationException(
                    $"This sync source is export-only (mesh → repo): importing from {repoUrl} is not allowed. " +
                    "Change the source's Sync direction to Bidirectional or Import-only to re-import."));
            // Two-way (config.TwoWay): don't overwrite/prune nodes changed on the server since the last
            // recorded sync (config.LastSyncedAt) — they are carried back on the next commit. `force`
            // overrides. Overwrites stay git-first when TwoWay is off (unchanged legacy behavior) —
            // but a BIDIRECTIONAL source additionally protects server-side ADDITIONS from the prune
            // (issue #604): the mesh is an editing surface too, so a node created/changed on the
            // server since the last sync and not yet committed to the branch is NOT a stale extra to
            // mirror away. One-directional import (repo → mesh mirror) keeps FullReplace semantics.
            var policy = new ImportConflictPolicy(config.TwoWay, config.LastSyncedAt, force,
                PreserveServerAdditions: config.Direction == SyncDirection.Bidirectional)
            {
                Reconcile = reconcile,
            };
            return ResolveAuth(userId).SelectMany(auth =>
            {
                var token = auth.Token;
                logger?.LogInformation("Re-importing {Space} at {Ref} (twoWay={TwoWay}, force={Force})",
                    spacePath, commitish, config.TwoWay, force);
                // Baseline for the git-diff scope: the last SUCCESSFULLY-synced commit. `force` ignores
                // it (deliberate full overwrite/prune). Because RecordSyncResult only advances the baseline on a clean
                // success (below), this base always names a commit the mesh REALLY reached — so the diff
                // is cumulative and self-heals a previously-failed push (its files are still in the diff
                // until an import actually lands them).
                return FetchAndImport(repoUrl, commitish, config.Subdirectory, token, spacePath,
                        SyncIgnore.For(config), progress, policy,
                        baseSha: force ? null : config.LastSyncCommitSha)
                    // 🚨 RECOMPILE WHAT THE SYNC CHANGED — part of the sync transaction, not a
                    // follow-up human step. Importing new Source/Code nodes and walking away leaves
                    // every affected NodeType serving its STALE assembly (the "assembly is the
                    // deliverable" failure the AGENTS.md deploy procedure hand-patched; paid again
                    // 2026-08-03/04). The set derives from what actually LANDED (written + pruned
                    // node paths — an unchanged/content-only sync releases nothing) and covers the
                    // owning types AND every shared=@ sharer, mesh-wide; releases are requested
                    // under SYSTEM, consistent with how the import's own writes land, and the set
                    // is logged onto the sync's activity via `progress`.
                    .SelectMany(x =>
                    {
                        var changed = x.Result.WrittenPaths.AddRange(x.Result.PrunedPaths);
                        return changed.Count == 0
                            ? Observable.Return(x)
                            : hub.ReleaseAffectedNodeTypes(changed, progress).Select(_ => x);
                    })
                    // 🚨 Only advance the last-sync baseline when the mesh is now IN SYNC with the repo.
                    // A two-way import that PRESERVED server-newer nodes leaves the mesh AHEAD of the repo
                    // (those edits aren't committed back yet); advancing LastSyncedAt would move the
                    // baseline past them, so the NEXT update would no longer see them as "newer on the
                    // server" and would overwrite them — losing the very edits two-way just protected.
                    // A FAILED import must ALSO not advance the baseline — otherwise the next diff would
                    // start past the commit whose nodes never landed, permanently skipping them.
                    // 🚨 A NO-OP update ("Skipped": the content fingerprint matched a prior import, so
                    // NOTHING was verified against the live partition) must not advance the conflict
                    // horizon either — it would move it past pending uncommitted server changes and
                    // disarm the very protection above, so a LATER push would prune a server-side
                    // addition (the second route to the #675 data loss — issue #677). It DOES record
                    // the commit as SEEN (LastSyncCommitSha only): the repo content at this commit is
                    // what the mesh already imported, so the up-to-date display and the git-diff base
                    // stay correct — a repo commit touching no node files must not leave the Space
                    // forever "behind". A clean RECONCILING import (nothing preserved) records both.
                    // 🚨 "did anything fail", NOT the literal "Failed". The importer has TWO
                    // failure-bearing outcomes — a whole-import "Failed" and the per-file
                    // "ImportedWithErrors" — and only the first was tested here, so a partial
                    // import advanced the baseline past the commit whose nodes never landed. The
                    // webhook's SkipReason then answered "already at this commit" for every later
                    // build, so the miss was PERMANENT until the repo produced a new commit, and
                    // the Space's own UI read "up to date" the whole time (#2229 item C). The
                    // canonical case: a repo shipping an instance of a type it introduces — the
                    // instance is refused "NodeType 'X' is not registered" on the first pass, and
                    // the retry that would land it once the type node exists never ran.
                    // 🚨 EVERY conclusion records WHEN it happened and WHAT it was (issue #3581) —
                    // including the two branches that deliberately advance nothing else. Before this,
                    // an import that preserved server-newer nodes or landed nothing wrote to the
                    // config node at all, so a source could sync every day and leave no trace of it:
                    // measured on both AKS portals, `Edu/_GitSync` carried a `lastSyncedAt` from
                    // July/August beside a `lastSyncCommitSha` from that morning, and the only way to
                    // date the sync was to compare the node's own `lastModified` against pair tags in
                    // ACR. The recency fields are pure observability — nothing branches on them —
                    // which is exactly why they can be stamped where the horizon must not be.
                    .SelectMany(x =>
                    {
                        var mayAdvance = MayAdvanceBaseline(x.Result);
                        var skipped = string.Equals(x.Result.Outcome, "Skipped",
                            StringComparison.OrdinalIgnoreCase);
                        return RecordSyncResult(spacePath, x.Result.Outcome,
                                // A commit whose nodes did not all land is NOT "seen": advancing
                                // here is the #2229 item C hole, and the guard stays where it was.
                                seenCommitSha: mayAdvance ? x.CommitSha : null,
                                // The conflict horizon moves only on a RECONCILING import — never on
                                // a fingerprint-matched no-op (#677), never on one that preserved
                                // server-newer nodes (#675).
                                advanceHorizon: mayAdvance && !skipped,
                                sourceId,
                                // A retirement the import HELD (a NodeType the repository dropped
                                // while the mesh still has instances) is drift the sync cannot close
                                // by itself — it is stated on the config, where the settings tab and
                                // the status surface read it, not only in the activity log.
                                note: HeldNote(x.Result),
                                // 🚨 #3945 — the SECOND, weaker pointer, and the whole reason it is
                                // a second one. "We have already looked at exactly these bytes" is
                                // not "the mesh holds this commit", and one field answering both is
                                // what made a source that cannot converge re-clone its entire
                                // repository on every green build of the source repository. This one
                                // is stamped whatever the outcome; whether it may LICENCE a skip is
                                // the flag beside it, never this sha alone.
                                attemptedCommitSha: x.CommitSha,
                                attemptWasFinal: x.Result.VerdictIsFinal)
                            .Select(_ => x.Result);
                    });
            });
        });
    }

    /// <summary>
    /// Whether an import's outcome permits ADVANCING this source's last-sync baseline
    /// (<c>LastSyncedAt</c> + <c>LastSyncCommitSha</c>). Pure — the whole decision in one place, so
    /// it can be stated as a fact rather than re-derived inside a reactive chain.
    ///
    /// <para>Three reasons to hold the baseline where it is:</para>
    /// <list type="bullet">
    ///   <item><b>Preserved &gt; 0</b> — a two-way import kept server-newer nodes, so the mesh is
    ///     AHEAD of the repo. Advancing past them makes the NEXT diff overwrite the very edits
    ///     two-way just protected (#675/#677).</item>
    ///   <item><b>Failed &gt; 0</b> — 🚨 <b>the #2229 item C hole.</b> Some nodes did not land. The
    ///     guard used to test the outcome LITERAL <c>"Failed"</c> only, and a per-file failure
    ///     reports <c>"ImportedWithErrors"</c> — a different string — so a partial import advanced
    ///     the pointer past the commit whose nodes never landed. Every later sync then answered
    ///     "already at this commit" (<c>GitHubWebhookProcessor.SkipReason</c>) and the miss became
    ///     PERMANENT until the repo produced a new commit, with the Space's own UI reading "up to
    ///     date" throughout. The canonical case is a repo shipping an instance of a type it
    ///     introduces: the instance is refused <c>NodeType 'X' is not registered</c> on the pass
    ///     that also writes the type node, so the retry that would land it never ran.</item>
    ///   <item><b>Outcome "Failed"</b> — the whole import failed; it carries no per-file tally to
    ///     read, so the literal is still the signal for that one.</item>
    /// </list>
    ///
    /// <para>A "Skipped" (fingerprint-matched no-op) outcome is NOT judged here — it is allowed
    /// through to <c>RecordSyncResult</c> with <c>advanceHorizon: false</c>, which advances the SEEN commit only and deliberately
    /// leaves the conflict horizon alone.</para>
    ///
    /// <para>🚨 <b>This is NOT the question "would attempting again change anything" (#3945), and it
    /// must never be relaxed into it.</b> Holding the baseline is what keeps un-landed content
    /// reachable; but because <c>GitHubWebhookProcessor.SkipReason</c> also read this same field to
    /// decide whether a delivery was FREE, holding it made every later green build of the source
    /// repository pay a full clone — 10/h on core, for as long as the source did not converge. That
    /// second question now has its own pair of fields
    /// (<see cref="GitHubSyncConfig.LastAttemptedCommitSha"/> +
    /// <see cref="GitHubSyncConfig.LastAttemptWasFinal"/>), fed by
    /// <see cref="StaticRepoImportResult.VerdictIsFinal"/>, so this one can stay exactly as
    /// conservative as #675 / #677 / #2229 item C need it to be.</para>
    /// </summary>
    /// <param name="result">The import outcome to judge.</param>
    /// <returns><c>true</c> when the mesh is genuinely in sync with the repo at this commit.</returns>
    internal static bool MayAdvanceBaseline(StaticRepoImportResult result)
        => result.Preserved == 0
           && result.Failed == 0
           && !string.Equals(result.Outcome, "Failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <see cref="GitHubSyncConfig.LastSyncNote"/> an import earns when it HELD a NodeType the
    /// repository retired while the mesh still holds instances of it
    /// (<see cref="StaticRepoImportResult.HeldNodeTypePaths"/>) — the in-mesh content the
    /// repository does not hold, named where an operator looks for the source's state. <c>null</c>
    /// when nothing was held, which also clears an older note (a note describes the LAST attempt
    /// only). Pure.
    /// </summary>
    /// <param name="result">The import's outcome.</param>
    internal static string? HeldNote(StaticRepoImportResult result)
        => result.HeldNodeTypePaths.Count == 0
            ? null
            : $"{result.HeldNodeTypePaths.Count} NodeType(s) the repository no longer carries are held "
              + "for their remaining instances (pending retirement — not pruned): "
              + string.Join(", ", result.HeldNodeTypePaths)
              + ". Retype or delete the instances in the mesh, or restore the type in the repository; "
              + "the next sync completes whichever you chose.";

    /// <summary>
    /// Asks GitHub — LIVE, nothing stored — for the configured branch's current HEAD commit,
    /// and reports whether the Space's last sync matches it ("are we on the latest on this
    /// branch?"). The branch name comes from the (local) config; the HEAD comes straight from
    /// GitHub, so the answer can never drift. Delegates rather than replicating branch state.
    /// </summary>
    public IObservable<BranchState> AskBranchState(string spacePath, string userId, string? sourceId = null)
    {
        return ReadConfig(spacePath, sourceId).Take(1).SelectMany(config =>
        {
            if (config?.RepositoryUrl is not { Length: > 0 } repoUrl)
                return Observable.Throw<BranchState>(new InvalidOperationException(
                    "No GitHub repository configured for this Space."));
            return ResolveAuth(userId).SelectMany(auth =>
            {
                var token = auth.Token;
                var branch = string.IsNullOrWhiteSpace(config.Branch) ? "main" : config.Branch;
                // Fetch resolves the branch ref to its current HEAD commit on GitHub.
                return repoClient.Fetch(repoUrl, branch, config.Subdirectory, token)
                    .Select(snapshot => new BranchState(
                        branch,
                        snapshot.CommitSha,
                        config.LastSyncCommitSha,
                        UpToDate: string.Equals(snapshot.CommitSha, config.LastSyncCommitSha, StringComparison.Ordinal)));
            });
        });
    }

    /// <summary>A resolved GitHub authentication: the token plus the user credential when the token is theirs (null = App identity).</summary>
    private sealed record ResolvedGitHubAuth(string Token, GitHubCredential? Credential);

    /// <summary>
    /// Resolves the token for a GitHub operation: the user's connected credential when present,
    /// else the platform's <b>GitHub App installation token</b> (the machine identity — server-side
    /// operations like the plugin registry's sync never require a personal login). Errors only when
    /// neither identity is available.
    /// </summary>
    private IObservable<ResolvedGitHubAuth> ResolveAuth(string userId) =>
        credentials.Get(userId).Take(1).SelectMany(cred =>
            cred?.AccessToken is { Length: > 0 } token
                ? Observable.Return(new ResolvedGitHubAuth(token, cred))
                : appTokens is { IsConfigured: true }
                    ? appTokens.GetInstallationToken().Select(t => new ResolvedGitHubAuth(t, null))
                    : Observable.Throw<ResolvedGitHubAuth>(new InvalidOperationException(
                        "Connect your GitHub account first (GitHub Sync settings → Connect), or configure the " +
                        "GitHub App identity (GitHub:App:ClientId + GitHub:App:PrivateKey).")));

    private IObservable<(StaticRepoImportResult Result, string CommitSha)> FetchAndImport(
        string repoUrl, string commitish, string? subdirectory, string token, string spaceId,
        SyncIgnore ignore, Action<string, LogLevel>? progress = null, ImportConflictPolicy? policy = null,
        string? baseSha = null)
    {
        return repoClient.Fetch(repoUrl, commitish, subdirectory, token).SelectMany(snapshot =>
        {
            // 🚨 A CONFIGURED SUBDIRECTORY THAT MATCHES NOTHING IS A CONFIGURATION ERROR, NOT AN
            // EMPTY REPO (issue #1326). The tree filter compares the configured prefix against repo
            // paths with StringComparison.Ordinal — correct, because git paths ARE case-sensitive —
            // so a casing/typo mismatch yields ZERO files, and an empty snapshot flows on as "the
            // repo carries nothing": every existing node becomes absent-from-the-source and, under
            // the default FullReplace, the import MIRRORS THE WHOLE SPACE AWAY. Refuse loudly and
            // name the subdirectory instead. Only guarded when a subdirectory is configured — a
            // genuinely empty repo (no subdirectory) is a legitimate first-sync state.
            if (snapshot.Files.Count == 0 && !string.IsNullOrWhiteSpace(subdirectory))
            {
                var message =
                    $"No files found under subdirectory '{subdirectory.Trim().Trim('/')}' at "
                    + $"{Short(snapshot.CommitSha)} in {repoUrl}. Refusing to import an empty snapshot — "
                    + "it would prune the whole Space. Check the subdirectory (including its exact "
                    + "capitalisation — git paths are case-sensitive) on the sync source.";
                logger?.LogWarning("[GitSync] {Space}: {Message}", spaceId, message);
                progress?.Invoke(message, LogLevel.Error);
                return Observable.Throw<(StaticRepoImportResult, string)>(
                    new InvalidOperationException(message));
            }
            // Git-diff scope: when we know the last SUCCESSFULLY-synced commit (a routine
            // webhook/update — not a force, not a first import), ask GitHub what changed between it
            // and the head and import ONLY those nodes. A null answer (no base, force, force-push,
            // truncated, or a compare error) falls back to a full import — never a silent
            // under-import. This is what stops a routine push from re-materialising the whole
            // partition and storming the live compiler (the memex-cloud outage loop, 2026-07-23).
            var readmePolicy = ReadmeFilePolicy.From(snapshot);
            // Reconciliation measures drift in the live mesh, not changes between Git commits.
            // B..B is empty even when another import replaced the live nodes with A.
            var diff = string.IsNullOrEmpty(baseSha) || policy?.Force == true || policy?.Reconcile == true
                ? Observable.Return<IReadOnlyList<string>?>(null)
                : repoClient.GetChangedPaths(repoUrl, baseSha!, snapshot.CommitSha, subdirectory, token);
            return diff.SelectMany(changedFiles =>
                ParseSnapshot(snapshot, spaceId, ignore, readmePolicy, progress).SelectMany(parsed =>
                {
                    // 🚨 The ignore rules travel WITH the source (issue #1326): the importer's prune
                    // needs them to tell "the repo dropped this node" from "this node never syncs".
                    // 🚨 So does the fetch's COMPLETENESS verdict (issue #3589): a truncated GitHub
                    // tree arrives as HTTP 200 with a partial file list, and the prune's inference
                    // ("absent from the source ⇒ deleted from the source") is unsound on one. The
                    // import still upserts everything it did read — only the deletion half is
                    // withheld, because a stale extra is recoverable and a silent delete is not.
                    var source = new InMemoryStaticRepoSource(
                        spaceId, parsed.Children, parsed.Root, parsed.ContentSyncs, ignore,
                        listingIsComplete: snapshot.ListingIsComplete,
                        ownsReadme: readmePolicy.IsPackage);
                    var changedNodePaths = ChangedNodePaths(changedFiles, spaceId);
                    if (changedNodePaths is not null)
                        logger?.LogInformation(
                            "[GitSync] {Space}: git-diff {Base}..{Head} → {Count} changed node(s) — "
                            + "importing only those (full partition left untouched).",
                            spaceId, Short(baseSha), Short(snapshot.CommitSha), changedNodePaths.Count);
                    return StaticRepoImporter.ImportSource(hub, source, logger, policy, changedNodePaths)
                        .Select(result => (result, snapshot.CommitSha));
                }));
        });
    }

    /// <summary>
    /// Maps the <c>git diff</c>'s subdirectory-relative changed FILE paths to the set of changed
    /// NODE paths (the importer's <c>changedNodePaths</c> scope), via <see cref="NodeFileMapper"/> —
    /// the canonical inverse of the export path mapping. <see langword="null"/> in → null out (diff
    /// unknown → full import). Content-collection assets (<c>{node}/content/**</c>) are excluded:
    /// they sync through a separate, unscoped path, so they never need to gate a node upsert.
    /// </summary>
    private static IReadOnlySet<string>? ChangedNodePaths(IReadOnlyList<string>? changedFiles, string spaceId)
    {
        if (changedFiles is null) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(rel) || ContentAssetMapper.IsContentPath(rel))
                continue;
            var (id, ns) = NodeFileMapper.FromRelativePath(rel);
            if (string.IsNullOrEmpty(id))
                continue;
            set.Add(ns.Length == 0 ? $"{spaceId}/{id}" : $"{spaceId}/{ns}/{id}");
        }
        return set;
    }

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha.Length <= 8 ? sha : sha[..8];

    private IObservable<(MeshNode? Root, IReadOnlyList<MeshNode> Children, IReadOnlyList<StaticContentSync> ContentSyncs)> ParseSnapshot(
        RepoSnapshot snapshot, string spaceId, SyncIgnore ignore, ReadmeFilePolicy readmePolicy,
        Action<string, LogLevel>? progress = null)
    {
        if (snapshot.Files.Count == 0)
            return Observable.Return(((MeshNode?)null, (IReadOnlyList<MeshNode>)Array.Empty<MeshNode>(),
                (IReadOnlyList<StaticContentSync>)Array.Empty<StaticContentSync>()));

        // Apply the Space's ignore rules on import too — a repo that carries ignored paths (e.g.
        // Release/ bookkeeping from an older export) must not re-create those nodes NOR re-sync their
        // content. Then split off the git-committed content-collection assets ({node}/content/**):
        // their raw bytes are mirrored into the owning node's content collection (never parsed as a
        // node), so committed course videos/posters land and stop getting wiped. Everything else flows
        // to node parsing as before.
        var kept = snapshot.Files.Where(f => !ignore.IsIgnored(f.Path)).ToArray();
        // Cheap path-only precheck first (no byte materialization for the common node file), then
        // classify only the content paths — f.Bytes is realized ONLY for an actual content asset.
        var classified = kept
            .Select(f => (File: f, Asset: ContentAssetMapper.IsContentPath(f.Path)
                ? ContentAssetMapper.TryClassify(f.Path, () => f.Bytes)
                : null))
            .ToArray();
        var contentSyncs = ContentAssetMapper.ToContentSyncs(
            spaceId, classified.Where(c => c.Asset is not null).Select(c => c.Asset!));

        return classified
            .Where(c => c.Asset is null)
            .Select(c => ParseFile(c.File, spaceId, readmePolicy.IsDeclaredNode))
            .Merge(8)
            .ToList()
            .Select(list =>
            {
                // 🚨 ONE write for every parse problem, not one per failing file. Each progress call
                // is a stream.Update that re-serialises the whole activity node, so per-file reporting
                // over n failures is O(n²) — and these parses run concurrently under Merge(8), so the
                // writes raced on a single node too. Every failing path is still named, so the audit
                // trail is unchanged; it simply arrives as one entry.
                var problems = list.Where(x => x.Problem is not null).Select(x => x.Problem!).ToArray();
                if (problems.Length > 0)
                    progress?.Invoke(
                        $"{problems.Length} file(s) could not be parsed and were skipped:{Environment.NewLine}"
                        + string.Join(Environment.NewLine, problems),
                        LogLevel.Error);

                var parsedNodes = list.Where(x => x.Node is not null).ToArray();
                var root = parsedNodes.FirstOrDefault(x => x.IsRoot).Node;
                var children = parsedNodes.Where(x => !x.IsRoot).Select(x => x.Node!).ToList();
                return (root, (IReadOnlyList<MeshNode>)children, contentSyncs);
            });
    }

    /// <summary>
    /// Parses one repo file into a node. A parse PROBLEM is RETURNED, never written straight to the
    /// activity: <see cref="ParseSnapshot"/> collects every problem and reports them in ONE write.
    ///
    /// <para>🚨 Why it is returned rather than logged here. Each <c>progress</c> call is a
    /// <c>stream.Update</c> on the activity node, and every such write re-serialises that node's whole
    /// content to compute its patch — so a line per failing file over n failures costs O(n²) CPU and
    /// allocation. These parses also run under <c>.Merge(8)</c>, so the per-file writes were
    /// concurrent on one node as well as quadratic. Same defect class as #1341 / #1172.</para>
    /// </summary>
    private IObservable<(MeshNode? Node, bool IsRoot, string? Problem)> ParseFile(
        RepoFile file, string spaceId, bool readmeIsNode = false)
    {
        // A generated repository README is display-only; a package manifest can instead declare
        // this same file as a real node. Honor that declaration on the Git sync update lane too.
        if (!readmeIsNode && string.Equals(file.Path, "README.md", StringComparison.OrdinalIgnoreCase))
            return Observable.Return(((MeshNode?)null, false, (string?)null));

        var ext = System.IO.Path.GetExtension(file.Path);
        // file.Content is already an in-memory string — the parse is pure CPU, no pool.
        // A file whose parser(s) all THREW is a malformed node file: the node it should have
        // become is silently missing after the import. Surface it on the owning activity as an
        // Error (ActivityRunner.Finish rolls the terminal status up to Failed) in addition to
        // the server log. Files with no registered parser (.yml, .py, …) stay silent — repos
        // legitimately carry non-node files.
        // The relative path handed to the parser must carry the SPACE prefix for CHILD nodes:
        // the parser derives the node path from it and bakes it into the content's
        // PrerenderedHtml as the base for RELATIVE markdown links (LinkUrlCleanupExtension).
        // Parsing with the bare in-repo path prerendered "../.." on
        // {space}/TDD/Exercise/X against "TDD/Exercise/X" — every relative link in the
        // stored prerender lost the partition segment (core #582). The id/namespace the
        // parser derives from the prefixed path are overridden by the rebase below either
        // way, so only the prerender base changes. The root index keeps the bare path (its
        // namespace is the space itself; prefixing would double it).
        var isRoot = NodeFileMapper.IsRootIndex(file.Path);
        var parseRelativePath = isRoot ? file.Path : $"{spaceId}/{file.Path}";
        string? problem = null;
        var parsed = parsers.TryParse(ext, file.Path, file.Content, parseRelativePath, (path, ex) =>
        {
            logger?.LogWarning(ex, "Failed to parse {Path} — file skipped on import.", path);
            problem = $"Failed to parse '{path}': {ex.Message} — file skipped.";
        });
        if (parsed is null) return Observable.Return(((MeshNode?)null, false, problem));
        // #3474 — the repo→mesh half of the NodeType content ownership rule, at the seam where a
        // repo file becomes a node: the file's MESH-OWNED compile bookkeeping never enters the
        // mesh. An UPDATE keeps the live node's values regardless (PreserveLiveOperational); a
        // CREATE has no live node, and until this line a first import wrote the file's verdict
        // verbatim as the type's initial live state — a repo whose files predate the export strip
        // (SerializeAll → StripOperational) is exactly the case the export side cannot reach.
        // 🚨 This line also covers the STATIC-REPO path: ParseSnapshot builds the nodes an
        // InMemoryStaticRepoSource serves (the serveFromPartition partitions — Agent, Model,
        // Harness, Skill) by mapping every file through THIS method. Split the two and that path
        // silently regains the file's verdict, with no test near it.
        parsed = NodeTypeOperationalContent.WithoutOperational(parsed, hub.JsonSerializerOptions);
        if (isRoot)
        {
            var root = parsed with
            {
                Id = spaceId,
                Namespace = "",
                MainNode = spaceId,
                NodeType = string.IsNullOrEmpty(parsed.NodeType) ? SpaceNodeType : parsed.NodeType,
            };
            return Observable.Return(((MeshNode?)root, true, problem));
        }
        var (id, ns) = NodeFileMapper.FromRelativePath(file.Path);
        var rebasedNs = string.IsNullOrEmpty(ns) ? spaceId : $"{spaceId}/{ns}";
        var node = parsed with { Id = id, Namespace = rebasedNs, MainNode = $"{rebasedNs}/{id}" };
        return Observable.Return(((MeshNode?)node, false, problem));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Config read / write
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Live config stream for GUI display — the <b>synced</b> <c>GetQuery</c> (Replay/RefCount,
    /// re-emits on change), the same pattern <c>ModelProviderService</c> uses for its per-user
    /// satellite nodes. A point <c>GetMeshNodeStream</c> read is NOT used here: the config lives at
    /// the hidden <c>{space}/_GitSync</c> satellite path, whose per-node hub does not serve the
    /// single-node reducer reliably (it timed out → "not configured"). Emits the config or null.
    /// </summary>
    public IObservable<GitHubSyncConfig?> WatchConfig(string spacePath, string? sourceId = null) =>
        WatchConfigNode(spacePath, sourceId).Select(Extract<GitHubSyncConfig>);

    /// <summary>Live <see cref="MeshNode"/> stream for one sync-source config node of the Space
    /// (or null when absent) — the synced <c>GetQuery</c>. The GUI editor binds to this node by
    /// path via <c>GetMeshNodeStream</c>; this stream is for service-side reads/displays.</summary>
    public IObservable<MeshNode?> WatchConfigNode(string spacePath, string? sourceId = null)
    {
        var path = ConfigPath(spacePath, sourceId);
        return hub.GetWorkspace()
            .GetQuery($"gitsync-cfg:{path}", $"path:{path} select:path,id,name,nodeType,content")
            .Select(nodes => nodes?.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Live stream of ALL of the Space's sync-source config nodes: the primary
    /// (<c>{space}/_GitSync</c>, when present) followed by every additional source
    /// (<c>{space}/_GitSync/{sourceId}</c>), ordered by source id. Re-emits when a source is
    /// added, removed, or edited — the synced <c>GetQuery</c> over the <c>_GitSync</c>
    /// namespace, combined with the primary's own stream.
    /// </summary>
    public IObservable<IReadOnlyList<MeshNode>> WatchConfigNodes(string spacePath)
    {
        var primaryPath = ConfigPath(spacePath);
        var children = hub.GetWorkspace()
            .GetQuery($"gitsync-cfgs:{spacePath}", $"namespace:{primaryPath} nodeType:{ConfigNodeType} select:path,id,namespace,name,nodeType,content")
            .Select(nodes => (nodes ?? [])
                .Where(n => string.Equals(n.Namespace, primaryPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                .ToList());
        return WatchConfigNode(spacePath).CombineLatest(children, (primary, extra) =>
        {
            var all = new List<MeshNode>();
            if (primary is not null) all.Add(primary);
            all.AddRange(extra);
            return (IReadOnlyList<MeshNode>)all;
        });
    }

    /// <summary>
    /// Adds a sync source to the Space: creates the <c>{space}/_GitSync/{sourceId}</c>
    /// config node (with defaults) named <paramref name="name"/>, where the id is the
    /// sanitized name. Create-on-absent — adding a source whose id already exists returns
    /// the existing node untouched. Configure the repo/branch/direction afterwards through
    /// the standard node editor bound to the returned node's path.
    /// </summary>
    public IObservable<MeshNode> AddSyncSource(string spacePath, string name)
    {
        var sourceId = SanitizeSourceId(name);
        if (string.IsNullOrEmpty(sourceId))
            return Observable.Throw<MeshNode>(new ArgumentException(
                "The sync-source name must contain at least one letter or digit.", nameof(name)));
        return EnsureConfigNode(spacePath, sourceId, name);
    }

    /// <summary>
    /// Removes an ADDITIONAL sync source (<c>{space}/_GitSync/{sourceId}</c>). The primary
    /// source node is never removed this way — clear its repository URL instead.
    /// </summary>
    public IObservable<bool> RemoveSyncSource(string spacePath, string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId))
            return Observable.Throw<bool>(new ArgumentException(
                "The primary sync source cannot be removed — clear its repository URL instead.",
                nameof(sourceId)));
        return meshService.DeleteNode(ConfigPath(spacePath, sourceId));
    }

    /// <summary>Sanitizes a display name into a node id: letters/digits/dash/underscore only.</summary>
    private static string SanitizeSourceId(string name) =>
        new string(name.Trim()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');

    /// <summary>
    /// Create-on-absent the Space's <c>_GitSync</c> config node (with defaults) so the standard
    /// node editor has a node to bind to. Existing node untouched. 🚨 Create-on-absent reads
    /// existence via the keyed <c>GetQuery</c> (empty-on-absent) and seeds through the
    /// node-lifecycle <c>CreateNode</c> — NEVER a point <c>GetMeshNodeStream(path).Update</c> on an
    /// absent path (that NotFound-storms). Mirrors <c>AiSettingsNodeType.EnsureExists</c>.
    /// Returns the existing or newly-created node.
    /// </summary>
    public IObservable<MeshNode> EnsureConfigNode(string spacePath, string? sourceId = null, string? name = null)
    {
        // Capture identity synchronously BEFORE the async WatchConfigNode hop (same reason as
        // UpdateConfig). meshService.CreateNode captures the AccessContext at its CALL — which here
        // happens inside the SelectMany continuation, where the AsyncLocal has been dropped, so the
        // Create is denied; the Catch below then degrades that denial into a 30s wait for a node that
        // never lands (the GitSync EnsureConfigNode / Editing_a_field CI timeout). Re-assert the
        // captured context on the create's subscribe so CreateNode's own capture picks it up.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var ctx = accessService?.Context ?? accessService?.CircuitContext;
        return WatchConfigNode(spacePath, sourceId).Take(1).SelectMany(existing =>
        {
            if (existing is not null) return Observable.Return(existing);
            // Primary source: {space}/_GitSync. Additional source: {space}/_GitSync/{sourceId}.
            var node = string.IsNullOrEmpty(sourceId)
                ? new MeshNode(ConfigId, spacePath)
                : new MeshNode(sourceId, ConfigPath(spacePath));
            node = node with
            {
                NodeType = ConfigNodeType,
                Name = name ?? (string.IsNullOrEmpty(sourceId) ? "GitHub Sync" : sourceId),
                State = MeshNodeState.Active,
                MainNode = spacePath,
                Content = new GitHubSyncConfig(),
            };
            // CreateNode is create-only (rejects an existing node) — if a concurrent caller won the
            // race, fall back to reading the now-present node rather than surfacing the conflict.
            var create = accessService is null || ctx is null
                ? meshService.CreateNode(node)
                : Observable.Using(() => accessService.SwitchAccessContext(ctx), _ => meshService.CreateNode(node));
            return create
                .Catch<MeshNode, Exception>(_ => WatchConfigNode(spacePath, sourceId).Where(n => n is not null).Select(n => n!).Take(1));
        });
    }

    /// <summary>One-shot config read for actions (Sync / Re-import). The synced query's first
    /// emission already reflects a committed write (the GUI auto-saves the repo URL on edit, so by
    /// Sync time the config is present).</summary>
    public IObservable<GitHubSyncConfig?> ReadConfig(string spacePath, string? sourceId = null)
        => WatchConfig(spacePath, sourceId).Take(1);

    /// <summary>Persists the repository settings (preserving the recorded last-sync state).</summary>
    public IObservable<MeshNode> SaveConfig(
        string spacePath, string? repositoryUrl, string branch, string? subdirectory,
        bool createBranchIfMissing, bool createRepoIfMissing,
        SyncDirection direction = SyncDirection.Bidirectional, string? sourceId = null,
        bool twoWay = false)
        => UpdateConfig(spacePath, c => c with
        {
            RepositoryUrl = string.IsNullOrWhiteSpace(repositoryUrl) ? null : repositoryUrl.Trim(),
            Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim(),
            Subdirectory = string.IsNullOrWhiteSpace(subdirectory) ? null : subdirectory.Trim().Trim('/'),
            CreateBranchIfMissing = createBranchIfMissing,
            CreateRepoIfMissing = createRepoIfMissing,
            Direction = direction,
            TwoWay = twoWay,
        }, sourceId);

    /// <summary>Read-modify-write a config field (current value from the synced query; null when absent).</summary>
    private IObservable<MeshNode> UpdateConfig(
        string spacePath, Func<GitHubSyncConfig, GitHubSyncConfig> update, string? sourceId = null)
    {
        // 🚨 Capture the caller's identity SYNCHRONOUSLY, here on the calling thread, BEFORE the
        // ReadConfig hop. ReadConfig's SelectMany continuation can run on a pool thread where the
        // AsyncLocal AccessContext has been dropped, so WriteConfig's CreateOrUpdateNodeRequest would
        // post under a null/system identity and RLS denies Create on {space}/_GitSync. It passes
        // locally only because ReadConfig completes synchronously when the config node is absent (the
        // continuation stays on the caller thread); under CI load ReadConfig emits async and the
        // context is lost — the GitSync-suite flake ("Access denied: Create permission required for
        // '{space}/_GitSync'", + the dependent waits that then time out). Thread it explicitly to the
        // write's post via WithAccessContext so it never depends on the AsyncLocal surviving the hop.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var ctx = accessService?.Context ?? accessService?.CircuitContext;
        return ReadConfig(spacePath, sourceId).SelectMany(current =>
            WriteConfig(spacePath, ctx, update(current ?? new GitHubSyncConfig()), sourceId));
    }

    /// <summary>The <see cref="GitHubSyncConfig.LastSyncOutcome"/> an EXPORT records. Imports record
    /// the importer's own outcome literal; the export path has no such tally, so it states its own.
    /// A wire value, never display text — the settings tab localizes it for the viewer.</summary>
    internal const string CommittedOutcome = "Committed";

    /// <summary>
    /// 🚨 <b>The ONE write path for every last-sync field</b> (issue #3581). Merges the recency pair
    /// — <see cref="GitHubSyncConfig.LastSyncAttemptAt"/> (always <c>now</c>) and
    /// <see cref="GitHubSyncConfig.LastSyncOutcome"/> — plus, when the caller says the outcome earns
    /// them, <see cref="GitHubSyncConfig.LastSyncCommitSha"/> and the
    /// <see cref="GitHubSyncConfig.LastSyncedAt"/> conflict horizon, atop the latest node content via
    /// <c>GetMeshNodeStream(path).Update</c> (read-modify-write). Touching only these fields means a
    /// concurrent GUI edit of the repository fields is never clobbered, and re-writing an unchanged
    /// value costs nothing: the write travels as an RFC 7396 merge patch of what actually changed.
    ///
    /// <para>🚨 <b>The clocks are separate ON PURPOSE and must stay so.</b> Folding the horizon
    /// into the recency stamp is the one change that must never be made — it would advance the
    /// horizon on exactly the outcomes that suppress it (a fingerprint-matched no-op, an import that
    /// preserved server-newer nodes, one that landed nothing), moving it past pending uncommitted
    /// server changes, disarming the two-way protection and letting a later push prune them
    /// (#675 / #677 / #2229 item C). What #3581 asked for was one write PATH; what it must never
    /// become is one FIELD. This is that path: the decision is stated once here instead of being
    /// re-derived at each call site.</para>
    /// </summary>
    /// <param name="spacePath">The Space whose sync source is being recorded.</param>
    /// <param name="outcome">The importer's outcome literal, or <see cref="CommittedOutcome"/>.</param>
    /// <param name="seenCommitSha">The commit the mesh has genuinely reached, or <c>null</c> to keep
    /// the recorded one — a partial or failed import must not move the diff base past nodes that
    /// never landed.</param>
    /// <param name="advanceHorizon">Whether this outcome RECONCILED mesh and repo.</param>
    /// <param name="sourceId">The sync source (null = the primary).</param>
    /// <param name="note">The hold reason / finding to state on the config, or null to clear it.</param>
    /// <param name="attemptedCommitSha">The commit an IMPORT attempt actually read, or <c>null</c>
    /// to CLEAR the attempt pair — which every non-import conclusion does, because an export moves
    /// the conflict horizon (so an earlier verdict at that commit is stale) and a hold means no
    /// attempt ran at all (#3945).</param>
    /// <param name="attemptWasFinal">Whether that attempt's verdict is final at that commit
    /// (<see cref="StaticRepoImportResult.VerdictIsFinal"/>). Meaningless without
    /// <paramref name="attemptedCommitSha"/>, and written in the same patch as it.</param>
    private IObservable<MeshNode> RecordSyncResult(
        string spacePath, string outcome, string? seenCommitSha, bool advanceHorizon,
        string? sourceId = null, string? note = null,
        string? attemptedCommitSha = null, bool attemptWasFinal = false)
    {
        var now = DateTimeOffset.UtcNow;
        return hub.GetWorkspace().GetMeshNodeStream(ConfigPath(spacePath, sourceId)).Update(node =>
        {
            var cur = Extract<GitHubSyncConfig>(node) ?? new GitHubSyncConfig();
            return node with
            {
                Content = cur with
                {
                    LastSyncAttemptAt = now,
                    LastSyncOutcome = outcome,
                    LastSyncCommitSha = seenCommitSha ?? cur.LastSyncCommitSha,
                    LastSyncedAt = advanceHorizon ? now : cur.LastSyncedAt,
                    // A hold's reason, or cleared by an attempt that ran: the note describes the
                    // LAST attempt only, never an older one.
                    LastSyncNote = note,
                    // 🚨 #3945 — ALWAYS written, never merged with what was already there. Unlike
                    // the baseline above (`?? cur.…`, a HIGH-WATER MARK that only ever advances)
                    // this pair describes the LAST ATTEMPT, so an older value surviving a conclusion
                    // that set none would licence a skip for an attempt that never happened. The
                    // RFC 7396 diff a cross-hub `stream.Update` ships emits a key present in the
                    // stored node and absent from the new content as `null` — an RFC 7396 REMOVE —
                    // so passing null here genuinely clears the stored value.
                    LastAttemptedCommitSha = attemptedCommitSha,
                    // Never true on its own: the flag is only ever read beside the sha, and a true
                    // with no sha would be a licence attached to no commit.
                    LastAttemptWasFinal = attemptedCommitSha is { Length: > 0 } && attemptWasFinal,
                },
            };
        });
    }

    /// <summary>The outcome literal recorded when a source is held back from a commit (by the
    /// sealed-publication gate, or the reconciler) rather than attempted.</summary>
    public const string HeldOutcome = "Held";

    /// <summary>
    /// Records that this source was HELD from advancing, and why — onto the config, so the reason
    /// is visible where the outcome is (2026-09-08: a source held for hours showed only
    /// <c>Skipped</c>). Moves nothing else: not the commit, not the horizon. Cold.
    ///
    /// <para>🚨 It also CLEARS the attempt pair (#3945), by leaving it at its default. A hold means
    /// no import ran, and a seal that has not arrived yet is the archetype of a condition that
    /// clears without the commit moving — so a hold must never leave a "final at this commit"
    /// licence standing for the delivery that follows it.</para>
    /// </summary>
    public IObservable<MeshNode> RecordHold(string spacePath, string? sourceId, string reason)
        => RecordSyncResult(spacePath, HeldOutcome, seenCommitSha: null, advanceHorizon: false,
            sourceId, note: reason);

    /// <summary>Writes the FULL config (no read) — used by <see cref="SaveConfig"/> (a programmatic
    /// / test API). The GUI does NOT use this: it edits the node through the standard
    /// <c>MeshNodeContentEditorControl</c> which binds directly to the node stream.</summary>
    private IObservable<MeshNode> WriteConfig(
        string spacePath, AccessContext? ctx, GitHubSyncConfig config, string? sourceId = null)
    {
        var node = (string.IsNullOrEmpty(sourceId)
            ? new MeshNode(ConfigId, spacePath)
            : new MeshNode(sourceId, ConfigPath(spacePath))) with
        {
            NodeType = ConfigNodeType,
            Name = string.IsNullOrEmpty(sourceId) ? "GitHub Sync" : sourceId,
            State = MeshNodeState.Active,
            MainNode = spacePath,
            Content = config,
        };
        // Carry the caller's identity (captured in UpdateConfig before the async ReadConfig hop) on
        // the create — otherwise RLS denies Create on {space}/_GitSync when the AsyncLocal is gone.
        // Off-router issuing (NodeOperationIssuingHub): the DI root mesh hub must be neither end
        // of the exchange, nor execute the target-less request (ROUTER_TRAFFIC).
        return hub.NodeOperationIssuingHub().Observe<CreateOrUpdateNodeResponse>(
                new CreateOrUpdateNodeRequest(node),
                o => ctx is null ? o : o.WithAccessContext(ctx))
            .FirstAsync()
            .Select(d => d.Message)
            .SelectMany(resp => resp.Success
                ? Observable.Return(resp.Node!)
                : Observable.Throw<MeshNode>(new InvalidOperationException(
                    $"Failed to save GitHub sync config: {resp.Error}")));
    }

    // ContentAs (tolerant): typed → as-is, degraded JsonElement → recovered, otherwise
    // null and LOGGED LOUD. Replaces a bare `catch { return null; }` that silently
    // no-op'd GitHub sync on a degraded config node.
    private T? Extract<T>(MeshNode? node) where T : class
        => node.ContentAs<T>(hub.JsonSerializerOptions, logger);
}
