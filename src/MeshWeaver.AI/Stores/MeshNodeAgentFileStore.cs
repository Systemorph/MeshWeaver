using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileSystemGlobbing;

namespace MeshWeaver.AI.Stores;

/// <summary>
/// Mesh-node implementation of the Microsoft Agent Framework's <see cref="AgentFileStore"/> — the
/// pluggable backing store its harness providers (file memory, file access) write through.
///
/// <para>MAF ships a <c>FileSystemAgentFileStore</c> (a directory on disk) and an
/// <c>InMemoryAgentFileStore</c>. Both are invisible to the rest of the platform: files an agent
/// writes to them are not versioned, not permissioned, not searchable, and vanish when the process
/// or pod does. This implementation maps the same operations onto mesh nodes instead, so an agent's
/// working files ARE first-class content — versioned, access-controlled, visible in the portal, and
/// reachable by every other mesh reader.</para>
///
/// <para><b>Mapping.</b> The store is rooted at a mesh path. A store-relative path <c>notes/x.md</c>
/// becomes the node at <c>{root}/notes/x.md</c> — the last segment is the node id, everything before
/// it the namespace. Files are <c>Markdown</c> nodes carrying <see cref="MarkdownContent"/>;
/// directories are <c>Group</c> nodes. Nesting is implicit in the path, so a directory node exists
/// only when <see cref="CreateDirectory"/> made one — <see cref="ListChildren"/> reports both, and a
/// file written into a never-created directory still lands correctly.</para>
///
/// <para><b>The API is reactive, end to end.</b> Reads are the live per-path
/// <c>GetMeshNodeStream</c> handle; listings and searches are the live synced <c>GetQuery</c>
/// collection; writes go through <c>stream.Update</c>, the platform's one mutation API. Every one of
/// those runs on the bounded <c>AgentStore</c> I/O pool under the caller's identity. <see cref="Task"/>
/// appears only in the handful of <c>*Async</c> members MAF declares abstract, each a one-line
/// adapter — see <see cref="MeshStoreAccess"/>.</para>
/// </summary>
/// <remarks>
/// <see cref="AgentFileStore"/> is marked <c>[Experimental("MAAI001")]</c> upstream; the project
/// opts in through <c>NoWarn</c> in <c>MeshWeaver.AI.csproj</c>.
/// </remarks>
public sealed class MeshNodeAgentFileStore : AgentFileStore
{
    private readonly MeshStoreAccess mesh;
    private readonly IMeshService meshService;
    private readonly string root;

    /// <summary>
    /// Wall-clock bound on a single regex match. The search pattern is supplied by the model, so it
    /// is untrusted input running on a bounded pool — an unbounded match is a denial-of-service on
    /// both the pool and the CPU.
    /// </summary>
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The mesh path this store is rooted at. Every store path resolves beneath it.</summary>
    public string Root => root;

    /// <summary>
    /// Creates a store rooted at <paramref name="root"/>.
    /// </summary>
    /// <param name="hub">Hub supplying the workspace, mesh service, I/O pool and the caller's identity.</param>
    /// <param name="root">
    /// Mesh path the store is rooted at — typically a path under the thread for session-scoped
    /// memory, or under the user's partition for memory that should outlive the thread.
    /// </param>
    public MeshNodeAgentFileStore(IMessageHub hub, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        mesh = new MeshStoreAccess(hub, nameof(MeshNodeAgentFileStore));
        meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        this.root = root.TrimEnd('/');
    }

    // ── The reactive surface: this IS the implementation ──────────────────────────────────────

    /// <summary>
    /// The LIVE content of a file — re-emits whenever the node changes. Never completes while the
    /// file exists, so consumers stay bound to it rather than sampling it.
    /// </summary>
    /// <param name="path">Store-relative path.</param>
    public IObservable<string?> Read(string path) =>
        mesh.Stream(() => mesh.ReadNode(ResolvePath(root, path)).Select(ContentOf));

