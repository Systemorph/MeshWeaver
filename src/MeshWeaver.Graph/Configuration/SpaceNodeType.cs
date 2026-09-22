using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Messaging;
using MeshWeaver.ContentCollections;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Represents a tenant container — a company, team, or organizational unit that owns
/// its own partition. Each Space gets a dedicated Postgres schema (provisioned eagerly
/// on create) and a root <c>AccessAssignment</c> granting the creator Admin.
/// </summary>
public record Space
{
    /// <summary>Short description of the space.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Long-form markdown body shown on the Space's Overview. Null uses the default
    /// welcome message; an empty string is an explicitly cleared page. Fill it to author
    /// the page yourself (mission statement, team intros, curated links, etc.).
    /// </summary>
    public string? Body { get; init; }

    /// <summary>URL of the space's website.</summary>
    public string? Website { get; init; }

    /// <summary>Content reference to the space's logo image.</summary>
    [ContentItem]
    public string? Logo { get; init; }

    /// <summary>Icon used to represent the space; defaults to the space icon. A RENDERABLE value — an
    /// image URL (e.g. <c>/static/NodeTypeIcons/space.svg</c>), an inline <c>&lt;svg&gt;</c>, or an emoji
    /// — NEVER a Fluent icon name (a bare name like "Building" can't render as an image and shows as
    /// text or a broken image).</summary>
    [ContentItem]
    [MeshNodeProperty(nameof(MeshNode.Icon))]
    public string Icon { get; init; } = "/static/NodeTypeIcons/space.svg";

    /// <summary>Geographic location of the space.</summary>
    public string? Location { get; init; }

    /// <summary>Contact email address for the space.</summary>
    public string? Email { get; init; }

    /// <summary>Whether the space has been verified.</summary>
    public bool IsVerified { get; init; }

    /// <summary>
    /// Timestamp when the space was created, when the writer recorded one — <c>null</c> otherwise.
    ///
    /// <para>🚨 <b>NULLABLE, AND WITHOUT A CLOCK DEFAULT, BECAUSE A DEFAULT HERE IS NOT
    /// IDEMPOTENT (#4382).</b> This used to read
    /// <c>public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;</c>. A Space
    /// DECLARED in a content repo omits <c>createdAt</c>, so every deserialization stamped a fresh
    /// clock reading; the incoming content then differed from the stored content on every install
    /// and the unchanged-skip could never hold. MeshWeaver.Plugins#1901's idempotence gate caught
    /// it — <c>re-install of the unchanged snapshot wrote 1 node(s) (expected 0)</c> — and core's
    /// own <c>samples/Graph/Data/{Systemorph,ACME,MeshWeaver}.json</c> had been re-written on every
    /// static-repo import for as long as the default existed, silently, because a re-write that
    /// produces the correct node goes green.</para>
    ///
    /// <para>🚨 <b>Stamping it at creation instead would not have fixed it</b>, and that is why
    /// there is no "set it once on create" here: a declaring file that omits the field would then
    /// present <c>null</c> against a STORED stamp, differ again, and re-write again. Provenance
    /// that a declaration does not carry cannot live on the declared content at all — the node's
    /// own version history already records when it first materialised, and that is the reading to
    /// use. Nothing in core or MeshWeaver.Plugins reads this property (measured 2026-09-15: its
    /// declaration is the only occurrence in either repo), so it is kept, nullable, for the rows
    /// that already carry a value rather than removed from a public surface.</para>
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>
/// Provides configuration for Space nodes in the graph. A Space is a tenant
/// container — the root of a per-tenant partition. Creating one is a fool-proof
/// server-side invariant (driven entirely from <c>OwnsPartitionProvisioningValidator</c>
/// + <see cref="SpacePostCreationHandler"/>, so it holds for MCP <c>create</c> and
/// every other caller):
///
/// <list type="number">
///   <item><b>Top-level only.</b> A Space's path is just its id (empty namespace).
///     <c>OwnsPartitionProvisioningValidator</c> (generic, reads
///     <c>NodeTypeDefinition.OwnsPartition</c>) rejects any create with a non-empty
///     namespace.</item>
///   <item><b>Eagerly provisioned partition.</b> The validator runs BEFORE the root
///     write and, under <c>AccessService.ImpersonateAsSystem</c>, calls every
///     <see cref="IPartitionStorageProvider"/>'s
///     <c>EnsurePartitionProvisionedAsync</c> — which routes to the
///     <c>public.ensure_partition_schema</c> Postgres stored procedure
///     (<c>PostgreSqlPartitionStorageProvider.EnsureSchemaAsync</c>). The per-Space
///     schema (<c>{id}.mesh_nodes</c> + every satellite table) therefore exists before
///     the Space root write or any child touch — no more <c>42P01 relation does not
///     exist</c>. Idempotent.</item>
///   <item><b>Routing primed.</b> <see cref="SpacePostCreationHandler.GetAdditionalNodes"/>
///     emits an <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/>. Persisting
///     it primes <c>PgPartitionCache</c> (positive) across the mesh and drives the
///     <c>partition_changes</c> pg_notify provisioning so every silo/mirror agrees the
///     partition exists.</item>
///   <item><b>Creator gets Admin.</b> The handler persists an <c>AccessAssignment</c> at
///     <c>{id}/_Access</c> as System, through the sealed boundary
///     <c>AccessService.RunAsSystem</c>; the write is <b>awaited</b>, so a failed grant
///     faults the create response rather than being silently dropped.</item>
/// </list>
///
/// Access rules: Read/Create/Update/Delete controlled by partition-level
/// permissions via <c>SecurityService</c>. Space instances live in
/// their own partition and mirror to <c>auth.mesh_nodes</c> via the V27 mirror
/// trigger (extended to include <c>Space</c> in V28) so a single-schema query
/// over <c>auth</c> can list every Space in the mesh.
/// </summary>
public static class SpaceNodeType
{
    /// <summary>The node-type identifier for Space nodes.</summary>
    public const string NodeType = "Space";

