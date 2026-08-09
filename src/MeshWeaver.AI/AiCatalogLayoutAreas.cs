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
    /// <summary>Area name for the scope-tabbed model-Tiers catalog. Menu href: <c>/Provider/AiModelTiers</c>.</summary>
    public const string TiersArea = "AiModelTiers";

    /// <summary>Registers the AI catalog areas on a layout definition.</summary>
    public static LayoutDefinition AddAiCatalogLayoutAreas(this LayoutDefinition layout)
        => layout
            .WithView(AgentsArea, AgentsCatalog)
            .WithView(SkillsArea, SkillsCatalog)
            .WithView(ProvidersArea, ProvidersCatalog)
            .WithView(ModelsArea, ModelsCatalog)
            .WithView(TiersArea, TiersCatalog);

    /// <summary>Registers the AI catalog areas on a hub configuration.</summary>
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

    private static UiControl TiersCatalog(LayoutAreaHost host, RenderingContext _)
        // Tiers are a PLATFORM registry, not a per-space one — deliberately the ONE catalog here
        // without scope tabs. A model node in any partition points at a tier by ID, so a space-local
        // tier set would make the same label mean different things in different spaces. One flat list
        // rooted at Provider/Tier, with the same "+" create button as every other catalog, so an
        // operator can add or edit a rung without leaving the page.
        => Controls.Tabs.WithSkin(s => s.WithWidth("100%"))
            .WithMeshSearch(host.Localize(TabGlobal),
                @namespace: ModelTierNodeType.RootNamespace, scope: "descendants",
                nodeType: ModelTierNodeType.NodeType,
                createNodeType: ModelTierNodeType.NodeType,
                createNamespace: ModelTierNodeType.RootNamespace,
                placeholder: host.Localize("aiCatalog.search.tiers.global"), configure: ScopeSearch);

    // 🌍 Tab labels and search placeholders are USER-VISIBLE, so they are catalog keys, never
    // literals — the portal ships English + German and a hard-coded string renders English for
    // every viewer. Resolution goes through host.Localize (→ AccessContext.Locale), never an
    // ambient CultureInfo, because a layout-area render hops the hub scheduler.
    private const string TabThisSpace = "aiCatalog.tab.thisSpace";
    private const string TabUser = "aiCatalog.tab.user";
    private const string TabGlobal = "aiCatalog.tab.global";

    /// <summary>
    /// The placeholder key for one catalog + scope, e.g. <c>aiCatalog.search.models.user</c>.
    ///
    /// <para>One key per COMBINATION rather than one template with the noun interpolated. English
    /// tolerates "Search your {noun}…"; German does not — the article and case inflect with the
    /// noun's gender ("Suche deine Modelle" vs "Suche deinen Provider"), so a shared template would
    /// be ungrammatical for at least one catalog no matter which wording won.</para>
    /// </summary>
    /// <param name="catalog">Catalog key: agents / skills / providers / models / tiers.</param>
    /// <param name="scope">Scope key: space / user / global.</param>
    /// <returns>The localization key.</returns>
    private static string SearchKey(string catalog, string scope) => $"aiCatalog.search.{catalog}.{scope}";

    /// <summary>
    /// Builds a <see cref="Controls.Tabs"/> catalog with the This-space / User / Global scope tabs
    /// for <paramref name="nodeType"/>. Each tab is a namespace-scoped mesh search whose
    /// <c>CreateNodeType</c> shows the "+" button; new nodes are created in that tab's namespace.
    /// </summary>
    /// <param name="host">The layout-area host — also the localization scope (AccessContext.Locale).</param>
    /// <param name="catalog">Catalog key for the placeholder lookup: agents / skills / providers / models.</param>
    /// <param name="nodeType">The node type each tab searches for.</param>
    /// <param name="globalNamespace">Namespace root of the platform-wide "Global" tab.</param>
    /// <returns>The composed tabs control.</returns>
    private static UiControl BuildScopeCatalog(
        LayoutAreaHost host, string catalog, string nodeType, string globalNamespace)
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
            tabs = tabs.WithMeshSearch(host.Localize(TabThisSpace),
                @namespace: contextNs, scope: "descendants", nodeType: nodeType,
                createNodeType: nodeType, createNamespace: contextNs,
                placeholder: host.Localize(SearchKey(catalog, "space")), configure: ScopeSearch);

        // "User" — the viewer's own partition.
        if (!string.IsNullOrEmpty(viewerHome))
            tabs = tabs.WithMeshSearch(host.Localize(TabUser),
                @namespace: viewerHome, scope: "descendants", nodeType: nodeType,
                createNodeType: nodeType, createNamespace: viewerHome,
                placeholder: host.Localize(SearchKey(catalog, "user")), configure: ScopeSearch);

        // "Global" — the platform-wide type root.
        tabs = tabs.WithMeshSearch(host.Localize(TabGlobal),
            @namespace: globalNamespace, scope: "descendants", nodeType: nodeType,
            createNodeType: nodeType, createNamespace: globalNamespace,
            placeholder: host.Localize(SearchKey(catalog, "global")), configure: ScopeSearch);

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