    /// <summary>
    /// Creates or overwrites a file, emitting the written node. Cold — the write runs on Subscribe.
    /// </summary>
    /// <param name="path">Store-relative path.</param>
    /// <param name="content">Text to store.</param>
    public IObservable<MeshNode> Write(string path, string content) =>
        mesh.Once(() => WriteCore(path, content));

    /// <summary>
    /// Whether a file (not a directory) currently exists — a bounded probe rather than the live
    /// stream, which would fault instead of reporting absence.
    /// </summary>
    /// <param name="path">Store-relative path.</param>
    public IObservable<bool> Exists(string path) =>
        mesh.Once(() => mesh.ProbeNode(ResolvePath(root, path))
            .Select(node => node is not null && !IsDirectory(node)));

    /// <summary>
    /// Deletes a FILE, emitting whether one existed there. A directory is never deleted through this
    /// path — it reports <see langword="false"/>, the same as an absent file.
    /// </summary>
    /// <param name="path">Store-relative path.</param>
    public IObservable<bool> Delete(string path) =>
        mesh.Once(() => DeleteCore(ResolvePath(root, path)));

    /// <summary>Ensures a directory marker node exists. Cold — the write runs on Subscribe.</summary>
    /// <param name="path">Store-relative path.</param>
    public IObservable<MeshNode> CreateDirectory(string path) =>
        mesh.Once(() => CreateDirectoryCore(path));

    /// <summary>
    /// The LIVE listing of a directory's direct children, subdirectories first — re-emits as the
    /// directory's contents change.
    /// </summary>
    /// <param name="directory">Store-relative directory path; empty for the root.</param>
    public IObservable<IReadOnlyCollection<FileStoreEntry>> ListChildren(string directory) =>
        mesh.Stream(() => ListChildrenCore(directory));

    /// <summary>
    /// The LIVE result of a content search — re-emits as matching content changes.
    /// </summary>
    /// <param name="directory">Store-relative directory to search; empty for the root.</param>
    /// <param name="regexPattern">Case-insensitive regex matched per line.</param>
    /// <param name="globPattern">Optional path glob restricting which files are searched.</param>
    /// <param name="recursive">Search all descendants rather than only direct children.</param>
    public IObservable<IReadOnlyCollection<FileSearchResult>> Search(
        string directory, string regexPattern, string? globPattern = null, bool recursive = false) =>
        mesh.Stream(() => SearchCore(directory, regexPattern, globPattern, recursive));

    // ── The Microsoft Agent Framework surface ─────────────────────────────────────────────────
    // Sanctioned SDK-boundary adapters, uniform: one expression each, NO async/await state machine
    // (the method just returns a Task), one ToTask per call. Task appears HERE and nowhere else —
    // these signatures are MAF's, not ours. Where a single value is required the .Take(1) sits at
    // this boundary, so every reactive method above stays live for every other consumer.

    /// <inheritdoc />
    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
        Write(path, content).ToTask(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Bounded, not live: MAF's contract is "the content, or null if absent", and the live
    /// <see cref="Read"/> never emits an absence — a missing node's stream faults instead.
    /// </remarks>
    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        ReadOnce(path).ToTask(cancellationToken);

    /// <summary>A bounded single read: the file's content, or <c>null</c> when it does not exist.</summary>
    /// <param name="path">Store-relative path.</param>
    private IObservable<string?> ReadOnce(string path) =>
        mesh.Once(() => mesh.ProbeNode(ResolvePath(root, path)).Select(ContentOf));

    /// <inheritdoc />
    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Exists(path).ToTask(cancellationToken);

    /// <inheritdoc />
    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        Delete(path).ToTask(cancellationToken);

    /// <inheritdoc />
    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        CreateDirectory(path).ToTask(cancellationToken);

    /// <inheritdoc />
    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory, CancellationToken cancellationToken = default) =>
        ListChildren(directory)
            .Take(1)
            .Select(entries => (IReadOnlyList<FileStoreEntry>)entries.ToList())
            .ToTask(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// One snapshot of a live query. Content search IS a query here — <see cref="Search"/> keeps
    /// re-emitting as matching content changes — but MAF's signature carries a single value, so the
    /// <c>.Take(1)</c> sits at this boundary like every other override's. Callers that want the
    /// result to stay current bind to <see cref="Search"/> instead.
    /// </remarks>
    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern = null,
        bool recursive = false,
        CancellationToken cancellationToken = default) =>
        Search(directory, regexPattern, globPattern, recursive)
            .Take(1)
            .Select(results => (IReadOnlyList<FileSearchResult>)results.ToList())
            .ToTask(cancellationToken);

