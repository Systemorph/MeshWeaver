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
/// signature). Every blocking/async leaf — Octokit calls, the OAuth HTTP, and the
/// file-format <c>SerializeAsync</c>/<c>ParseAsync</c> — runs inside
/// <see cref="IIoPool"/> per <c>Doc/Architecture/ControlledIoPooling.md</c>.</para>
/// </summary>
public sealed class GitHubSyncService
{
    public const string ConfigId = "_GitSync";
    public const string ConfigNodeType = "GitHubSyncConfig";
    public const string SpaceNodeType = "Space";

    private readonly IMessageHub hub;
    private readonly IMeshService meshService;
    private readonly IGitHubRepoClient repoClient;
    private readonly GitHubCredentialService credentials;
    private readonly ILogger? logger;
    private readonly IoPoolRegistry ioPools;
    private readonly FileFormatParserRegistry parsers;

    public GitHubSyncService(
        IMessageHub hub,
        IMeshService meshService,
        IGitHubRepoClient repoClient,
        GitHubCredentialService credentials,
        ILogger<GitHubSyncService>? logger = null)
    {
        this.hub = hub;
        this.meshService = meshService;
        this.repoClient = repoClient;
        this.credentials = credentials;
        this.logger = logger;
        ioPools = hub.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions);
    }

    private IIoPool FsPool => ioPools.Get(IoPoolNames.FileSystem);

    public static string ConfigPath(string spacePath) => $"{spacePath}/{ConfigId}";

    // ══════════════════════════════════════════════════════════════════════════
    //  EXPORT — mesh → GitHub (the "sync back")
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mirrors the Space subtree into its configured GitHub repo as a single commit,
    /// authenticated as <paramref name="userId"/>, and stores the resulting commit
    /// SHA on the Space's <see cref="GitHubSyncConfig"/>. Emits the push result.
    /// </summary>
    public IObservable<GitHubPushResult> SyncToGitHub(string spacePath, string userId)
    {
        return ReadConfig(spacePath).Take(1).SelectMany(config =>
        {
            if (config?.RepositoryUrl is not { Length: > 0 } repoUrl)
                return Observable.Throw<GitHubPushResult>(new InvalidOperationException(
                    "No repository URL is set for this Space yet. In the Repository section above, enter a " +
                    "URL like https://github.com/owner/repo (the repo is created automatically if it doesn't " +
                    "exist), then Sync."));

            return credentials.Get(userId).Take(1).SelectMany(cred =>
            {
                if (cred?.AccessToken is not { Length: > 0 } token)
                    return Observable.Throw<GitHubPushResult>(new InvalidOperationException(
                        "Connect your GitHub account first (GitHub Sync settings → Connect)."));

                return SnapshotNodes(spacePath).SelectMany(nodes =>
                    SerializeAll(nodes, spacePath).SelectMany(files =>
                    {
                        var (name, email) = AuthorIdentity(cred);
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
                            RecordLastSync(spacePath, result.CommitSha).Select(_ => result));
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
        return FsPool.Invoke(ct => serializer.SerializeAsync(node, ct))
            .Select(content => (RepoFile?)new RepoFile(repoPath, content));
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
        return credentials.Get(userId).Take(1).SelectMany(cred =>
        {
            if (cred?.AccessToken is not { Length: > 0 } token)
                return Observable.Throw<StaticRepoImportResult>(new InvalidOperationException(
                    "Connect your GitHub account first."));

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
            return meshService.CreateNode(spaceNode)
                .SelectMany(_ => FetchAndImport(repositoryUrl, commitish, subdirectory, token, newSpaceId))
                .Select(x => x.Result);
        });
    }

    /// <summary>
    /// Re-imports an existing Space to the state of <paramref name="commitish"/> (a
    /// branch or commit SHA), mirroring the repo into the partition (add/update/prune),
    /// and records the resolved commit SHA on the Space's config. This is the
    /// "change the commit and re-import to that state" operation.
    /// </summary>
    public IObservable<StaticRepoImportResult> ReimportAtCommit(string spacePath, string commitish, string userId)
    {
        return ReadConfig(spacePath).Take(1).SelectMany(config =>
        {
            if (config?.RepositoryUrl is not { Length: > 0 } repoUrl)
                return Observable.Throw<StaticRepoImportResult>(new InvalidOperationException(
                    "No GitHub repository configured for this Space."));
            return credentials.Get(userId).Take(1).SelectMany(cred =>
            {
                if (cred?.AccessToken is not { Length: > 0 } token)
                    return Observable.Throw<StaticRepoImportResult>(new InvalidOperationException(
                        "Connect your GitHub account first."));
                logger?.LogInformation("Re-importing {Space} at {Ref}", spacePath, commitish);
                return FetchAndImport(repoUrl, commitish, config.Subdirectory, token, spacePath)
                    .SelectMany(x => RecordLastSync(spacePath, x.CommitSha).Select(_ => x.Result));
            });
        });
    }

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
        return FsPool.Invoke(ct => parsers.TryParseAsync(ext, file.Path, file.Content, file.Path, ct))
            .Select(parsed =>
            {
                if (parsed is null) return ((MeshNode?)null, false);
                if (NodeFileMapper.IsRootIndex(file.Path))
                {
                    var root = parsed with
                    {
                        Id = spaceId,
                        Namespace = "",
                        MainNode = spaceId,
                        NodeType = string.IsNullOrEmpty(parsed.NodeType) ? SpaceNodeType : parsed.NodeType,
                    };
                    return ((MeshNode?)root, true);
                }
                var (id, ns) = NodeFileMapper.FromRelativePath(file.Path);
                var rebasedNs = string.IsNullOrEmpty(ns) ? spaceId : $"{spaceId}/{ns}";
                var node = parsed with { Id = id, Namespace = rebasedNs, MainNode = $"{rebasedNs}/{id}" };
                return ((MeshNode?)node, false);
            });
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
    public IObservable<GitHubSyncConfig?> WatchConfig(string spacePath) =>
        WatchConfigNode(spacePath).Select(Extract<GitHubSyncConfig>);

    /// <summary>Live <see cref="MeshNode"/> stream for the Space's <c>_GitSync</c> config node
    /// (or null when absent) — the synced <c>GetQuery</c>. The GUI editor binds to this node by
    /// path via <c>GetMeshNodeStream</c>; this stream is for service-side reads/displays.</summary>
    public IObservable<MeshNode?> WatchConfigNode(string spacePath)
    {
        var path = ConfigPath(spacePath);
        return hub.GetWorkspace()
            .GetQuery($"gitsync-cfg:{spacePath}", $"path:{path}")
            .Select(nodes => nodes?.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Create-on-absent the Space's <c>_GitSync</c> config node (with defaults) so the standard
    /// node editor has a node to bind to. Existing node untouched. 🚨 Create-on-absent reads
    /// existence via the keyed <c>GetQuery</c> (empty-on-absent) and seeds through the
    /// node-lifecycle <c>CreateNode</c> — NEVER a point <c>GetMeshNodeStream(path).Update</c> on an
    /// absent path (that NotFound-storms). Mirrors <c>AiSettingsNodeType.EnsureExists</c>.
    /// Returns the existing or newly-created node.
    /// </summary>
    public IObservable<MeshNode> EnsureConfigNode(string spacePath)
    {
        var path = ConfigPath(spacePath);
        return WatchConfigNode(spacePath).Take(1).SelectMany(existing =>
        {
            if (existing is not null) return Observable.Return(existing);
            var node = new MeshNode(ConfigId, spacePath)
            {
                NodeType = ConfigNodeType,
                Name = "GitHub Sync",
                State = MeshNodeState.Active,
                MainNode = spacePath,
                Content = new GitHubSyncConfig(),
            };
            // CreateNode is create-only (rejects an existing node) — if a concurrent caller won the
            // race, fall back to reading the now-present node rather than surfacing the conflict.
            return meshService.CreateNode(node)
                .Catch<MeshNode, Exception>(_ => WatchConfigNode(spacePath).Where(n => n is not null).Select(n => n!).Take(1));
        });
    }

    /// <summary>One-shot config read for actions (Sync / Re-import). The synced query's first
    /// emission already reflects a committed write (the GUI auto-saves the repo URL on edit, so by
    /// Sync time the config is present).</summary>
    public IObservable<GitHubSyncConfig?> ReadConfig(string spacePath) => WatchConfig(spacePath).Take(1);

    /// <summary>Persists the repository settings (preserving the recorded last-sync state).</summary>
    public IObservable<MeshNode> SaveConfig(
        string spacePath, string? repositoryUrl, string branch, string? subdirectory,
        bool createBranchIfMissing, bool createRepoIfMissing)
        => UpdateConfig(spacePath, c => c with
        {
            RepositoryUrl = string.IsNullOrWhiteSpace(repositoryUrl) ? null : repositoryUrl.Trim(),
            Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim(),
            Subdirectory = string.IsNullOrWhiteSpace(subdirectory) ? null : subdirectory.Trim().Trim('/'),
            CreateBranchIfMissing = createBranchIfMissing,
            CreateRepoIfMissing = createRepoIfMissing,
        });

    /// <summary>Read-modify-write a config field (current value from the synced query; null when absent).</summary>
    private IObservable<MeshNode> UpdateConfig(string spacePath, Func<GitHubSyncConfig, GitHubSyncConfig> update)
        => ReadConfig(spacePath).SelectMany(current => WriteConfig(spacePath, update(current ?? new GitHubSyncConfig())));

    /// <summary>
    /// Records the last-sync result by MERGING only <see cref="GitHubSyncConfig.LastSyncedAt"/> /
    /// <see cref="GitHubSyncConfig.LastSyncCommitSha"/> atop the latest node content via
    /// <c>GetMeshNodeStream(path).Update</c> (read-modify-write). Touching only those two fields
    /// means a concurrent GUI edit of the repository fields is never clobbered.
    /// </summary>
    private IObservable<MeshNode> RecordLastSync(string spacePath, string commitSha)
    {
        var now = DateTimeOffset.UtcNow;
        return hub.GetWorkspace().GetMeshNodeStream(ConfigPath(spacePath)).Update(node =>
        {
            var cur = Extract<GitHubSyncConfig>(node) ?? new GitHubSyncConfig();
            return node with { Content = cur with { LastSyncedAt = now, LastSyncCommitSha = commitSha } };
        });
    }

    /// <summary>Writes the FULL config (no read) — used by <see cref="SaveConfig"/> (a programmatic
    /// / test API). The GUI does NOT use this: it edits the node through the standard
    /// <c>MeshNodeContentEditorControl</c> which binds directly to the node stream.</summary>
    private IObservable<MeshNode> WriteConfig(string spacePath, GitHubSyncConfig config)
    {
        var node = new MeshNode(ConfigId, spacePath)
        {
            NodeType = ConfigNodeType,
            Name = "GitHub Sync",
            State = MeshNodeState.Active,
            MainNode = spacePath,
            Content = config,
        };
        return hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
            .FirstAsync()
            .Select(d => d.Message)
            .SelectMany(resp => resp.Success
                ? Observable.Return(resp.Node!)
                : Observable.Throw<MeshNode>(new InvalidOperationException(
                    $"Failed to save GitHub sync config: {resp.Error}")));
    }

    private T? Extract<T>(MeshNode? node) where T : class
    {
        if (node?.Content is null) return null;
        if (node.Content is T typed) return typed;
        if (node.Content is System.Text.Json.JsonElement je)
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<T>(je.GetRawText(), hub.JsonSerializerOptions); }
            catch { return null; }
        }
        return null;
    }
}
