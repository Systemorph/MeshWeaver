using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
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
        // Caller's effective permissions: the Run button only renders when
        // Execute is granted. Live SecurityService observable — re-emits when
        // assignments change.
        var permissionStream = host.Hub.GetEffectivePermissions(hubPath);

        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(permissionStream, (node, perms) => (UiControl?)BuildContent(host, node, perms));
    }

    private static UiControl BuildContent(LayoutAreaHost host, MeshNode? node, Permission callerPermissions = Permission.All)
    {
        var hubAddress = host.Hub.Address;
        var codeConfig = node?.Content as CodeConfiguration;
        var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        var title = node?.Name ?? node?.Id ?? "Code";
        var isExecutable = codeConfig?.IsExecutable == true;
        var canExecute = callerPermissions.HasFlag(Permission.Execute);

        // Header: title gets flex:1 so the action group is pushed hard-right.
        // (justify-content: space-between alone wasn't doing it because the
        // H1 has its own intrinsic width and the actions were docking
        // immediately after it.)
        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; align-items: center; margin-left: auto;");

        if (isExecutable)
        {
            // Always render Run when the node is executable. The server-side
            // ExecuteScriptRequest handler enforces Permission.Execute — clients
            // without it get back a DeliveryFailure (Unauthorized). Hiding the
            // button client-side hid it even from admins when the live
            // permission stream had a transient empty emission, which is exactly
            // the state we just spent a session debugging.
            actions = actions.WithView(Controls.Button("Run")
                .WithIconStart(FluentIcons.Play())
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    ctx.Host.Hub.Post(
                        new ExecuteScriptRequest(),
                        o => o.WithTarget(hubAddress));
                    return Task.CompletedTask;
                }));
        }

        actions = actions.WithView(Controls.Button("")
            .WithIconStart(FluentIcons.Edit())
            .WithAppearance(Appearance.Accent)
            .WithNavigateToHref(new LayoutAreaReference(EditArea).ToHref(hubAddress)));

        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; align-items: center; gap: 12px; margin-bottom: 16px;")
            .WithView(Controls.H1(title).WithStyle("flex: 1; margin: 0;"))
            .WithView(actions);

        stack = stack.WithView(headerRow);

        // Language + last-executed (when, by whom). No <a href> for activity
        // history — the dedicated activities list area below replaces it.
        var language = codeConfig?.Language ?? "csharp";
        var infoBits = new List<string>
        {
            $"Language: <span style=\"font-family: monospace;\">{System.Net.WebUtility.HtmlEncode(language)}</span>",
        };
        if (isExecutable)
        {
            if (codeConfig?.LastExecutedAt is { } lastRun)
            {
                var when = $"<span title=\"{lastRun:O}\">{lastRun:g} UTC</span>";
                var by = !string.IsNullOrEmpty(codeConfig.LastExecutedBy)
                    ? $" by <strong>{System.Net.WebUtility.HtmlEncode(codeConfig.LastExecutedBy)}</strong>"
                    : "";
                infoBits.Add($"Last executed: {when}{by}");
            }
            else
            {
                infoBits.Add("<span style=\"font-style: italic;\">Never executed</span>");
            }
        }
        var infoLineHtml =
            "<div style=\"display: flex; align-items: baseline; gap: 16px; " +
            "color: var(--neutral-foreground-hint); margin-bottom: 16px; font-size: 0.85rem;\">" +
            string.Join("", infoBits.Select(b => $"<span>{b}</span>")) +
            "</div>";
        stack = stack.WithView(Controls.Html(infoLineHtml));

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

        if (isExecutable)
        {
            // Output: the LATEST activity's Progress area (log + inline cancel).
            // No more polling a kernel/* address that may not exist — the
            // activity hub serves its own Progress view, so the pane shows
            // historical output immediately on page load.
            stack = stack.WithView(Controls.Html("<h3 style=\"margin-top: 32px;\">Output</h3>"));
            if (!string.IsNullOrEmpty(codeConfig?.LastActivityPath))
            {
                stack = stack.WithView(new LayoutAreaControl(
                        new Address(codeConfig.LastActivityPath!),
                        new LayoutAreaReference(ActivityLayoutAreas.ProgressArea))
                    .WithStyle("margin-top: 8px; padding: 12px; background: var(--neutral-layer-3); border-radius: 4px; min-height: 48px;"));
            }
            else
            {
                stack = stack.WithView(Controls.Html(
                    "<div style=\"margin-top: 8px; padding: 12px; background: var(--neutral-layer-3); " +
                    "border-radius: 4px; color: var(--neutral-foreground-hint); font-style: italic;\">" +
                    "Click <strong>Run</strong> to see output here.</div>"));
            }

            // Activity history: a real searchable list of past runs scoped to
            // this Code node's activity namespace. Replaces the dead
            // /{path}/_activity/* deep-link.
            var activityNamespace = ResolveActivityNamespace(hubAddress, codeConfig);
            stack = stack.WithView(Controls.Html("<h3 style=\"margin-top: 32px;\">Activity history</h3>"));
            stack = stack.WithView(Controls.MeshSearch
                .WithNamespace(activityNamespace)
                .WithHiddenQuery($"nodeType:Activity")
                .WithPlaceholder("Search past runs…")
                .WithExcludeBasePath(true));
        }

        return stack;
    }

    /// <summary>
    /// Mirror of the resolution rule in
    /// <c>CodeNodeType.HandleExecuteScript</c>: where activities for this Code
    /// node are written. Used by the Content view so the activity-history
    /// search points at the same place the runs land.
    /// </summary>
    private static string ResolveActivityNamespace(Address hubAddress, CodeConfiguration? code)
    {
        var partitionRoot = hubAddress.Segments.Length > 0 ? hubAddress.Segments[0] : hubAddress.Path;
        var parent = code?.ActivityParentPath switch
        {
            null => partitionRoot,
            "{viewer}" => partitionRoot, // viewer-specific runs spread across partitions; show the source partition
            var p => p
        };
        return $"{parent}/_Activity";
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

        // OWN node stream for the NavMenu highlight (canonical MeshNodeReference reducer).
        var ownNodeStream = host.Workspace.GetMeshNodeStream();

        // Observe sibling Code nodes reactively
        host.UpdateData(SiblingNodesDataId, Array.Empty<MeshNode>());

        if (meshQuery != null)
        {
            meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
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
                    .CombineLatest(ownNodeStream)
                    .Select(tuple =>
                    {
                        var (siblings, currentNode) = tuple;
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
                var label = sibling.Name ?? sibling.Id;
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
        var nodeName = host.Hub.Address.Id ?? "";

        host.UpdateData(codeDataId, initialCode);
        host.UpdateData(displayNameDataId, nodeName);

        // Header
        stack = stack.WithView(Controls.H2($"Edit: {nodeName}")
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

        // Monaco editor. LSP opt-in: when this Code node sits under a NodeType's Source/
        // subtree, enable live Roslyn diagnostics (Stage-1 IMeshLanguageService). The
        // Edit view's hub address IS the Code MeshNode path, so we can derive both the
        // NodeType path and the source path from it. For Code nodes outside a Source/
        // subtree (rare — typically standalone scripts), skip LSP wiring.
        var sourcePath = host.Hub.Address.ToString();
        var sourceMarkerIdx = sourcePath.IndexOf("/Source/", StringComparison.Ordinal);
        CodeEditorLanguageServerConfig? lspConfig = sourceMarkerIdx > 0 && language == "csharp"
            ? new CodeEditorLanguageServerConfig(
                NodeTypePath: sourcePath.Substring(0, sourceMarkerIdx),
                SourcePath: sourcePath)
            : null;

        var editor = new CodeEditorControl()
            .WithLanguage(language)
            .WithHeight("500px")
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true);

        editor = editor with
        {
            DataContext = LayoutAreaReference.GetDataPointer(codeDataId),
            Value = new JsonPointerReference(""),
            LanguageServer = lspConfig,
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

        // Save button — sync click action; subscribes to the form snapshot then posts.
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(actx =>
            {
                host.Stream.GetDataStream<string>(codeDataId)
                    .Take(1)
                    .Subscribe(currentCode =>
                    {
                        var updatedCodeConfiguration = codeConfig with { Code = currentCode };
                        var delivery = actx.Host.Hub.Post(
                            new DataChangeRequest { ChangedBy = actx.Host.Stream.ClientId }.WithUpdates(updatedCodeConfiguration),
                            o => o.WithTarget(hubAddress))!;
                        actx.Host.Hub.Observe(delivery).Subscribe(
                            callbackResponse =>
                            {
                                if (callbackResponse.Message is not DataChangeResponse responseMsg)
                                {
                                    var errorDialog = Controls.Dialog(
                                        Controls.Markdown($"**Error saving code:** Unexpected response `{callbackResponse.Message?.GetType().Name ?? "null"}`."),
                                        "Save Failed"
                                    ).WithSize("M");
                                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                                    return;
                                }
                                if (responseMsg.Log.Status != ActivityStatus.Succeeded)
                                {
                                    var errorDialog = Controls.Dialog(
                                        Controls.Markdown($"**Error saving code:**\n\n{responseMsg.Log}"),
                                        "Save Failed"
                                    ).WithSize("M");
                                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                                    return;
                                }
                                var overviewHref = new LayoutAreaReference(OverviewArea).ToHref(hubAddress);
                                actx.Host.UpdateArea(actx.Area, new RedirectControl(overviewHref));
                            },
                            ex =>
                            {
                                var errorDialog = Controls.Dialog(
                                    Controls.Markdown($"**Error saving code:**\n\n{ex.Message}"),
                                    "Save Failed"
                                ).WithSize("M");
                                actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                            });
                    });
                return Task.CompletedTask;
            }));

        stack = stack.WithView(buttonRow);

        return stack;
    }
}