    // ── Cores ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates or overwrites the file node.
    ///
    /// <para><b>Overwrite</b> goes through <c>GetMeshNodeStream(path).Update(...)</c> — THE mutation
    /// API. The owning hub serialises every writer through its single-threaded action block, so
    /// concurrent writes to the same file cannot race, and a cross-hub write sends only the RFC 7396
    /// merge patch rather than the whole node.</para>
    ///
    /// <para><b>Creation</b> cannot go through it: <c>stream.Update</c> targets a node's per-node
    /// hub, and for a path that does not exist yet that hub never activates — the write comes back
    /// <c>DeliveryFailure: No node found</c>. Bringing a node into being is node LIFECYCLE, which is
    /// what <c>CreateNode</c> is for. Hence the probe: existing → mutate, absent → create.</para>
    /// </summary>
    private IObservable<MeshNode> WriteCore(string path, string content)
    {
        var relative = NormalizeRelative(path);
        if (relative.Length == 0)
            return Observable.Throw<MeshNode>(
                new ArgumentException("Cannot write to the store root itself.", nameof(path)));

        var fullPath = $"{root}/{relative}";
        var (ns, id) = SplitPath(fullPath);
        // Parse (not a bare wrap) so the node renders in the portal like any other markdown
        // document. It runs on the pool thread, never on a hub action block.
        var parsed = MarkdownContent.Parse(content, currentNodePath: fullPath);

        return mesh.ProbeNode(fullPath).SelectMany(existing => existing is null
            ? meshService.CreateNode(new MeshNode(id, ns)
            {
                Name = id,
                NodeType = MarkdownNodeType.NodeType,
                Content = parsed,
            })
            : mesh.Hub.GetMeshNodeStream(fullPath).Update(node => node with
            {
                Name = node.Name ?? id,
                NodeType = MarkdownNodeType.NodeType,
                Content = parsed,
            }));
    }

    /// <summary>
    /// Deletes the node, reporting whether it existed. MAF's contract distinguishes "deleted" from
    /// "was not there", so the probe comes first and a genuine absence short-circuits to
    /// <see langword="false"/> rather than issuing a delete that would fail. <c>DeleteNode</c> is
    /// node LIFECYCLE (not a content mutation), the sanctioned use of the mesh service.
    /// </summary>
    private IObservable<bool> DeleteCore(string fullPath) =>
        mesh.ProbeNode(fullPath)
            .SelectMany(node => node is null || IsDirectory(node)
                // Absent, or a directory: either way there is no FILE here to delete. Reported as
                // false rather than deleting the directory — Exists() already draws that distinction,
                // and a delete that silently removed a folder (and orphaned everything filed under
                // it) is not what "delete this file" can be allowed to mean.
                ? Observable.Return(false)
                : meshService.DeleteNode(fullPath));

