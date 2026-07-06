using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Layout views for Agent nodes.
/// - Catalog: List of all agents
/// - Details: Overview of the Agent
/// - Edit: Edit agent configuration
/// </summary>
public static class AgentView
{
    /// <summary>Area name for the agent catalog (list of all agents).</summary>
    public const string CatalogArea = "Catalog";

    /// <summary>Area name for an agent's details/overview view.</summary>
    public const string DetailsArea = "Details";

    /// <summary>Area name for the agent configuration editor.</summary>
    public const string EditArea = "Edit";

    /// <summary>
    /// Adds the Agent views to the hub's layout for Agent nodes.
    /// Catalog is the default view showing all agents.
    /// Includes UCR areas ($Data, $Schema, $Model) for unified content references.
    /// Note: $Content is registered by ContentCollectionsExtensions.AddContentCollections.
    /// </summary>
    public static MessageHubConfiguration AddAgentView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(DetailsArea)
            .WithView(CatalogArea, Catalog)
            .WithView(DetailsArea, Details)
            .WithView(EditArea, Edit)
            // UCR special areas for unified content references
            .WithView(MeshNodeLayoutAreas.DataArea, MeshNodeLayoutAreas.Data)
            .WithView(MeshNodeLayoutAreas.SchemaArea, MeshNodeLayoutAreas.Schema)
            .WithView(MeshNodeLayoutAreas.ModelArea, DataModelLayoutArea.DataModel));

    /// <summary>
    /// Renders the Catalog view showing all agents.
    /// </summary>
    public static UiControl Catalog(LayoutAreaHost host, RenderingContext ctx)
    {
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) =>
                {
                    var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
                    if (meshQuery == null)
                        return Observable.Return(RenderError("Query service not available."));

                    return meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:Agent"))
                        .Select(change => BuildCatalogContent(change.Items.ToList()))
                        .Catch<UiControl, Exception>(_ => Observable.Return(BuildCatalogContent([])));
                },
                "Content");
    }

    private static UiControl BuildCatalogContent(List<MeshNode> agents)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header
        stack = stack.WithView(Controls.Html("<h1 style=\"margin: 0 0 8px 0;\">Agents</h1>"));
        stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin: 0 0 24px 0;\">AI agents configured in the system</p>"));

        if (agents.Count == 0)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No agents found.</p>"));
            return stack;
        }

        // Results count
        stack = stack.WithView(Controls.Html($"<p style=\"color: #888; margin-bottom: 12px; font-size: 0.9em;\">Showing {agents.Count} agent{(agents.Count != 1 ? "s" : "")}</p>"));

        // Agent grid
        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));
        foreach (var node in agents)
        {
            grid = grid.WithView(
                MeshNodeThumbnailControl.FromNode(node, node.Path),
                itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(3));
        }
        stack = stack.WithView(grid);

        return stack;
    }

    /// <summary>
    /// Renders the Details area for an Agent.
    /// Shows an overview of the agent configuration. Node-level metadata (name,
    /// description, icon, group, order) is read from the owning MeshNode — the single
    /// source of truth — and only agent-specific behaviour from the AgentConfiguration.
    /// </summary>
    public static UiControl Details(LayoutAreaHost host, RenderingContext ctx)
    {
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => host.Workspace.GetMeshNodeStream()
                    .Select(node =>
                    {
                        if (node == null)
                            return RenderLoading("Loading agent...");
                        // A freshly-created Agent has no AgentConfiguration content yet — the create
                        // flow persists an Active node with null Content and edits it afterward. Show a
                        // default (empty) config instead of an endless "Loading…".
                        var agent = AsAgentConfiguration(node, host.Hub.JsonSerializerOptions)
                                    ?? new AgentConfiguration { Id = node.Id };
                        return BuildDetailsLayout(host, node, agent);
                    }),
                "Content"
            );
    }

    /// <summary>Resolves the typed <see cref="AgentConfiguration"/> from a node's Content,
    /// tolerating a <see cref="JsonElement"/> when the hub's registry isn't AI-typed.</summary>
    private static AgentConfiguration? AsAgentConfiguration(MeshNode? node, JsonSerializerOptions jsonOptions)
        => node?.Content switch
        {
            AgentConfiguration ac => ac,
            JsonElement je => TryDeserialiseConfig(je, jsonOptions),
            _ => null,
        };

    private static AgentConfiguration? TryDeserialiseConfig(JsonElement je, JsonSerializerOptions jsonOptions)
    {
        try { return JsonSerializer.Deserialize<AgentConfiguration>(je.GetRawText(), jsonOptions); }
        catch { return null; }
    }

    private static UiControl BuildDetailsLayout(LayoutAreaHost host, MeshNode node, AgentConfiguration agent)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with edit button — display name from the node.
        var displayName = node.Name ?? node.Id.Wordify();
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 8px;")
            .WithView(Controls.Html($"<h1 style=\"margin: 0;\">{System.Web.HttpUtility.HtmlEncode(displayName)}</h1>"))
            .WithView(Controls.Button("")
                .WithIconStart(FluentIcons.Edit())
                .WithAppearance(Appearance.Accent)
                .WithNavigateToHref(new LayoutAreaReference(EditArea).ToHref(hubAddress)));

        stack = stack.WithView(headerRow);

        // Description — from the node.
        if (!string.IsNullOrEmpty(node.Description))
        {
            stack = stack.WithView(Controls.Html($"<p style=\"color: #666; margin: 0 0 24px 0;\">{System.Web.HttpUtility.HtmlEncode(node.Description)}</p>"));
        }

        // Attributes badges
        var attributes = BuildAttributeBadges(agent);
        if (!string.IsNullOrEmpty(attributes))
        {
            stack = stack.WithView(Controls.Html($"<div style=\"margin-bottom: 24px;\">{attributes}</div>"));
        }

        // Info card — node-level rows read from the node; only the context pattern
        // (genuinely agent-specific) comes from the configuration.
        var infoCard = Controls.Stack
            .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 20px; margin-bottom: 24px;");

        infoCard = infoCard.WithView(BuildInfoRow("ID", node.Id));
        if (!string.IsNullOrEmpty(node.Category))
            infoCard = infoCard.WithView(BuildInfoRow("Group", node.Category));
        if (!string.IsNullOrEmpty(node.Icon))
            infoCard = infoCard.WithView(BuildInfoRow("Icon", node.Icon));
        if (node.Order is { } order)
            infoCard = infoCard.WithView(BuildInfoRow("Display Order", order.ToString()));
        if (!string.IsNullOrEmpty(agent.ContextMatchPattern))
            infoCard = infoCard.WithView(BuildInfoRow("Context Pattern", agent.ContextMatchPattern));

        stack = stack.WithView(infoCard);

        // Instructions section
        stack = stack.WithView(Controls.Html("<h3 style=\"margin: 0 0 12px 0;\">Instructions</h3>"));
        if (!string.IsNullOrEmpty(agent.Instructions))
        {
            stack = stack.WithView(new MarkdownControl(agent.Instructions).WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 20px; margin-bottom: 24px;"));
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888; margin-bottom: 24px;\">No instructions configured.</p>"));
        }

        // Delegations section
        if (agent.Delegations is { Count: > 0 })
        {
            stack = stack.WithView(Controls.Html("<h3 style=\"margin: 0 0 12px 0;\">Delegations</h3>"));

            var delegationsList = Controls.Stack
                .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 20px; margin-bottom: 24px;");

            foreach (var delegation in agent.Delegations)
            {
                var delegationItem = Controls.Stack
                    .WithStyle("padding: 12px; border-left: 4px solid #0366d6; margin-bottom: 12px; background: var(--neutral-layer-1); border-radius: 4px;")
                    .WithView(Controls.Html($"<strong style=\"color: #0366d6;\">{System.Web.HttpUtility.HtmlEncode(delegation.AgentPath)}</strong>"));

                if (!string.IsNullOrEmpty(delegation.Instructions))
                {
                    delegationItem = delegationItem.WithView(Controls.Html($"<p style=\"margin: 8px 0 0 0; color: #666;\">{System.Web.HttpUtility.HtmlEncode(delegation.Instructions)}</p>"));
                }

                delegationsList = delegationsList.WithView(delegationItem);
            }

            stack = stack.WithView(delegationsList);
        }

        return stack;
    }

    private static string BuildAttributeBadges(AgentConfiguration agent)
    {
        var badges = ImmutableList<string>.Empty;

        if (agent.IsDefault)
            badges = badges.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(40, 167, 69, 0.2); color: #28a745; border-radius: 16px; font-size: 13px; font-weight: 600;'>Default Agent</span>");

        if (agent.ExposedInNavigator)
            badges = badges.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(3, 102, 214, 0.2); color: #0366d6; border-radius: 16px; font-size: 13px; font-weight: 600;'>Exposed in Navigator</span>");

        if (agent.Delegations is { Count: > 0 })
            badges = badges.Add($"<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(255, 193, 7, 0.2); color: #ffc107; border-radius: 16px; font-size: 13px; font-weight: 600;'>{agent.Delegations.Count} Delegation{(agent.Delegations.Count != 1 ? "s" : "")}</span>");

        return string.Join("", badges);
    }

    /// <summary>
    /// Renders the Edit area for an Agent. Node-level fields (name, description, icon,
    /// group, order) persist to the owning MeshNode; agent-specific fields persist to the
    /// AgentConfiguration content. Both land in one <c>stream.Update</c> so there is a
    /// single source of truth — no duplicated metadata.
    /// </summary>
    public static UiControl Edit(LayoutAreaHost host, RenderingContext ctx)
    {
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => host.Workspace.GetMeshNodeStream()
                    .Select(node =>
                    {
                        if (node == null)
                            return RenderLoading("Loading agent...");
                        // A freshly-created Agent has no AgentConfiguration content yet — edit a
                        // default (empty) config; the first Save persists it to the node stream.
                        var agent = AsAgentConfiguration(node, host.Hub.JsonSerializerOptions)
                                    ?? new AgentConfiguration { Id = node.Id };
                        return BuildEditLayout(host, node, agent);
                    }),
                "Content"
            );
    }

    private static UiControl BuildEditLayout(LayoutAreaHost host, MeshNode node, AgentConfiguration agent)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Data IDs for each editable field
        var displayNameDataId = Guid.NewGuid().AsString();
        var descriptionDataId = Guid.NewGuid().AsString();
        var iconNameDataId = Guid.NewGuid().AsString();
        var groupNameDataId = Guid.NewGuid().AsString();
        var orderDataId = Guid.NewGuid().AsString();
        var contextMatchPatternDataId = Guid.NewGuid().AsString();
        var isDefaultDataId = Guid.NewGuid().AsString();
        var exposedInNavigatorDataId = Guid.NewGuid().AsString();
        var instructionsDataId = Guid.NewGuid().AsString();

        // Initialize data streams — node-level fields from the node, agent-level from config.
        host.UpdateData(displayNameDataId, node.Name ?? "");
        host.UpdateData(descriptionDataId, node.Description ?? "");
        host.UpdateData(iconNameDataId, node.Icon ?? "");
        host.UpdateData(groupNameDataId, node.Category ?? "");
        host.UpdateData(orderDataId, (node.Order ?? 0).ToString());
        host.UpdateData(contextMatchPatternDataId, agent.ContextMatchPattern ?? "");
        host.UpdateData(isDefaultDataId, agent.IsDefault ? "true" : "false");
        host.UpdateData(exposedInNavigatorDataId, agent.ExposedInNavigator ? "true" : "false");
        host.UpdateData(instructionsDataId, agent.Instructions ?? "");

        // Header
        var displayName = node.Name ?? node.Id.Wordify();
        stack = stack.WithView(Controls.Html($"<h2 style=\"margin-bottom: 24px;\">Edit: {System.Web.HttpUtility.HtmlEncode(displayName)}</h2>"));

        // Form fields
        var formStyle = "display: grid; grid-template-columns: 160px 1fr; gap: 12px; align-items: center; margin-bottom: 12px;";

        // Display Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Display Name:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter display name...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(displayNameDataId) }));

        // Description
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Description:</label>"))
            .WithView(new TextAreaControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter description...")
                .WithRows(3)
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(descriptionDataId) }));

        // Group Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Group Name:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., Insurance, Todo...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(groupNameDataId) }));

        // Icon Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Icon Name:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., Compass, Shield...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(iconNameDataId) }));

        // Display Order
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Display Order:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("0")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(orderDataId) }));

        // Context Match Pattern
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Context Pattern:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., address.type==pricing")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(contextMatchPatternDataId) }));

        // Boolean fields as text (true/false)
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Default Agent:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("true or false")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(isDefaultDataId) }));

        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Exposed in Navigator:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("true or false")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(exposedInNavigatorDataId) }));

        // Instructions (code editor for markdown)
        stack = stack.WithView(Controls.Html("<h3 style=\"margin: 24px 0 8px 0;\">Instructions</h3>"));
        stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin-bottom: 8px;\">System prompt for the agent (supports Markdown)</p>"));

        var editor = new CodeEditorControl()
            .WithLanguage("markdown")
            .WithHeight("400px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            .WithPlaceholder("Enter instructions...");

        editor = editor with
        {
            DataContext = LayoutAreaReference.GetDataPointer(instructionsDataId),
            Value = new JsonPointerReference("")
        };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 16px;");

        // Cancel button
        var detailsHref = new LayoutAreaReference(DetailsArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(detailsHref));

        // Save button
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(actx =>
            {
                // Sync click action — Subscribe to combined snapshot of all form streams,
                // then write through the canonical mesh-node mutation API. Node-level
                // fields land on the MeshNode; agent-specific fields on its Content. One
                // update, one source of truth.
                Observable.CombineLatest(
                    host.Stream.GetDataStream<string>(displayNameDataId).Take(1),
                    host.Stream.GetDataStream<string>(descriptionDataId).Take(1),
                    host.Stream.GetDataStream<string>(iconNameDataId).Take(1),
                    host.Stream.GetDataStream<string>(groupNameDataId).Take(1),
                    host.Stream.GetDataStream<string>(orderDataId).Take(1),
                    host.Stream.GetDataStream<string>(contextMatchPatternDataId).Take(1),
                    host.Stream.GetDataStream<string>(isDefaultDataId).Take(1),
                    host.Stream.GetDataStream<string>(exposedInNavigatorDataId).Take(1),
                    host.Stream.GetDataStream<string>(instructionsDataId).Take(1),
                    (newDisplayName, newDescription, newIconName, newGroupName, newOrderStr,
                     newContextMatchPattern, newIsDefaultStr, newExposedInNavigatorStr, newInstructions) =>
                        (newDisplayName, newDescription, newIconName, newGroupName, newOrderStr,
                         newContextMatchPattern, newIsDefaultStr, newExposedInNavigatorStr, newInstructions))
                    .Take(1)
                    .Subscribe(form =>
                    {
                        var (newDisplayName, newDescription, newIconName, newGroupName, newOrderStr,
                             newContextMatchPattern, newIsDefaultStr, newExposedInNavigatorStr, newInstructions) = form;
                        int? newOrder = int.TryParse(newOrderStr, out var parsed) ? parsed : null;
                        var newIsDefault = newIsDefaultStr?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                        var newExposedInNavigator = newExposedInNavigatorStr?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

                        actx.Host.Workspace.GetMeshNodeStream().Update(current =>
                        {
                            var baseConfig = AsAgentConfiguration(current, actx.Host.Hub.JsonSerializerOptions)
                                              ?? agent;
                            return current with
                            {
                                // Node-level metadata — the single source of truth.
                                Name = string.IsNullOrWhiteSpace(newDisplayName) ? current.Id : newDisplayName,
                                Description = string.IsNullOrWhiteSpace(newDescription) ? null : newDescription,
                                Icon = string.IsNullOrWhiteSpace(newIconName) ? null : newIconName,
                                Category = string.IsNullOrWhiteSpace(newGroupName) ? null : newGroupName,
                                Order = newOrder,
                                // Agent-specific behaviour.
                                Content = baseConfig with
                                {
                                    ContextMatchPattern = string.IsNullOrWhiteSpace(newContextMatchPattern) ? null : newContextMatchPattern,
                                    IsDefault = newIsDefault,
                                    ExposedInNavigator = newExposedInNavigator,
                                    Instructions = string.IsNullOrWhiteSpace(newInstructions) ? null : newInstructions
                                }
                            };
                        }).Subscribe(
                            _ =>
                            {
                                var overviewHref = new LayoutAreaReference(DetailsArea).ToHref(hubAddress);
                                actx.Host.UpdateArea(actx.Area, new RedirectControl(overviewHref));
                            },
                            ex =>
                            {
                                actx.Host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                                    ?.CreateLogger(typeof(AgentView))
                                    .LogWarning(ex, "Agent edit save failed for {Path}", node.Path);
                                var dialog = Controls.Dialog(
                                    Controls.Markdown($"**Error saving:**\n\n{ex.Message}"),
                                    "Save Failed"
                                ).WithSize("M");
                                actx.Host.UpdateArea(DialogControl.DialogArea, dialog);
                            });
                    });
                return Task.CompletedTask;
            }));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    private static UiControl BuildInfoRow(string label, string value)
    {
        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 0; border-bottom: 1px solid var(--neutral-stroke-divider);")
            .WithView(Controls.Html($"<strong style=\"width: 150px; flex-shrink: 0;\">{System.Web.HttpUtility.HtmlEncode(label)}:</strong>"))
            .WithView(Controls.Html($"<span>{System.Web.HttpUtility.HtmlEncode(value)}</span>"));
    }

    private static UiControl RenderLoading(string message)
        => Controls.Stack
            .WithStyle("padding: 24px; display: flex; align-items: center; justify-content: center;")
            .WithView(Controls.Progress(message, 0));

    private static UiControl RenderError(string message)
        => new MarkdownControl($"> [!CAUTION]\n> {message}\n");
}
