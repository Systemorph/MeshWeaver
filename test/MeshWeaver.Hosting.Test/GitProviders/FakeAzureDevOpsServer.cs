#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace MeshWeaver.Hosting.Test.GitProviders;

/// <summary>
/// Azure DevOps at the HTTP boundary: an <see cref="HttpMessageHandler"/> that answers the Git
/// REST API (<c>api-version=7.1</c>) the way <c>dev.azure.com</c> does, over
/// <see cref="InMemoryGitRemote"/>s registered per <c>{org}/{project}/{repo}</c>. It is an external
/// service, so this is where the parity suite fakes it — the production
/// <see cref="MeshWeaver.GitSync.AzureReposRepoClient"/> runs unchanged on top.
///
/// <para>Served: the repository (<c>GET …/repositories/{repo}</c>, 404 when not registered),
/// <c>refs</c> (prefix <c>filter</c>; <c>POST</c> creates), <c>commits/{id}</c>, <c>items</c>
/// (recursion <c>Full</c> at a commit; 404 for a path the commit does not have), <c>blobs/{id}</c>
/// (octet stream), <c>pushes</c> (the ref's old id must be its current head — a stale push is 409;
/// an add of an existing path, an edit or delete of a missing one, or a commit with no changes is
/// 400, as Azure DevOps refuses them) and <c>diffs/commits</c> (the merge base as
/// <c>commonCommit</c>). Every request must carry <c>Authorization: Bearer</c> with
/// <see cref="ExpectedToken"/>, else 401.</para>
/// </summary>
public sealed class FakeAzureDevOpsServer : HttpMessageHandler
{
    private ImmutableDictionary<string, InMemoryGitRemote> repositories =
        ImmutableDictionary<string, InMemoryGitRemote>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private ImmutableList<string> requests = [];

    /// <summary>The bearer token every request must carry.</summary>
    public string ExpectedToken { get; init; } = "entra-token";

    /// <summary>Every request served, as <c>METHOD /relative?query</c> below the repository root.</summary>
    public ImmutableList<string> Requests => requests;

    private ImmutableDictionary<string, (string TagObject, string Commit)> annotatedTags =
        ImmutableDictionary<string, (string TagObject, string Commit)>.Empty;

    /// <summary>
    /// When set, <c>items</c> answers 404 "repository not found" although refs and commits still
    /// resolve — the repository deleted, or the identity's Read revoked, between two calls of one
    /// fetch. The case that must FAIL a fetch rather than read as an empty snapshot.
    /// </summary>
    public bool ItemsSeeNoRepository { get; set; }

    /// <summary>Adds an ANNOTATED tag: its ref names a tag object, and only <c>peelTags=true</c>
    /// reveals the commit (<c>peeledObjectId</c>), as on Azure DevOps.</summary>
    public void AddAnnotatedTag(string organization, string project, string repository, string tag, string commit)
    {
        var tagObject = InMemoryGitRemote.BlobId(Encoding.UTF8.GetBytes("tag " + tag + " " + commit));
        ImmutableInterlocked.Update(ref annotatedTags, t => t.SetItem(Key(organization, project, repository) + "|" + tag, (tagObject, commit)));
    }

    /// <summary>Registers (or returns) the repository <c>{org}/{project}/{repo}</c>.</summary>
    public InMemoryGitRemote Repository(string organization, string project, string repository)
        => ImmutableInterlocked.GetOrAdd(ref repositories, Key(organization, project, repository), _ => new InMemoryGitRemote());

