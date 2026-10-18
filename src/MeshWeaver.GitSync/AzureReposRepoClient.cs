using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The <see cref="IGitHubRepoClient"/> for an <b>Azure Repos</b> repository (MeshWeaver#6500,
/// policy <c>azure-repos-two-way</c>): the same import, export and two-way sync a GitHub source
/// has, over the Azure DevOps Git REST API (<c>api-version=7.1</c>) — <c>refs</c>, <c>commits</c>,
/// <c>items</c> + <c>blobs</c>, <c>pushes</c> and <c>diffs/commits</c>. <see cref="GitProviderRepoClient"/>
/// routes every Azure Repos URL here and every other URL to the GitHub client, so
/// <see cref="GitHubSyncService"/> runs ONE code path for both providers.
///
/// <para><b>Semantics are the contract the GitHub clients keep</b> and the parity suite pins them
/// side by side (<c>AzureReposGitHubParityTest</c>): mirror-within-subdirectory (paths outside the
/// prefix untouched, files no longer exported deleted), a missing branch based on the default
/// branch's head (never an orphan), an empty repository initialised by the first push, strict
/// UTF-8 text/binary classification (<see cref="RepoFileCodec"/>) and binary-lossless pushes
/// (every change travels <c>base64encoded</c>), a diff that answers <see langword="null"/> — "full
/// import" — whenever it cannot be computed reliably, and an ancestry question that answers
/// <see langword="false"/> whenever it cannot be established.</para>
///
/// <para><b>Two deliberate differences, both invisible to the sync.</b> (1) An export that changes
/// nothing creates no commit: the pushes API refuses a commit without changes, so the result names
/// the branch's current head — the commit that holds exactly the exported content, which is what
/// the sync records either way. (2) Creating the repository is not done (the GitHub path creates a
/// private one): an Azure Repos repository lives in a project the client administers, so a missing
/// one is refused by name.</para>
///
/// <para><b>Reactive end-to-end.</b> Every request is an IO leaf run inside <see cref="IIoPool"/>
/// (<see cref="IoPoolNames.Http"/>); every member returns a COLD observable, so nothing happens
/// before Subscribe. The token is the Entra token <see cref="AzureReposTokenService"/> minted from
/// the instance's DECLARED workload identity, sent as <c>Authorization: Bearer</c>; nothing secret
/// is stored or read here.</para>
///
/// <para>Pull requests and issues are not offered on Azure Repos in this version: those members
/// refuse by name (<see cref="AzureReposSyncPolicy"/>).</para>
/// </summary>
public sealed class AzureReposRepoClient : IGitHubRepoClient
{
    /// <summary>The Azure DevOps REST API version every request names.</summary>
    public const string ApiVersion = "7.1";

    /// <summary>The object id git writes for "no object" — the old id of a ref that does not exist yet.</summary>
    public const string ZeroObjectId = "0000000000000000000000000000000000000000";

    /// <summary>How many blob reads one fetch runs at once (each still takes an <see cref="IoPoolNames.Http"/> slot).</summary>
    private const int BlobReadConcurrency = 8;

    private readonly IoPoolRegistry ioPools;
    private readonly HttpClient http;
    private readonly ILogger<AzureReposRepoClient>? logger;

    /// <summary>Initializes the client.</summary>
    /// <param name="ioPools">The mesh's IO pools; every request runs on <see cref="IoPoolNames.Http"/>.</param>
    /// <param name="httpClient">The HTTP client to send with; a default one is created when null.
    /// Tests pass one over an in-memory Azure DevOps handler.</param>
    /// <param name="logger">Optional logger.</param>
    public AzureReposRepoClient(
        IoPoolRegistry ioPools,
        HttpClient? httpClient = null,
        ILogger<AzureReposRepoClient>? logger = null)
    {
        this.ioPools = ioPools;
        http = httpClient ?? new HttpClient();
        this.logger = logger;
    }

    private IIoPool Http => ioPools.Get(IoPoolNames.Http);

    // ══════════════════════════════════════════════════════════════════════════
    //  Export — one push of the mirror
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mirrors the request's files into the repository as ONE commit (<c>POST …/pushes</c>):
    /// reads the target branch's head and the blobs under the subdirectory there, then pushes an
    /// <c>add</c> per new path, an <c>edit</c> per path whose bytes changed and a <c>delete</c> per
    /// path no longer exported. The push names the head it read as the ref's old id, so a
    /// concurrent push to the branch is refused by Azure DevOps rather than overwritten.
    /// </summary>
    public IObservable<GitHubPushResult> Push(GitHubPushRequest request)
        => Observable.Defer(() =>
        {
            var repo = Parse(request.RepositoryUrl);
            var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch.Trim();
            var prefix = NormalizePrefix(request.Subdirectory);
            var token = request.AccessToken;
            return GetRepository(repo, request.RepositoryUrl, token)
                .SelectMany(info => BranchHead(repo, branch, token)
                    .SelectMany(head =>
                    {
                        if (head is not null)
                            return Observable.Return((Base: (string?)head, RefExists: true));
                        if (!request.CreateBranchIfMissing)
                            return Observable.Throw<(string? Base, bool RefExists)>(new InvalidOperationException(
                                $"Branch '{branch}' does not exist and branch auto-create is disabled."));
                        // A missing branch is based on the DEFAULT branch's head — never an orphan
                        // (the policy the GitHub path pins in NewBranchBaseTest). An EMPTY repository
                        // has no default head: the first push creates the branch from nothing.
                        return (info.DefaultBranch is { Length: > 0 } defaultBranch
                                ? BranchHead(repo, defaultBranch, token)
                                : Observable.Return<string?>(null))
                            .Select(baseSha => (Base: baseSha, RefExists: false));
                    }))
                .SelectMany(target => (target.Base is { } baseSha
                        ? ListBlobs(repo, baseSha, prefix, token)
                        : Observable.Return<IReadOnlyList<AzureReposItem>>([]))
                    .SelectMany(existing =>
                    {
                        var changes = MirrorChanges(prefix, existing, request.Files);
                        var deleted = changes.Count(c => c.ChangeType == ChangeDelete);
                        if (changes.Count == 0)
                        {
                            // Nothing differs: the head already holds exactly the exported content.
                            if (target.Base is not { } unchanged)
                                return Observable.Throw<GitHubPushResult>(GitSyncRefusal.Of(
                                    "gitsync.azure.nothingToExport", ("url", request.RepositoryUrl)).ToException());
                            return (target.RefExists
                                    ? Observable.Return(unchanged)
                                    : CreateRef(repo, branch, unchanged, token))
                                .Select(sha => new GitHubPushResult(
                                    sha, request.RepositoryUrl, request.Files.Count, 0, RepoCreated: false));
                        }
                        // A new branch over an existing history is created at its base first, so the
                        // push below is an ordinary fast-forward of a ref that exists.
                        var refReady = !target.RefExists && target.Base is { } branchBase
                            ? CreateRef(repo, branch, branchBase, token)
                            : Observable.Return(target.Base ?? ZeroObjectId);
                        return refReady
                            .SelectMany(oldObjectId => PushCommit(repo, branch, oldObjectId, changes, request))
                            .Select(sha => new GitHubPushResult(
                                sha, request.RepositoryUrl, request.Files.Count, deleted, RepoCreated: false));
                    }));
        });

    // ══════════════════════════════════════════════════════════════════════════
    //  Import — snapshot, head, diff, ancestry
    // ══════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        => Fetch(repositoryUrl, commitish, subdirectory, accessToken, _ => true);

    /// <summary>
    /// The snapshot at <paramref name="commitish"/>: the commit is resolved, the blobs under the
    /// subdirectory are LISTED (<c>items</c>, recursion <c>Full</c>), the filter selects from that
    /// listing, and only the selected blobs are read (<c>blobs/{id}</c>, octet stream). A
    /// subdirectory that does not exist at the commit answers an empty listing — the importer's
    /// own rules (refusal of a typo, retirement of a deleted folder) decide what that means.
    /// </summary>
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken,
        Func<string, bool> pathFilter)
    {
        ArgumentNullException.ThrowIfNull(pathFilter);
        return Observable.Defer(() =>
        {
            var repo = Parse(repositoryUrl);
            var prefix = NormalizePrefix(subdirectory);
            return ResolveCommit(repo, repositoryUrl, commitish, accessToken)
                .SelectMany(sha => ListBlobs(repo, sha, prefix, accessToken)
                    .SelectMany(items =>
                    {
                        var selected = items
                            .Select(i => (Item: i, Relative: i.RepoPath[prefix.Length..]))
                            .Where(x => pathFilter(x.Relative))
                            .ToArray();
                        if (selected.Length == 0)
                            return Observable.Return(new RepoSnapshot(sha, []));
                        return selected
                            .Select(x => ReadBlob(repo, x.Item.ObjectId, accessToken)
                                .Select(bytes => RepoFileCodec.FromBytes(x.Relative, bytes)))
                            .MergeBounded(BlobReadConcurrency)
                            .ToList()
                            .Select(files => new RepoSnapshot(sha,
                                files.OrderBy(f => f.Path, StringComparer.Ordinal).ToImmutableList()));
                    }));
        });
    }

    /// <inheritdoc />
    public IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
        => Observable.Defer(() => ResolveCommit(Parse(repositoryUrl), repositoryUrl, commitish, accessToken));

    /// <summary>
    /// The subdirectory-relative paths that changed between two commits (<c>diffs/commits</c>).
    /// <see langword="null"/> — "diff unknown, import in full" — when the base is not an ancestor
    /// of the head (a force-push: the common commit is not the base), when Azure DevOps reports the
    /// change list as incomplete, or when the call fails; never an empty list for an answer it
    /// does not have.
    /// </summary>
    public IObservable<IReadOnlyList<string>?> GetChangedPaths(
        string repositoryUrl, string baseSha, string headSha, string? subdirectory, string accessToken)
        => Observable.Defer(() =>
            {
                var repo = Parse(repositoryUrl);
                var prefix = NormalizePrefix(subdirectory);
                return Diff(repo, baseSha, headSha, accessToken)
                    .Select(diff => ChangedPaths(diff, baseSha, prefix));
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Azure Repos diff {Base}..{Head} of {Repo} could not be read; the import reads the full snapshot.",
                    baseSha, headSha, repositoryUrl);
                return Observable.Return<IReadOnlyList<string>?>(null);
            });

    /// <summary>
    /// Whether <paramref name="ancestorSha"/> is reachable from <paramref name="descendantRef"/>:
    /// the common commit of the two is the ancestor itself. <see langword="false"/> whenever that
    /// cannot be established (an unknown sha, a failed call).
    /// </summary>
    public IObservable<bool> IsAncestor(
        string repositoryUrl, string ancestorSha, string descendantRef, string accessToken)
        => Observable.Defer(() =>
            {
                var repo = Parse(repositoryUrl);
                return ResolveCommit(repo, repositoryUrl, descendantRef, accessToken)
                    .SelectMany(descendant => string.Equals(descendant, ancestorSha, StringComparison.OrdinalIgnoreCase)
                        ? Observable.Return(true)
                        : Diff(repo, ancestorSha, descendant, accessToken)
                            .Select(diff => string.Equals(
                                diff["commonCommit"]?.GetValue<string>(), ancestorSha, StringComparison.OrdinalIgnoreCase)));
            })
            .Catch((Exception ex) =>
            {
                logger?.LogDebug(ex, "Azure Repos ancestry {Ancestor} → {Ref} of {Repo} not established.",
                    ancestorSha, descendantRef, repositoryUrl);
                return Observable.Return(false);
            });

    /// <summary>Creates a branch at the commit its base ref resolves to (<c>POST …/refs</c>).</summary>
    public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request)
        => Observable.Defer(() =>
        {
            var repo = Parse(request.RepositoryUrl);
            return ResolveCommit(repo, request.RepositoryUrl, request.BaseRef, request.AccessToken)
                .SelectMany(sha => CreateRef(repo, request.NewBranch, sha, request.AccessToken))
                .Select(sha => new GitHubBranchResult(request.NewBranch, sha));
        });

    // ══════════════════════════════════════════════════════════════════════════
    //  Not offered on Azure Repos — refused by name
    // ══════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
        => Unsupported<GitHubPullRequestInfo>(request.RepositoryUrl, AzureReposSyncPolicy.Unsupported.PullRequest);

    /// <inheritdoc />
    public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(string repositoryUrl, int number, string accessToken)
        => Unsupported<GitHubPullRequestInfo>(repositoryUrl, AzureReposSyncPolicy.Unsupported.PullRequest);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
        string repositoryUrl, PullRequestStatus? state, string accessToken)
        => Unsupported<IReadOnlyList<GitHubPullRequestSummary>>(repositoryUrl, AzureReposSyncPolicy.Unsupported.PullRequest);

    /// <inheritdoc />
    public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(string repositoryUrl, int number, string accessToken)
        => Unsupported<GitHubPullRequestDetail>(repositoryUrl, AzureReposSyncPolicy.Unsupported.PullRequest);

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentPullRequest(
        string repositoryUrl, int number, string body, string accessToken)
        => Unsupported<GitHubIssueComment>(repositoryUrl, AzureReposSyncPolicy.Unsupported.PullRequest);

    /// <inheritdoc />
    public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
        => Unsupported<GitHubMergeResult>(request.RepositoryUrl, AzureReposSyncPolicy.Unsupported.PullRequest);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
        string repositoryUrl, GitHubIssueState? state, string accessToken)
        => Unsupported<IReadOnlyList<GitHubIssue>>(repositoryUrl, AzureReposSyncPolicy.Unsupported.Issues);

    /// <inheritdoc />
    public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
        => Unsupported<GitHubIssue>(repositoryUrl, AzureReposSyncPolicy.Unsupported.Issues);

    /// <inheritdoc />
    public IObservable<GitHubIssue?> FindIssueState(string repositoryUrl, int number, string accessToken)
        => Unsupported<GitHubIssue?>(repositoryUrl, AzureReposSyncPolicy.Unsupported.Issues);

    /// <inheritdoc />
    public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request)
        => Unsupported<GitHubIssue>(request.RepositoryUrl, AzureReposSyncPolicy.Unsupported.Issues);

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentIssue(
        string repositoryUrl, int number, string body, string accessToken)
        => Unsupported<GitHubIssueComment>(repositoryUrl, AzureReposSyncPolicy.Unsupported.Issues);

    /// <inheritdoc />
    public IObservable<GitHubIssue> SetIssueState(
        string repositoryUrl, int number, GitHubIssueState state, string accessToken)
        => Unsupported<GitHubIssue>(repositoryUrl, AzureReposSyncPolicy.Unsupported.Issues);

    private static IObservable<T> Unsupported<T>(string repositoryUrl, string operation)
        => Observable.Throw<T>(AzureReposSyncPolicy.Refuse(repositoryUrl, operation)!.ToException());

    // ══════════════════════════════════════════════════════════════════════════
    //  Pure mirror planning (offline-testable)
    // ══════════════════════════════════════════════════════════════════════════

    internal const string ChangeAdd = "add";
    internal const string ChangeEdit = "edit";
    internal const string ChangeDelete = "delete";

    /// <summary>One change of the mirror commit: a git path (rooted, <c>/a/b.md</c>) and, for add
    /// and edit, the bytes it carries.</summary>
    internal sealed record MirrorChange(string ChangeType, string Path, byte[]? Content);

    /// <summary>
    /// The changes that make the subdirectory match <paramref name="files"/> exactly, given the
    /// blobs it holds now. Unchanged bytes (same git blob id) produce no change. Pure.
    /// </summary>
    internal static IReadOnlyList<MirrorChange> MirrorChanges(
        string prefix, IReadOnlyList<AzureReposItem> existing, IReadOnlyList<RepoFile> files)
    {
        var current = existing.ToDictionary(i => i.RepoPath, i => i.ObjectId, StringComparer.Ordinal);
        var exported = new HashSet<string>(StringComparer.Ordinal);
        var changes = ImmutableList.CreateBuilder<MirrorChange>();
        foreach (var file in files)
        {
            var path = prefix + file.Path.TrimStart('/');
            if (!exported.Add(path))
                continue;
            var bytes = file.Bytes;
            if (current.TryGetValue(path, out var objectId))
            {
                if (!string.Equals(objectId, GitBlobId(bytes), StringComparison.OrdinalIgnoreCase))
                    changes.Add(new MirrorChange(ChangeEdit, "/" + path, bytes));
            }
            else
            {
                changes.Add(new MirrorChange(ChangeAdd, "/" + path, bytes));
            }
        }
        foreach (var path in current.Keys.Where(p => !exported.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
            changes.Add(new MirrorChange(ChangeDelete, "/" + path, null));
        return changes.ToImmutable();
    }

    /// <summary>The git blob id of <paramref name="bytes"/> — <c>sha1("blob {n}\0" + bytes)</c>, the
    /// id Azure DevOps reports as an item's <c>objectId</c>. Pure.</summary>
    internal static string GitBlobId(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        var buffer = new byte[header.Length + bytes.Length];
        header.CopyTo(buffer, 0);
        bytes.CopyTo(buffer, header.Length);
#pragma warning disable CA5350 // git's object id IS sha1; this names an object, it secures nothing
        return Convert.ToHexStringLower(SHA1.HashData(buffer));
#pragma warning restore CA5350
    }

    /// <summary>The <c>pushes</c> request body for one mirror commit. Pure.</summary>
    internal static JsonObject PushBody(
        string branch, string oldObjectId, IReadOnlyList<MirrorChange> changes,
        string message, string authorName, string authorEmail)
    {
        var who = new JsonObject { ["name"] = authorName, ["email"] = authorEmail };
        var jsonChanges = new JsonArray();
        foreach (var change in changes)
        {
            var entry = new JsonObject
            {
                ["changeType"] = change.ChangeType,
                ["item"] = new JsonObject { ["path"] = change.Path },
            };
            if (change.Content is { } content)
                entry["newContent"] = new JsonObject
                {
                    ["content"] = Convert.ToBase64String(content),
                    ["contentType"] = "base64encoded",
                };
            jsonChanges.Add(entry);
        }
        return new JsonObject
        {
            ["refUpdates"] = new JsonArray(new JsonObject
            {
                ["name"] = "refs/heads/" + branch,
                ["oldObjectId"] = oldObjectId,
            }),
            ["commits"] = new JsonArray(new JsonObject
            {
                ["comment"] = message,
                ["author"] = who.DeepClone(),
                ["committer"] = who.DeepClone(),
                ["changes"] = jsonChanges,
            }),
        };
    }

    /// <summary>
    /// The subdirectory-relative changed paths of a <c>diffs/commits</c> answer, or null when the
    /// answer is not a reliable diff of base → head. Renames contribute both their old and new
    /// path. Pure.
    /// </summary>
    internal static IReadOnlyList<string>? ChangedPaths(JsonNode diff, string baseSha, string prefix)
    {
        if (!string.Equals(diff["commonCommit"]?.GetValue<string>(), baseSha, StringComparison.OrdinalIgnoreCase))
            return null;   // the base is not an ancestor of the head — a rewritten history
        if (diff["allChangesIncluded"]?.GetValue<bool>() != true)
            return null;   // a partial list must never read as "nothing else changed"
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var change in diff["changes"]?.AsArray() ?? [])
        {
            if (change?["item"] is not JsonObject item || item["isFolder"]?.GetValue<bool>() == true)
                continue;
            foreach (var raw in new[] { item["path"]?.GetValue<string>(), change["sourceServerItem"]?.GetValue<string>() })
            {
                var path = raw?.TrimStart('/');
                if (string.IsNullOrEmpty(path))
                    continue;
                if (prefix.Length > 0 && !path.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                paths.Add(path[prefix.Length..]);
            }
        }
        return paths.ToImmutableList();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  REST leaves
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A blob listed under a commit: its repo-relative path (no leading slash) and git object id.</summary>
    internal sealed record AzureReposItem(string RepoPath, string ObjectId);

    private sealed record RepositoryInfo(string? DefaultBranch);

    private static AzureReposRepository Parse(string repositoryUrl)
        => AzureReposRepository.TryParse(repositoryUrl, out var repo)
            ? repo
            : throw new ArgumentException($"'{repositoryUrl}' is not an Azure Repos repository URL.", nameof(repositoryUrl));

    /// <summary>The repository's REST root, <c>https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}</c>.</summary>
    internal static string ApiRoot(AzureReposRepository repo)
        => $"https://dev.azure.com/{Uri.EscapeDataString(repo.Organization)}/{Uri.EscapeDataString(repo.Project)}"
           + $"/_apis/git/repositories/{Uri.EscapeDataString(repo.Repository)}";

    private static string Url(AzureReposRepository repo, string relative, params (string Name, string Value)[] query)
    {
        var builder = new StringBuilder(ApiRoot(repo)).Append(relative);
        var first = true;
        foreach (var (name, value) in query.Append(("api-version", ApiVersion)))
        {
            builder.Append(first ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }
        return builder.ToString();
    }

    private IObservable<RepositoryInfo> GetRepository(AzureReposRepository repo, string repositoryUrl, string token)
        => Send(HttpMethod.Get, Url(repo, ""), token, body: null)
            .Select(response => response.Status == HttpStatusCode.NotFound
                ? throw GitSyncRefusal.Of("gitsync.azure.repositoryNotFound", ("url", repositoryUrl)).ToException()
                : Ensure(response, "read the repository"))
            .Select(json => new RepositoryInfo(json["defaultBranch"]?.GetValue<string>() is { } full
                ? StripHeads(full)
                : null));

    /// <summary>The head of <paramref name="branch"/>, or null when the branch does not exist.</summary>
    private IObservable<string?> BranchHead(AzureReposRepository repo, string branch, string token)
        => Ref(repo, "heads/" + branch, token);

    private IObservable<string?> Ref(AzureReposRepository repo, string name, string token)
        => Send(HttpMethod.Get, Url(repo, "/refs", ("filter", name)), token, body: null)
            .Select(response => Ensure(response, "read refs"))
            .Select(json => (json["value"]?.AsArray() ?? [])
                // filter is a PREFIX match (heads/main also lists heads/main-old) — take the exact ref.
                .FirstOrDefault(r => string.Equals(r?["name"]?.GetValue<string>(), "refs/" + name, StringComparison.Ordinal))
                ?["objectId"]?.GetValue<string>());

    /// <summary>
    /// The full commit id a commitish names: a branch, else a tag, else a commit id (full or
    /// abbreviated). Refused with git's own meaning when none resolves.
    /// </summary>
    private IObservable<string> ResolveCommit(
        AzureReposRepository repo, string repositoryUrl, string commitish, string token)
    {
        var name = string.IsNullOrWhiteSpace(commitish) ? "main" : commitish.Trim();
        if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            name = name["refs/heads/".Length..];
        var looksLikeSha = name.Length is >= 7 and <= 40 && name.All(Uri.IsHexDigit);
        IObservable<string?> commit = looksLikeSha
            ? CommitId(repo, name, token)
            : Observable.Return<string?>(null);
        // A full commit id names exactly one commit: one call, no ref lookups.
        if (looksLikeSha && name.Length == 40)
            return commit.Select(sha => sha ?? throw new InvalidOperationException(
                $"'{name}' is not a commit of {repositoryUrl}."));
        return BranchHead(repo, name, token)
            .SelectMany(head => head is not null
                ? Observable.Return<string?>(head)
                : Ref(repo, "tags/" + name, token))
            .SelectMany(tag => tag is not null ? Observable.Return<string?>(tag) : commit)
            .Select(sha => sha ?? throw new InvalidOperationException(
                $"'{name}' is not a branch, tag or commit of {repositoryUrl}."));
    }

    private IObservable<string?> CommitId(AzureReposRepository repo, string id, string token)
        => Send(HttpMethod.Get, Url(repo, "/commits/" + Uri.EscapeDataString(id)), token, body: null)
            .Select(response => response.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest
                ? null
                : Ensure(response, "read a commit")["commitId"]?.GetValue<string>());

    /// <summary>Every blob under <paramref name="prefix"/> at <paramref name="commit"/>; empty when the
    /// path does not exist there.</summary>
    private IObservable<IReadOnlyList<AzureReposItem>> ListBlobs(
        AzureReposRepository repo, string commit, string prefix, string token)
        => Send(HttpMethod.Get, Url(repo, "/items",
                ("scopePath", "/" + prefix.TrimEnd('/')),
                ("recursionLevel", "Full"),
                ("versionDescriptor.version", commit),
                ("versionDescriptor.versionType", "commit")), token, body: null)
            .Select(response => response.Status == HttpStatusCode.NotFound
                ? (IReadOnlyList<AzureReposItem>)[]
                : (IReadOnlyList<AzureReposItem>)(Ensure(response, "list items")["value"]?.AsArray() ?? [])
                    .Where(i => i?["isFolder"]?.GetValue<bool>() != true
                                && !string.Equals(i?["gitObjectType"]?.GetValue<string>(), "tree", StringComparison.OrdinalIgnoreCase))
                    .Select(i => new AzureReposItem(
                        (i!["path"]?.GetValue<string>() ?? "").TrimStart('/'),
                        i["objectId"]?.GetValue<string>() ?? ""))
                    .Where(i => i.RepoPath.Length > 0
                                && (prefix.Length == 0 || i.RepoPath.StartsWith(prefix, StringComparison.Ordinal)))
                    .ToImmutableList());

    private IObservable<byte[]> ReadBlob(AzureReposRepository repo, string objectId, string token)
        => SendBytes(Url(repo, "/blobs/" + Uri.EscapeDataString(objectId), ("$format", "octetstream")), token);

    private IObservable<JsonNode> Diff(AzureReposRepository repo, string baseSha, string headSha, string token)
        => Send(HttpMethod.Get, Url(repo, "/diffs/commits",
                ("baseVersion", baseSha), ("baseVersionType", "commit"),
                ("targetVersion", headSha), ("targetVersionType", "commit"),
                ("$top", "10000")), token, body: null)
            .Select(response => Ensure(response, "read a diff"));

    /// <summary>Creates <c>refs/heads/{branch}</c> at <paramref name="sha"/>; emits the sha.</summary>
    private IObservable<string> CreateRef(AzureReposRepository repo, string branch, string sha, string token)
        => Send(HttpMethod.Post, Url(repo, "/refs"), token, new JsonArray(new JsonObject
            {
                ["name"] = "refs/heads/" + branch,
                ["oldObjectId"] = ZeroObjectId,
                ["newObjectId"] = sha,
            }))
            .Select(response =>
            {
                var json = Ensure(response, "create a branch");
                var update = (json["value"]?.AsArray() ?? []).FirstOrDefault();
                if (update?["success"]?.GetValue<bool>() == false)
                    throw new InvalidOperationException(
                        $"Azure Repos refused to create branch '{branch}': {update["updateStatus"]?.GetValue<string>()}.");
                return sha;
            });

    private IObservable<string> PushCommit(
        AzureReposRepository repo, string branch, string oldObjectId,
        IReadOnlyList<MirrorChange> changes, GitHubPushRequest request)
        => Send(HttpMethod.Post, Url(repo, "/pushes"), request.AccessToken,
                PushBody(branch, oldObjectId, changes, request.CommitMessage, request.AuthorName, request.AuthorEmail))
            .Select(response => Ensure(response, "push"))
            .Select(json => json["commits"]?.AsArray().FirstOrDefault()?["commitId"]?.GetValue<string>()
                            ?? throw new InvalidOperationException(
                                $"Azure Repos accepted the push to {request.RepositoryUrl} but named no commit."))
            .Do(sha => logger?.LogInformation(
                "Pushed {Changes} change(s) to {Repo}@{Branch} as {Sha}.",
                changes.Count, request.RepositoryUrl, branch, sha));

    private sealed record ApiResponse(HttpStatusCode Status, string Body);

    /// <summary>One JSON request, inside the HTTP pool. Never throws for a non-2xx status — the
    /// caller decides what a 404 means; <see cref="Ensure"/> turns anything else into an error.</summary>
    private IObservable<ApiResponse> Send(HttpMethod method, string url, string token, JsonNode? body)
        => Http.Invoke(ct => SendAsync(method, url, token, body, ct));

    private async Task<ApiResponse> SendAsync(
        HttpMethod method, string url, string token, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        Authorize(request, token);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new ApiResponse(response.StatusCode, text);
    }

    private IObservable<byte[]> SendBytes(string url, string token)
        => Http.Invoke(ct => SendBytesAsync(url, token, ct));

    private async Task<byte[]> SendBytesAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(request, token);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw Failure(response.StatusCode, detail, "read a blob");
        }
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static void Authorize(HttpRequestMessage request, string token)
    {
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static JsonNode Ensure(ApiResponse response, string what)
        => (int)response.Status is >= 200 and < 300
            ? JsonNode.Parse(response.Body.Length == 0 ? "{}" : response.Body) ?? new JsonObject()
            : throw Failure(response.Status, response.Body, what);

    /// <summary>A failed call, carrying Azure DevOps' own message (its error body's <c>message</c>).</summary>
    private static InvalidOperationException Failure(HttpStatusCode status, string body, string what)
    {
        string? message = null;
        try
        {
            message = JsonNode.Parse(body)?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            // Not a JSON error body (a proxy page, an empty 401): the status says what is known.
        }
        return new InvalidOperationException(
            $"Azure Repos could not {what} (HTTP {(int)status}){(message is { Length: > 0 } m ? ": " + m : ".")}");
    }

    private static string StripHeads(string reference)
        => reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;

    /// <summary>
    /// Normalizes the mirror subdirectory to a <c>segment/…/</c> prefix and REJECTS any shape that
    /// could escape it: rooted paths, <c>.</c>/<c>..</c> segments, backslashes — the same rule the
    /// git-protocol client applies, because the prefix names the deletion target of the mirror.
    /// </summary>
    internal static string NormalizePrefix(string? subdirectory)
    {
        var s = subdirectory?.Trim().Trim('/');
        if (string.IsNullOrEmpty(s))
            return "";
        if (s.Contains('\\') || s.Split('/').Any(seg => seg.Length == 0 || seg is "." or ".."))
            throw new ArgumentException($"Invalid repo subdirectory '{subdirectory}'.");
        return s + "/";
    }
}
