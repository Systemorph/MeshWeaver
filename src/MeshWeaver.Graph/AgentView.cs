using System.Reactive.Linq;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for Agent nodes.
/// - Catalog: List of all agents
/// - Details: Overview of the Agent
/// - Edit: Edit agent configuration
/// </summary>
public static class AgentView
{
    public const string CatalogArea = "Catalog";
    public const string DetailsArea = "Details";
    public const string EditArea = "Edit";

    private const string AgentDataId = "agent";

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
                (h, c) => Observable.FromAsync(async () =>
                {
                    var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
                    if (meshQuery == null)
                        return RenderError("Query service not available.");

                    List<MeshNode> agents;
                    try
                    {
                        agents = await meshQuery.QueryAsync<MeshNode>("nodeType:Agent").ToListAsync();
                    }
                    catch
                    {
                        agents = [];
                    }

                    return BuildCatalogContent(agents);
                }),
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
    /// Shows an overview of the agent configuration.
    /// </summary>
    public static UiControl Details(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to agent data stream
        host.SubscribeToDataStream(AgentDataId, host.Workspace.GetNodeContent<AgentConfiguration>());

        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<AgentConfiguration>(AgentDataId)
                    .Select(agent =>
                    {
                        if (agent == null)
                            return RenderLoading("Loading agent...");
                        return BuildDetailsLayout(host, agent);
                    }),
                "Content"
            );
    }

    private static UiControl BuildDetailsLayout(LayoutAreaHost host, AgentConfiguration agent)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header with edit button
        var displayName = agent.DisplayName ?? agent.Id.Wordify();
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 8px;")
            .WithView(Controls.Html($"<h1 style=\"margin: 0;\">{System.Web.HttpUtility.HtmlEncode(displayName)}</h1>"))
            .WithView(Controls.Button("")
                .WithIconStart(FluentIcons.Edit())
                .WithAppearance(Appearance.Accent)
                .WithNavigateToHref(new LayoutAreaReference(EditArea).ToHref(hubAddress)));

        stack = stack.WithView(headerRow);

        // Description
        if (!string.IsNullOrEmpty(agent.Description))
        {
            stack = stack.WithView(Controls.Html($"<p style=\"color: #666; margin: 0 0 24px 0;\">{System.Web.HttpUtility.HtmlEncode(agent.Description)}</p>"));
        }

        // Attributes badges
        var attributes = BuildAttributeBadges(agent);
        if (!string.IsNullOrEmpty(attributes))
        {
            stack = stack.WithView(Controls.Html($"<div style=\"margin-bottom: 24px;\">{attributes}</div>"));
        }

        // Info card
        var infoCard = Controls.Stack
            .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 20px; margin-bottom: 24px;");

        infoCard = infoCard.WithView(BuildInfoRow("ID", agent.Id));
        if (!string.IsNullOrEmpty(agent.GroupName))
            infoCard = infoCard.WithView(BuildInfoRow("Group", agent.GroupName));
        if (!string.IsNullOrEmpty(agent.Icon))
            infoCard = infoCard.WithView(BuildInfoRow("Icon", agent.Icon));
        infoCard = infoCard.WithView(BuildInfoRow("Display Order", agent.Order.ToString()));
        if (!string.IsNullOrEmpty(agent.PreferredModel))
            infoCard = infoCard.WithView(BuildInfoRow("Preferred Model", agent.PreferredModel));
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
        var badges = new List<string>();

        if (agent.IsDefault)
            badges.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(40, 167, 69, 0.2); color: #28a745; border-radius: 16px; font-size: 13px; font-weight: 600;'>Default Agent</span>");

        if (agent.ExposedInNavigator)
            badges.Add("<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(3, 102, 214, 0.2); color: #0366d6; border-radius: 16px; font-size: 13px; font-weight: 600;'>Exposed in Navigator</span>");

        if (agent.Delegations is { Count: > 0 })
            badges.Add($"<span style='display: inline-block; margin: 4px 8px 4px 0; padding: 6px 12px; background: rgba(255, 193, 7, 0.2); color: #ffc107; border-radius: 16px; font-size: 13px; font-weight: 600;'>{agent.Delegations.Count} Delegation{(agent.Delegations.Count != 1 ? "s" : "")}</span>");

        return string.Join("", badges);
    }

    /// <summary>
    /// Renders the Edit area for an Agent.
    /// </summary>
    public static UiControl Edit(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to agent data stream
        host.SubscribeToDataStream(AgentDataId, host.Workspace.GetNodeContent<AgentConfiguration>());

        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<AgentConfiguration>(AgentDataId)
                    .Select(agent =>
                    {
                        if (agent == null)
                            return RenderLoading("Loading agent...");
                        return BuildEditLayout(host, agent);
                    }),
                "Content"
            );
    }

    private static UiControl BuildEditLayout(LayoutAreaHost host, AgentConfiguration agent)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Data IDs for each editable field
        var displayNameDataId = Guid.NewGuid().AsString();
        var descriptionDataId = Guid.NewGuid().AsString();
        var iconNameDataId = Guid.NewGuid().AsString();
        var groupNameDataId = Guid.NewGuid().AsString();
        var orderDataId = Guid.NewGuid().AsString();
        var preferredModelDataId = Guid.NewGuid().AsString();
        var contextMatchPatternDataId = Guid.NewGuid().AsString();
        var isDefaultDataId = Guid.NewGuid().AsString();
        var exposedInNavigatorDataId = Guid.NewGuid().AsString();
        var instructionsDataId = Guid.NewGuid().AsString();

        // Initialize data streams
        host.UpdateData(displayNameDataId, agent.DisplayName ?? "");
        host.UpdateData(descriptionDataId, agent.Description ?? "");
        host.UpdateData(iconNameDataId, agent.Icon ?? "");
        host.UpdateData(groupNameDataId, agent.GroupName ?? "");
        host.UpdateData(orderDataId, agent.Order.ToString());
        host.UpdateData(preferredModelDataId, agent.PreferredModel ?? "");
        host.UpdateData(contextMatchPatternDataId, agent.ContextMatchPattern ?? "");
        host.UpdateData(isDefaultDataId, agent.IsDefault ? "true" : "false");
        host.UpdateData(exposedInNavigatorDataId, agent.ExposedInNavigator ? "true" : "false");
        host.UpdateData(instructionsDataId, agent.Instructions ?? "");

        // Header
        var displayName = agent.DisplayName ?? agent.Id.Wordify();
        stack = stack.WithView(Controls.Html($"<h2 style=\"margin-bottom: 24px;\">Edit: {System.Web.HttpUtility.HtmlEncode(displayName)}</h2>"));

        // Form fields
        var formStyle = "display: grid; grid-template-columns: 160px 1fr; gap: 12px; align-items: center; margin-bottom: 12px;";

        // Display Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Display Name:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter display name...")
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(displayNameDataId) }));

        // Description
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Description:</label>"))
            .WithView(new TextAreaControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter description...")
                .WithRows(3)
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(descriptionDataId) }));

        // Group Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Group Name:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., Insurance, Todo...")
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(groupNameDataId) }));

        // Icon Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Icon Name:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., Compass, Shield...")
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(iconNameDataId) }));

        // Display Order
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Display Order:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("0")
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(orderDataId) }));

        // Preferred Model
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Preferred Model:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., claude-sonnet-4-5")
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(preferredModelDataId) }));

        // Context Match Pattern
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Context Pattern:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., address.type==pricing")
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(contextMatchPatternDataId) }));

        // Boolean fields as text (true/false)
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Default Agent:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("true or false")
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(isDefaultDataId) }));

        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Exposed in Navigator:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("true or false")
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(exposedInNavigatorDataId) }));

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
            .WithClickAction(async actx =>
            {
                // Get all field values
                var newDisplayName = await host.Stream.GetDataStream<string>(displayNameDataId).FirstAsync();
                var newDescription = await host.Stream.GetDataStream<string>(descriptionDataId).FirstAsync();
                var newIconName = await host.Stream.GetDataStream<string>(iconNameDataId).FirstAsync();
                var newGroupName = await host.Stream.GetDataStream<string>(groupNameDataId).FirstAsync();
                var newOrderStr = await host.Stream.GetDataStream<string>(orderDataId).FirstAsync();
                var newPreferredModel = await host.Stream.GetDataStream<string>(preferredModelDataId).FirstAsync();
                var newContextMatchPattern = await host.Stream.GetDataStream<string>(contextMatchPatternDataId).FirstAsync();
                var newIsDefaultStr = await host.Stream.GetDataStream<string>(isDefaultDataId).FirstAsync();
                var newExposedInNavigatorStr = await host.Stream.GetDataStream<string>(exposedInNavigatorDataId).FirstAsync();
                var newInstructions = await host.Stream.GetDataStream<string>(instructionsDataId).FirstAsync();

                // Parse order
                if (!int.TryParse(newOrderStr, out var newOrder))
                    newOrder = 0;

                // Parse booleans
                var newIsDefault = newIsDefaultStr?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                var newExposedInNavigator = newExposedInNavigatorStr?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

                // Update the AgentConfiguration
                var updatedAgent = agent with
                {
                    DisplayName = string.IsNullOrWhiteSpace(newDisplayName) ? null : newDisplayName,
                    Description = string.IsNullOrWhiteSpace(newDescription) ? null : newDescription,
                    Icon = string.IsNullOrWhiteSpace(newIconName) ? null : newIconName,
                    GroupName = string.IsNullOrWhiteSpace(newGroupName) ? null : newGroupName,
                    Order = newOrder,
                    PreferredModel = string.IsNullOrWhiteSpace(newPreferredModel) ? null : newPreferredModel,
                    ContextMatchPattern = string.IsNullOrWhiteSpace(newContextMatchPattern) ? null : newContextMatchPattern,
                    IsDefault = newIsDefault,
                    ExposedInNavigator = newExposedInNavigator,
                    Instructions = string.IsNullOrWhiteSpace(newInstructions) ? null : newInstructions
                };

                using var cts = new CancellationTokenSource(10.Seconds());
                var delivery = actx.Host.Hub.Post(
                    new DataChangeRequest { ChangedBy = actx.Host.Stream.ClientId }.WithUpdates(updatedAgent),
                    o => o.WithTarget(hubAddress))!;
                var callbackResponse = await actx.Host.Hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), cts.Token);
                var responseMsg = ((IMessageDelivery<DataChangeResponse>)callbackResponse).Message;

                if (responseMsg.Log.Status != ActivityStatus.Succeeded)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error saving:**\n\n{responseMsg.Log}"),
                        "Save Failed"
                    ).WithSize("M");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

                // Navigate back to details
                var overviewHref = new LayoutAreaReference(DetailsArea).ToHref(hubAddress);
                actx.Host.UpdateArea(actx.Area, new RedirectControl(overviewHref));
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