    /// <summary>
    /// Ensures the directory marker node exists, or completes trivially for the root (which is
    /// implicit — every store path already hangs off it). Idempotent: an existing directory is
    /// returned as-is rather than rewritten, so calling this repeatedly costs nothing.
    /// </summary>
    private IObservable<MeshNode> CreateDirectoryCore(string path)
    {
        var relative = NormalizeRelative(path);
        if (relative.Length == 0)
            return Observable.Return(new MeshNode(root));

        var fullPath = $"{root}/{relative}";
        var (ns, id) = SplitPath(fullPath);

        return mesh.ProbeNode(fullPath).SelectMany(existing => existing switch
        {
            // Already a directory — idempotent, nothing to write.
            not null when IsDirectory(existing) => Observable.Return(existing),
            // A FILE occupies the path. Returning it would report success while leaving the caller
            // with no directory, and the next write beneath it would land somewhere it did not ask
            // for. Surface the conflict.
            not null => Observable.Throw<MeshNode>(new InvalidOperationException(
                $"Cannot create directory '{path}': a file already exists at that path.")),
            _ => meshService.CreateNode(new MeshNode(id, ns)
            {
                Name = id,
                NodeType = GroupNodeType.NodeType,
            }),
        });
    }

    /// <summary>
    /// Lists direct children off the live synced query. MAF's contract puts subdirectories before
    /// files; within each group we order by name so listings are stable across backends and calls.
    /// </summary>
    private IObservable<IReadOnlyCollection<FileStoreEntry>> ListChildrenCore(string directory) =>
        // Metadata-only consumer: a listing reports name + kind and never touches Content, so
        // `content` is deliberately NOT named. See Doc/DataMesh/QuerySyntax → "select: decides
        // whether Content is loaded at all".
        mesh.Query($"path:{ResolvePath(root, directory)} scope:children select:path,id,name,nodeType")
            .Select(nodes => (IReadOnlyCollection<FileStoreEntry>)nodes
                .Select(node => new FileStoreEntry(
                    NameOf(node.Path),
                    IsDirectory(node) ? FileStoreEntry.Directory : FileStoreEntry.File))
                .OrderBy(entry => entry.Type == FileStoreEntry.Directory ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());

    /// <summary>
    /// Content regex search over the live synced query. The synced collection carries whole nodes,
    /// content included, so a search is ONE subscription — no second read per candidate — and it
    /// re-evaluates automatically whenever any matching file changes.
    /// </summary>
    private IObservable<IReadOnlyCollection<FileSearchResult>> SearchCore(
        string directory, string regexPattern, string? globPattern, bool recursive)
    {
        var searchRoot = ResolvePath(root, directory);
        var scope = recursive ? "descendants" : "children";
        // 🚨 The pattern comes from the MODEL, not from us. An unbounded Regex over arbitrary input
        // can backtrack catastrophically, and it would burn a bounded AgentStore pool slot and a core
        // while doing it. Bound every match; a pattern that trips the bound surfaces as an error the
        // tool reports, never as a silent empty result.
        var regex = new Regex(
            regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexMatchTimeout);
        var matcher = BuildMatcher(globPattern);

        // 🚨 Content-bearing consumer: the regex runs against each node's Content, so `content` is
        // named DELIBERATELY. Omitting it makes Postgres project NULL::jsonb and every match
        // silently disappears — no error, no empty result signal, just a search that finds nothing.
        return mesh.Query($"path:{searchRoot} scope:{scope} select:path,id,name,nodeType,content")
            .Select(nodes => (IReadOnlyCollection<FileSearchResult>)nodes
                .Where(node => !IsDirectory(node))
                .Select(node => (Node: node, Relative: RelativeTo(searchRoot, node.Path)))
                .Where(candidate => candidate.Relative.Length > 0
                                    && (matcher is null || matcher.Match(candidate.Relative).HasMatches))
                .Select(candidate => MatchFile(candidate.Relative, ContentOf(candidate.Node), regex))
                .Where(result => result is not null)
                .Select(result => result!)
                .OrderBy(result => result.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    /// <summary>
    /// Applies the regex line-by-line, producing MAF's result shape (1-based line numbers, plus a
    /// snippet), or <see langword="null"/> when the file does not match at all.
    /// </summary>
    private static FileSearchResult? MatchFile(string relativePath, string? content, Regex regex)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        var lines = content.Split('\n');
        var matches = new List<FileSearchMatch>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (regex.IsMatch(line))
                matches.Add(new FileSearchMatch { LineNumber = i + 1, Line = line });
        }

        return matches.Count == 0
            ? null
            : new FileSearchResult
            {
                FileName = relativePath,
                Snippet = matches[0].Line,
                MatchingLines = matches,
            };
    }

    /// <summary>Builds the glob matcher, or <see langword="null"/> when no pattern was given.</summary>
    private static Matcher? BuildMatcher(string? globPattern)
    {
        if (string.IsNullOrWhiteSpace(globPattern))
            return null;
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(globPattern);
        return matcher;
    }

    /// <summary>Extracts the text a file node carries, tolerating typed or JSON-round-tripped content.</summary>
    private static string? ContentOf(MeshNode? node) => node?.Content switch
    {
        null => null,
        MarkdownContent markdown => markdown.Content,
        string text => text,
        JsonElement json => json.ValueKind == JsonValueKind.Object
                            && json.TryGetProperty("content", out var value)
                            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null,
        _ => null,
    };

    /// <summary>True when the node is a directory marker rather than a file.</summary>
    private static bool IsDirectory(MeshNode node) =>
        string.Equals(node.NodeType, GroupNodeType.NodeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalises a store-relative path and resolves it against <paramref name="storeRoot"/>.
    /// </summary>
    /// <param name="storeRoot">The store's mesh root path.</param>
    /// <param name="relativePath">Caller-supplied relative path; empty means the root itself.</param>
    private static string ResolvePath(string storeRoot, string? relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        return normalized.Length == 0 ? storeRoot : $"{storeRoot}/{normalized}";
    }

    /// <summary>
    /// Normalises a store-relative path to <c>a/b/c</c> form: backslashes folded to forward slashes,
    /// empty and <c>.</c> segments dropped, leading/trailing separators trimmed. Rejects absolute
    /// paths and any <c>..</c> segment — MAF requires implementations to enforce that a store path
    /// can never escape its root.
    /// </summary>
    /// <param name="relativePath">Caller-supplied relative path.</param>
    /// <exception cref="ArgumentException">A <c>..</c> segment or a rooted path was supplied.</exception>
    private static string NormalizeRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        var candidate = relativePath.Replace('\\', '/').Trim();
        if (candidate.StartsWith('/'))
            throw new ArgumentException(
                $"Store paths are relative to the store root; '{relativePath}' is rooted.",
                nameof(relativePath));

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
                continue;
            if (segment == "..")
                throw new ArgumentException(
                    $"Store paths must not escape the root; '{relativePath}' contains '..'.",
                    nameof(relativePath));
            kept.Add(segment);
        }

        return string.Join('/', kept);
    }

    /// <summary>
    /// Splits a full mesh path into the <c>(namespace, id)</c> pair a <see cref="MeshNode"/> is
    /// constructed from — a node's <c>Path</c> is <c>Namespace/Id</c>, so the last segment is the id
    /// and everything before it the namespace.
    /// </summary>
    private static (string Namespace, string Id) SplitPath(string fullPath)
    {
        var lastSeparator = fullPath.LastIndexOf('/');
        return lastSeparator < 0
            ? (string.Empty, fullPath)
            : (fullPath[..lastSeparator], fullPath[(lastSeparator + 1)..]);
    }

    /// <summary>The last path segment — the node id, which is the store's file name.</summary>
    private static string NameOf(string path)
    {
        var lastSeparator = path.LastIndexOf('/');
        return lastSeparator < 0 ? path : path[(lastSeparator + 1)..];
    }

    /// <summary>
    /// Re-expresses a full mesh path as a path relative to <paramref name="basePath"/>, which is
    /// what MAF's search results report. Returns empty when the path is not beneath the base.
    /// </summary>
    private static string RelativeTo(string basePath, string fullPath) =>
        fullPath.Length > basePath.Length
        && fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)
        && fullPath[basePath.Length] == '/'
            ? fullPath[(basePath.Length + 1)..]
            : string.Empty;
}