    /// <summary>
    /// Marker registered once per builder so <see cref="AddSpaceType{TBuilder}"/> is
    /// idempotent: the test base registers Space by default AND individual tests call
    /// <c>AddSpaceType()</c>, so the second call must be a no-op. Registering the
    /// access rule / post-creation handler / validator twice would run the
    /// creator-admin grant twice and stack two access rules per Space.
    /// </summary>
    private sealed class SpaceTypeMarker;

    /// <summary>
    /// Default welcome body rendered for a Space when the node has no
    /// PreRenderedHtml of its own. Plain markdown — no pseudo-HTML.
    /// Per-Space overrides live in each Space's own <c>index.md</c>
    /// (set on <see cref="MeshNode.PreRenderedHtml"/>).
    /// <para>The mesh catalog is the node's <c>Search</c> area (namespace tree by default;
    /// <c>?groupBy=type|category|flat</c> and <c>?subtree=true</c> tune it — see the
    /// "Mesh Search &amp; Catalogs" doc). It is embedded INLINE in this template via the
    /// <c>@@("area/Search")</c> operator (a "Contents" section), NOT a hardcoded layout section —
    /// so an author owns it in the editable Body and can move, tune (<c>@@("area/Search?groupBy=…")</c>),
    /// or remove it like any other content.</para>
    /// </summary>
    public const string WelcomeMarkdown = """
        # Welcome

        This is your space's home page.

        Start by structuring the content you want to share here — a short introduction,
        a mission statement, links to the teams and projects that matter to you.

        ## Tips to get started

        - **Create some content.** Use the menu above to add pages, demos, or documents.
          You can always come back and ask the assistant to summarize what's inside.
        - **Bring in existing files.** Drop markdown, images, or documents into the
          content collection; they show up automatically.
        - **Chat with your space.** Use the chat input below to ask questions,
          kick off an agent, or draft content together.

        ## Replace this page

        This welcome text is a placeholder shown until the Space has its own overview.
        To make it your own, edit this Space's **Body**: open the Space's menu
        (top-right **⋯**) → **Edit**, then write your overview in the **Body** field
        (plain markdown — headings, links, tables, and `@@`-embeds all work). Or simply
        ask the assistant in the chat below to draft it — it writes to the same Body field.
        See [Configurable Home & Space Pages](/Doc/GUI/ConfigurablePages) for the full guide.

        ## Contents

        @@("area/Search")
        """;

