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
                    "No GitHub repository configured for this Space — set the repository URL in GitHub Sync settings."));

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
                        return repoClient.Push(request).SelectMany(result =>
                            UpdateConfig(spacePath, c => c with
                            {
                                LastSyncedAt = DateTimeOffset.UtcNow,
                                LastSyncCommitSha = result.CommitSha,
                            }).Select(_ => result));
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
                    .SelectMany(x => UpdateConfig(spacePath, c => c with
                    {
                        LastSyncCommitSha = x.CommitSha,
                        LastSyncedAt = DateTimeOffset.UtcNow,
                    }).Select(_ => x.Result));
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

    /// <summary>Reads the Space's <see cref="GitHubSyncConfig"/>, or null when none is set.</summary>
    public IObservable<GitHubSyncConfig?> ReadConfig(string spacePath)
    {
        var path = ConfigPath(spacePath);
        return hub.GetWorkspace()
            .GetQuery($"gitsync-cfg:{spacePath}", $"path:{path}")
            .Take(1)
            .Select(nodes => Extract<GitHubSyncConfig>(
                nodes?.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase))));
    }

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

    private IObservable<MeshNode> UpdateConfig(string spacePath, Func<GitHubSyncConfig, GitHubSyncConfig> update)
    {
        return ReadConfig(spacePath).Take(1).SelectMany(current =>
        {
            var updated = update(current ?? new GitHubSyncConfig());
            var node = new MeshNode(ConfigId, spacePath)
            {
                NodeType = ConfigNodeType,
                Name = "GitHub Sync",
                State = MeshNodeState.Active,
                MainNode = spacePath,
                Content = updated,
            };
            return hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
                .FirstAsync()
                .Select(d => d.Message)
                .SelectMany(resp => resp.Success
                    ? Observable.Return(resp.Node!)
                    : Observable.Throw<MeshNode>(new InvalidOperationException(
                        $"Failed to save GitHub sync config: {resp.Error}")));
        });
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
