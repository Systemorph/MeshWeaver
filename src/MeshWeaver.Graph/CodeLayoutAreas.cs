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
    /// <summary>Area name for the Content layout area.</summary>
    public const string ContentArea = "Content";
    /// <summary>Area name for the Overview layout area.</summary>
    public const string OverviewArea = "Overview";
    /// <summary>Area name for the Edit layout area.</summary>
    public const string EditArea = "Edit";

    /// <summary>Area id of the notebook-cell frame inside the Content area.</summary>
    public const string CellArea = "CodeCell";
    /// <summary>Area id of the cell toolbar (Run / Cancel / Edit + metadata) inside the cell frame.</summary>
    public const string CellToolbarArea = "CellToolbar";
    /// <summary>Area id of the code segment inside the cell frame.</summary>
    public const string CellCodeArea = "CellCode";
    /// <summary>Area id of the output segment (last run's Progress embed) inside the cell frame.</summary>
    public const string CellOutputArea = "CellOutput";
    /// <summary>Area id of the Run button inside the cell toolbar.</summary>
    public const string RunButtonArea = "Run";
    /// <summary>Area id of the Cancel button inside the cell toolbar.</summary>
    public const string CancelButtonArea = "Cancel";
    /// <summary>Area id of the Edit button inside the cell toolbar.</summary>
    public const string EditButtonArea = "Edit";

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
    /// Renders the Content area as a notebook cell (Jupyter-style):
    /// one framed block whose top edge carries the cell toolbar (Run / Cancel /
    /// Edit + language and last-run metadata), the code beneath it, and the last
    /// run's output attached directly below the code inside the same frame.
    /// This is the default area, used when embedding via LayoutAreaControl with
    /// empty reference (e.g. @@path embeds in markdown pages).
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
    {
        var nodeStream = host.Workspace.GetMeshNodeStream();

        // The LAST run's live ActivityLog: keyed off the node's LastActivityPath
        // and re-switched whenever a new run stamps a fresh path. Drives the cell
        // toolbar's Cancel visibility reactively — the toolbar re-renders when the
        // activity transitions Running → terminal. DistinctUntilChanged on the
        // (Status, RequestedStatus) pair keeps per-log-message emissions from
        // re-rendering the whole Content area (the output pane is a live
        // LayoutAreaControl embed and streams its own messages).
        var lastActivityStream = nodeStream
            .Select(node => node.ContentAs<CodeConfiguration>(host.Hub.JsonSerializerOptions)?.LastActivityPath)
            .DistinctUntilChanged()
            .Select(path => string.IsNullOrEmpty(path)
                ? Observable.Return<ActivityLog?>(null)
                : host.Workspace.GetMeshNodeStream(path!)
                    .Select(n => n.ContentAs<ActivityLog>(host.Hub.JsonSerializerOptions)))
            .Switch()
            .DistinctUntilChanged(log => (log?.Status, log?.RequestedStatus))
            .StartWith((ActivityLog?)null);

        return nodeStream.CombineLatest(lastActivityStream,
            (node, lastActivity) => (UiControl?)BuildContent(host, node, lastActivity));
    }

    private static UiControl BuildContent(LayoutAreaHost host, MeshNode? node, ActivityLog? lastActivity)
    {
        var hubAddress = host.Hub.Address;
        var codeConfig = node.ContentAs<CodeConfiguration>(host.Hub.JsonSerializerOptions);
        var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        var title = node?.Name ?? node?.Id ?? "Code";
        var isExecutable = codeConfig?.IsExecutable == true;
        var language = codeConfig?.Language ?? "csharp";

        // Page header: title only. Run/Cancel/Edit live in the cell toolbar
        // below — ONE source of truth for the notebook controls, no second Run
        // stranded in the page header far away from the output it drives.
        stack = stack.WithView(Controls.H1(title).WithStyle("margin: 0 0 16px 0;"));

        // ── Notebook cell ────────────────────────────────────────────────────
        // One visually framed block: toolbar on the top edge, code beneath it,
        // output attached directly under the code inside the same frame.
        var cell = Controls.Stack
            .WithWidth("100%")
            .WithStyle("border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; " +
                       "overflow: hidden; background: var(--neutral-layer-1);");

        cell = cell.WithView(
            BuildCellToolbar(hubAddress, codeConfig, isExecutable, language, lastActivity),
            CellToolbarArea);

        // Code segment (markdown fence rendering, unchanged).
        if (!string.IsNullOrEmpty(codeConfig?.Code))
        {
            cell = cell.WithView(Controls.Markdown($"```{language}\n{codeConfig.Code}\n```")
                    .WithStyle("width: 100%; overflow: auto; padding: 0 12px;"),
                CellCodeArea);
        }
        else
        {
            cell = cell.WithView(Controls.Body("No code defined.")
                    .WithStyle("display: block; padding: 12px; color: var(--neutral-foreground-hint); font-style: italic;"),
                CellCodeArea);
        }

        if (isExecutable)
        {
            // Output segment: the LATEST activity's Progress area (log + status
            // badge), directly beneath the code INSIDE the cell frame so the
            // Run button and its result are visually one unit. Jupyter-esque
            // left accent + thin separator mark it as the cell's output.
            const string outputStyle =
                "border-top: 1px solid var(--neutral-stroke-rest); " +
                "border-left: 3px solid var(--accent-fill-rest); " +
                "background: var(--neutral-layer-2); padding: 10px 12px;";
            if (!string.IsNullOrEmpty(codeConfig?.LastActivityPath))
            {
                cell = cell.WithView(new LayoutAreaControl(
                            new Address(codeConfig.LastActivityPath!),
                            new LayoutAreaReference(ActivityLayoutAreas.ProgressArea))
                        .WithStyle(outputStyle),
                    CellOutputArea);
            }
            else
            {
                // Not yet run: a one-line subtle hint, not a large empty pane.
                cell = cell.WithView(Controls.Body("Not yet run.")
                        .WithStyle($"display: block; {outputStyle} " +
                                   "color: var(--neutral-foreground-hint); font-style: italic; font-size: 0.85rem;"),
                    CellOutputArea);
            }
        }

        stack = stack.WithView(cell, CellArea);

        // No activity history below the cell (removed 2026-07-02 on UX feedback:
        // it reads as noise under a notebook cell — the run's own output is the
        // record that matters here). Past runs remain reachable through the
        // owner's activity feed.

        return stack;
    }

    /// <summary>
    /// The cell's toolbar: ▶ Run (accent), ⏹ Cancel (only while the last run is
    /// actually running and no cancel is already in flight — the shared
    /// <see cref="ActivityLayoutAreas.IsCancelButtonVisible"/> predicate), ✎ Edit,
    /// then subtle right-aligned metadata (language badge, last-run provenance).
    /// </summary>
    private static UiControl BuildCellToolbar(
        Address hubAddress,
        CodeConfiguration? codeConfig,
        bool isExecutable,
        string language,
        ActivityLog? lastActivity)
    {
        var toolbar = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; align-items: center; gap: 8px; padding: 6px 10px; " +
                       "background: var(--neutral-layer-2); border-bottom: 1px solid var(--neutral-stroke-rest);");

        if (isExecutable)
        {
            // Always render Run when the node is executable. The server-side
            // ExecuteScriptRequest handler enforces Permission.Execute — clients
            // without it get back a DeliveryFailure (Unauthorized). Hiding the
            // button client-side hid it even from admins when the live
            // permission stream had a transient empty emission, which is exactly
            // the state we once spent a session debugging.
            toolbar = toolbar.WithView(Controls.Button("Run")
                    .WithIconStart(FluentIcons.Play())
                    .WithAppearance(Appearance.Accent)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.Hub.Post(
                            new ExecuteScriptRequest(),
                            o => o.WithTarget(hubAddress));
                        return Task.CompletedTask;
                    }),
                RunButtonArea);

            // Cancel: classic notebook stop control, attached to the same
            // toolbar as Run. Per the Activity Control Plane pattern the click
            // patches RequestedStatus = Cancelled on the activity node via the
            // canonical hub.CancelActivity extension — no bespoke request type.
            var lastActivityPath = codeConfig?.LastActivityPath;
            if (!string.IsNullOrEmpty(lastActivityPath)
                && lastActivity is not null
                && ActivityLayoutAreas.IsCancelButtonVisible(lastActivity))
            {
                toolbar = toolbar.WithView(Controls.Button("Cancel")
                        .WithIconStart(FluentIcons.Stop())
                        .WithClickAction(ctx =>
                        {
                            ctx.Host.Hub.CancelActivity(lastActivityPath!);
                            return Task.CompletedTask;
                        }),
                    CancelButtonArea);
            }
        }

        toolbar = toolbar.WithView(Controls.Button("")
                .WithIconStart(FluentIcons.Edit())
                .WithAppearance(Appearance.Accent)
                .WithNavigateToHref(new LayoutAreaReference(EditArea).ToHref(hubAddress)),
            EditButtonArea);

        // Right-aligned, subtle metadata: language badge + last-run provenance.
        var meta = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; align-items: baseline; gap: 12px; margin-left: auto; " +
                       "color: var(--neutral-foreground-hint); font-size: 0.8rem;");
        meta = meta.WithView(Controls.Body(language)
            .WithStyle("font-family: monospace; padding: 1px 8px; " +
                       "border: 1px solid var(--neutral-stroke-rest); border-radius: 10px;"));
        if (isExecutable)
        {
            var lastRunText = codeConfig?.LastExecutedAt is { } lastRun
                ? $"last run {lastRun.Humanize()}"
                  + (string.IsNullOrEmpty(codeConfig.LastExecutedBy) ? "" : $" by {codeConfig.LastExecutedBy}")
                : "never executed";
            meta = meta.WithView(Controls.Body(lastRunText).WithStyle("font-style: italic;"));
        }
        toolbar = toolbar.WithView(meta);

        return toolbar;
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

        // Same side-menu splitter treatment as the NodeType shell: panes scroll
        // independently, height fills the layout-area container (no viewport math).
        return Controls.Splitter
            .WithClass("shell-splitter")
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("100%"))
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