    /// <summary>Reads the request body (the one IO this fake does), then answers it synchronously.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Answer(request, body);
    }

    private HttpResponseMessage Answer(HttpRequestMessage request, string? body)
    {
        if (request.Headers.Authorization is not { Scheme: "Bearer" } auth || auth.Parameter != ExpectedToken)
            return Error(HttpStatusCode.Unauthorized, "TF400813: The user is not authorized to access this resource.");
        var uri = request.RequestUri!;
        if (!string.Equals(uri.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase))
            return Error(HttpStatusCode.NotFound, "unknown host " + uri.Host);
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (query["api-version"] != "7.1")
            return Error(HttpStatusCode.BadRequest, "api-version 7.1 is required");

        // /{org}/{project}/_apis/git/repositories/{repo}[/rest…]
        var segments = uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 6 || segments[2] != "_apis" || segments[3] != "git" || segments[4] != "repositories")
            return Error(HttpStatusCode.NotFound, "not a git repositories route");
        var rest = string.Join('/', segments.Skip(6));
        ImmutableInterlocked.Update(ref requests, r => r.Add($"{request.Method} /{rest}{uri.Query}"));
        var repoKey = Key(segments[0], segments[1], segments[5]);
        if (!repositories.TryGetValue(repoKey, out var repo))
            return Error(HttpStatusCode.NotFound,
                $"TF401019: The Git repository with name or identifier {segments[5]} does not exist or you do not have permissions for the operation you are attempting.");

        return (request.Method.Method, rest) switch
        {
            ("GET", "") => Json(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["name"] = segments[5],
                ["defaultBranch"] = repo.DefaultBranch is { } d ? "refs/heads/" + d : null,
            }),
            ("GET", "refs") => Refs(repoKey, repo, query["filter"] ?? "", query["peelTags"] == "true"),
            ("POST", "refs") => CreateRefs(repo, body!),
            ("GET", "items") => ItemsSeeNoRepository
                ? Error(HttpStatusCode.NotFound, $"TF401019: The Git repository with name or identifier {segments[5]} does not exist.")
                : Items(repo, query),
            ("POST", "pushes") => Push(repo, body!),
            ("GET", "diffs/commits") => Diff(repo, query),
            ("GET", var r) when r.StartsWith("commits/", StringComparison.Ordinal) =>
                repo.FindCommit(r["commits/".Length..]) is { } c
                    ? Json(new JsonObject { ["commitId"] = c.Sha, ["comment"] = c.Message })
                    : Error(HttpStatusCode.NotFound, "TF401175: The version descriptor could not be resolved."),
            ("GET", var r) when r.StartsWith("blobs/", StringComparison.Ordinal) => Blob(repo, r["blobs/".Length..]),
            _ => Error(HttpStatusCode.NotFound, $"no route {request.Method} {rest}"),
        };
    }

    private HttpResponseMessage Refs(string repoKey, InMemoryGitRemote repo, string filter, bool peel)
    {
        var branches = repo.Branches
            .Where(b => ("heads/" + b.Key).StartsWith(filter, StringComparison.Ordinal))
            .OrderBy(b => b.Key, StringComparer.Ordinal)
            .Select(b => (JsonNode)new JsonObject { ["name"] = "refs/heads/" + b.Key, ["objectId"] = b.Value });
        var tags = annotatedTags
            .Where(t => t.Key.StartsWith(repoKey + "|", StringComparison.Ordinal))
            .Select(t => (Name: t.Key[(repoKey.Length + 1)..], t.Value))
            .Where(t => ("tags/" + t.Name).StartsWith(filter, StringComparison.Ordinal))
            .Select(t =>
            {
                var entry = new JsonObject { ["name"] = "refs/tags/" + t.Name, ["objectId"] = t.Value.TagObject };
                if (peel)
                    entry["peeledObjectId"] = t.Value.Commit;
                return (JsonNode)entry;
            });
        return Json(new JsonObject { ["value"] = new JsonArray(branches.Concat(tags).ToArray()) });
    }

    private static HttpResponseMessage CreateRefs(InMemoryGitRemote repo, string body)
    {
        var results = new JsonArray();
        foreach (var update in JsonNode.Parse(body)!.AsArray())
        {
            var name = update!["name"]!.GetValue<string>();
            var branch = name.StartsWith("refs/heads/", StringComparison.Ordinal) ? name["refs/heads/".Length..] : name;
            var ok = update["oldObjectId"]!.GetValue<string>() == ZeroId
                     && repo.CreateBranch(branch, update["newObjectId"]!.GetValue<string>());
            results.Add(new JsonObject
            {
                ["name"] = name,
                ["newObjectId"] = update["newObjectId"]!.GetValue<string>(),
                ["success"] = ok,
                ["updateStatus"] = ok ? "succeeded" : "staleOldObjectId",
            });
        }
        return Json(new JsonObject { ["value"] = results });
    }

    private static HttpResponseMessage Items(InMemoryGitRemote repo, System.Collections.Specialized.NameValueCollection query)
    {
        if (query["recursionLevel"] != "Full" || query["versionDescriptor.versionType"] != "commit")
            return Error(HttpStatusCode.BadRequest, "this fake serves recursionLevel=Full at a commit");
        if (repo.FindCommit(query["versionDescriptor.version"] ?? "") is not { } commit)
            return Error(HttpStatusCode.NotFound, "TF401175: The version descriptor could not be resolved.",
                "GitUnresolvableToCommitException");
        var scope = (query["scopePath"] ?? "/").Trim('/');
        var prefix = scope.Length == 0 ? "" : scope + "/";
        var blobs = commit.Tree.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (scope.Length > 0 && blobs.Length == 0)
            return Error(HttpStatusCode.NotFound, $"TF401174: The item '/{scope}' could not be found in the repository.",
                "GitItemNotFoundException");
        var folders = blobs
            .SelectMany(kv => Parents(kv.Key))
            .Where(f => f.Length >= scope.Length)
            .Prepend(scope)
            .Distinct(StringComparer.Ordinal);
        var value = new JsonArray();
        foreach (var folder in folders)
            value.Add(new JsonObject { ["path"] = "/" + folder, ["isFolder"] = true, ["gitObjectType"] = "tree" });
        foreach (var (path, bytes) in blobs)
            value.Add(new JsonObject
            {
                ["path"] = "/" + path,
                ["objectId"] = InMemoryGitRemote.BlobId(bytes),
                ["gitObjectType"] = "blob",
            });
        return Json(new JsonObject { ["count"] = value.Count, ["value"] = value });
    }

    private static IEnumerable<string> Parents(string path)
    {
        for (var i = path.IndexOf('/'); i > 0; i = path.IndexOf('/', i + 1))
            yield return path[..i];
    }

    private static HttpResponseMessage Blob(InMemoryGitRemote repo, string objectId)
    {
        var bytes = repo.Commits.Values.SelectMany(c => c.Tree.Values)
            .FirstOrDefault(b => string.Equals(InMemoryGitRemote.BlobId(b), objectId, StringComparison.OrdinalIgnoreCase));
        return bytes is null
            ? Error(HttpStatusCode.NotFound, "TF401028: The reference could not be found.")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    private static HttpResponseMessage Push(InMemoryGitRemote repo, string body)
    {
        var json = JsonNode.Parse(body)!;
        var refUpdate = json["refUpdates"]![0]!;
        var name = refUpdate["name"]!.GetValue<string>();
        var branch = name["refs/heads/".Length..];
        var oldObjectId = refUpdate["oldObjectId"]!.GetValue<string>();
        var head = repo.Head(branch);
        if (!string.Equals(head ?? ZeroId, oldObjectId, StringComparison.OrdinalIgnoreCase))
            return Error(HttpStatusCode.Conflict,
                $"TF402455: Pushes to this branch are not permitted; the old object id {oldObjectId} is not the head of {name}.");
        var commit = json["commits"]![0]!;
        var changes = commit["changes"]!.AsArray();
        if (changes.Count == 0)
            return Error(HttpStatusCode.BadRequest, "TF401027: A commit must contain at least one change.");
        // Validated against the tree the push names, BEFORE anything is written: Azure DevOps
        // refuses the whole push for one bad change.
        var current = head is null ? InMemoryGitRemote.EmptyTree : repo.Commits[head].Tree;
        foreach (var change in changes)
        {
            var path = change!["item"]!["path"]!.GetValue<string>().TrimStart('/');
            var type = change["changeType"]!.GetValue<string>();
            var exists = current.ContainsKey(path);
            if ((type == "add" && exists) || (type is "edit" or "delete" && !exists))
                return Error(HttpStatusCode.BadRequest, $"TF401028: cannot {type} '/{path}'.");
            if (type is "add" or "edit" && change["newContent"]?["contentType"]?.GetValue<string>() != "base64encoded")
                return Error(HttpStatusCode.BadRequest, "this fake reads base64encoded content only");
        }
        var written = repo.CommitTo(branch, tree =>
        {
            foreach (var change in changes)
            {
                var path = change!["item"]!["path"]!.GetValue<string>().TrimStart('/');
                tree = change["changeType"]!.GetValue<string>() == "delete"
                    ? tree.Remove(path)
                    : tree.SetItem(path, Convert.FromBase64String(change["newContent"]!["content"]!.GetValue<string>()));
            }
            return tree;
        }, commit["comment"]!.GetValue<string>(), expectedHead: head, checkExpectedHead: true);
        return Json(new JsonObject
        {
            ["pushId"] = repo.Commits.Count,
            ["commits"] = new JsonArray(new JsonObject { ["commitId"] = written.Sha }),
            ["refUpdates"] = new JsonArray(new JsonObject { ["name"] = name, ["newObjectId"] = written.Sha }),
        }, HttpStatusCode.Created);
    }

    private static HttpResponseMessage Diff(InMemoryGitRemote repo, System.Collections.Specialized.NameValueCollection query)
    {
        if (repo.FindCommit(query["baseVersion"] ?? "") is not { } from
            || repo.FindCommit(query["targetVersion"] ?? "") is not { } to)
            return Error(HttpStatusCode.NotFound, "TF401175: The version descriptor could not be resolved.",
                "GitUnresolvableToCommitException");
        var changes = new JsonArray();
        foreach (var path in from.Tree.Keys.Union(to.Tree.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            var inFrom = from.Tree.TryGetValue(path, out var a);
            var inTo = to.Tree.TryGetValue(path, out var b);
            var type = !inFrom ? "add" : !inTo ? "delete" : a!.SequenceEqual(b!) ? null : "edit";
            if (type is not null)
                changes.Add(new JsonObject
                {
                    ["item"] = new JsonObject { ["path"] = "/" + path, ["gitObjectType"] = "blob" },
                    ["changeType"] = type,
                });
        }
        return Json(new JsonObject
        {
            ["allChangesIncluded"] = true,
            ["commonCommit"] = repo.MergeBase(from.Sha, to.Sha),
            ["baseCommit"] = from.Sha,
            ["targetCommit"] = to.Sha,
            ["changes"] = changes,
        });
    }

    private const string ZeroId = "0000000000000000000000000000000000000000";

    private static string Key(string organization, string project, string repository)
        => $"{organization}/{project}/{repository}";

    private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string message, string typeKey = "GitRepositoryNotFoundException")
        => Json(new JsonObject { ["message"] = message, ["typeKey"] = typeKey }, status);
}
