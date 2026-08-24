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
            // Seeding the platform defaults ships with the framework because a user with no app
            // records has no way to reach the Store — that is not deployment-specific, even though
            // WHICH apps get seeded is (it comes from Admin/HomeConfig).
            .AddSingleton<ILogonAction, SeedDefaultAppsLogonAction>()
            // Disjoint from the adoption below: that one fills a record with NO icon, this one
            // moves a record OFF an icon core shipped and has since replaced.
            .AddSingleton<ILogonAction, DefaultAppIconRefreshLogonAction>()
            .AddSingleton<ILogonAction, AppIconAdoptionLogonAction>());
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
