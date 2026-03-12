using System.ComponentModel;
using System.Reactive.Linq;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for Code nodes.
/// - Content (default): Read-only code display as markdown code block
/// - Overview: Splitter with sibling code list and embedded content view
/// - Edit: Monaco editor with language support
/// </summary>
public static class CodeLayoutAreas
{
    public const string ContentArea = "Content";
    public const string OverviewArea = "Overview";
    public const string EditArea = "Edit";

    private const string CodeDataId = "code";
    private const string SiblingNodesDataId = "siblingCodeNodes";

    /// <summary>
    /// Adds the Code views to the hub's layout for Code nodes.
    /// Default area is Content (simple markdown code block) so that
    /// LayoutAreaControl(address, new LayoutAreaReference("")) renders the simple view
    /// without recursion when embedded in the Overview Splitter.
    /// </summary>
    public static MessageHubConfiguration AddCodeViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(ContentArea)
            .WithView(ContentArea, Content)
            .WithView(OverviewArea, Overview)
            .WithView(EditArea, Edit)
            .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Renders the Content area showing code in a markdown code block.
    /// This is the default area, used when embedding via LayoutAreaControl with empty reference.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return (UiControl?)BuildContent(host, node);
        });
    }

    private static UiControl BuildContent(LayoutAreaHost host, MeshNode? node)
    {
        var hubAddress = host.Hub.Address;
        var codeConfig = node?.Content as CodeConfiguration;
        var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        // Header with title and edit button
        var title = node?.Name ?? codeConfig?.DisplayName ?? codeConfig?.Id ?? "Code";
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.H1(title))
            .WithView(Controls.Button("")
                .WithIconStart(FluentIcons.Edit())
                .WithAppearance(Appearance.Accent)
                .WithNavigateToHref(new LayoutAreaReference(EditArea).ToHref(hubAddress)));

        stack = stack.WithView(headerRow);

        // Language badge
        var language = codeConfig?.Language ?? "csharp";
        stack = stack.WithView(Controls.Body($"Language: {language}")
            .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 16px;"));

        // Code block
        if (!string.IsNullOrEmpty(codeConfig?.Code))
        {
            stack = stack.WithView(Controls.Markdown($"```{language}\n{codeConfig.Code}\n```")
                .WithStyle("width: 100%; overflow: auto;"));
        }
        else
        {
            stack = stack.WithView(Controls.Body("No code defined.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic;"));
        }

        return stack;
    }

    /// <summary>
    /// Renders the Overview area as a Splitter with a left NavMenu listing sibling Code nodes
    /// and a right pane embedding this node's Content via LayoutAreaControl.
    /// </summary>
    [Browsable(false)]
    public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubAddress = host.Hub.Address;
        var hubPath = hubAddress.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();

        // Derive the parent NodeType path by stripping the last two segments (Code/{id})
        var segments = hubPath.Split('/');
        var parentPath = segments.Length >= 3
            ? string.Join("/", segments.Take(segments.Length - 2))
            : hubPath;

        // Get current node stream for the NavMenu highlight
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        // Observe sibling Code nodes reactively
        host.UpdateData(SiblingNodesDataId, Array.Empty<MeshNode>());

        if (meshQuery != null)
        {
            meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{parentPath} nodeType:{CodeNodeType.NodeType} scope:descendants"))
                .Scan(new List<MeshNode>(), (list, change) =>
                {
                    if (change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset)
                        return change.Items.ToList();
                    foreach (var item in change.Items)
                    {
                        if (change.ChangeType == QueryChangeType.Added)
                            list.Add(item);
                        else if (change.ChangeType == QueryChangeType.Removed)
                            list.RemoveAll(n => n.Path == item.Path);
                        else if (change.ChangeType == QueryChangeType.Updated)
                        {
                            list.RemoveAll(n => n.Path == item.Path);
                            list.Add(item);
                        }
                    }
                    return list;
                })
                .Subscribe(codeNodes => host.UpdateData(SiblingNodesDataId, codeNodes.ToArray()));
        }

        var siblingStream = host.Stream.GetDataStream<MeshNode[]>(SiblingNodesDataId);

        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                // Left pane: NavMenu listing sibling Code nodes
                (h, c) => siblingStream
                    .CombineLatest(nodeStream)
                    .Select(tuple =>
                    {
                        var (siblings, nodes) = tuple;
                        var currentNode = nodes?.FirstOrDefault(n => n.Path == hubPath);
                        return BuildCodeNavMenu(hubAddress, hubPath, currentNode, siblings);
                    }),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                // Right pane: embed this node's Content (default area) via LayoutAreaControl
                new LayoutAreaControl(hubAddress, new LayoutAreaReference("")),
                skin => skin.WithSize("*")
            );
    }

    /// <summary>
    /// Builds the left NavMenu for the Overview Splitter showing sibling Code nodes.
    /// </summary>
    private static UiControl BuildCodeNavMenu(
        object hubAddress,
        string currentPath,
        MeshNode? currentNode,
        IReadOnlyCollection<MeshNode>? siblings)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        var codeGroup = new NavGroupControl("Code Files")
            .WithIcon(FluentIcons.Code())
            .WithSkin(s => s.WithExpanded(true));

        if (siblings != null && siblings.Count > 0)
        {
            foreach (var sibling in siblings)
            {
                var codeConfig = sibling.Content as CodeConfiguration;
                var label = sibling.Name ?? codeConfig?.DisplayName ?? sibling.Id;
                var siblingHref = new LayoutAreaReference(OverviewArea).ToHref(sibling.Path);
                codeGroup = codeGroup.WithView(
                    new NavLinkControl(label, CustomIcons.CSharp(), siblingHref)
                );
            }
        }
        else
        {
            codeGroup = codeGroup.WithView(
                Controls.Body("No code files").WithStyle("padding: 4px 16px; display: block; color: var(--neutral-foreground-hint);")
            );
        }

        navMenu = navMenu.WithNavGroup(codeGroup);

        return navMenu;
    }

    /// <summary>
    /// Renders the Monaco editor for editing code.
    /// </summary>
    [Browsable(false)]
    public static UiControl Edit(LayoutAreaHost host, RenderingContext ctx)
    {
        host.SubscribeToDataStream(CodeDataId, host.Workspace.GetNodeContent<CodeConfiguration>());

        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<CodeConfiguration>(CodeDataId)
                    .Select(codeConfig =>
                    {
                        if (codeConfig == null)
                            return (UiControl)Controls.Progress("Loading code...", 0);
                        return BuildEditContent(host, codeConfig);
                    }),
                "Editor"
            );
    }

    private static UiControl BuildEditContent(LayoutAreaHost host, CodeConfiguration codeConfig)
    {
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        var codeDataId = Guid.NewGuid().AsString();
        var displayNameDataId = Guid.NewGuid().AsString();

        var initialCode = codeConfig.Code ?? "";
        var language = codeConfig.Language ?? "csharp";
        var displayName = codeConfig.DisplayName ?? "";

        host.UpdateData(codeDataId, initialCode);
        host.UpdateData(displayNameDataId, displayName);

        // Header
        stack = stack.WithView(Controls.H2($"Edit: {codeConfig.DisplayName ?? codeConfig.Id}")
            .WithStyle("margin-bottom: 16px;"));

        // DisplayName field
        var displayNameRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.Label("Display Name:").WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter display name...")
                .WithStyle("flex: 1; max-width: 400px;")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(displayNameDataId) });

        stack = stack.WithView(displayNameRow);

        // Monaco editor
        var editor = new CodeEditorControl()
            .WithLanguage(language)
            .WithHeight("500px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true);

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

        // Cancel button
        var viewHref = new LayoutAreaReference(OverviewArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(viewHref));

        // Save button
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
            {
                var currentCode = await host.Stream.GetDataStream<string>(codeDataId).FirstAsync();
                var currentDisplayName = await host.Stream.GetDataStream<string>(displayNameDataId).FirstAsync();

                var updatedCodeConfiguration = codeConfig with
                {
                    Code = currentCode,
                    DisplayName = string.IsNullOrWhiteSpace(currentDisplayName) ? null : currentDisplayName
                };

                using var cts = new CancellationTokenSource(10.Seconds());
                var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                    new DataChangeRequest { ChangedBy = actx.Host.Stream.ClientId }.WithUpdates(updatedCodeConfiguration),
                    o => o.WithTarget(hubAddress),
                    cts.Token);

                if (response.Message.Log.Status != ActivityStatus.Succeeded)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error saving code:**\n\n{response.Message.Log}"),
                        "Save Failed"
                    ).WithSize("M");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

                // Navigate back to overview
                var overviewHref = new LayoutAreaReference(OverviewArea).ToHref(hubAddress);
                actx.Host.UpdateArea(actx.Area, new RedirectControl(overviewHref));
            }));

        stack = stack.WithView(buttonRow);

        return stack;
    }
}
