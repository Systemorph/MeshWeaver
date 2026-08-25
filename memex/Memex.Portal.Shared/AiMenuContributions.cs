using System.Collections.Generic;
using MeshWeaver.AI;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace Memex.Portal.Shared;

/// <summary>
/// The AI (✨) menu's NAVIGATION entries as seeded <see cref="UiContribution"/> nodes (WS7
/// slice 3, design #1645): Threads / Models / Tiers / Providers / Agents / Skills each open a
/// catalog URL, so they are pure links — data, not behavior — and ride the same contribution lane
/// a plugin's AI-menu entry arrives through. The one entry that stays COMPILED is "New thread"
/// (<c>MemexConfiguration.AiMenuItems</c>): it is an imperative click-action sentinel whose
/// destination is resolved from the circuit's viewer identity at click time — behavior, which the
/// closed contribution vocabulary deliberately cannot express.
/// </summary>
public static class AiMenuContributions
{
    /// <summary>
    /// The seeds, exposed for the pinning tests (the same single-source-of-truth contract
    /// <c>MemexConfiguration.AiMenuItems</c> keeps for the imperative entry).
    /// </summary>
    internal static IReadOnlyList<MeshNode> Seeds { get; } =
    [
        Seed("AiThreads", "Threads", new UiContribution
        {
            Context = NodeMenuItemsExtensions.AiMenuContext,
            Area = "AiThreads",
            Label = "Threads",
            LabelKey = "menu.threads",
            Icon = "/static/NodeTypeIcons/chat.svg",
            Order = 10,
            Href = "/search?q=nodeType%3AThread&groupBy=Namespace",
            Tooltip = "Conversation threads across every namespace",
            TooltipKey = "menu.threadsTooltip",
        }),
        Seed("AiModels", "Models", new UiContribution
        {
            Context = NodeMenuItemsExtensions.AiMenuContext,
            Area = "AiModels",
            Label = "Models",
            LabelKey = "menu.models",
            Icon = "/static/NodeTypeIcons/sparkle.svg",
            Order = 20,
            Href = $"/{ModelProviderNodeType.RootNamespace}/{AiCatalogLayoutAreas.ModelsArea}",
            Tooltip = "Language models — global, space, and user",
            TooltipKey = "menu.modelsTooltip",
        }),
        // Tiers sit between Models and Providers on purpose: it is the third thing you configure
        // when you set a deployment up — which models exist, what each one is FOR, and whose key
        // pays.
        Seed("AiModelTiers", "Tiers", new UiContribution
        {
            Context = NodeMenuItemsExtensions.AiMenuContext,
            Area = "AiModelTiers",
            Label = "Tiers",
            LabelKey = "menu.modelTiers",
            Icon = "/static/NodeTypeIcons/task-list.svg",
            Order = 22,
            Href = $"/{ModelProviderNodeType.RootNamespace}/{AiCatalogLayoutAreas.TiersArea}",
            Tooltip = "Model tiers — what each model is used for",
            TooltipKey = "menu.modelTiersTooltip",
        }),
        Seed("AiProviders", "Providers", new UiContribution
        {
            Context = NodeMenuItemsExtensions.AiMenuContext,
            Area = "AiProviders",
            Label = "Providers",
            LabelKey = "menu.providers",
            Icon = "/static/NodeTypeIcons/key.svg",
            Order = 25,
            Href = $"/{ModelProviderNodeType.RootNamespace}/{AiCatalogLayoutAreas.ProvidersArea}",
            Tooltip = "AI providers — endpoints + keys",
            TooltipKey = "menu.providersTooltip",
        }),
        Seed("AiAgents", "Agents", new UiContribution
        {
            Context = NodeMenuItemsExtensions.AiMenuContext,
            Area = "AiAgents",
            Label = "Agents",
            LabelKey = "menu.agents",
            Icon = "/static/NodeTypeIcons/bot.svg",
            Order = 30,
            Href = $"/Agent/{AiCatalogLayoutAreas.AgentsArea}",
            Tooltip = "AI agents — global, space, and user",
            TooltipKey = "menu.agentsTooltip",
        }),
        Seed("AiSkills", "Skills", new UiContribution
        {
            Context = NodeMenuItemsExtensions.AiMenuContext,
            Area = "AiSkills",
            Label = "Skills",
            LabelKey = "menu.skills",
            Icon = "/static/NodeTypeIcons/rocket.svg",
            Order = 40,
            Href = $"/{SkillNodeType.RootNamespace}/{AiCatalogLayoutAreas.SkillsArea}",
            Tooltip = "Reusable skills — global, space, and user",
            TooltipKey = "menu.skillsTooltip",
        }),
    ];

    /// <summary>Seeds the AI menu's navigation entries as platform-static contribution nodes.</summary>
    public static MeshBuilder AddAiMenuContributions(this MeshBuilder builder)
        => builder.AddMeshNodes(Seeds);

    private static MeshNode Seed(string id, string name, UiContribution content)
        => new(id, "Admin/UiContribution")
        {
            NodeType = UiContributionNodeType.NodeType,
            Name = name,
            Content = content,
        };
}
