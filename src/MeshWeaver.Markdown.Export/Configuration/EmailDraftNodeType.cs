using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// The <c>EmailDraft</c> node type — a per-user, per-document draft of the "Share ⇒ as email"
/// compose form.
///
/// <para><b>🚨 Where the draft lives, and why it is NOT under the document.</b> The obvious place is
/// a satellite under the document being shared (<c>{doc}/_Draft/…</c>). That placement CANNOT be made
/// private, and a draft holds recipient addresses and a personal message. Access in this framework is
/// additive along the path and is folded in SQL on <c>main_node</c>: a satellite whose main node is
/// the document is readable by everyone who can read the document, through four independent read
/// paths (<c>PermissionEvaluator</c>'s scope walk, the <c>MeshNodeStreamCache</c> gate,
/// <c>SatelliteAccessRule</c>, and the <c>user_effective_permissions</c> fold in
/// <c>PostgreSqlSqlGenerator</c>). No per-node owner flag, deny assignment or "private" visibility
/// exists to override that, and <c>PartitionAccessPolicy.BreaksInheritance</c> — the nearest thing —
/// is not honoured by the SQL fold at all, so listing/search would still leak.
/// <c>ThreadComposerNodeType.PathForNode</c> is the live precedent of that leak: one user's chat
/// draft under a shared node is readable today by any other reader of that node.</para>
///
/// <para><b>So the draft is anchored under its AUTHOR</b> — <c>{userId}/_Draft/{documentKey}</c> —
/// which is the framework's actual mechanism for per-user private state, shared by
/// <c>{userId}/_Settings/Notifications</c>, <c>{userId}/_UserActivity/{key}</c> and
/// <c>{userId}/Feedback/{id}</c>. Privacy is then structural rather than asserted: the self-scope
/// owner rule grants a user Admin at the scope equal to their own id, and no other regular user
/// holds any grant on that partition, so a draft is closed by default on every read path including
/// SQL listing. It is still a satellite anchored to a real owner — never a top-level node — and it
/// still names the document it belongs to, both in its path key and in
/// <see cref="EmailDraft.DocumentPath"/>.</para>
///
/// <para><b>Storage table:</b> <c>_Draft</c> is deliberately NOT registered in
/// <c>SatelliteTableMapping.Defaults</c>, so it routes to the partition's ordinary
/// <c>mesh_nodes</c> table — exactly as the <c>_Settings</c> segment does. That is the right answer
/// here: drafts are few, per-user, and only ever read by exact path, so a dedicated satellite table
/// buys no query shape — while adding one WOULD require a schema migration across every already
/// provisioned partition (the <c>V26_AddNotificationsSatelliteTable</c> shape), since partition
/// provisioning is promise-cached and never re-run for existing partitions.</para>
/// </summary>
public static class EmailDraftNodeType
{
    /// <summary>The NodeType value identifying email-draft nodes.</summary>
    public const string NodeType = "EmailDraft";

    /// <summary>The per-user satellite segment holding compose drafts.</summary>
    public const string DraftSegment = "_Draft";

    /// <summary>
    /// How long an untouched draft is still offered back to its author. Past this, opening the
    /// dialog starts from a clean form instead of resurrecting a months-old recipient — the draft
    /// exists to survive a consent round trip and a closed tab, not to become a permanent shadow
    /// copy of a mail nobody sent. Made explicit rather than implicit, per the lifecycle contract.
    /// </summary>
    public static TimeSpan MaxAge => TimeSpan.FromDays(7);

