using System.Reactive.Linq;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Blazor.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for NodeType definition nodes.
/// - Catalog: Default view showing instances of this type as thumbnails
/// - Details: Overview of the NodeType
/// - CodeView: Split view with left menu and code display
/// - CodeEdit: Monaco editor for code editing
/// - HubConfigView: View HubConfiguration
/// - HubConfigEdit: Monaco editor for HubConfiguration
/// </summary>
public static class NodeTypeView
{
    public const string CatalogArea = "Catalog";
    public const string DetailsArea = "Details";
    public const string CodeViewArea = "Code";
    public const string CodeEditArea = "CodeEdit";
    public const string HubConfigViewArea = "HubConfig";
    public const string HubConfigEditArea = "HubConfigEdit";

    // Data keys for data section
    private const string DefinitionDataId = "definition";
    private const string CodeFilesDataId = "codeFiles";
    private const string CodeFileDataId = "codeFile";
    private const string SelectionDataId = "selection";
    private const string CatalogSearchDataId = "catalogSearch";
    private const string CatalogLimitDataId = "catalogLimit";
    private const int DefaultPageSize = 20;

    /// <summary>
    /// Adds the NodeType views to the hub's layout for NodeType nodes.
    /// Catalog is the default view showing instances of this type as thumbnails.
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(CatalogArea)
            .WithView(CatalogArea, Catalog)
            .WithView(DetailsArea, Details)
            .WithView(CodeViewArea, CodeView)
            .WithView(CodeEditArea, CodeEdit)
            .WithView(HubConfigViewArea, HubConfigView)
            .WithView(HubConfigEditArea, HubConfigEdit));

    /// <summary>
    /// Renders the Catalog view showing instances of this NodeType as thumbnails.
    /// Includes search bar for RSQL filtering and Load More for pagination.
    /// </summary>
    public static UiControl Catalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubAddress = host.Hub.Address;
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        // Subscribe to definition stream
        host.SubscribeToDataStream(DefinitionDataId, host.Workspace.GetNodeContent<NodeTypeDefinition>());

        // Initialize catalog state
        host.UpdateData(CatalogSearchDataId, "");
        host.UpdateData(CatalogLimitDataId, DefaultPageSize.ToString());

        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<NodeTypeDefinition>(DefinitionDataId)
                    .CombineLatest(
                        h.GetDataStream<string>(CatalogSearchDataId),
                        h.GetDataStream<string>(CatalogLimitDataId))
                    .Throttle(TimeSpan.FromMilliseconds(300))
                    .SelectMany(async tuple =>
                    {
                        var (definition, search, limitStr) = tuple;
                        if (definition == null)
                            return RenderLoading("Loading...");

                        var limit = int.TryParse(limitStr, out var l) ? l : DefaultPageSize;
                        return await BuildCatalogViewAsync(host, hubAddress, definition, persistence, search, limit);
                    }),
                "Content");
    }

    private static async Task<UiControl> BuildCatalogViewAsync(
        LayoutAreaHost host,
        object hubAddress,
        NodeTypeDefinition definition,
        IPersistenceService? persistence,
        string? searchFilter,
        int limit)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var title = definition.DisplayName ?? definition.Id;

        // Search bar
        var searchRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-bottom: 16px; align-items: center;")
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Search or filter (e.g., name:*acme*)")
                .WithStyle("flex: 1;")
                .WithIconStart(FluentIcons.Search())
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(CatalogSearchDataId) })
            .WithView(Controls.Button("Clear")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(actx =>
                {
                    host.UpdateData(CatalogSearchDataId, "");
                    host.UpdateData(CatalogLimitDataId, DefaultPageSize.ToString());
                }));

        stack = stack.WithView(searchRow);

        // Subtitle
        var subtitleText = string.IsNullOrWhiteSpace(searchFilter)
            ? $"Showing recent {System.Web.HttpUtility.HtmlEncode(title)}s"
            : $"Filtered {System.Web.HttpUtility.HtmlEncode(title)}s";
        stack = stack.WithView(Controls.Html($"<p style=\"color: #666; margin-bottom: 16px;\">{subtitleText}</p>"));

        if (persistence == null)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">Persistence service not available.</p>"));
            return stack;
        }

        // Build query - combine base query with user search
        var nodeTypePath = string.IsNullOrEmpty(definition.Namespace)
            ? definition.Id
            : $"{definition.Namespace}/{definition.Id}";
        var baseQuery = definition.ChildrenQuery
            ?? $"source:activity nodeType:{nodeTypePath} sort:lastAccessedAt-desc";

        // Request one more than limit to detect if there are more items
        var queryLimit = limit + 1;
        var query = BuildCatalogQuery(baseQuery, searchFilter, queryLimit);

        var nodes = new List<MeshNode>();
        var isActivityQuery = query.Contains("source:activity", StringComparison.OrdinalIgnoreCase);

        try
        {
            // Search from root namespace to find all instances regardless of their location
            await foreach (var item in persistence.QueryAsync(query, ""))
            {
                if (item is UserActivityRecord activity)
                {
                    // Load the actual MeshNode for the activity record
                    var node = await persistence.GetNodeAsync(activity.NodePath);
                    if (node != null)
                        nodes.Add(node);
                }
                else if (item is MeshNode mn)
                {
                    nodes.Add(mn);
                }
            }
        }
        catch
        {
            // Query may fail if no activity data yet - that's ok
        }

        // Fallback: if activity query returned no results and no search filter, query actual nodes
        if (nodes.Count == 0 && isActivityQuery && string.IsNullOrWhiteSpace(searchFilter))
        {
            try
            {
                // Build fallback query without source:activity
                // Search from root namespace to find all instances regardless of location
                var fallbackQuery = $"nodeType:{nodeTypePath} scope:descendants limit:{queryLimit}";
                await foreach (var item in persistence.QueryAsync(fallbackQuery, ""))
                {
                    if (item is MeshNode mn)
                        nodes.Add(mn);
                }
            }
            catch
            {
                // Fallback query failed - that's ok
            }
        }

        // Check if there are more items
        var hasMore = nodes.Count > limit;
        if (hasMore)
        {
            nodes = nodes.Take(limit).ToList();
        }

        // Thumbnail grid
        if (nodes.Count == 0)
        {
            var noResultsMsg = string.IsNullOrWhiteSpace(searchFilter)
                ? "No items found."
                : "No items match your search.";
            stack = stack.WithView(Controls.Html($"<p style=\"color: #888;\">{noResultsMsg}</p>"));
        }
        else
        {
            // Results count
            var countText = hasMore
                ? $"Showing {nodes.Count}+ items"
                : $"Showing {nodes.Count} item{(nodes.Count != 1 ? "s" : "")}";
            stack = stack.WithView(Controls.Html($"<p style=\"color: #888; margin-bottom: 12px; font-size: 0.9em;\">{countText}</p>"));

            var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(3));
            foreach (var node in nodes)
            {
                grid = grid.WithView(
                    MeshNodeThumbnailControl.FromNode(node, node.Path),
                    itemSkin => itemSkin.WithXs(12).WithSm(12).WithMd(6).WithLg(6));
            }
            stack = stack.WithView(grid);

            // Load More button
            if (hasMore)
            {
                var newLimit = limit + DefaultPageSize;
                var loadMoreRow = Controls.Stack
                    .WithStyle("margin-top: 24px; display: flex; justify-content: center;")
                    .WithView(Controls.Button("Load More")
                        .WithAppearance(Appearance.Neutral)
                        .WithIconEnd(FluentIcons.ChevronDown())
                        .WithClickAction(actx =>
                        {
                            host.UpdateData(CatalogLimitDataId, newLimit.ToString());
                        }));
                stack = stack.WithView(loadMoreRow);
            }
        }

        return stack;
    }

    /// <summary>
    /// Builds the final catalog query by combining base query with user search filter.
    /// </summary>
    private static string BuildCatalogQuery(string baseQuery, string? searchFilter, int limit)
    {
        // Remove any existing limit: from base query (we'll add our own)
        var query = System.Text.RegularExpressions.Regex.Replace(
            baseQuery,
            @"limit:\d+\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        // Add user's search filter if provided
        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            var trimmedSearch = searchFilter.Trim();

            // Check if it looks like a query filter (contains field:value pattern)
            var isQuery = trimmedSearch.Contains(':') && !trimmedSearch.StartsWith('"');

            if (isQuery)
            {
                // Append as query filter
                query = query + " " + trimmedSearch;
            }
            else
            {
                // Treat as text search - add directly (bare text is text search in GitHub syntax)
                query = query + " " + trimmedSearch;
            }
        }

        // Add limit
        query = query.Trim() + " limit:" + limit;

        return query;
    }

    /// <summary>
    /// Renders the main Details area for a NodeType.
    /// Shows an overview of the NodeType configuration.
    /// Returns static structure with data-bound dynamic parts.
    /// </summary>
    public static UiControl Details(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data streams - data will be stored in data section
        host.SubscribeToDataStream(DefinitionDataId, host.Workspace.GetNodeContent<NodeTypeDefinition>());
        host.SubscribeToDataStream(CodeFileDataId, host.Workspace.GetSingle<CodeConfiguration>());

        // Return static structure with nested observable view for content
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<NodeTypeDefinition>(DefinitionDataId)
                    .CombineLatest(h.GetDataStream<CodeConfiguration>(CodeFileDataId))
                    .Select(tuple =>
                    {
                        var (definition, codeFile) = tuple;
                        if (definition == null)
                            return RenderLoading("Loading NodeType definition...");
                        return BuildDetailsLayout(host, definition, codeFile);
                    }),
                "Content"
            );
    }

    /// <summary>
    /// Builds the Details layout with overview and navigation.
    /// </summary>
    private static UiControl BuildDetailsLayout(
        LayoutAreaHost host,
        NodeTypeDefinition content,
        CodeConfiguration? codeFile)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Header
        var title = content.DisplayName ?? content.Id;
        stack = stack.WithView(Controls.Html($"<h1 style=\"margin: 0 0 8px 0;\">{System.Web.HttpUtility.HtmlEncode(title)}</h1>"));
        stack = stack.WithView(Controls.Html($"<p style=\"color: #666; margin: 0 0 24px 0;\">NodeType Configuration</p>"));

        // Type info card
        var infoCard = Controls.Stack
            .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 20px; margin-bottom: 24px;");

        infoCard = infoCard.WithView(BuildInfoRow("ID", content.Id));
        if (!string.IsNullOrEmpty(content.DisplayName))
            infoCard = infoCard.WithView(BuildInfoRow("Display Name", content.DisplayName));
        if (!string.IsNullOrEmpty(content.Description))
            infoCard = infoCard.WithView(BuildInfoRow("Description", content.Description));
        if (!string.IsNullOrEmpty(content.IconName))
            infoCard = infoCard.WithView(BuildInfoRow("Icon", content.IconName));
        infoCard = infoCard.WithView(BuildInfoRow("Display Order", content.DisplayOrder.ToString()));

        var hasCode = !string.IsNullOrEmpty(codeFile?.Code);
        infoCard = infoCard.WithView(BuildInfoRow("Has Code", hasCode ? "Yes" : "No"));
        infoCard = infoCard.WithView(BuildInfoRow("Has Configuration", !string.IsNullOrEmpty(content.Configuration) ? "Yes" : "No"));

        stack = stack.WithView(infoCard);

        // Navigation buttons
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;");

        var codeHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("View Code & Configuration")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Code())
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(codeHref))));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Renders the split view with left menu and code/config display.
    /// Returns static Splitter structure with data-bound content panes.
    /// </summary>
    public static UiControl CodeView(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubAddress = host.Hub.Address;

        // Get data streams directly
        var definitionStream = host.Workspace.GetNodeContent<NodeTypeDefinition>();
        var codeFilesStream = host.Workspace.GetStream<CodeConfiguration>()!;
        var selectionStream = host.Stream.GetDataStream<string?>(SelectionDataId);

        // Initialize selection to "configuration" (show node type definition by default)
        host.UpdateData(SelectionDataId, "configuration");

        // Return static Splitter structure with observable nested views
        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                // Left menu - observable, updates when definition or code files load
                (h, c) => definitionStream
                    .CombineLatest(codeFilesStream)
                    .Select(tuple =>
                    {
                        var (definition, codeFiles) = tuple;
                        if (definition == null)
                            return RenderLoading("Loading...");
                        return BuildLeftMenu(host, hubAddress, definition, codeFiles);
                    }),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                // Main pane - reacts to selection
                (h, c) => definitionStream
                    .CombineLatest(codeFilesStream, selectionStream)
                    .Select(tuple =>
                    {
                        var (definition, codeFiles, selection) = tuple;
                        if (definition == null)
                            return RenderLoading("Loading...");
                        return BuildMainPane(host, hubAddress, definition, codeFiles, selection);
                    }),
                skin => skin.WithSize("*")
            );
    }

    /// <summary>
    /// Builds the left navigation menu with Configuration and Code files entries.
    /// </summary>
    private static UiControl BuildLeftMenu(
        LayoutAreaHost host,
        object hubAddress,
        NodeTypeDefinition content,
        IReadOnlyCollection<CodeConfiguration>? codeFiles)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        // Back to Catalog link
        var catalogHref = new LayoutAreaReference(CatalogArea).ToHref(hubAddress);
        navMenu = navMenu.WithView(
            new NavLinkControl("← Back to Catalog", FluentIcons.ArrowLeft(), null)
                .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(catalogHref)))
        );

        // Node type definition entry - switches main view to configuration
        navMenu = navMenu.WithView(
            new NavLinkControl(content.DisplayName ?? content.Id, FluentIcons.Settings(), null)
                .WithClickAction(actx => host.UpdateData(SelectionDataId, "configuration"))
        );

        // Code section
        var codeGroup = new NavGroupControl("Code")
            .WithIcon(FluentIcons.Code())
            .WithSkin(s => s.WithExpanded(true));

        if (codeFiles != null && codeFiles.Count > 0)
        {
            foreach (var file in codeFiles)
            {
                var fileId = file.Id;
                codeGroup = codeGroup.WithView(
                    new NavLinkControl(file.DisplayName ?? file.Id, CustomIcons.CSharp(), null)
                        .WithClickAction(actx => host.UpdateData(SelectionDataId, fileId))
                );
            }
        }
        else
        {
            codeGroup = codeGroup.WithView(
                Controls.Html("<span style=\"padding: 4px 16px; display: block; color: #888;\">No code files</span>")
            );
        }

        navMenu = navMenu.WithNavGroup(codeGroup);

        // Dependencies section (if any)
        if (content.Dependencies != null && content.Dependencies.Count > 0)
        {
            var depsGroup = new NavGroupControl("Dependencies")
                .WithIcon(FluentIcons.Link())
                .WithSkin(s => s.WithExpanded(false));

            foreach (var dep in content.Dependencies)
            {
                depsGroup = depsGroup.WithView(
                    Controls.Html($"<span style=\"padding: 4px 16px; display: block;\">{System.Web.HttpUtility.HtmlEncode(dep)}</span>")
                );
            }

            navMenu = navMenu.WithNavGroup(depsGroup);
        }

        return navMenu;
    }

    /// <summary>
    /// Builds the main content pane based on selection.
    /// Shows either configuration or a code file.
    /// </summary>
    private static UiControl BuildMainPane(
        LayoutAreaHost host,
        object hubAddress,
        NodeTypeDefinition definition,
        IReadOnlyCollection<CodeConfiguration>? codeFiles,
        string? selection)
    {
        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; min-height: 100%; overflow: auto;");

        // Show configuration
        if (selection == "configuration")
        {
            return BuildConfigurationPane(stack, hubAddress, definition);
        }

        // Show selected code file or first one
        var codeFile = codeFiles?.FirstOrDefault(f => f.Id == selection)
            ?? codeFiles?.FirstOrDefault();

        if (codeFile == null)
        {
            return stack.WithView(Controls.Html("<p style=\"color: #888;\">No code files available.</p>"));
        }

        return BuildCodeFilePane(stack, hubAddress, codeFile);
    }

    /// <summary>
    /// Builds the read-only view of NodeTypeDefinition in the main pane.
    /// Shows all properties with Configuration as one of them.
    /// </summary>
    private static UiControl BuildConfigurationPane(StackControl stack, object hubAddress, NodeTypeDefinition definition)
    {
        var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);

        // Header with edit button
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.Html($"<h2 style=\"margin: 0;\">{System.Web.HttpUtility.HtmlEncode(definition.DisplayName ?? definition.Id)}</h2>"))
            .WithView(
                Controls.Button("")
                    .WithIconStart(FluentIcons.Edit())
                    .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(editHref)))
            );

        stack = stack.WithView(headerRow);

        // Properties card
        var propsCard = Controls.Stack
            .WithStyle("background: var(--neutral-layer-2); border-radius: 8px; padding: 20px; margin-bottom: 24px;");

        propsCard = propsCard.WithView(BuildInfoRow("ID", definition.Id));
        propsCard = propsCard.WithView(BuildInfoRow("Namespace", definition.Namespace));

        if (!string.IsNullOrEmpty(definition.DisplayName))
            propsCard = propsCard.WithView(BuildInfoRow("Display Name", definition.DisplayName));

        if (!string.IsNullOrEmpty(definition.Description))
            propsCard = propsCard.WithView(BuildInfoRow("Description", definition.Description));

        if (!string.IsNullOrEmpty(definition.IconName))
            propsCard = propsCard.WithView(BuildInfoRow("Icon", definition.IconName));

        propsCard = propsCard.WithView(BuildInfoRow("Display Order", definition.DisplayOrder.ToString()));

        if (!string.IsNullOrEmpty(definition.ChildrenQuery))
            propsCard = propsCard.WithView(BuildInfoRow("Children Query", definition.ChildrenQuery));

        if (definition.Dependencies != null && definition.Dependencies.Count > 0)
            propsCard = propsCard.WithView(BuildInfoRow("Dependencies", string.Join(", ", definition.Dependencies)));

        stack = stack.WithView(propsCard);

        // Configuration section (lambda expression)
        if (!string.IsNullOrEmpty(definition.Configuration))
        {
            stack = stack.WithView(Controls.Html("<h3 style=\"margin: 16px 0 8px 0;\">Configuration</h3>"));
            stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin-bottom: 8px;\">Lambda expression for configuring the message hub:</p>"));
            var markdown = $"```csharp\n{definition.Configuration}\n```";
            stack = stack.WithView(new MarkdownControl(markdown).WithStyle("width: 100%;"));
        }

        return stack;
    }

    /// <summary>
    /// Builds a code file view in the main pane.
    /// </summary>
    private static UiControl BuildCodeFilePane(StackControl stack, object hubAddress, CodeConfiguration codeFile)
    {
        var editHref = new LayoutAreaReference(CodeEditArea).ToHref(hubAddress);

        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.Html($"<h2 style=\"margin: 0;\">{System.Web.HttpUtility.HtmlEncode(codeFile.DisplayName ?? codeFile.Id)}</h2>"));

        if (!string.IsNullOrEmpty(codeFile.Code))
        {
            headerRow = headerRow.WithView(
                Controls.Button("")
                    .WithIconStart(FluentIcons.Edit())
                    .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(editHref)))
            );
        }

        stack = stack.WithView(headerRow);

        if (!string.IsNullOrEmpty(codeFile.Code))
        {
            var markdown = $"```{codeFile.Language}\n{codeFile.Code}\n```";
            stack = stack.WithView(new MarkdownControl(markdown).WithStyle("width: 100%;"));
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No code defined.</p>"));
        }

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing code files.
    /// Returns static structure with data-bound editor.
    /// </summary>
    public static UiControl CodeEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data streams
        host.SubscribeToDataStream(CodeFileDataId, host.Workspace.GetSingle<CodeConfiguration>());
        host.SubscribeToDataStream(DefinitionDataId, host.Workspace.GetNodeContent<NodeTypeDefinition>());

        // Return structure with nested observable view for editor
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<CodeConfiguration>(CodeFileDataId)
                    .Select(codeFile => BuildCodeEditContent(host, codeFile, "")),
                "Editor"
            );
    }

    private static UiControl BuildCodeEditContent(
        LayoutAreaHost host,
        CodeConfiguration? codeFile,
        string dependencyCode)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var codeDataId = Guid.NewGuid().AsString();
        var displayNameDataId = Guid.NewGuid().AsString();

        // Get initial code and language
        string initialCode = codeFile?.Code ?? "";
        string language = codeFile?.Language ?? "csharp";
        string displayName = codeFile?.DisplayName ?? "";

        host.UpdateData(codeDataId, initialCode);
        host.UpdateData(displayNameDataId, displayName);

        // DisplayName editor at top
        var displayNameRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Display Name:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter display name...")
                .WithStyle("flex: 1; max-width: 400px;")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(displayNameDataId) });

        stack = stack.WithView(displayNameRow);

        // Monaco editor bound to the data stream with autocomplete support
        var editor = new CodeEditorControl()
            .WithLanguage(language)
            .WithHeight("500px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true);

        if (!string.IsNullOrEmpty(dependencyCode))
        {
            editor = editor.WithExtraTypeDefinitions(dependencyCode);
        }

        editor = editor with
        {
            DataContext = LayoutAreaReference.GetDataPointer(codeDataId),
            Value = new JsonPointerReference("")
        };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 16px;");

        // Save button - update workspace stream which will sync to persistence
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
                {
                    var currentCode = await host.Stream.GetDataStream<string>(codeDataId).FirstAsync();
                    var currentDisplayName = await host.Stream.GetDataStream<string>(displayNameDataId).FirstAsync();

                    // Update the CodeConfiguration
                    var updatedCodeConfiguration = (codeFile ?? new CodeConfiguration()) with
                    {
                        Code = currentCode,
                        Language = language,
                        DisplayName = string.IsNullOrWhiteSpace(currentDisplayName) ? null : currentDisplayName
                    };

                    // Update via workspace - will sync to persistence
                    using var cts = new CancellationTokenSource(10.Seconds());
                    var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                        new DataChangeRequest().WithUpdates(updatedCodeConfiguration),
                        o => o.WithTarget(hubAddress),
                        cts.Token);

                    if (response.Message.Log.Status != ActivityStatus.Succeeded)
                    {
                        // Show error dialog
                        var errorDialog = Controls.Dialog(
                            Controls.Markdown($"**Error saving code:**\n\n{response.Message.Log}"),
                            "Save Failed"
                        ).WithSize("M");
                        actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                        return;
                    }

                    // Navigate back to view
                    var viewHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
                    actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref));
                }));

        // Cancel button
        var viewHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref))));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>
    /// Renders the view for Configuration.
    /// Returns static structure with data-bound content.
    /// </summary>
    public static UiControl HubConfigView(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data stream
        host.SubscribeToDataStream(DefinitionDataId, host.Workspace.GetNodeContent<NodeTypeDefinition>());

        // Return structure with nested observable view
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<NodeTypeDefinition>(DefinitionDataId)
                    .Select(content => content == null
                        ? RenderLoading("Loading...")
                        : BuildHubConfigViewContent(host, content)),
                "Content"
            );
    }

    private static UiControl BuildHubConfigViewContent(LayoutAreaHost host, NodeTypeDefinition content)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        stack = stack.WithView(Controls.Html("<h2 style=\"margin-bottom: 16px;\">Configuration</h2>"));
        stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin-bottom: 16px;\">Lambda expression: <code>Func&lt;MessageHubConfiguration, MessageHubConfiguration&gt;</code></p>"));

        if (!string.IsNullOrEmpty(content.Configuration))
        {
            var markdown = $"```csharp\n{content.Configuration}\n```";
            stack = stack.WithView(new MarkdownControl(markdown));

            // Edit button
            var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(hubAddress);
            stack = stack.WithView(
                Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("margin-top: 16px;")
                    .WithView(Controls.Button("Edit")
                        .WithAppearance(Appearance.Accent)
                        .WithIconStart(FluentIcons.Edit())
                        .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(editHref))))
            );
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: #888;\">No Configuration defined.</p>"));
        }

        // Back button
        var codeHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
        stack = stack.WithView(Controls.Button("Back")
            .WithAppearance(Appearance.Neutral)
            .WithStyle("margin-top: 24px;")
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(codeHref))));

        return stack;
    }

    /// <summary>
    /// Renders the Monaco editor for editing Configuration.
    /// Returns static structure with data-bound editor.
    /// </summary>
    public static UiControl HubConfigEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data streams
        host.SubscribeToDataStream(DefinitionDataId, host.Workspace.GetNodeContent<NodeTypeDefinition>());
        host.SubscribeToDataStream(CodeFileDataId, host.Workspace.GetSingle<CodeConfiguration>());

        // Return structure with nested observable view
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<NodeTypeDefinition>(DefinitionDataId)
                    .CombineLatest(h.GetDataStream<CodeConfiguration>(CodeFileDataId))
                    .Select(tuple =>
                    {
                        var (content, codeFile) = tuple;
                        if (content == null)
                            return RenderLoading("Loading...");
                        var allCode = codeFile?.Code ?? "";
                        return BuildHubConfigEditContent(host, content, allCode);
                    }),
                "Editor"
            );
    }

    private static UiControl BuildHubConfigEditContent(LayoutAreaHost host, NodeTypeDefinition content, string allCodeForAutocomplete)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");

        // Data IDs for each editable field
        var displayNameDataId = Guid.NewGuid().AsString();
        var descriptionDataId = Guid.NewGuid().AsString();
        var iconNameDataId = Guid.NewGuid().AsString();
        var displayOrderDataId = Guid.NewGuid().AsString();
        var childrenQueryDataId = Guid.NewGuid().AsString();
        var dependenciesDataId = Guid.NewGuid().AsString();
        var configurationDataId = Guid.NewGuid().AsString();

        // Initialize data streams
        host.UpdateData(displayNameDataId, content.DisplayName ?? "");
        host.UpdateData(descriptionDataId, content.Description ?? "");
        host.UpdateData(iconNameDataId, content.IconName ?? "");
        host.UpdateData(displayOrderDataId, content.DisplayOrder.ToString());
        host.UpdateData(childrenQueryDataId, content.ChildrenQuery ?? "");
        host.UpdateData(dependenciesDataId, content.Dependencies != null ? string.Join(", ", content.Dependencies) : "");
        host.UpdateData(configurationDataId, content.Configuration ?? "config => config");

        // Header
        stack = stack.WithView(Controls.Html($"<h2 style=\"margin-bottom: 16px;\">Edit: {System.Web.HttpUtility.HtmlEncode(content.DisplayName ?? content.Id)}</h2>"));

        // Form fields
        var formStyle = "display: grid; grid-template-columns: 150px 1fr; gap: 12px; align-items: center; margin-bottom: 12px;";

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
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(descriptionDataId) }));

        // Icon Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Icon Name:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., Document, Folder...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(iconNameDataId) }));

        // Display Order
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Display Order:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("0")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(displayOrderDataId) }));

        // Children Query
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Children Query:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Query for children (e.g., nodeType:Person)")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(childrenQueryDataId) }));

        // Dependencies
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Html("<label style=\"font-weight: 500;\">Dependencies:</label>"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Comma-separated node type paths...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(dependenciesDataId) }));

        // Configuration (code editor)
        stack = stack.WithView(Controls.Html("<h3 style=\"margin: 24px 0 8px 0;\">Configuration</h3>"));
        stack = stack.WithView(Controls.Html("<p style=\"color: #666; margin-bottom: 8px;\">Lambda expression: <code>config => config.AddData(...)</code></p>"));

        var editor = new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight("250px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            .WithPlaceholder("config => config");

        if (!string.IsNullOrEmpty(allCodeForAutocomplete))
        {
            editor = editor.WithExtraTypeDefinitions(allCodeForAutocomplete);
        }

        editor = editor with
        {
            DataContext = LayoutAreaReference.GetDataPointer(configurationDataId),
            Value = new JsonPointerReference("")
        };

        stack = stack.WithView(editor);

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 16px;");

        // Save button - update workspace stream
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
            {
                // Get all field values
                var displayName = await host.Stream.GetDataStream<string>(displayNameDataId).FirstAsync();
                var description = await host.Stream.GetDataStream<string>(descriptionDataId).FirstAsync();
                var iconName = await host.Stream.GetDataStream<string>(iconNameDataId).FirstAsync();
                var displayOrderStr = await host.Stream.GetDataStream<string>(displayOrderDataId).FirstAsync();
                var childrenQuery = await host.Stream.GetDataStream<string>(childrenQueryDataId).FirstAsync();
                var dependenciesStr = await host.Stream.GetDataStream<string>(dependenciesDataId).FirstAsync();
                var configuration = await host.Stream.GetDataStream<string>(configurationDataId).FirstAsync();

                // Parse display order
                if (!int.TryParse(displayOrderStr, out var displayOrder))
                    displayOrder = 0;

                // Parse dependencies
                List<string>? dependencies = null;
                if (!string.IsNullOrWhiteSpace(dependenciesStr))
                {
                    dependencies = dependenciesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (dependencies.Count == 0)
                        dependencies = null;
                }

                // Update the NodeTypeDefinition with all properties
                var updatedDefinition = content with
                {
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description,
                    IconName = string.IsNullOrWhiteSpace(iconName) ? null : iconName,
                    DisplayOrder = displayOrder,
                    ChildrenQuery = string.IsNullOrWhiteSpace(childrenQuery) ? null : childrenQuery,
                    Dependencies = dependencies,
                    Configuration = string.IsNullOrWhiteSpace(configuration) ? null : configuration
                };

                using var cts = new CancellationTokenSource(10.Seconds());
                var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                    new DataChangeRequest().WithUpdates(updatedDefinition),
                    o => o.WithTarget(hubAddress),
                    cts.Token);

                if (response.Message.Log.Status != ActivityStatus.Succeeded)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error saving:**\n\n{response.Message.Log}"),
                        "Save Failed"
                    ).WithSize("M");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

                // Navigate back to view
                var viewHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
                actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref));
            }));

        // Cancel button
        var viewHref = new LayoutAreaReference(CodeViewArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(actx => actx.Host.UpdateArea(actx.Area, new RedirectControl(viewHref))));

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