    /// <summary>
    /// Registers the Space node type on the mesh builder: the type node, content type,
    /// access rule, post-creation handler (creator-Admin grant), and the last-admin
    /// invariant validator. Idempotent — a second call is a no-op.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to register the node type on.</param>
    /// <returns>The same builder, to allow fluent chaining.</returns>
    public static TBuilder AddSpaceType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        // Idempotent guard: the marker check runs against the LIVE IServiceCollection
        // inside ConfigureServices, so it is correct regardless of whether the host
        // invokes the service-config lambda synchronously (MeshHostApplicationBuilder,
        // test base) or deferred during host build (MeshHostBuilder). The very first
        // call registers the marker + node/type + the three singletons; every later
        // call short-circuits. The builder mutations (AddMeshNodes / WithMeshType) are
        // co-located inside the guarded lambda so they, too, run exactly once.
        builder.ConfigureServices(services =>
        {
            if (services.Any(d => d.ServiceType == typeof(SpaceTypeMarker)))
                return services;
            services.AddSingleton<SpaceTypeMarker>();

            builder.AddMeshNodes(CreateMeshNode());
            builder.WithMeshType<Space>();

            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SpaceAccessRule(sp.GetRequiredService<IMessageHub>()));
            // The top-level invariant + eager schema provisioning is handled generically
            // by OwnsPartitionProvisioningValidator (it reads NodeTypeDefinition.OwnsPartition,
            // which CreateMeshNode sets true) — no Space-specific validator needed.
            services.AddSingleton<INodePostCreationHandler>(sp =>
                new SpacePostCreationHandler(
                    sp.GetRequiredService<IMeshService>(),
                    sp.GetRequiredService<AccessService>(),
                    sp.GetService<ILoggerFactory>()?.CreateLogger<SpacePostCreationHandler>()));
            // A Space must always retain at least one admin: block deleting / denying
            // the last non-denied Admin AccessAssignment on any partition's _Access.
            services.AddSingleton<INodeValidator>(sp =>
                new SpaceAdminInvariantValidator(
                    sp.GetRequiredService<IMessageHub>(),
                    sp.GetService<ILoggerFactory>()?.CreateLogger<SpaceAdminInvariantValidator>()));
            // 🚨 NO per-type partition teardown here any more (#3436). Deleting a Space still
            // drops the whole partition — PartitionDropPostDeletionHandler now matches every
            // partition ROOT structurally and is registered ONCE by AddGraph. Registering it per
            // NodeType was the defect: the two hand-written registrations (Space here, User in
            // AddUserType) stood for "every partition-owning type", and four Store/Plugin-rooted
            // partitions kept their Postgres schemas when they were deleted.
            return services;
        });
        // Space instances are NOT publicly readable — partition access controls visibility.
        // The type definition itself remains visible; instances are filtered by user permissions.
        return builder;
    }

    /// <summary>
    /// Builds the MeshNode definition for the Space node type, including its content type,
    /// content collections, layout areas, and partition-owning routing configuration.
    /// </summary>
    /// <returns>The Space node-type definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Space",
        NodeType = "NodeType",
        Icon = "/static/NodeTypeIcons/space.svg",
        Content = new NodeTypeDefinition { DefaultNamespace = "", RestrictedToNamespaces = [""], OwnsPartition = true },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Space>())
            .AddContentCollections()
            .AddDefaultLayoutAreas()
            // The Space Overview and Edit VIEWS ride the MeshWeaver.Graph.Views module — including
            // the rule that editing a Space edits its markdown body rather than a property form.
            .ApplyNodeHubContributions(NodeType)
    };

    /// <summary>
    /// Post-creation handler that makes a Space create a fool-proof partition root.
    /// Triggered implicitly by <c>RunPostCreationHandlersObs</c> when a Space is
    /// created via a normal <c>CreateNodeRequest</c> (MCP <c>create</c> and every
    /// other caller funnel through there).
    ///
    /// <para>The per-Space schema is already provisioned eagerly by
    /// <c>OwnsPartitionProvisioningValidator</c> before the root write; this handler
    /// (1) emits the <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/>
    /// that primes <c>PgPartitionCache</c> + the <c>partition_changes</c> notify pump,
    /// and (2) grants the creator Admin.</para>
    ///
    /// <para><b>The creator-admin grant</b> runs as System (the new user / brand-new
    /// partition root means the caller can't already hold Create on it — the canonical
    /// infrastructure-write case) and is <b>awaited</b>, not fire-and-forget: a
    /// failed grant faults <see cref="Handle"/>, which
    /// <c>RunPostCreationHandlersObs</c> surfaces as a failed create response
    /// instead of silently dropping it.</para>
    ///
    /// <para>🚨 It runs through <c>ImpersonationScopeExtensions.RunAsSystem</c>, never a raw
    /// <c>Observable.Using(accessService.ImpersonateAsSystem, …)</c>. The raw shape opens the
    /// AsyncLocal scope on the SUBSCRIBING thread and disposes it when the cross-hub create
    /// terminates on another one, so it LATCHES <c>system-security</c> onto the create flow that
    /// invoked this handler — which is where #4061's "intermittent" Admin/Partition denial came
    /// from. See the body of <see cref="Handle"/>, and
    /// <c>SpaceGrantScopeDoesNotLatchTheCreateFlowTest</c>, which pins it.</para>
    /// </summary>
    private class SpacePostCreationHandler(
        IMeshService meshService,
        AccessService accessService,
        ILogger<SpacePostCreationHandler>? logger) : INodePostCreationHandler
    {
        public string NodeType => SpaceNodeType.NodeType;

        // The creator-Admin grant is part of the Space create's contract, NOT a best-effort
        // side effect: a Space that persists without granting its creator ownership is an
        // un-navigable, ownerless partition (the "created a space but I have no access" bug).
        // So a failed/absent grant must FAULT the create (CreateNodeResponse.Fail) rather than
        // be swallowed into a silent Ok — see RunPostCreationHandlersObs + FailsCreateOnError.
        public bool FailsCreateOnError => true;

        public IObservable<System.Reactive.Unit> Handle(MeshNode createdNode, string? createdBy)
        {
            // System needs no grant (Permission.All). When a Space root is materialized under
            // System impersonation — the static-repo import, onboarding, or the central partition
            // bootstrap (MeshExtensions.EnsurePartitionRoot) — there is no per-creator grant to
            // write: catalog read access comes from the partition's publicRead _Policy, not a
            // spurious system-security AccessAssignment under {id}/_Access. (Checked BEFORE the
            // empty-createdBy guard: System is a legitimate no-grant creator, not a missing owner.)
            if (string.Equals(createdBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogDebug("Skipping creator-Admin grant for system-created Space at {Path}", createdNode.Path);
                return Observable.Empty<System.Reactive.Unit>();
            }

            // No (non-system) creator identity → we CANNOT make anyone the owner. A Space MUST
            // have an owner, so this is a hard failure of the create, not a warn-and-continue:
            // faulting here (FailsCreateOnError) stops us shipping an ownerless Space and surfaces
            // the real upstream defect (a create that arrived with no AccessContext identity).
            if (string.IsNullOrEmpty(createdBy))
            {
                logger?.LogError("Cannot assign Admin role: no creator identity for Space at {Path}", createdNode.Path);
                return Observable.Throw<System.Reactive.Unit>(new InvalidOperationException(
                    $"Cannot create Space '{createdNode.Path}' without a creator identity to grant ownership to. "
                    + "The create request carried no AccessContext.ObjectId (and was not System-impersonated)."));
            }

            logger?.LogInformation("Granting Admin role to {User} on Space {Path}", createdBy, createdNode.Path);
            // The ONE definition of the creator grant, shared with the in-mesh owning types'
            // handler (PartitionOwnership) so the two cannot drift.
            var assignmentNode = PartitionOwnership.CreatorAdminGrant(createdNode, createdBy);
            // Grant under System impersonation so the write authorises (creator can't already
            // hold Create on a brand-new partition root). Return the observable directly — the
            // caller subscribes; a failure propagates through OnError so RunPostCreationHandlers
            // reports it. Pure reactive, no Task, no ToTask bridge.
            //
            // 🚨 RunAsSystem, NEVER `Observable.Using(() => accessService.ImpersonateAsSystem(), …)`
            // (#4061 — this site is the mechanism behind that issue's "intermittent" denial, and it
            // was the last entry MeshWeaver.Graph held in test/ImpersonationScopeSites.allow).
            // Impersonation is an AsyncLocal store/restore pair. Rx runs the resource factory on the
            // SUBSCRIBING thread and disposes the resource when the inner observable TERMINATES —
            // for this CROSS-HUB create, the owning hub's response thread. AccessContextScope's
            // restore is thread-affine, so it writes nothing over there, and NOTHING ever closes the
            // scope on the subscriber: the flow that invoked this handler keeps `system-security`
            // for everything it does next.
            //
            // That subscriber is MeshExtensions.RunPostCreationHandlersObs, which goes on to persist
            // and ANNOUNCE Admin/Partition/{id}. #4197 measured BOTH identities on that announcement
            // in ONE run, 41 ms apart — `user=system-security` while the latch was in effect and
            // `user=Roland`, denied on Admin/Partition/{id}, when it was not — and fixed the
            // announcement by declaring its identity as a VALUE. That closed the symptom at one call
            // site; this closes the SOURCE. An accidental Permission.All is the more serious half:
            // its failure mode is a write silently succeeding where the user would have been refused
            // (#1444), and a stage that "works" only while a sibling scope has not been torn down is
            // not working.
            //
            // The seal does not weaken the write: RunAsSystem enters the scope at Subscribe, so the
            // cold create still eager-captures System, and leaves it on the way out of that same
            // Subscribe. The .Do is inside the work factory so emission-time behaviour is unchanged
            // (ImpersonationScopeExtensions: "compose the WIDEST cold pipeline inside work").
            return accessService
                .RunAsSystem(() => meshService.CreateNode(assignmentNode)
                    .Do(_ => logger?.LogInformation(
                        "Granted Admin to {User} on Space {Path} at {GrantPath}",
                        createdBy, createdNode.Path, assignmentNode.Path)))
                .Select(_ => System.Reactive.Unit.Default);
        }

        /// <summary>
        /// Emits the per-Space <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/>.
        /// Persisting this primes the partition cache + notify-driven schema
        /// provisioning so the partition is consistently routable across the mesh.
        /// </summary>
        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode)
        {
            yield return PartitionOwnership.PartitionDefinitionNode(
                createdNode, $"Partition for space {createdNode.Name ?? createdNode.Id}");
        }
    }

    /// <summary>
    /// DI-registered access rule for Space nodes.
    /// Read: requires partition Read permission — or, for a platform admin, the Space being
    /// system-owned (see <see cref="ReadAccess"/>). Create: any authenticated identity for a
    /// top-level Space (the creator becomes its Admin), parent Create otherwise. Update: requires
    /// <see cref="Permission.Update"/>; Delete: requires <see cref="Permission.Delete"/>.
    /// </summary>
    private class SpaceAccessRule(IMessageHub hub) : INodeTypeAccessRule
    {
        public string NodeType => SpaceNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
        {
            if (string.IsNullOrEmpty(userId))
                return Observable.Return(false);

            if (context.Operation == NodeOperation.Read)
                return ReadAccess(context.Node.Path, userId);

            if (context.Operation == NodeOperation.Create)
            {
                // Anyone authenticated may create a top-level Space — they become its
                // Admin via SpacePostCreationHandler. A Space is ALWAYS top-level
                // (SpaceTopLevelValidator rejects non-empty namespaces), and a brand-new
                // top-level partition has no parent to hold Create on, so requiring
                // CheckPermission(parentPath, Create) here locked everyone except global
                // admins out of creating spaces (the logic lost in the Organization→Space
                // migration). Gate only on a present identity (userId != null, already
                // checked above). Nested creates still require parent Create — though the
                // validator rejects them first.
                if (string.IsNullOrEmpty(context.Node.Namespace))
                    return Observable.Return(true);
                var parentPath = context.Node.GetParentPath() ?? context.Node.Path;
                return hub.CheckPermission(parentPath, userId, Permission.Create);
            }

            if (context.Operation == NodeOperation.Update)
                return hub.CheckPermission(context.Node.Path, userId, Permission.Update);

            // 🚨 DELETE IS NOT UPDATE, and this rule is now the ONLY thing that says so.
            // Deleting a Space tears down a partition and drops its backing store
            // (SpacePostDeletionHandler), so it has always required Permission.Delete in practice —
            // but that demand lived in the delete handler's pre-flight, NOT here, and this rule said
            // Update. Nothing could reach the gap while the pre-flight ran its own
            // Permission.Delete check on every node; routing that pre-flight through this rule
            // (#2913) makes the rule authoritative, so the demand has to be written down where the
            // decision is taken. Same permission, same outcome for every caller — the second
            // opinion is simply gone.
            if (context.Operation == NodeOperation.Delete)
                return hub.CheckPermission(context.Node.Path, userId, Permission.Delete);

            return Observable.Return(false);
        }

        /// <summary>
        /// Read on the Space's ROOT node: the ordinary fold, OR — for a platform admin only — the
        /// Space being SYSTEM-OWNED.
        ///
        /// <para>A one-way <c>_GitSync</c> makes a Space system-owned: every write grant on it is
        /// refused (<c>AccessAssignmentGuard.IsForbiddenOnSystemOwned</c>) or retracted
        /// (<c>SystemOwnedAccessRetractionHandler</c>), so NO human can hold Read on it the ordinary
        /// way, and a platform admin — deliberately not a data superuser — holds nothing there
        /// either. The platform created such a Space (<c>MeshWeaver</c> on memex.meshweaver.cloud,
        /// 2026-09-12), the platform re-imports it on every green build, and the operator who has
        /// to stop that could not even see that the Space existed. This is the ownership case the
        /// Admin-partition model has no answer for: a partition owned by the platform itself, with
        /// nobody to ask for a grant.</para>
        ///
        /// <para><b>Scope of the widening.</b> The Space node only — its name, description and
        /// kind — never its content: children are their own node types and go through the fold,
        /// where the admin still holds nothing. Update and Delete are untouched; an admin can look
        /// at a system-owned Space and remove its sync config (<c>GitHubSyncConfigAccessRule</c>),
        /// and nothing more. A bijective sync is not system-owned (<c>AccessAssignmentGuard.IsSystemOwned</c>):
        /// there the mesh nodes are somebody's working copy, and their space stays theirs.</para>
        ///
        /// <para><b>Cost.</b> The probe — one storage read of <c>{space}/_GitSync</c> — runs only
        /// after the fold denied AND the caller is a platform admin; an ordinary viewer's Read is
        /// byte-for-byte the check it always was. Each leg is a live, never-completing fold, hence
        /// the <c>Take(1)</c>s; an empty leg completes empty and the caller's
        /// <c>NodeTypeAccessRuleGate.Evaluate</c> reports it Undetermined — fail closed.</para>
        /// </summary>
        private IObservable<bool> ReadAccess(string spacePath, string userId)
            => hub.CheckPermission(spacePath, userId, Permission.Read)
                .Take(1)
                .SelectMany(granted => granted
                    ? Observable.Return(true)
                    : hub.IsGlobalAdmin(userId)
                        .Take(1)
                        .SelectMany(isAdmin => isAdmin
                            ? NodeTypeAccessRuleGate
                                .ReadSubjectNode(hub, AccessAssignmentGuard.SyncConfigPath(spacePath))
                                .Select(sync => AccessAssignmentGuard.IsSystemOwned(
                                    sync, hub.JsonSerializerOptions))
                            : Observable.Return(false)));
    }
}
