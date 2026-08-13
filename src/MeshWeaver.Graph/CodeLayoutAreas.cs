using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
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
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for Code nodes.
/// - Content (default): the notebook cell. For a viewer holding Update the code segment IS an
///   inline Monaco editor (edit mode is the mode — no Edit button, auto-saved, Run persists the
///   buffer first); for everyone else it is the read-only markdown code block.
/// - Overview: Splitter with sibling code list and embedded content view
/// - Edit: Monaco editor with language support (kept for deep links and metadata edits)
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
    /// <summary>Area id of the "code changed — re-run" chip inside the cell toolbar.</summary>
    public const string StaleChipArea = "StaleChip";

    /// <summary>Area id of the toolbar's execution-state chip (Running… / ✓ Done / ✗ Failed).</summary>
    public const string StatusChipArea = "StatusChip";
    /// <summary>Area id of the Cancel button inside the cell toolbar.</summary>
    public const string CancelButtonArea = "Cancel";
    /// <summary>Area id of the Edit button inside the cell toolbar.</summary>
    public const string EditButtonArea = "Edit";
    /// <summary>Area id of the Cancel button inside the copy-to-home dialog.</summary>
    public const string CopyDialogCancelArea = "CopyDialogCancel";
    /// <summary>Area id of the Confirm button inside the copy-to-home dialog.</summary>
    public const string CopyDialogConfirmArea = "CopyDialogConfirm";

    /// <summary>
    /// Data id of the EDIT-MODE cell's code buffer — what the inline editor binds. Seeded ONCE
    /// per rendered area from the node's stored code (see <see cref="Content"/>) and written by
    /// the editor from then on; Run snapshots it so the kernel always executes what the viewer
    /// sees.
    /// </summary>
    public const string CellBufferDataId = "cellCode";

    private const string CodeDataId = "code";
    private const string SiblingNodesDataId = "siblingCodeNodes";

    /// <summary>
    /// Whether the cell's output pane is showing the result of code that has since been EDITED.
    /// True only when we can prove it: the node must have run (<c>LastExecutedAt</c>) AND carry the
    /// fingerprint of what that run submitted. An absent hash means "unknown" — a node last executed
    /// before <see cref="CodeConfiguration.LastExecutedCodeHash"/> existed — and unknown must read as
    /// NOT stale, or every legacy Code node on every mesh would light up amber at once.
    /// <para>Pure and static so the staleness rule is unit-testable without a hub.</para>
    /// </summary>
    internal static bool IsOutputStale(CodeConfiguration? code) =>
        code is { LastExecutedAt: not null, LastExecutedCodeHash: not null and not "" }
        && CodeFingerprint.Of(code.Code, code.Language) != code.LastExecutedCodeHash;

    /// <summary>
    /// The Run button's glyph: a re-run arrow once the cell is stale, the play triangle otherwise.
    /// Only the GLYPH changes — the label stays "Run", because that word is how both readers and
    /// every e2e suite find the control.
    /// </summary>
    internal static Icon RunGlyph(bool isStale) =>
        isStale ? FluentIcons.ArrowSync() : FluentIcons.Play();

    /// <summary>
    /// Languages a Code node can be authored in. C# runs in-process on the Roslyn kernel; Python routes
    /// to a connected <c>py/python-kernel</c> worker participant (see <c>CodeNodeType.HandleExecuteScript</c>);
    /// the rest are first-class for authoring / syntax highlighting / display. Immutable constant lookup.
    /// </summary>
    private static readonly string[] LanguageOptions =
        { "csharp", "python", "typescript", "javascript", "json", "sql", "markdown" };

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

        // The VIEWER's effective permissions on this node — the canonical reactive
        // check (hub.GetEffectivePermissions; resolves the caller from the ambient
        // AccessContext at call time, same as GitHubSyncSettingsTab / CopyLayoutArea).
        // Drives the Edit button's shape: Update permission → direct edit
        // navigation; no Update → the copy-to-home dialog trigger.
        var permissionStream = host.Hub
            .GetEffectivePermissions(host.Hub.Address.ToString())
            .DistinctUntilChanged();

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

        // The edit-mode buffer: seeded ONCE from the first emission and never re-seeded — from
        // then on the EDITOR is the writer (its auto-save persists through the node stream
        // cache), and a save echo re-seeding the buffer would clobber whatever the viewer typed
        // since. The editor height is fixed from the same seed so the editor CONTROL stays
        // identical across re-renders: the client diff then never re-mounts Monaco (a re-mount
        // mid-typing loses cursor and scroll).
        var editorSetup = nodeStream
            .Where(n => n is not null)
            .Select(n => n.ContentAs<CodeConfiguration>(host.Hub.JsonSerializerOptions)?.Code ?? "")
            .Take(1)
            .Select(code =>
            {
                host.UpdateData(CellBufferDataId, code);
                return CellEditorHeight(code);
            })
            // Render is never held hostage to the seed: a defensive null-first emission (or a
            // ghost node) still paints the page with the floor height; the real seed follows.
            .StartWith(CellEditorHeight(null));

        return nodeStream.CombineLatest(lastActivityStream, permissionStream, editorSetup,
            (node, lastActivity, permissions, editorHeight) =>
                (UiControl?)BuildContent(host, node, lastActivity, permissions, editorHeight));
    }

    /// <summary>
    /// The inline editor's height for a cell seeded with <paramref name="code"/>: Monaco's 19px
    /// per line plus the frame chrome, clamped to [96, 480] px. Computed once from the SEED —
    /// a height that followed the text would change the editor control on every save and
    /// re-mount Monaco under the viewer's cursor. Pure.
    /// </summary>
    public static string CellEditorHeight(string? code)
    {
        var lines = string.IsNullOrEmpty(code) ? 1 : code!.Split('\n').Length;
        var px = Math.Clamp(lines * 19 + 20, 96, 480);
        return $"{px}px";
    }

    private static UiControl BuildContent(
        LayoutAreaHost host, MeshNode? node, ActivityLog? lastActivity, Permission permissions,
        string editorHeight)
    {
        var hubAddress = host.Hub.Address;
        var codeConfig = node.ContentAs<CodeConfiguration>(host.Hub.JsonSerializerOptions);
        var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        var title = node?.Name ?? node?.Id ?? "Code";
        var isExecutable = codeConfig?.IsExecutable == true;
        var language = codeConfig?.Language ?? "csharp";
        var canEdit = permissions.HasFlag(Permission.Update);

        // Page header: title only. Run/Cancel/Edit live in the cell toolbar
        // below — ONE source of truth for the notebook controls, no second Run
        // stranded in the page header far away from the output it drives.
        stack = stack.WithView(Controls.H1(title).WithStyle("margin: 0 0 16px 0;"));

        // ── Notebook cell ────────────────────────────────────────────────────
        // One visually framed block: code on top, the run's output attached
        // directly under it, and the toolbar as a composer-style bar on the
        // BOTTOM edge (2026-07-03 UX feedback: the controls belong at the foot
        // of the cell, like a chat composer, not above the code).
        var cell = Controls.Stack
            .WithWidth("100%")
            .WithStyle("border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; " +
                       "overflow: hidden; background: var(--neutral-layer-1);");

        // Code segment. A viewer who may edit gets the EDITOR — edit mode IS the mode, there is
        // no separate page behind a button. Everyone else keeps the read-only fence rendering.
        if (canEdit)
        {
            cell = cell.WithView(BuildCellEditor(hubAddress.ToString(), language, editorHeight),
                CellCodeArea);
        }
        else if (!string.IsNullOrEmpty(codeConfig?.Code))
        {
            cell = cell.WithView(Controls.Markdown($"```{language}\n{codeConfig.Code}\n```")
                    .WithStyle("width: 100%; overflow: auto; padding: 0 12px;"),
                CellCodeArea);
        }
        else
        {
            cell = cell.WithView(Controls.Body(host.Localize("ui.noCodeDefined"))
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
                cell = cell.WithView(Controls.Body(host.Localize("ui.notYetRun"))
                        .WithStyle($"display: block; {outputStyle} " +
                                   "color: var(--neutral-foreground-hint); font-style: italic; font-size: 0.85rem;"),
                    CellOutputArea);
            }
        }

        // Toolbar LAST — the composer bar at the bottom of the cell frame,
        // below the output segment.
        cell = cell.WithView(
            BuildCellToolbar(hubAddress, codeConfig, isExecutable, language, lastActivity, canEdit),
            CellToolbarArea);

        stack = stack.WithView(cell, CellArea);

        // No activity history below the cell (removed 2026-07-02 on UX feedback:
        // it reads as noise under a notebook cell — the run's own output is the
        // record that matters here). Past runs remain reachable through the
        // owner's activity feed.

        return stack;
    }

    /// <summary>
    /// The cell's toolbar — the composer bar on the BOTTOM edge of the cell:
    /// ▶ Run (accent), ⏹ Cancel (only while the last run is actually running and
    /// no cancel is already in flight — the shared
    /// <see cref="ActivityLayoutAreas.IsCancelButtonVisible"/> predicate),
    /// then subtle right-aligned metadata (language badge, last-run provenance).
    /// <para>A viewer WITH <see cref="Permission.Update"/> gets NO Edit button — their cell's
    /// code segment already IS the editor (see <see cref="BuildCellEditor"/>), and their Run
    /// persists the buffer before executing (<see cref="RunFromBuffer"/>). A read-only viewer
    /// keeps the Edit button, which opens the copy-to-home dialog
    /// (<see cref="OpenCopyToHomeDialog"/>) offering to copy the node into their own home space
    /// via the standard copy machinery.</para>
    /// </summary>
    internal static UiControl BuildCellToolbar(
        Address hubAddress,
        CodeConfiguration? codeConfig,
        bool isExecutable,
        string language,
        ActivityLog? lastActivity,
        bool canEdit, string? locale = null)
    {
        // Stale = the output pane above is showing a run of code that has since been edited. The
        // toolbar goes amber, matching the NodeType editor's "Source changed — needs compile" panel,
        // so "what you're looking at is out of date" reads the same way across the product.
        var isStale = isExecutable && IsOutputStale(codeConfig);
        var toolbar = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; align-items: center; gap: 8px; padding: 6px 10px; " +
                       (isStale
                           ? "background: var(--warning-fill-rest, #fef3c7); " +
                             "border-top: 1px solid var(--warning-stroke-rest, #fcd34d);"
                           : "background: var(--neutral-layer-2); " +
                             "border-top: 1px solid var(--neutral-stroke-rest);"));

        if (isExecutable)
        {
            // Always render Run when the node is executable. The server-side
            // ExecuteScriptRequest handler enforces Permission.Execute — clients
            // without it get back a DeliveryFailure (Unauthorized). Hiding the
            // button client-side hid it even from admins when the live
            // permission stream had a transient empty emission, which is exactly
            // the state we once spent a session debugging.
            // For an EDITOR the click persists the cell buffer FIRST: the kernel executes the
            // STORED code, the editor's auto-save is debounced, and running anything other than
            // what the viewer sees is the classic stale-cell trap.
            toolbar = toolbar.WithView(Controls.Button(LocalizationCatalog.Get("common.run", locale))
                    .WithIconStart(RunGlyph(isStale))
                    .WithAppearance(Appearance.Accent)
                    .WithClickAction(ctx =>
                    {
                        if (canEdit)
                            RunFromBuffer(ctx.Host, hubAddress);
                        else
                            ctx.Host.Hub.Post(
                                new ExecuteScriptRequest(),
                                o => o.WithTarget(hubAddress));
                        return Task.CompletedTask;
                    }),
                RunButtonArea);

            if (isStale)
                toolbar = toolbar.WithView(
                    Controls.Body(LocalizationCatalog.Get("code.staleCell", locale))
                        .WithStyle("font-size: 0.8rem; font-weight: 600; " +
                                   "color: var(--warning-foreground, #92400e);"),
                    StaleChipArea);

            // Cancel: classic notebook stop control, attached to the same
            // toolbar as Run. Per the Activity Control Plane pattern the click
            // patches RequestedStatus = Cancelled on the activity node via the
            // canonical hub.CancelActivity extension — no bespoke request type.
            var lastActivityPath = codeConfig?.LastActivityPath;
            if (!string.IsNullOrEmpty(lastActivityPath)
                && lastActivity is not null
                && ActivityLayoutAreas.IsCancelButtonVisible(lastActivity))
            {
                toolbar = toolbar.WithView(Controls.Button(LocalizationCatalog.Get("common.cancel", locale))
                        .WithIconStart(FluentIcons.Stop())
                        .WithClickAction(ctx =>
                        {
                            ctx.Host.Hub.CancelActivity(lastActivityPath!);
                            return Task.CompletedTask;
                        }),
                    CancelButtonArea);
            }
        }

        if (!canEdit)
        {
            // Read-only viewer: Edit opens the copy-to-home dialog (no NavigateToHref — the
            // click action drives the DialogControl area). Identity resolution + the actual
            // copy happen at CLICK time under the clicker's AccessContext.
            // An EDITOR gets no Edit button at all: their cell IS the editor (see
            // BuildContent) — there is no second mode to navigate to.
            var sourcePath = hubAddress.ToString();
            toolbar = toolbar.WithView(Controls.Button(LocalizationCatalog.Get("common.edit", locale))
                    .WithIconStart(FluentIcons.Edit())
                    .WithClickAction(ctx =>
                    {
                        OpenCopyToHomeDialog(ctx, sourcePath);
                        return Task.CompletedTask;
                    }),
                EditButtonArea);
        }

        // Right-aligned, subtle metadata: execution state, language badge, last-run provenance.
        var meta = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; align-items: baseline; gap: 12px; margin-left: auto; " +
                       "color: var(--neutral-foreground-hint); font-size: 0.8rem;");
        // The execution state lives HERE, in the toolbar — not as a heading above the output.
        // Running shows live progress in the pane; once terminal, the chip is what says the cell
        // is idle (✓ Done / ✗ Failed / …) so the output pane can show the result alone.
        if (isExecutable && CellStatusChip(lastActivity, locale) is { } statusChip)
            meta = meta.WithView(statusChip, StatusChipArea);
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
    /// The toolbar's execution-state chip — the ONE place that says whether the cell is actively
    /// executing: "Running…" (accent) while the last activity runs, the terminal status word with
    /// its glyph (✓ Done / ✗ Failed / ⚠ warnings / ⊘ Cancelled, coloured like the output pane)
    /// once it is idle, and <c>null</c> for a cell that was never run (the provenance text already
    /// says "never executed" — a second control saying the same would be noise). Pure — the
    /// vocabulary is <see cref="ActivityLayoutAreas.StatusGlyph"/>, shared with the output pane,
    /// so the two surfaces cannot drift apart.
    /// </summary>
    /// <param name="lastActivity">The cell's last run, or null when it never ran.</param>
    /// <param name="locale">Viewer locale for the status words; null falls back to English.</param>
    public static UiControl? CellStatusChip(ActivityLog? lastActivity, string? locale = null)
    {
        if (lastActivity is null)
            return null;

        if (lastActivity.Status == ActivityStatus.Running)
            return Controls.Body(LocalizationCatalog.Get("ui.running", locale))
                .WithStyle("font-weight: 600; color: var(--accent-fill-rest);");

        var (glyph, label) = ActivityLayoutAreas.StatusGlyph(lastActivity.Status, locale);
        return Controls.Body($"{glyph} {label}")
            .WithStyle($"font-weight: 600; color: {ActivityLayoutAreas.StatusColor(lastActivity.Status)};");
    }

    /// <summary>
    /// The EDIT-MODE code segment: an inline Monaco bound to the cell buffer (seeded once in
    /// <see cref="Content"/>), with Roslyn language services for C# and AUTO-SAVE back into this
    /// node — the circuit-side <c>IMeshNodeStreamCache</c> seam (<c>CodeEditorView</c>), so the
    /// debounced write carries the viewer's own identity. Every property is deterministic for a
    /// given node + seed, so re-renders (each auto-save echoes a node emission) produce an
    /// IDENTICAL control and the client diff leaves the live editor — and the cursor — alone.
    /// </summary>
    internal static UiControl BuildCellEditor(string sourcePath, string language, string editorHeight)
    {
        var editor = new CodeEditorControl()
            .WithLanguage(language)
            .WithHeight(editorHeight)
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            .WithAutoSave(sourcePath);
        if (language == "csharp")
            editor = editor.WithLanguageServer(OwnerPathOf(sourcePath), sourcePath);
        return editor with
        {
            DataContext = LayoutAreaReference.GetDataPointer(CellBufferDataId),
            Value = new JsonPointerReference(""),
        };
    }

    /// <summary>
    /// The compilation OWNER of a source path — the NodeType above <c>/Source/</c> when there is
    /// one (siblings in scope), else the parent (a standalone script cell — e.g. a course
    /// lesson's <c>{lesson}/Source/{cell}</c> — answers from the kernel's script environment).
    /// Shared by the inline cell editor and the Edit area. Pure.
    /// </summary>
    internal static string OwnerPathOf(string sourcePath)
    {
        var sourceMarkerIdx = sourcePath.IndexOf("/Source/", StringComparison.Ordinal);
        var lastSlashIdx = sourcePath.LastIndexOf('/');
        return sourceMarkerIdx > 0
            ? sourcePath.Substring(0, sourceMarkerIdx)
            : lastSlashIdx > 0 ? sourcePath.Substring(0, lastSlashIdx) : sourcePath;
    }

    /// <summary>
    /// Run for an EDITOR: persist the cell buffer FIRST, then execute. The kernel runs the
    /// STORED code and the editor's auto-save is debounced, so the freshest keystrokes may not
    /// have landed yet — executing without this save runs code the viewer is no longer looking
    /// at. The save mirrors the Edit area's (snapshot both, write only when they differ, post
    /// under the clicker's context); an unchanged buffer executes immediately.
    /// </summary>
    private static void RunFromBuffer(LayoutAreaHost host, Address hubAddress)
    {
        void Execute() => host.Hub.Post(new ExecuteScriptRequest(), o => o.WithTarget(hubAddress));
        void Fail(string detail) => host.UpdateArea(DialogControl.DialogArea, Controls.Dialog(
                Controls.Markdown($"**Save before run failed:**\n\n{detail}"), "Save Failed")
            .WithSize("M").WithClosable(true));

        host.Stream.GetDataStream<string>(CellBufferDataId).Take(1)
            .CombineLatest(host.Workspace.GetMeshNodeStream().Take(1), (buffer, node) => (buffer, node))
            .Take(1)
            .Subscribe(t =>
            {
                var config = t.node.ContentAs<CodeConfiguration>(host.Hub.JsonSerializerOptions);
                if (config is null || (config.Code ?? "") == (t.buffer ?? ""))
                {
                    Execute();
                    return;
                }
                var delivery = host.Hub.Post(
                    new DataChangeRequest { ChangedBy = host.Stream.ClientId }
                        .WithUpdates(config with { Code = t.buffer }),
                    o => o.WithTarget(hubAddress))!;
                host.Hub.Observe(delivery).Subscribe(
                    response =>
                    {
                        if (response.Message is DataChangeResponse { Log.Status: ActivityStatus.Succeeded })
                            Execute();
                        else
                            Fail((response.Message as DataChangeResponse)?.Log.ToString()
                                 ?? response.Message?.GetType().Name ?? "no response");
                    },
                    ex => Fail(ex.Message));
            });
    }

    /// <summary>
    /// Opens the read-only-viewer Edit dialog: explains the node is read-only for
    /// the current viewer and offers to copy it into their home space (Confirm /
    /// Cancel). Confirm runs the standard <see cref="NodeCopyHelper.CopyNodeTree"/>
    /// machinery into <c>{viewerHome}/{nodeId}</c> and navigates to the copy's
    /// Edit area. Anonymous / hub-shaped identities (no home partition) get a
    /// sign-in hint instead.
    /// </summary>
    private static void OpenCopyToHomeDialog(UiActionContext ctx, string sourcePath)
    {
        var hub = ctx.Host.Hub;
        var viewerHome = ResolveViewerHome(hub.ServiceProvider.GetService<AccessService>());
        if (viewerHome is null)
        {
            ctx.Host.UpdateArea(DialogControl.DialogArea, Controls.Dialog(
                    Controls.Markdown(
                        "**Sign in required.** This content is read-only, and copying it " +
                        "into your own home space requires a signed-in account."),
                    "Read-only content")
                .WithSize("S").WithClosable(true));
            return;
        }

        var nodeId = sourcePath[(sourcePath.LastIndexOf('/') + 1)..];
        var copyPath = $"{viewerHome}/{nodeId}";

        var body = Controls.Stack
            .WithStyle("gap: 16px;")
            .WithView(Controls.Markdown(
                "This content is read-only for you. We'll copy it to your home space " +
                "where you can edit your own version."))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("gap: 8px; justify-content: flex-end;")
                .WithView(Controls.Button(ctx.Host.Localize("common.cancel"))
                        .WithAppearance(Appearance.Neutral)
                        .WithClickAction(c =>
                        {
                            c.Host.UpdateArea(DialogControl.DialogArea, null!);
                            return Task.CompletedTask;
                        }),
                    CopyDialogCancelArea)
                .WithView(Controls.Button(ctx.Host.Localize("ui.copyToHome"))
                        .WithAppearance(Appearance.Accent)
                        .WithIconStart(FluentIcons.Copy())
                        .WithClickAction(c =>
                        {
                            CopyToHomeAndNavigate(c, sourcePath, viewerHome, copyPath);
                            return Task.CompletedTask;
                        }),
                    CopyDialogConfirmArea));

        ctx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(body, "Copy to your home space?").WithSize("M").WithClosable(true));
    }

    /// <summary>
    /// Confirm handler of the copy-to-home dialog: deep-copies the node subtree
    /// into the viewer's home partition via the standard
    /// <see cref="NodeCopyHelper.CopyNodeTree"/> (permission checks live in the
    /// upsert handlers — the viewer needs Create on their own home, which they
    /// always hold), closes the dialog, and navigates to the copy's Edit area.
    /// </summary>
    private static void CopyToHomeAndNavigate(
        UiActionContext ctx, string sourcePath, string targetNamespace, string copyPath)
    {
        var hub = ctx.Host.Hub;
        var logger = hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        NodeCopyHelper.CopyNodeTree(
                meshService, meshService, hub, sourcePath, targetNamespace, force: false, logger)
            .Subscribe(
                copied =>
                {
                    logger?.LogInformation(
                        "Copy-to-home complete: {Count} node(s) from {Source} to {Target}",
                        copied, sourcePath, targetNamespace);
                    ctx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    ctx.Host.UpdateArea(ctx.Area,
                        new RedirectControl(new LayoutAreaReference(EditArea).ToHref(copyPath)));
                },
                ex =>
                {
                    logger?.LogWarning(ex, "Copy-to-home failed for {Source} -> {Target}",
                        sourcePath, targetNamespace);
                    ctx.Host.UpdateArea(DialogControl.DialogArea, Controls.Dialog(
                            Controls.Markdown($"**Copy failed:**\n\n{ex.Message}"),
                            "Copy Failed")
                        .WithSize("M").WithClosable(true));
                });
    }

    /// <summary>
    /// The signed-in viewer's HOME partition, resolved from the ambient
    /// <see cref="AccessService"/> (per-delivery context first, then the durable
    /// circuit context) — mirroring <c>AgentPickerProjection.ResolveUserHome</c>.
    /// System / anonymous / hub-shaped principals yield <c>null</c>: they have no
    /// home partition to copy into.
    /// </summary>
    private static string? ResolveViewerHome(AccessService? accessService)
    {
        if (accessService is null)
            return null;
        foreach (var candidate in new[] { accessService.Context?.ObjectId, accessService.CircuitContext?.ObjectId })
            if (!string.IsNullOrEmpty(candidate)
                && candidate != WellKnownUsers.System
                && !string.Equals(candidate, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase)
                && !AccessService.LooksLikeHubPrincipal(candidate))
                return candidate;
        return null;
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
                        return BuildCodeNavMenu(hubAddress, hubPath, currentNode, siblings, locale: host.ViewerLocale());
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
        IReadOnlyCollection<MeshNode>? siblings, string? locale = null)
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
                    new NavLinkControl(label, CustomIcons.ForLanguage(SiblingLanguage(sibling)), siblingHref)
                );
            }
        }
        else
        {
            codeGroup = codeGroup.WithView(
                Controls.Body(LocalizationCatalog.Get("ui.noCodeFiles", locale)).WithStyle("padding: 4px 16px; display: block; color: var(--neutral-foreground-hint);")
            );
        }

        navMenu = navMenu.WithNavGroup(codeGroup);

        return navMenu;
    }

    /// <summary>
    /// Best-effort language of a sibling Code node, read from its <see cref="CodeConfiguration"/> content
    /// (typed or still-raw JSON). Used to pick the nav-menu glyph; falls back to null (→ the C# icon).
    /// </summary>
    private static string? SiblingLanguage(MeshNode node)
    {
        if (node.Content is CodeConfiguration code)
            return code.Language;
        if (node.Content is JsonElement json
            && json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("language", out var language)
            && language.ValueKind == JsonValueKind.String)
            return language.GetString();
        return null;
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
        var languageDataId = Guid.NewGuid().AsString();

        var initialCode = codeConfig.Code ?? "";
        var language = codeConfig.Language ?? "csharp";
        var nodeName = host.Hub.Address.Id ?? "";

        host.UpdateData(codeDataId, initialCode);
        host.UpdateData(displayNameDataId, nodeName);
        host.UpdateData(languageDataId, language);

        // Header
        stack = stack.WithView(Controls.H2($"Edit: {nodeName}")
            .WithStyle("margin-bottom: 16px;"));

        // DisplayName field
        var displayNameRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.Label(host.Localize("ui.displayName")).WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter display name...")
                .WithStyle("flex: 1; max-width: 400px;")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(displayNameDataId) });

        stack = stack.WithView(displayNameRow);

        // Language selector — pick C#, Python, TypeScript, JavaScript, … so a Code node can be DEFINED
        // in any first-class language (persisted onto CodeConfiguration.Language on Save). Python code
        // then executes on the connected py/python-kernel worker; C# runs in-process on the Roslyn kernel.
        var languageRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: center; margin-bottom: 16px;")
            .WithView(Controls.Label(host.Localize("ui.language")).WithStyle("font-weight: 500;"))
            .WithView((new SelectControl(new JsonPointerReference(""), Array.Empty<object>())
                    .WithOptions(LanguageOptions)) with
                { DataContext = LayoutAreaReference.GetDataPointer(languageDataId) });

        stack = stack.WithView(languageRow);

        // Monaco editor. LSP opt-in for EVERY C# Code node — live Roslyn diagnostics AND
        // completions (IMeshLanguageService). The Edit view's hub address IS the Code
        // MeshNode path, so both the owner path and the source path derive from it:
        //   • under a NodeType's Source/ subtree → the owner is that NodeType, and the
        //     language service works in its compilation (siblings in scope);
        //   • anywhere else (a standalone script cell — e.g. a course lesson's
        //     {lesson}/Source/{cell}, whose owner is a Markdown page, or a bare Code node)
        //     → the owner is simply the parent, which is not a NodeType, and the language
        //     service answers from the KERNEL'S SCRIPT ENVIRONMENT instead (script parsing,
        //     the kernel's imports/references, the script globals in scope).
        // Either way the editor completes exactly what that cell can actually compile, so
        // there is no longer a reason to leave standalone scripts without language services.
        var sourcePath = host.Hub.Address.ToString();
        CodeEditorLanguageServerConfig? lspConfig = language == "csharp"
            ? new CodeEditorLanguageServerConfig(
                NodeTypePath: OwnerPathOf(sourcePath),
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
        buttonRow = buttonRow.WithView(Controls.Button(host.Localize("common.cancel"))
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(viewHref));

        // Save button — sync click action; subscribes to the form snapshot then posts.
        buttonRow = buttonRow.WithView(Controls.Button(host.Localize("common.save"))
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(actx =>
            {
                // Snapshot both the edited code AND the chosen language (both seeded, so each Take(1)
                // emits its current value) and persist them together.
                host.Stream.GetDataStream<string>(codeDataId).Take(1)
                    .CombineLatest(host.Stream.GetDataStream<string>(languageDataId).Take(1),
                        (currentCode, currentLanguage) => (currentCode, currentLanguage))
                    .Take(1)
                    .Subscribe(edited =>
                    {
                        var (currentCode, currentLanguage) = edited;
                        var chosenLanguage = string.IsNullOrWhiteSpace(currentLanguage)
                            ? codeConfig.Language
                            : currentLanguage;
                        var updatedCodeConfiguration = codeConfig with
                        {
                            Code = currentCode,
                            Language = chosenLanguage!,
                        };
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
