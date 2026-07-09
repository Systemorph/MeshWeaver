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
    public const string ConfigId = "_GitSync";
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
        // Plugin-owned node types (for example Slide, registered by the Slides plugin's
        // AddSlides()) contribute an IMarkdownContentMapper so their content round-trips typed
        // through git export/import instead of downgrading to MarkdownContent. Resolved from the
        // hub's DI so core GitSync never references a plugin content type.
        var contentMappers = hub.ServiceProvider.GetServices<IMarkdownContentMapper>().ToArray();
        parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions, contentMappers);
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
    /// </summary>
    public IObservable<GitHubPushResult> SyncToGitHub(string spacePath, string userId, string? sourceId = null)
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
                return SnapshotNodes(spacePath).SelectMany(nodes =>
                    SerializeAll(nodes, spacePath).SelectMany(files =>
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
                        return repoClient.Push(request).SelectMany(result =>
                            RecordLastSync(spacePath, result.CommitSha, sourceId).Select(_ => result));
                    }));
            });
        });
    }

    /// <summary>Root + descendants of the Space, filtered to exportable content nodes.</summary>
    private IObservable<IReadOnlyList<MeshNode>> SnapshotNodes(string spacePath)
    {
        var root = hub.GetWorkspace().GetMeshNodeStream(spacePath)
            .Where(n => n is not null).Take(1).Timeout(TimeSpan.FromSeconds(30));
        var descendants = meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{spacePath} scope:descendants"))
            .Take(1).Select(c => (IEnumerable<MeshNode>)c.Items);
        return root.CombineLatest(descendants, (r, desc) =>
        {
            var all = new List<MeshNode>();
            if (r is not null) all.Add(r);
            all.AddRange(desc);
            return Filter(all, spacePath);
        });
    }

    /// <summary>
    /// Keeps content nodes only: drops satellite/governance subtrees (a <c>_</c>-prefixed
    /// segment after the partition root — <c>_GitSync</c>, <c>_Access</c>, <c>_Activity</c>,
    /// threads, notifications, …) and honours <see cref="SyncBehavior"/>.
    /// </summary>
    private static IReadOnlyList<MeshNode> Filter(List<MeshNode> all, string partition)
    {
        var excludedRoots = all
            .Where(n => n.SyncBehavior == SyncBehavior.ExcludeThisAndChildren)
            .Select(n => n.Path)
            .ToArray();
        bool underExcluded(string p) =>
            excludedRoots.Any(r => p.StartsWith(r + "/", StringComparison.Ordinal));

        return all
            .Where(n => !string.IsNullOrEmpty(n.Path)
                        && !n.Segments.Skip(1).Any(s => s.StartsWith('_'))
                        && n.SyncBehavior == SyncBehavior.Include
                        && !underExcluded(n.Path))
            .GroupBy(n => n.Path, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private IObservable<IReadOnlyList<RepoFile>> SerializeAll(IReadOnlyList<MeshNode> nodes, string partition)
    {
        if (nodes.Count == 0)
            return Observable.Return((IReadOnlyList<RepoFile>)Array.Empty<RepoFile>());
        var allPaths = nodes.Select(n => n.Path).ToArray();
        return nodes
            .Select(n => SerializeOne(n, partition, allPaths))
            .Merge(8)
            .Where(f => f is not null).Select(f => f!)
            .ToList()
            .Select(list => AppendReadme(list, nodes, partition));
    }

    /// <summary>
    /// Adds a top-level <c>README.md</c> rendered from the Space root's body so the GitHub
    /// repo page shows a landing page. The authoritative root remains <c>index.json</c>;
    /// import skips <c>README.md</c> so it never becomes a stray node.
    /// </summary>
    private IReadOnlyList<RepoFile> AppendReadme(IList<RepoFile> files, IReadOnlyList<MeshNode> nodes, string partition)
    {
        var list = files.ToList();
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

    private IObservable<RepoFile?> SerializeOne(MeshNode node, string partition, string[] allPaths)
    {
        var serializer = parsers.GetSerializerFor(node);
        if (serializer is null)
        {
            logger?.LogWarning("No serializer for node {Path} (type {Type}) — skipping.", node.Path, node.NodeType);
            return Observable.Return<RepoFile?>(null);
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
    /// </summary>
    public IObservable<StaticRepoImportResult> ImportFromGitHub(
        string repositoryUrl, string commitish, string newSpaceId, string newSpaceName,
        string? subdirectory, string userId)
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
                Content = new Space { Name = newSpaceName },
            };
            logger?.LogInformation("Importing {Repo}@{Ref} into new Space {Space}", repositoryUrl, commitish, newSpaceId);
            var createSpace = accessService is null || ctx is null
                ? meshService.CreateNode(spaceNode)
                : Observable.Using(() => accessService.SwitchAccessContext(ctx), _ => meshService.CreateNode(spaceNode));
            return createSpace
                .SelectMany(_ => FetchAndImport(repositoryUrl, commitish, subdirectory, token, newSpaceId))
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
    /// </summary>
    public IObservable<StaticRepoImportResult> ReimportAtCommit(
        string spacePath, string commitish, string userId, string? sourceId = null)
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
            return ResolveAuth(userId).SelectMany(auth =>
            {
                var token = auth.Token;
                logger?.LogInformation("Re-importing {Space} at {Ref}", spacePath, commitish);
                return FetchAndImport(repoUrl, commitish, config.Subdirectory, token, spacePath)
                    .SelectMany(x => RecordLastSync(spacePath, x.CommitSha, sourceId).Select(_ => x.Result));
            });
        });
    }

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
        string repoUrl, string commitish, string? subdirectory, string token, string spaceId)
    {
        return repoClient.Fetch(repoUrl, commitish, subdirectory, token).SelectMany(snapshot =>
            ParseSnapshot(snapshot, spaceId).SelectMany(parsed =>
            {
                var source = new InMemoryStaticRepoSource(spaceId, parsed.Children, parsed.Root);
                return StaticRepoImporter.ImportSource(hub, source, logger)
                    .Select(result => (result, snapshot.CommitSha));
            }));
    }

    private IObservable<(MeshNode? Root, IReadOnlyList<MeshNode> Children)> ParseSnapshot(
        RepoSnapshot snapshot, string spaceId)
    {
        if (snapshot.Files.Count == 0)
            return Observable.Return(((MeshNode?)null, (IReadOnlyList<MeshNode>)Array.Empty<MeshNode>()));
        return snapshot.Files
            .Select(f => ParseFile(f, spaceId))
            .Merge(8)
            .Where(x => x.Node is not null)
            .ToList()
            .Select(list =>
            {
                var root = list.FirstOrDefault(x => x.IsRoot).Node;
                var children = list.Where(x => !x.IsRoot).Select(x => x.Node!).ToList();
                return (root, (IReadOnlyList<MeshNode>)children);
            });
    }

    private IObservable<(MeshNode? Node, bool IsRoot)> ParseFile(RepoFile file, string spaceId)
    {
        // The top-level README.md is a GitHub display file emitted on export — never a node.
        if (string.Equals(file.Path, "README.md", StringComparison.OrdinalIgnoreCase))
            return Observable.Return(((MeshNode?)null, false));

        var ext = System.IO.Path.GetExtension(file.Path);
        // file.Content is already an in-memory string — the parse is pure CPU, no pool.
        var parsed = parsers.TryParse(ext, file.Path, file.Content, file.Path);
        if (parsed is null) return Observable.Return(((MeshNode?)null, false));
        if (NodeFileMapper.IsRootIndex(file.Path))
        {
            var root = parsed with
            {
                Id = spaceId,
                Namespace = "",
                MainNode = spaceId,
                NodeType = string.IsNullOrEmpty(parsed.NodeType) ? SpaceNodeType : parsed.NodeType,
            };
            return Observable.Return(((MeshNode?)root, true));
        }
        var (id, ns) = NodeFileMapper.FromRelativePath(file.Path);
        var rebasedNs = string.IsNullOrEmpty(ns) ? spaceId : $"{spaceId}/{ns}";
        var node = parsed with { Id = id, Namespace = rebasedNs, MainNode = $"{rebasedNs}/{id}" };
        return Observable.Return(((MeshNode?)node, false));
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
            .GetQuery($"gitsync-cfg:{path}", $"path:{path}")
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
            .GetQuery($"gitsync-cfgs:{spacePath}", $"namespace:{primaryPath} nodeType:{ConfigNodeType}")
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
        SyncDirection direction = SyncDirection.Bidirectional, string? sourceId = null)
        => UpdateConfig(spacePath, c => c with
        {
            RepositoryUrl = string.IsNullOrWhiteSpace(repositoryUrl) ? null : repositoryUrl.Trim(),
            Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim(),
            Subdirectory = string.IsNullOrWhiteSpace(subdirectory) ? null : subdirectory.Trim().Trim('/'),
            CreateBranchIfMissing = createBranchIfMissing,
            CreateRepoIfMissing = createRepoIfMissing,
            Direction = direction,
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

    /// <summary>
    /// Records the last-sync result by MERGING only <see cref="GitHubSyncConfig.LastSyncedAt"/> /
    /// <see cref="GitHubSyncConfig.LastSyncCommitSha"/> atop the latest node content via
    /// <c>GetMeshNodeStream(path).Update</c> (read-modify-write). Touching only those two fields
    /// means a concurrent GUI edit of the repository fields is never clobbered.
    /// </summary>
    private IObservable<MeshNode> RecordLastSync(string spacePath, string commitSha, string? sourceId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return hub.GetWorkspace().GetMeshNodeStream(ConfigPath(spacePath, sourceId)).Update(node =>
        {
            var cur = Extract<GitHubSyncConfig>(node) ?? new GitHubSyncConfig();
            return node with { Content = cur with { LastSyncedAt = now, LastSyncCommitSha = commitSha } };
        });
    }

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
        return hub.Observe<CreateOrUpdateNodeResponse>(
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