    /// <summary>Registers the built-in "EmailDraft" MeshNode on the mesh builder.</summary>
    public static TBuilder AddEmailDraftType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<EmailDraft>(nameof(EmailDraft)));
        return builder;
    }

    /// <summary>Creates the MeshNode definition for the EmailDraft node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Email Draft",
        IsSatelliteType = true,
        // A half-written mail is not content: it must never surface in search, in the
        // create menu, or as agent context.
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<EmailDraft>())
    };

    /// <summary>The namespace holding one user's drafts: <c>{userId}/_Draft</c>.</summary>
    public static string NamespaceFor(string userId) => $"{userId}/{DraftSegment}";

    /// <summary>
    /// The draft id for a document — its path with separators folded, the same encoding
    /// <c>_UserActivity</c> uses to key a per-user satellite by node path. Deterministic, so
    /// reopening the dialog for the same document reuses ONE node rather than accumulating.
    /// </summary>
    public static string DraftIdFor(string documentPath) => documentPath.Replace("/", "_");

    /// <summary>The full draft path for (<paramref name="userId"/>, <paramref name="documentPath"/>).</summary>
    public static string PathFor(string userId, string documentPath) =>
        $"{NamespaceFor(userId)}/{DraftIdFor(documentPath)}";

    /// <summary>
    /// Create-on-absent (idempotent, reactive) of this user's draft for this document, emitting the
    /// draft node path once it is bindable.
    ///
    /// <para>Existence is read through <c>GetQuery</c> (empty-on-absent) rather than a point
    /// <c>GetMeshNodeStream</c> probe of a maybe-absent path — probing an absent path is what
    /// NotFound-storms a hub.</para>
    ///
    /// <para>An existing draft is returned untouched when it is still fresh, and RESET to a clean
    /// form when it is older than <see cref="MaxAge"/>, so a stale recipient can never resurface
    /// weeks later.</para>
    /// </summary>
    /// <param name="hub">The hub whose service provider resolves the mesh service and workspace.</param>
    /// <param name="userId">The author — the partition the draft is filed under.</param>
    /// <param name="documentPath">The document the mail is about.</param>
    /// <param name="defaults">The clean form to seed (and to reset a stale draft to).</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A cold observable emitting the draft node path. Subscribe to run it.</returns>
    public static IObservable<string> EnsureExists(
        IMessageHub hub,
        string userId,
        string documentPath,
        EmailDraft defaults,
        ILogger? logger = null)
    {
        var path = PathFor(userId, documentPath);
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null || string.IsNullOrEmpty(userId))
            return Observable.Return(path);

        MeshNode BuildNode() => new(DraftIdFor(documentPath), NamespaceFor(userId))
        {
            NodeType = NodeType,
            Name = $"Email draft: {documentPath}",
            State = MeshNodeState.Active,
            Content = defaults with { DocumentPath = documentPath },
        };

        return hub.GetWorkspace()
            .GetQuery($"{NodeType}|{path}",
                $"path:{path} nodeType:{NodeType} select:path,id,namespace,name,nodeType,content,lastModified")
            .Take(1)
            .SelectMany(nodes =>
            {
                var existing = nodes.FirstOrDefault(n =>
                    string.Equals(n.NodeType, NodeType, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    logger?.LogInformation("Seeding email draft for {User} on {Document}", userId, documentPath);
                    return meshService.CreateNode(BuildNode())
                        .Select(_ => path)
                        // Idempotent: a concurrent first-writer won the create race.
                        .Catch<string, Exception>(ex => IsAlreadyExists(ex)
                            ? Observable.Return(path)
                            : Observable.Throw<string>(ex));
                }

                if (existing.LastModified != default
                    && DateTimeOffset.UtcNow - existing.LastModified <= MaxAge)
                    return Observable.Return(path);

                logger?.LogInformation(
                    "Resetting stale email draft for {User} on {Document} (last touched {LastModified})",
                    userId, documentPath, existing.LastModified);
                return hub.GetWorkspace().GetMeshNodeStream(path)
                    .Update(node => node with { Content = defaults with { DocumentPath = documentPath } })
                    .Select(_ => path);
            });
    }

    /// <summary>
    /// Drops the draft — after a successful send, or when the author explicitly discards it. A
    /// missing node is success: discarding twice, or discarding a draft the send already removed,
    /// is not an error.
    /// </summary>
    /// <param name="hub">The hub whose service provider resolves the mesh service.</param>
    /// <param name="draftPath">The draft node path.</param>
    /// <returns>A cold observable completing when the draft is gone. Subscribe to run it.</returns>
    public static IObservable<Unit> Discard(IMessageHub hub, string draftPath)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null || string.IsNullOrEmpty(draftPath))
            return Observable.Return(Unit.Default);

        return meshService.DeleteNode(draftPath)
            .Select(_ => Unit.Default)
            .Catch<Unit, Exception>(ex => IsNotFound(ex)
                ? Observable.Return(Unit.Default)
                : Observable.Throw<Unit>(ex));
    }

    private static bool IsAlreadyExists(Exception ex) =>
        ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    private static bool IsNotFound(Exception ex) =>
        ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
}
