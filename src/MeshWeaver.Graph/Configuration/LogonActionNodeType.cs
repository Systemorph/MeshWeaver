using System.Collections.Generic;
using MeshWeaver.Graph.Logon;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The <b>LogonAction</b> node type — a platform action an admin declares IN-PLATFORM to run for
/// each user at logon. Nodes live at <c>Admin/_LogonAction/{id}</c> in the Admin partition, where
/// platform admins have standing write and ordinary users do not.
///
/// <para>🚨 <b>Zero action nodes ship.</b> The framework is core; the actions are deployment data.
/// That is the whole point: memex.meshweaver.cloud carries the agentic-engineering courses and
/// systemorph.com does not, so a shipped default naming those courses would pin a dangling path on
/// every portal that lacks them. A portal that declares nothing runs nothing, and adding an action
/// is an <c>mcp create</c>, not an image roll.</para>
///
/// <para>The <c>_LogonAction</c> segment is a plain hidden namespace, exactly like
/// <see cref="AppNodeType.UserNamespace"/>: NOT a satellite type (no <c>IsSatelliteType</c>, no
/// <c>SatelliteTableMapping</c> entry — an unmapped <c>_</c> segment routes to <c>mesh_nodes</c>),
/// and the leading underscore already keeps it out of the search context.</para>
///
/// <para>🚨 The discriminator is <c>LogonAction</c> and it therefore CLAIMS the top-level path
/// <c>LogonAction</c> (a built-in node type's definition node does — see
/// <see cref="AppNodeType.NodeType"/>'s note on why "App" was not usable). That name is not one real
/// content uses, so the claim is collision-improbable; the segment stays <c>_LogonAction</c>.</para>
///
/// <para>Full treatment: <c>Doc/Architecture/LogonActions</c>.</para>
/// </summary>
public static class LogonActionNodeType
{
    /// <summary>The NodeType discriminator.</summary>
    public const string NodeType = "LogonAction";

    /// <summary>The Admin partition that holds the declarations.</summary>
    public const string AdminPartition = HomeConfigNodeType.AdminPartition;

    /// <summary>The hidden namespace segment holding the declarations.</summary>
    public const string NamespaceSegment = "_LogonAction";

    /// <summary>The namespace the declarations live in: <c>Admin/_LogonAction</c>.</summary>
    public const string ActionNamespace = AdminPartition + "/" + NamespaceSegment;

    /// <summary>The path of one declaration: <c>Admin/_LogonAction/{id}</c>.</summary>
    public static string PathFor(string actionId) => $"{ActionNamespace}/{actionId}";

    /// <summary>
    /// Registers the logon-action node type, its typed content, the runner, and the platform's
    /// code-declared actions.
    /// </summary>
    public static TBuilder AddLogonActionType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<LogonAction>(nameof(LogonAction)));
        builder.ConfigureServices(services => services
            // Mesh-scoped singleton: its lifetime IS the mesh's, so nothing survives disposal and
            // nothing bleeds between tests (Doc/Architecture/NoStaticState).
            .AddSingleton<LogonActionRunner>()
            .AddSingleton<SignInNotificationTargets>()
            .AddSingleton<ILogonAction, AnnounceSignInLogonAction>()
            .AddSingleton<ILogonAction, AppIconAdoptionLogonAction>());
        return builder;
    }

    /// <summary>
    /// Asks to be told when a user signs in: <paramref name="address"/> receives a
    /// <see cref="UserSignedIn"/> event, fire-and-forget, once per sign-in.
    ///
    /// <para>Core owns "a user signed in" and nothing else — it does not know which partitions exist
    /// or what they would do about it. A deployment that carries no subscriber registers none, so
    /// there is nothing to probe and no NotFound to tolerate.</para>
    ///
    /// <para>🚨 <b>This belongs in DEPLOYMENT configuration</b> (where the mesh is built — e.g.
    /// <c>MemexConfiguration</c>), NOT in a module's own <c>HubConfiguration</c>, and the reason is
    /// process topology rather than API surface. This registry is a mesh-scoped singleton, so it is
    /// per-PROCESS; the announcement is posted by whichever process handles the sign-in. A module's
    /// per-type hub configuration runs only when that node's hub ACTIVATES — on one silo, at some
    /// point, possibly never — so a module registering itself there would populate the registry in a
    /// process that may not be the one announcing, and the event would silently go nowhere. It would
    /// look self-contained and work on a monolith, which is the worst combination.</para>
    ///
    /// <para>The one line per subscriber in deployment configuration is therefore not a compromise
    /// on module self-containment; it is the only placement that runs in every process. If a module
    /// genuinely must declare its own subscription, that wants to be DATA (a node the announcer
    /// reads) rather than a call — same reason logon ACTIONS are data.</para>
    ///
    /// <para>The handler runs on the subscriber's own hub under the delivery's identity, so it acts
    /// as the signing-in user. Nothing is expected back — an event with a response would put the
    /// subscriber's availability on the user's sign-in path.</para>
    /// </summary>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="address">The hub address to notify.</param>
    public static TBuilder AddSignInNotificationTarget<TBuilder>(this TBuilder builder, Address address)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<SignInNotificationTargets>();
            return services;
        });
        builder.ConfigureHub(config => config.WithInitialization(hub =>
        {
            hub.ServiceProvider.GetService<SignInNotificationTargets>()?.Add(address);
            return System.Reactive.Linq.Observable.Return(System.Reactive.Unit.Default);
        }));
        return builder;
    }

    /// <summary>
    /// Registers an additional code-declared logon action. For anything DEPLOYMENT-SPECIFIC declare
    /// a <see cref="LogonAction"/> node instead — a code action ships to every portal by
    /// construction, which is exactly wrong for a migration naming content only one portal has.
    /// </summary>
    public static TBuilder AddLogonAction<TBuilder, TAction>(this TBuilder builder)
        where TBuilder : MeshBuilder
        where TAction : class, ILogonAction
    {
        builder.ConfigureServices(services => services.AddSingleton<ILogonAction, TAction>());
        return builder;
    }

    /// <summary>MeshNode definition for <c>nodeType:LogonAction</c> — typed content plus the
    /// standard node-bound content editor, so an admin edits the declaration in the portal.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Logon Action",
        Icon = "/static/NodeTypeIcons/play.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .AddMeshDataSource(source => source.WithContentType<LogonAction>())
    };
}
