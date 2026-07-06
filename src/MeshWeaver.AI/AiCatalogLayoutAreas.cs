using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// The top-bar AI menu's catalog areas — Agents / Skills / Providers / Models — each a
/// <b>scope-tabbed</b> catalog (This space · User · Global) with a "+" create button per tab.
///
/// <para>Replaces the old flat <c>/search?q=nodeType:X&amp;groupBy=Namespace</c> targets: those
/// grouped by namespace but offered no way to <b>create</b> a new entry, which is the gap this
/// closes. Each tab is a scoped <see cref="MeshSearchControl"/> (via
/// <see cref="CatalogExtensions.WithMeshSearch"/>) whose <c>CreateNodeType</c> renders the "+"
/// button — the same primitive the Threads catalog and the user home page already use.</para>
///
/// <para><b>Scope.</b> The catalog is anchored on the node it is opened from
/// (<c>host.Hub.Address</c>). The <c>This space</c> tab shows ONLY when that anchor is a real
/// partition that is neither the global type root nor the viewer's own home — so the global
/// top-bar entry (anchored on the type root, e.g. <c>/Agent/AiAgents</c>) shows just
/// <c>User</c> + <c>Global</c>, matching the ask "if started from the user, there will be no
/// space".</para>
/// </summary>
public static class AiCatalogLayoutAreas
{
    /// <summary>Area name for the scope-tabbed Agents catalog. Menu href: <c>/Agent/AiAgents</c>.</summary>
    public const string AgentsArea = "AiAgents";
    /// <summary>Area name for the scope-tabbed Skills catalog. Menu href: <c>/Skill/AiSkills</c>.</summary>
    public const string SkillsArea = "AiSkills";
    /// <summary>Area name for the scope-tabbed Providers catalog. Menu href: <c>/Provider/AiProviders</c>.</summary>
    public const string ProvidersArea = "AiProviders";
    /// <summary>Area name for the scope-tabbed Models catalog. Menu href: <c>/Provider/AiModels</c>.</summary>
    public const string ModelsArea = "AiModels";

    /// <summary>Registers the four AI catalog areas on a layout definition.</summary>
    public static LayoutDefinition AddAiCatalogLayoutAreas(this LayoutDefinition layout)
        => layout
            .WithView(AgentsArea, AgentsCatalog)
            .WithView(SkillsArea, SkillsCatalog)
            .WithView(ProvidersArea, ProvidersCatalog)
            .WithView(ModelsArea, ModelsCatalog);

    /// <summary>Registers the four AI catalog areas on a hub configuration.</summary>
    public static MessageHubConfiguration AddAiCatalogLayoutAreas(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.AddAiCatalogLayoutAreas());

    private static UiControl AgentsCatalog(LayoutAreaHost host, RenderingContext _)
        => BuildScopeCatalog(host, "agents", AgentNodeType.NodeType, globalNamespace: "Agent");

    private static UiControl SkillsCatalog(LayoutAreaHost host, RenderingContext _)
        => BuildScopeCatalog(host, "skills", SkillNodeType.NodeType, globalNamespace: SkillNodeType.RootNamespace);

    private static UiControl ProvidersCatalog(LayoutAreaHost host, RenderingContext _)
        => BuildScopeCatalog(host, "providers", ModelProviderNodeType.NodeType, globalNamespace: ModelProviderNodeType.RootNamespace);

    private static UiControl ModelsCatalog(LayoutAreaHost host, RenderingContext _)
        // Models live UNDER the "Provider" partition (LanguageModelNodeType remark), so the global
        // scope roots at ModelProviderNodeType.RootNamespace ("Provider"), not "Model".
        => BuildScopeCatalog(host, "models", LanguageModelNodeType.NodeType, globalNamespace: ModelProviderNodeType.RootNamespace);

    /// <summary>
    /// Builds a <see cref="Controls.Tabs"/> catalog with the This-space / User / Global scope tabs
    /// for <paramref name="nodeType"/>. Each tab is a namespace-scoped mesh search whose
    /// <c>CreateNodeType</c> shows the "+" button; new nodes are created in that tab's namespace.
    /// </summary>
    private static UiControl BuildScopeCatalog(
        LayoutAreaHost host, string plural, string nodeType, string globalNamespace)
    {
        var contextNs = host.Hub.Address.ToString();
        var viewerHome = ResolveViewerHome(host);

        var tabs = Controls.Tabs.WithSkin(s => s.WithWidth("100%"));

        // "This space" — only when anchored on a real partition that is neither the global type
        // root nor the viewer's own home (that IS the "User" tab). Absent from the global top-bar
        // entry and from a user's own area.
        var isSpace = !string.IsNullOrEmpty(contextNs)
            && !string.Equals(contextNs, globalNamespace, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contextNs, viewerHome, StringComparison.OrdinalIgnoreCase);
        if (isSpace)
            tabs = tabs.WithMeshSearch("This space",
                @namespace: contextNs, scope: "descendants", nodeType: nodeType,
                createNodeType: nodeType, createNamespace: contextNs,
                placeholder: $"Search {plural} in this space…", configure: ScopeSearch);

        // "User" — the viewer's own partition.
        if (!string.IsNullOrEmpty(viewerHome))
            tabs = tabs.WithMeshSearch("User",
                @namespace: viewerHome, scope: "descendants", nodeType: nodeType,
                createNodeType: nodeType, createNamespace: viewerHome,
                placeholder: $"Search your {plural}…", configure: ScopeSearch);

        // "Global" — the platform-wide type root.
        tabs = tabs.WithMeshSearch("Global",
            @namespace: globalNamespace, scope: "descendants", nodeType: nodeType,
            createNodeType: nodeType, createNamespace: globalNamespace,
            placeholder: $"Search global {plural}…", configure: ScopeSearch);

        return tabs;
    }

    // Common per-tab search skin — a reactive flat card grid with an inviting empty state.
    private static MeshSearchControl ScopeSearch(MeshSearchControl s) => s
        .WithRenderMode(MeshSearchRenderMode.Flat)
        .WithShowEmptyMessage(true)
        .WithReactiveMode(true)
        .WithMaxColumns(4);

    /// <summary>
    /// Resolves the current viewer's home partition (their <c>ObjectId</c>), skipping the
    /// system identity and hub principals — mirrors <c>ThreadComposerView.ResolveUser</c>.
    /// Returns <c>null</c> while the identity is still resolving (no "User" tab is shown then).
    /// </summary>
    private static string? ResolveViewerHome(LayoutAreaHost host)
    {
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        if (access is null)
            return null;
        foreach (var candidate in new[] { access.Context?.ObjectId, access.CircuitContext?.ObjectId })
            if (!string.IsNullOrEmpty(candidate)
                && candidate != WellKnownUsers.System
                && !AccessService.LooksLikeHubPrincipal(candidate))
                return candidate;
        return null;
    }
}
