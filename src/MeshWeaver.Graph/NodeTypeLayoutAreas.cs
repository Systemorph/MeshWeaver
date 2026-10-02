using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Reactive;
using System.Reactive.Threading.Tasks;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for NodeType definition nodes.
/// Uses standard MeshNodeView.Search with NodeTypeCatalogMode for showing instances.
/// - Overview: Split view with left menu and configuration/code display (default)
/// - HubConfigView: View HubConfiguration
/// - HubConfigEdit: Monaco editor for HubConfiguration
/// </summary>
public static class NodeTypeLayoutAreas
{
    /// <summary>Area name for the NodeType instance Search layout area.</summary>
    public const string SearchArea = "Search";
    /// <summary>Area name for the NodeType Overview layout area (the default split view with side menu and configuration/code display).</summary>
    public const string OverviewArea = "Overview";
    /// <summary>Area name for the NodeType Configuration layout area.</summary>
    public const string ConfigurationArea = "Configuration";
    /// <summary>Area name for the HubConfiguration view layout area.</summary>
    public const string HubConfigViewArea = "HubConfig";
    /// <summary>Area name for the HubConfiguration edit layout area (Monaco editor).</summary>
    public const string HubConfigEditArea = "HubConfigEdit";
    /// <summary>Area name for the NodeType Releases layout area.</summary>
    public const string ReleasesArea = "Releases";

    /// <summary>
    /// "Code" area on the per-NodeType hub: renders one source/test Code file
    /// (the area Id is the Code node's path) INSIDE the shared <see cref="Shell"/>,
    /// so navigating the Sources/Tests trees keeps the NodeType side menu. The
    /// Code node's own page (<see cref="CodeLayoutAreas"/>) remains for direct
    /// navigation from search results etc.
    /// </summary>
    public const string CodeArea = "Code";

    /// <summary>
    /// "Progress" area name on the per-NodeType hub. GUI clients data-bind here
    /// after receiving a <see cref="MeshWeaver.Messaging.DeliveryFailure"/> with
    /// <see cref="MeshWeaver.Messaging.ErrorType.CompilationInProgress"/>: the
    /// area renders a live status line driven by the NodeType's own MeshNode
    /// stream (<c>CompilationStatus</c>, <c>CompilationError</c>) and, when a
    /// compile activity is in flight (<c>LastCompilationActivityPath</c>),
    /// embeds <see cref="ActivityLayoutAreas.ProgressArea"/> on the activity hub
    /// so the user sees Roslyn diagnostics line-by-line.
    /// </summary>
    public const string ProgressArea = "Progress";

    // Data keys for data section
    private const string DefinitionDataId = "definition";
    private const string CodeFileDataId = "codeFile";
    private const string CodeNodesDataId = "codeNodes";

    /// <summary>
    /// Gets the OWN MeshNode of the layout host via the canonical
    /// <c>MeshNodeReference</c> reducer (per Doc/Architecture/AsynchronousCalls.md).
    /// </summary>
    private static IObservable<MeshNode?> GetNodeStream(LayoutAreaHost host)
        => host.Workspace.GetMeshNodeStream();

    /// <summary>
    /// Adds NodeType catalog views plus all standard node views (Settings, Files, Threads, Chat,
    /// AccessControl, etc.). Use this for NodeType definitions that should also support
    /// the full node management experience (e.g., Organization, custom domain types).
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeLayoutAreas(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .AddNodeTypeView()
            .AddLayout(layout => layout.WithDefaultArea(OverviewArea));

    /// <summary>
    /// Adds the NodeType views to the hub's layout for NodeType nodes.
    /// Every primary area (Overview, Configuration, Releases, Search) renders inside the
    /// shared <see cref="Shell"/> — one side menu with the type's concerns (source/test
    /// trees grouped by query name, releases, instances, related types) framing the content.
    /// Includes UCR areas ($Data, $Schema, $Model) for unified content references.
    /// Note: $Content is registered by ContentCollectionsExtensions.AddContentCollections.
    /// </summary>
    public static MessageHubConfiguration AddNodeTypeView(this MessageHubConfiguration configuration)
        => configuration
            .Set(new NodeTypeCatalogMode())  // Enable NodeType catalog mode
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(SearchArea, Search)  // standard instance search, wrapped in the shell
                // The NodeType page draws the standard header itself (OverviewContent →
                // MeshNodeLayoutAreas.BuildHeader), so the framework adds no second line (#4500).
                .WithNodePage(OverviewArea, Overview, NodePageProvenance.RenderedByThePage)
                .WithView(ConfigurationArea, Configuration)
                .WithView(HubConfigViewArea, HubConfigView)
                .WithView(HubConfigEditArea, HubConfigEdit)
                .WithView(ReleasesArea, Releases)
                .WithView(CodeArea, Code)
                .WithView(ProgressArea, Progress)
                // UCR special areas for unified content references
                .WithView(MeshNodeLayoutAreas.DataArea, MeshNodeLayoutAreas.Data)
                .WithView(MeshNodeLayoutAreas.SchemaArea, MeshNodeLayoutAreas.Schema)
                // $Model on a NodeType shows the INSTANCE data model (compiled types),
                // not the definition hub's own registry — diagram with a JSON toggle.
                .WithView(MeshNodeLayoutAreas.ModelArea, NodeTypeDataModel));

    /// <summary>
    /// Compile-progress view for a NodeType. Subscribes to the NodeType's own
    /// MeshNode stream and renders a status line + (when a compile activity is
    /// in flight) an embedded <see cref="ActivityLayoutAreas.ProgressArea"/>
    /// from the activity hub. GUI clients land here after the routing grain
    /// returns <see cref="MeshWeaver.Messaging.ErrorType.CompilationInProgress"/>;
    /// the area keeps updating as the NodeType's state transitions through
    /// Pending → Compiling → Ok/Error.
    /// </summary>
    public static IObservable<UiControl?> Progress(LayoutAreaHost host, RenderingContext _)
        => CompileProgressView(host, host.Workspace.GetMeshNodeStream(),
            nodeTypePath: host.Hub.Address.Path,
            redirectOnOk: $"/{host.Hub.Address.Path}");

    /// <summary>
    /// Core of the compile-progress page, shared by two surfaces: the per-NodeType hub's
    /// own <see cref="ProgressArea"/> (redirects to the NodeType's page on Ok) and the
    /// per-INSTANCE compilation-in-progress overlay
    /// (<c>NodeTypeEnrichmentHelpers.WithCompilationInProgressOverlay</c>), which observes
    /// the type's stream cross-hub and redirects back to the INSTANCE the user asked for.
    /// Besides the type's own status + live activity log, it renders the compile SWEEP
    /// context — how many NodeTypes are compiled vs still queued (the framework-bump
    /// warm-up recompiles every dynamic type) — so a waiting user sees the whole queue
    /// advance instead of a silent page.
    /// </summary>
    internal static IObservable<UiControl?> CompileProgressView(
        LayoutAreaHost host,
        IObservable<MeshNode?> typeNodeStream,
        string nodeTypePath,
        string redirectOnOk)
    {
        // Sweep context: the same synced NodeType query the GUI's compile indicator uses.
        // StartWith keeps it from GATING the primary render (the progress page must never
        // itself be a blank page), and a faulted query degrades to "no sweep section" with
        // the fault logged — the type's own progress keeps rendering.
        var sweepStream = host.Workspace
            .GetQuery("nodetypes-compile-sweep",
                // Every NodeType definition — a catalog, mesh-wide by nature (#3202).
                MeshWideQuery.Declare("nodeType:NodeType select:path,id,name,nodeType,content"))
            .Catch((Exception ex) =>
            {
                host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(NodeTypeLayoutAreas))
                    .LogWarning(ex, "Compile-sweep query failed — progress page renders without sweep context");
                return Observable.Return<IEnumerable<MeshNode>>([]);
            })
            .StartWith((IEnumerable<MeshNode>)[]);

        return typeNodeStream.CombineLatest(sweepStream, (node, sweepNodes) =>
            {
                // ContentAs, not a CLR type test: on the overlay surface the type node
                // arrives over a cross-hub sync stream and may still be an un-materialized
                // JsonElement (see IsCompileSettled's remarks on exactly this trap).
                var def = node?.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);
                if (node is null || def is null)
                    return (UiControl?)Controls.Markdown(
                            $"*{host.Localize("ui.noNodeTypeDefinition")}*")
                        .WithId(AreaFrameClassifier.CompileProgressId);

                // 🚨 THE SCOPED STATUS, never the raw field (#3472). `Ok` is a claim scoped
                // to CompiledFrameworkVersion: a record that compiled successfully for ANOTHER
                // platform build reads Ok here and used to redirect the viewer straight back to
                // the page that cannot render — which then bounces them back to this overlay.
                // That loop is what a client saw for two and a half hours on 2026-09-06 while
                // every instrument reported green.
                var reportedStatus = NodeTypeBuildIdentity.ReportedStatus(def);

                // Compile finished cleanly → redirect to the now-addressable page. The user
                // only landed here because activation could not complete mid-compile; once
                // it's Ok the real view resolves, so send them there. "When it ends, we redirect."
                if (reportedStatus == CompilationStatus.Ok)
                    return (UiControl?)Controls.Redirect(redirectOnOk);

                var nodeName = node.Name ?? node.Id;
                var (icon, header, body) = RenderProgressLines(host, def, reportedStatus);
                var stack = Controls.Stack
                    .WithStyle("padding: 12px; gap: 8px;")
                    // Title carries the NodeType name: "⏳ Compiling… — <NodeType>".
                    .WithView(Controls.Markdown($"### {icon} {header} — {nodeName}"));
                if (!string.IsNullOrEmpty(body))
                    stack = stack.WithView(Controls.Markdown(body));

                if (def.CompilationStatus == CompilationStatus.Error)
                {
                    // Compilation-failed is a LEGAL terminal status. Surface it as a proper page:
                    // the flat summary is in `body` above; here, for each affected source file, a
                    // link to the Code node + a read-only Monaco editor with the errors MARKED at
                    // their exact position (the captured per-file CompilationDiagnostics drive the
                    // overlay) — so the user sees WHAT broke and WHERE, never an indefinite spinner.
                    // Do NOT embed the activity LayoutAreaControl here — the compile activity is
                    // history and may be unaddressable; subscribing to an inexistent address is the
                    // resubscribe storm that wedged the portal. The Recompile button flips
                    // RequestedReleaseAt + Force on the NodeType's node via the shared stream
                    // handle — cross-hub on the instance overlay surface, local on the NodeType's
                    // own hub; the compile watcher reacts either way.
                    stack = AppendCompileErrorSources(stack, def);
                    stack = stack.WithView(Controls.Button(host.Localize("ui.recompile"))
                        .WithAppearance(Appearance.Accent)
                        .WithClickAction(_ =>
                        {
                            // 🚨 ContentAs, never `curr?.Content is NodeTypeDefinition`. On the
                            // INSTANCE overlay surface this is a cross-hub write, and UpdateRemote
                            // hands the lambda the mirror value verbatim — normally an
                            // un-materialized JsonElement. The CLR type test then fails, the lambda
                            // returns `curr` unchanged, and the write is a silent no-op: the one
                            // remedy this stuck page offers the user did nothing at all (#2409).
                            host.Hub.GetMeshNodeStream(nodeTypePath)
                                .Update(curr => curr
                                        ?.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions)
                                        is { } cd
                                    ? curr with
                                    {
                                        Content = cd with
                                        {
                                            RequestedReleaseAt = DateTimeOffset.UtcNow,
                                            RequestedReleaseForce = true
                                        }
                                    }
                                    : curr!)
                                .Subscribe(_ => { },
                                    ex => host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                                        ?.CreateLogger(typeof(NodeTypeLayoutAreas))
                                        .LogWarning(ex, "Recompile trigger failed for {Path}", nodeTypePath));
                            return Task.CompletedTask;
                        }));
                }
                else if (def.CompilationStatus == CompilationStatus.Unavailable)
                {
                    // The state could not be DETERMINED — no Roslyn diagnostics exist to
                    // mark up, and no code fix is implied. Offer the one thing that helps:
                    // a LINK (never an embedded area — the activity is history and may be
                    // unaddressable) to whatever the last compile did manage to log.
                    if (!string.IsNullOrEmpty(def.LastCompilationActivityPath))
                        stack = stack.WithView(Controls.Markdown(
                            $"[{host.Localize("ui.viewCompileLog")}](/{def.LastCompilationActivityPath})"));
                }
                else if (!string.IsNullOrEmpty(def.LastCompilationActivityPath))
                {
                    // Live activity log = the "show details" of an IN-FLIGHT compile
                    // (Compiling / Pending only — the activity is fresh and being written).
                    // Embedding it for a terminal state risks a subscription to a gone
                    // activity node → the inexistent-address storm. Roslyn diagnostics
                    // stream in line by line via the activity hub's ProgressArea.
                    stack = stack.WithView(new LayoutAreaControl(
                            new Address(def.LastCompilationActivityPath!),
                            new LayoutAreaReference(ActivityLayoutAreas.ProgressArea))
                        .WithStyle("margin-top: 8px; padding: 12px; background: var(--neutral-layer-3); border-radius: 4px; min-height: 48px;"));
                }

                // While THIS type is queued/compiling, show the whole sweep: after a
                // framework bump every dynamic type recompiles, and "your page plus 40
                // more are queued" is the honest answer to "why is this taking so long".
                if (def.CompilationStatus is CompilationStatus.Pending or CompilationStatus.Compiling)
                    stack = AppendSweepSummary(stack, host, sweepNodes);

                // 🚨 Every frame this surface serves is tagged so a CONSUMER — not just a human
                // reading the headline — can tell "the type is still building, keep waiting" from
                // the framework's terminal "Area not found". Since the compilation-in-progress
                // overlay serves this page on EVERY area of an instance, a waiter that latched it
                // as the real content would fail on the assertion instead of waiting (#1411).
                return (UiControl?)stack.WithId(AreaFrameClassifier.CompileProgressId);
            });
    }

    /// <summary>
    /// Sweep context for the compile-progress page: an "N of M types compiled" progress
    /// bar plus the type currently compiling and the queued count, derived from the synced
    /// <c>nodeType:NodeType</c> query snapshot (works on every deployment — no dependency
    /// on the pre-warmer being enabled). Rendered ONLY while more than one type is in
    /// flight/queued: a solitary recompile keeps its focused single-type page.
    /// </summary>
    private static StackControl AppendSweepSummary(
        StackControl stack, LayoutAreaHost host, IEnumerable<MeshNode> sweepNodes)
    {
        var defs = sweepNodes
            .Select(n => (Node: n, Def: n.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions)))
            // Only types that participate in compilation: with source to build or a compile
            // state already recorded. Pure marker types stay out of the totals.
            // 🚨 Single-sourced (#3006): enrichment reads the SAME predicate to decide whether an
            // instance may bind the mesh default configuration. A type this summary counts as
            // compiling while enrichment treats it as inert is the disagreement that pinned an
            // instance to the default chain — and its areas — for the grain's whole life.
            .Where(x => NodeTypeDefinition.ParticipatesInCompilation(x.Def))
            .ToList();

        var total = defs.Count;
        var pending = defs.Count(x => x.Def!.CompilationStatus == CompilationStatus.Pending);
        var compiling = defs
            .Where(x => x.Def!.CompilationStatus == CompilationStatus.Compiling)
            .Select(x => x.Node.Name ?? x.Node.Id)
            .ToList();
        var inFlight = pending + compiling.Count;
        // The sweep section only earns its place when the queue is bigger than this page's
        // own type — otherwise the single-type view above already tells the whole story.
        if (total == 0 || inFlight <= 1)
            return stack;

        var done = defs.Count(x => x.Def!.CompilationStatus
            is CompilationStatus.Ok or CompilationStatus.Error or CompilationStatus.Unavailable);
        var percent = (int)Math.Round(100.0 * done / total);

        var lines = new List<string>();
        if (compiling.Count > 0)
            lines.Add(host.Localize("ui.compileNowCompiling", string.Join(", ", compiling)));
        if (pending > 0)
            lines.Add(host.Localize("ui.compileQueued", pending));

        var sweep = Controls.Stack
            .WithStyle("margin-top: 12px; gap: 4px;")
            .WithView(Controls.Progress(
                host.Localize("ui.compileTypesReady", done, total), percent));
        if (lines.Count > 0)
            sweep = sweep.WithView(Controls.Markdown(string.Join(" · ", lines)));
        return stack.WithView(sweep);
    }

    /// <summary>
    /// For each source file a FAILED compile flagged, append a link to the Code node and a
    /// read-only Monaco editor showing that file with its diagnostics MARKED at their exact
    /// position (the IDE-style error overlay). Driven by the captured, structured
    /// <see cref="NodeTypeDefinition.CompilationDiagnostics"/> (per-file <see cref="DiagnosticInfo"/>),
    /// so the markers land exactly where Roslyn flagged them and the editor reads the live
    /// source straight from the Code node's content stream (single source of truth, no replica).
    /// Location-less diagnostics (assembly-level) stay in the flat summary rendered above.
    /// No-op when there are no structured diagnostics (e.g. an older compile before capture).
    /// </summary>
    private static StackControl AppendCompileErrorSources(StackControl stack, NodeTypeDefinition def)
    {
        foreach (var view in BuildCompileErrorSourceViews(def))
            stack = stack.WithView(view);
        return stack;
    }

    /// <summary>
    /// Pure, testable builder for the compile-error source views: for each source file the failed
    /// compile flagged (grouped by <see cref="DiagnosticInfo"/> <see cref="SourceLocation.SourcePath"/>,
    /// ordinal-ordered so the page is deterministic), emits — IN ORDER — a markdown link to the
    /// Code node followed by a read-only <see cref="CodeEditorControl"/> bound to that node's source
    /// with the diagnostics MARKED at their exact position (the IDE-style error overlay). One
    /// link + one editor per file. Location-less diagnostics (assembly-level) are left to the flat
    /// summary. Empty when there are no structured diagnostics.
    /// </summary>
    internal static IReadOnlyList<UiControl> BuildCompileErrorSourceViews(NodeTypeDefinition def)
    {
        var located = (def.CompilationDiagnostics ?? [])
            .Where(d => d.Location is { } loc && !string.IsNullOrEmpty(loc.SourcePath))
            .GroupBy(d => d.Location!.SourcePath, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (located.Count == 0)
            return [];

        var views = new List<UiControl>(located.Count * 2);
        foreach (var group in located)
        {
            var sourcePath = group.Key;
            var fileName = sourcePath.Split('/').LastOrDefault() ?? sourcePath;
            var errorCount = group.Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnCount = group.Count(d => d.Severity == DiagnosticSeverity.Warning);
            var counts = errorCount > 0
                ? $"{errorCount} error{(errorCount == 1 ? "" : "s")}"
                : $"{warnCount} warning{(warnCount == 1 ? "" : "s")}";

            // Link straight to the source Code node so the user can open and fix it.
            views.Add(Controls.Markdown($"##### [{fileName}](/{sourcePath}) — {counts}")
                .WithStyle("margin: 16px 0 4px 0;"));

            var markers = group
                .Select(d => new CodeEditorDiagnostic(
                    d.Location!.Range.Start.Line, d.Location.Range.Start.Character,
                    d.Location.Range.End.Line, d.Location.Range.End.Character,
                    (int)d.Severity, d.Message, d.Id))
                .ToList();

            views.Add(new CodeEditorControl()
                .WithLanguage("csharp")
                .WithReadonly(true)
                .WithLineNumbers(true)
                .WithMinimap(false)
                .WithHeight("360px")
                .WithDiagnostics(markers) with
            {
                // Node-bound: read the source straight from the Code node's content stream
                // (live, single source of truth — no /data replica). bindContent:true targets
                // the CodeConfiguration content; "Code" is its source-text field.
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(sourcePath, bindContent: true),
                Value = new JsonPointerReference("Code")
            });
        }
        return views;
    }

    /// <param name="reportedStatus">The status this PROCESS may honestly report —
    /// <c>NodeTypeBuildIdentity.ReportedStatus</c>, never the raw field. 🚨 The green
    /// "✓ Compiled" line below was rendered from the raw <c>CompilationStatus</c> alone and
    /// PRINTED <c>CompiledFrameworkVersion</c> beside it as decoration, without ever comparing it
    /// — so a type whose every per-instance hub was dead showed the operator a green tick with
    /// the evidence against it in the same sentence (#3472).</param>
    private static (string Icon, string Header, string Body) RenderProgressLines(
        LayoutAreaHost host, NodeTypeDefinition def, CompilationStatus? reportedStatus)
    {
        var hasSource = !string.IsNullOrWhiteSpace(def.Configuration)
            || !string.IsNullOrWhiteSpace(def.HubConfiguration)
            || (def.Sources is { Count: > 0 });

        // 🚨 #3583 — STALE-BUT-SERVING comes BEFORE the green cache-hit line: the record IS Ok
        // with a usable build, and that is exactly why the operator must be told it is the LAST
        // build, not the current source. Both module versions and both fingerprints, and what is
        // awaited. Localized: a viewer of an ordinary page never sees this (the page renders),
        // but the NodeType page is read in both languages.
        if (reportedStatus == CompilationStatus.Ok
            && def.BuildProvenance == BuildProvenance.StaleAdopted
            && !string.IsNullOrEmpty(def.LatestAssemblyPath))
            return ("⏸", host.Localize("ui.buildStaleServing"),
                host.Localize("ui.buildStaleServingBody",
                    ModuleVersionCompatibility.Display(def.AdoptedModuleVersion),
                    Short(def.AdoptedSourceFingerprint),
                    ModuleVersionCompatibility.Display(def.CurrentModuleVersion),
                    Short(def.CurrentSourceFingerprint),
                    Short(NodeTypeCompilationHelpers.FrameworkVersion)));

        // Cache-hit (Status=Ok with the usable-assembly fields populated) — the
        // routing grain re-used the existing assembly without re-running Roslyn.
        // Surface that as a discrete state so the operator sees "we didn't burn
        // CPU to re-prove this assembly works."
        if (reportedStatus == CompilationStatus.Ok
            && !string.IsNullOrEmpty(def.LatestAssemblyCollection)
            && !string.IsNullOrEmpty(def.LatestAssemblyPath))
        {
            var coll = def.LatestAssemblyCollection!;
            var path = def.LatestAssemblyPath!;
            return ("✓", "Compiled",
                $"Using cached assembly `{coll}/{path}` (compile version `{def.LastCompiledVersion}`, framework `{def.CompiledFrameworkVersion}`).");
        }

        return reportedStatus switch
        {
            // 🚨 Localized, unlike its English-only siblings on this operator page: this
            // arm is the one a VIEWER of an ordinary content page reaches, because a foreign
            // build is precisely the state in which every instance of the type falls back to
            // this overlay instead of rendering.
            CompilationStatus.Foreign => ("⚠", host.Localize("ui.compileForeignFramework"),
                host.Localize("ui.compileForeignFrameworkBody",
                    Short(def.CompiledFrameworkVersion),
                    Short(NodeTypeCompilationHelpers.FrameworkVersion))),
            CompilationStatus.Compiling => ("⏳", "Compiling…",
                $"Running Roslyn against {(def.Sources?.Count ?? 0)} source binding(s). The activity log below streams diagnostics live."),
            CompilationStatus.Pending => ("▶", "Compile queued",
                "Initiating compilation — the per-NodeType compile watcher has flipped status to Pending and the activity hub is being created."),
            CompilationStatus.Error => ("✗", "Compilation failed",
                string.IsNullOrEmpty(def.CompilationError)
                    ? "The last compile failed — no diagnostic captured. Click **Recycle** on the parent NodeType to retry."
                    : $"```text\n{def.CompilationError}\n```"),
            // 🚨 NOT a failure: the last attempt could not DETERMINE the state (a settle
            // or lookup timeout). Never phrase this as broken code.
            CompilationStatus.Unavailable => ("❔", "Compile state unknown",
                (string.IsNullOrEmpty(def.CompilationError)
                    ? "The last attempt to determine this type's build state did not complete."
                    : $"```text\n{def.CompilationError}\n```")
                + "\n\nNothing is known to be wrong with the code — click **Recompile** to try again."),
            // null / Unknown — split on whether there's anything to compile at all.
            _ => hasSource
                ? ("…", "Waiting for compile to start",
                   "Sources are present but no compile activity has been kicked yet. Activation will trigger one on first instance request.")
                : ("·", "No compile required",
                   "This NodeType has no `Configuration` / `HubConfiguration` / `Sources` — instances activate against the default node-hub config.")
        };
    }

    /// <summary>First eight characters of a framework build identity — the same width the
    /// assembly-store filename tag carries, so a page and a DLL name compare by eye.
    ///
    /// <para>🚨 An absent identity renders as an EM DASH, never "(none)". This value is
    /// substituted into <c>ui.compileForeignFrameworkBody</c>, so an English literal here would
    /// appear untranslated in the middle of a German sentence — and the house preference is
    /// exactly this: a language-neutral glyph beats a translated word (AGENTS.md, i18n). The
    /// English "(none)" that <c>NodeTypeBuildIdentity.Short</c> uses is correct THERE, because
    /// that one feeds log lines, which stay English by house rule.</para>
    /// </summary>
    private static string Short(string? identity)
        => string.IsNullOrEmpty(identity)
            ? "—"
            : identity[..Math.Min(8, identity.Length)];

    /// <summary>
    /// Shared shell for every primary NodeType area: a horizontal splitter with the
    /// type's side menu (Overview / Configuration / source &amp; test trees / Releases /
    /// Instances / Related) on the left and the area's content on the right. One menu
    /// built from one stream combination — every area shows the same, fully populated
    /// navigation (the previous per-area menus passed nulls and rendered empty
    /// Sources/Tests sections on some pages).
    /// </summary>
    private static UiControl Shell(
        LayoutAreaHost host,
        Func<LayoutAreaHost, RenderingContext, IObservable<UiControl?>> mainContent)
    {
        var hubAddress = host.Hub.Address;
        var hubPath = hubAddress.ToString();

        var sourceGroupsStream = GetCodeGroupsStream(host, tests: false);
        var testGroupsStream = GetCodeGroupsStream(host, tests: true);
        var nodeTypesStream = QueryNodesStream(host,
            $"path:{hubPath} nodeType:NodeType scope:descendants");
        var agentsStream = QueryNodesStream(host,
            $"path:{hubPath} nodeType:Agent scope:descendants");

        // `shell-splitter` (standard-page-layout.css) gives both panes a definite-height
        // flex context: the menu and the content scroll independently, the splitter bar
        // is draggable (Min/Max bound) and carries the collapse/expand chevrons for the
        // menu pane. Height fills the parent .layout-area-container instead of a
        // viewport-minus-magic-number guess.
        return Controls.Splitter
            .WithClass("shell-splitter")
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("100%"))
            .WithView(
                (h, c) => GetNodeStream(host)
                    .CombineLatest(sourceGroupsStream, testGroupsStream, nodeTypesStream, agentsStream)
                    .Select(tuple =>
                    {
                        var (node, sourceGroups, testGroups, nodeTypes, agents) = tuple;
                        if (node == null)
                            return RenderLoading("Loading...");
                        return BuildSideMenu(hubAddress, node, sourceGroups, testGroups, nodeTypes, agents, host.Hub.JsonSerializerOptions);
                    }),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("480px").WithCollapsible(true)
            )
            .WithView(
                // The splitter pane wants a non-nullable control stream; a null
                // emission from the content area renders as the loading state.
                // No wrapper control: the pane's child div is the scroll container
                // (`.shell-splitter .fluent-multi-splitter-pane > div` in
                // standard-page-layout.css) — tests and embeddings see the area's
                // actual control as the pane content.
                (h, c) => mainContent(h, c).Select(v => v ?? RenderLoading("Loading...")),
                skin => skin.WithSize("*")
            );
    }

    /// <summary>
    /// Resolves the NodeType's Sources or Tests into named groups: each
    /// <see cref="CodeQueryGroup"/> (from the <c>name=</c> prefix, default
    /// <c>src</c>/<c>test</c>) paired with the live query results for its queries.
    /// Re-resolves whenever the definition changes, so renames/new queries appear
    /// without a reload.
    /// </summary>
    private static IObservable<IReadOnlyList<(CodeQueryGroup Group, IReadOnlyList<MeshNode> Nodes)>> GetCodeGroupsStream(
        LayoutAreaHost host, bool tests)
    {
        return GetNodeStream(host)
            .Select(node =>
            {
                if (node == null)
                    return Observable.Return<IReadOnlyList<(CodeQueryGroup, IReadOnlyList<MeshNode>)>>([]);
                var def = node.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);
                var groups = tests
                    ? CodeQueryResolver.GroupAll(def?.Tests, CodeQueryResolver.DefaultTests,
                        node.Path, CodeQueryResolver.DefaultTestGroupName)
                    : CodeQueryResolver.GroupAll(def?.Sources, CodeQueryResolver.DefaultSources,
                        node.Path, CodeQueryResolver.DefaultSourceGroupName);
                if (groups.Count == 0)
                    return Observable.Return<IReadOnlyList<(CodeQueryGroup, IReadOnlyList<MeshNode>)>>([]);
                var streams = groups.Select(g =>
                    RunQueries(host, g.ExpandedQueries).Select(nodes => (Group: g, Nodes: nodes)));
                return Observable.CombineLatest(streams)
                    .Select(list => (IReadOnlyList<(CodeQueryGroup, IReadOnlyList<MeshNode>)>)list.ToList());
            })
            .Switch();
    }

    /// <summary>
    /// Live query stream that degrades to an empty list on error (logged at Warning so
    /// silent timeouts surface in test output) — one bad query must not blank the menu.
    /// </summary>
    private static IObservable<IReadOnlyList<MeshNode>> QueryNodesStream(LayoutAreaHost host, string query)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(NodeTypeLayoutAreas));
        // 🚨 Live, access-carrying, shared-handle query — NOT IMeshService.Query(...).Take(1). The
        // latter is the hand-woven trap from DebuggingMessageFlow/AsynchronousCalls: a Take(1) on a
        // query that NEVER emits an Initial (a not-yet-provisioned partition, a cold descendant scope)
        // hangs forever, so the Shell's side-menu CombineLatest never completes and the NodeType GUI
        // shell never renders (the FutuRe/LineOfBusiness 50s render deadlock). GetQuery emits
        // empty-on-absent and re-emits on change, so the menu always renders.
        // NO `select:` — the query is CALLER-SUPPLIED (a NodeType's own Sources/Tests
        // query) and the cache id IS that string, so the projection belongs to whoever
        // authored the query, not here. Leaving it unprojected keeps the full node,
        // which is the conservative choice at an open seam.
        return host.Workspace.GetQuery(query, query)
            .Select(nodes => (IReadOnlyList<MeshNode>)nodes.ToList())
            .Catch<IReadOnlyList<MeshNode>, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Query '{Query}' failed; falling back to empty list", query);
                return Observable.Return((IReadOnlyList<MeshNode>)[]);
            });
    }

    /// <summary>
    /// Renders the Overview area for a NodeType — the landing page. Inside the shared
    /// <see cref="Shell"/>: compile status, the markdown Description, a Configuration
    /// summary, the named source/test queries, and the latest three releases with a
    /// link to the full release history.
    /// </summary>
    public static UiControl Overview(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, OverviewContent);

    private static IObservable<UiControl?> OverviewContent(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubAddress = host.Hub.Address;
        var hubPath = hubAddress.ToString();
        var locale = host.ViewerLocale();

        // Templates: the compile panel binds to the status projection it publishes, and the latest
        // releases are listed by the GUI — neither waits on this stream. What still renders from the
        // node below is the header (MeshNodeLayoutAreas.BuildHeader takes the node) and the summary
        // sections derived from the definition.
        var compilePanel = BuildCompileStatusPanel(hubPath)
            .PublishingTo(NodeTypeStatusView.DataId, StatusProjection(host));
        var latestReleases = BuildLatestReleasesSection(hubPath, locale);

        return GetNodeStream(host)
            .Select(node =>
            {
                if (node == null)
                    return RenderLoading("Loading...");
                var typeDef = node.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);

                var content = Controls.Stack.WithWidth("100%")
                    .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host, typeDef)
                        + " padding-top: 8px; padding-bottom: 32px; gap: 8px;");
                content = content.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

                // Compile-state banner + Compile button — the "ability to compile" affordance on
                // the landing page.
                content = content.WithView(compilePanel);

                // Markdown description from the mesh node's NodeTypeDefinition.
                if (!string.IsNullOrEmpty(typeDef?.Description))
                    content = content.WithView(Controls.Markdown(typeDef.Description));

                content = content.WithView(BuildConfigurationSection(hubAddress, node, typeDef, locale: locale));
                content = content.WithView(NodeTypeDataModelAreas.BuildOverviewSection(host));
                content = content.WithView(BuildQueriesSection("Source queries",
                    CodeQueryResolver.GroupAll(typeDef?.Sources, CodeQueryResolver.DefaultSources,
                        node.Path, CodeQueryResolver.DefaultSourceGroupName)));
                content = content.WithView(BuildQueriesSection("Test queries",
                    CodeQueryResolver.GroupAll(typeDef?.Tests, CodeQueryResolver.DefaultTests,
                        node.Path, CodeQueryResolver.DefaultTestGroupName)));
                content = content.WithView(latestReleases);

                return (UiControl?)content;
            });
    }

    internal static UiControl BuildSectionHeader(string title, string? href = null, string? linkLabel = null)
    {
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: baseline; margin-top: 24px; " +
                       "padding-bottom: 6px; border-bottom: 1px solid var(--neutral-stroke-divider);")
            .WithView(Controls.H3(title).WithStyle("margin: 0;"));
        if (href != null)
            header = header.WithView(Controls.Markdown($"[{linkLabel ?? "More"} →]({href})")
                .WithStyle("font-size: 13px;"));
        return header;
    }

    /// <summary>
    /// Configuration summary on the Overview: the notable settings as read-only rows
    /// plus a link to the full Configuration area where they're edited.
    /// </summary>
    private static UiControl BuildConfigurationSection(object hubAddress, MeshNode node, NodeTypeDefinition? def, string? locale = null)
    {
        var configHref = new LayoutAreaReference(ConfigurationArea).ToHref(hubAddress);
        var section = Controls.Stack.WithWidth("100%")
            .WithView(BuildSectionHeader("Configuration", configHref, "Open configuration"));

        var rows = new List<(string Label, string? Value)>
        {
            ("Default Namespace", def?.DefaultNamespace),
            ("Children Query", def?.ChildrenQuery),
            ("Storage Table", def?.StorageTable),
            ("Owns Partition", def?.OwnsPartition == true ? "Yes" : null),
            ("Page Max Width", def?.PageMaxWidth),
            ("Dependencies", def?.Dependencies is { Count: > 0 } deps ? string.Join(", ", deps) : null),
            ("Configuration Lambda", string.IsNullOrWhiteSpace(def?.Configuration) ? null : "Defined"),
        };

        var hasAny = false;
        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            section = section.WithView(BuildInfoRow(label, value!));
            hasAny = true;
        }

        if (!hasAny)
            section = section.WithView(Controls.Body(LocalizationCatalog.Get("ui.allDefaults", locale))
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 8px 0;"));
        return section;
    }

    /// <summary>
    /// Lists the named source/test queries exactly as configured — one line per query,
    /// grouped label first (<c>src — `namespace:Source scope:subtree`</c>).
    /// </summary>
    private static UiControl BuildQueriesSection(string title, IReadOnlyList<CodeQueryGroup> groups, string? locale = null)
    {
        var section = Controls.Stack.WithWidth("100%")
            .WithView(BuildSectionHeader(title));

        if (groups.Count == 0)
        {
            return section.WithView(Controls.Body(LocalizationCatalog.Get("ui.noneConfigured", locale))
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 8px 0;"));
        }

        foreach (var group in groups)
            foreach (var raw in group.RawQueries)
                section = section.WithView(
                    Controls.Markdown($"**{group.Name}** — `{raw}`")
                        .WithStyle("padding: 2px 0;"));
        return section;
    }

    /// <summary>
    /// The latest three releases (newest first) with a link to the full Releases area — listed by
    /// the GUI (<see cref="ReleasesList"/>), never loaded here.
    /// </summary>
    private static UiControl BuildLatestReleasesSection(string nodeTypePath, string? locale)
        => Controls.Stack.WithWidth("100%")
            .WithView(BuildSectionHeader(
                LocalizationCatalog.Get("ui.latestReleases", locale),
                new LayoutAreaReference(ReleasesArea).ToHref(nodeTypePath),
                LocalizationCatalog.Get("ui.allReleases", locale)))
            .WithView(ReleasesList(nodeTypePath, limit: 3));

    /// <summary>
    /// Renders the Configuration area for a NodeType: the shared <see cref="Shell"/> with the
    /// settings TEMPLATE (<see cref="BuildConfigurationTemplate"/>) as content. The template is
    /// emitted at once; its fields bind to the node and its status lines to the
    /// <see cref="NodeTypeStatusView"/> projection.
    /// </summary>
    public static UiControl Configuration(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, (h, c) => Observable.Return<UiControl?>(
            BuildConfigurationTemplate(host.Hub.Address.ToString(), host.ViewerLocale())
                .PublishingTo(NodeTypeStatusView.DataId, StatusProjection(host))));

    /// <summary>
    /// The NodeType's compile state as data for the templates (<see cref="NodeTypeStatusView"/>) —
    /// the ONE read of the node behind the Configuration, Releases, HubConfig and Overview status
    /// lines. A projection, not a control: the templates bind to it by pointer and never wait for it.
    /// </summary>
    private static IObservable<NodeTypeStatusView> StatusProjection(LayoutAreaHost host)
    {
        var nodeTypePath = host.Hub.Address.ToString();
        var locale = host.ViewerLocale();
        var options = host.Hub.JsonSerializerOptions;
        return host.Workspace.GetMeshNodeStream()
            .Select(node => NodeTypeStatusView.From(
                node, node?.ContentAs<NodeTypeDefinition>(options), nodeTypePath, locale));
    }

    /// <summary>
    /// Renders the instance search for this NodeType inside the shared <see cref="Shell"/> —
    /// the standard <see cref="MeshNodeLayoutAreas.Search"/> with NodeTypeCatalogMode lists
    /// and searches the type's instances.
    /// </summary>
    public static UiControl Search(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, MeshNodeLayoutAreas.Search);

    /// <summary>
    /// Renders a single source/test Code file inside the shared <see cref="Shell"/>.
    /// The area Id is the Code node's path; the content embeds that node's default
    /// (Content) area via <see cref="LayoutAreaControl"/>, so the file view stays
    /// live while the NodeType side menu stays put. This is where the Sources/Tests
    /// tree links point — previously they navigated to the Code node's own page,
    /// which swapped the NodeType menu for the code-sibling menu ("the menu keeps
    /// disappearing").
    /// </summary>
    public static UiControl Code(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, CodeContent);

    /// <summary>
    /// The NodeType's <c>$Model</c> area inside the shared <see cref="Shell"/>:
    /// the instance data model as a Mermaid class diagram with a tab to switch to
    /// the JSON schema and back; with an Id, the detail page for that type.
    /// </summary>
    public static UiControl NodeTypeDataModel(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, NodeTypeDataModelAreas.Content);

    private static IObservable<UiControl?> CodeContent(LayoutAreaHost host, RenderingContext ctx)
    {
        var codePath = host.Reference.Id?.ToString();
        if (string.IsNullOrEmpty(codePath))
            return Observable.Return<UiControl?>(Controls.Markdown(
                    "*No source file selected — pick one from the Sources or Tests tree.*")
                .WithStyle("padding: 24px;"));

        return Observable.Return<UiControl?>(
            new LayoutAreaControl(new Address(codePath!), new LayoutAreaReference(CodeLayoutAreas.ContentArea))
                .WithStyle("width: 100%;"));
    }

    /// <summary>
    /// Builds the NodeType side menu — the one navigation surface every NodeType area
    /// shares. Concerns, top to bottom: Overview (landing), Configuration, the source
    /// tree grouped by query name, the test tree grouped by query name, Releases,
    /// Instances (search for nodes of this type), and Related (declared dependencies,
    /// NodeTypes and Agents under this namespace).
    /// </summary>
    private static UiControl BuildSideMenu(
        object hubAddress,
        MeshNode node,
        IReadOnlyList<(CodeQueryGroup Group, IReadOnlyList<MeshNode> Nodes)> sourceGroups,
        IReadOnlyList<(CodeQueryGroup Group, IReadOnlyList<MeshNode> Nodes)> testGroups,
        IReadOnlyList<MeshNode> nodeTypes,
        IReadOnlyList<MeshNode> agents,
        System.Text.Json.JsonSerializerOptions options)
    {
        var content = node.ContentAs<NodeTypeDefinition>(options);
        // Scrolling comes from `.shell-splitter .navmenu` in standard-page-layout.css —
        // NavMenuView ignores inline Style on its root. Collapse/expand is the splitter
        // pane's affordance (chevrons on the bar), not the NavMenu hamburger.
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        navMenu = navMenu.WithView(new NavLinkControl("Overview", FluentIcons.Home(),
            new LayoutAreaReference(OverviewArea).ToHref(hubAddress)));
        navMenu = navMenu.WithView(new NavLinkControl("Configuration", FluentIcons.Settings(),
            new LayoutAreaReference(ConfigurationArea).ToHref(hubAddress)));
        navMenu = navMenu.WithView(new NavLinkControl("Data model", FluentIcons.Database(),
            new LayoutAreaReference(MeshNodeLayoutAreas.ModelArea).ToHref(hubAddress)));

        // Sources + Tests trees — the resolved outputs of the configured queries,
        // grouped by query name (default `src` / `test`), so the user sees exactly
        // what compiles — including shared code pulled in from other namespaces.
        navMenu = navMenu.WithNavGroup(BuildCodeNavGroup(
            "Sources", FluentIcons.Code(), node.Path, sourceGroups, hubAddress));
        navMenu = navMenu.WithNavGroup(BuildCodeNavGroup(
            "Tests", FluentIcons.Beaker(), node.Path, testGroups, hubAddress));

        navMenu = navMenu.WithView(new NavLinkControl("Releases", FluentIcons.Box(),
            new LayoutAreaReference(ReleasesArea).ToHref(hubAddress)));
        navMenu = navMenu.WithView(new NavLinkControl("Instances", FluentIcons.Search(),
            new LayoutAreaReference(SearchArea).ToHref(hubAddress)));

        // Related — navigation to related types: declared dependencies (e.g. the
        // dimensions a cube type references) plus NodeTypes and Agents under this
        // namespace. Only rendered when there's something to link.
        var related = new NavGroupControl("Related")
            .WithIcon(FluentIcons.Link())
            .WithSkin(s => s.WithExpanded(true));
        var hasRelated = false;

        if (content?.Dependencies is { Count: > 0 } deps)
        {
            foreach (var dep in deps)
            {
                related = related.WithView(new NavLinkControl(
                    dep.Split('/').LastOrDefault() ?? dep, FluentIcons.Document(), $"/{dep}"));
                hasRelated = true;
            }
        }

        foreach (var typeNode in nodeTypes.OrderBy(n => n.Order).ThenBy(n => n.Name))
        {
            related = related.WithView(new NavLinkControl(
                typeNode.Name ?? typeNode.Id, FluentIcons.DocumentText(), $"/{typeNode.Path}"));
            hasRelated = true;
        }

        foreach (var agentNode in agents.OrderBy(n => n.Order).ThenBy(n => n.Name))
        {
            related = related.WithView(new NavLinkControl(
                agentNode.Name ?? agentNode.Id, FluentIcons.Bot(), $"/{agentNode.Path}"));
            hasRelated = true;
        }

        if (hasRelated)
            navMenu = navMenu.WithNavGroup(related);

        return navMenu;
    }

    /// <summary>
    /// Builds the hierarchical navigation group for the resolved Sources or Tests:
    /// one sub-group per named query (the <c>name=</c> prefix; default <c>src</c>/<c>test</c>),
    /// each containing the file tree its queries resolved to. Files under the group's
    /// namespace root are displayed at their relative path (folders by namespace
    /// segment); files outside it — shared code pulled in via <c>@path</c> or
    /// cross-NodeType <c>namespace:</c> queries — group under one folder PER PACKAGE
    /// (the owning partition root), whose header links to the package's page.
    /// </summary>
    internal static NavGroupControl BuildCodeNavGroup(
        string groupLabel,
        Icon groupIcon,
        string rootPath,
        IReadOnlyList<(CodeQueryGroup Group, IReadOnlyList<MeshNode> Nodes)>? groups,
        object? hubAddress = null, string? locale = null)
    {
        var root = new NavGroupControl(groupLabel)
            .WithIcon(groupIcon)
            .WithSkin(s => s.WithExpanded(true));

        if (groups == null || groups.Count == 0 || groups.All(g => g.Nodes.Count == 0))
        {
            return root.WithView(
                Controls.Body($"No {groupLabel.ToLowerInvariant()} yet")
                    .WithStyle("padding: 4px 16px; display: block; color: var(--neutral-foreground-hint);"));
        }

        foreach (var (group, nodes) in groups)
        {
            var sub = new NavGroupControl(group.Name)
                .WithIcon(FluentIcons.Folder())
                .WithSkin(s => s.WithExpanded(true));

            if (nodes.Count == 0)
            {
                sub = sub.WithView(
                    Controls.Body(LocalizationCatalog.Get("ui.empty", locale))
                        .WithStyle("padding: 4px 16px; display: block; color: var(--neutral-foreground-hint);"));
            }
            else
            {
                // Relativise against the group's own namespace root when one is
                // determinable (so the default `src` group shows files directly,
                // not nested under a redundant "Source/" folder); otherwise fall
                // back to the NodeType's path.
                var basePath = group.BaseNamespace ?? rootPath;
                var tree = BuildCodeTreeForNavigation(basePath, nodes);
                foreach (var child in tree.OrderedChildren())
                    sub = AppendCodeTreeNode(sub, child, hubAddress);
            }

            root = root.WithGroup(sub);
        }

        return root;
    }

    /// <summary>
    /// Groups resolved code nodes into a tree: local files (paths starting with
    /// <c>{rootPath}/</c>) are relativised; foreign files are grouped under one
    /// folder PER PACKAGE — the partition root (first path segment) that owns
    /// them — with their package-relative path preserved. The old single
    /// "(shared)" umbrella buried the origin under full absolute paths
    /// ("shared → (shared) → Underwriting → SampleData → Source → …"); naming
    /// the package directly lets the renderer give the folder the package icon
    /// and link its header to the package's page.
    /// </summary>
    internal static CodeTreeFolder BuildCodeTreeForNavigation(string rootPath, IReadOnlyCollection<MeshNode> nodes)
    {
        var rootPrefix = rootPath + "/";
        var tree = new CodeTreeFolder("");

        foreach (var node in nodes.OrderBy(n => n.Path, StringComparer.Ordinal))
        {
            if (node.Path.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                var relative = node.Path.Substring(rootPrefix.Length);
                tree.Insert(relative.Split('/'), 0, node);
            }
            else
            {
                // One folder per package. GetOrAddFolder (not a fresh folder +
                // AddFolder) so a package whose name collides with a local
                // relative folder MERGES instead of silently replacing it —
                // no file may ever drop out of the tree.
                var segments = node.Path.Split('/');
                var packageFolder = tree.GetOrAddFolder(segments[0]);
                packageFolder.MarkAsPackage(segments[0]);
                // Package-relative insert; a degenerate single-segment path
                // (a top-level Code node) becomes a leaf named like the package.
                packageFolder.Insert(segments, segments.Length == 1 ? 0 : 1, node);
            }
        }
        return tree;
    }

    /// <summary>
    /// Runs a sequence of expanded queries via the LIVE <c>workspace.GetQuery</c> and returns the
    /// de-duplicated MeshNode results. Empty input → empty result, so the default "no sources/tests
    /// yet" state still renders cleanly.
    /// </summary>
    // 🚨 GetQuery, NOT IMeshService.Query(...).Take(1): the latter HANGS when a query never emits an
    // Initial (cold/unprovisioned scope), wedging the Shell's side-menu CombineLatest forever (the
    // FutuRe/LineOfBusiness render deadlock). GetQuery is live, shared (one upstream per id), carries
    // the subscriber's identity, and emits empty-on-absent — so the menu always renders. One shared
    // handle for the whole expanded set (params queries); fold to a deduped list.
    private static IObservable<IReadOnlyList<MeshNode>> RunQueries(
        LayoutAreaHost host,
        IEnumerable<string> queries)
    {
        var queryList = queries.ToList();
        if (queryList.Count == 0)
            return Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>());

        // NO `select:` — caller-supplied queries again (the code-tree group), and the id is
        // derived from them; see QueryNodesStream above for the same reasoning.
        return host.Workspace.GetQuery("codegroup:" + string.Join("|", queryList), queryList.ToArray())
            .Select(nodes =>
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var results = new List<MeshNode>();
                foreach (var n in nodes)
                    if (n?.Path is { Length: > 0 } p && seen.Add(p))
                        results.Add(n);
                return (IReadOnlyList<MeshNode>)results;
            });
    }

    private static NavGroupControl AppendCodeTreeNode(NavGroupControl parent, CodeTreeNode node, object? hubAddress)
    {
        if (node is CodeTreeLeaf leaf)
        {
            // Inside a NodeType shell, open the file in the shell's own Code area so
            // the side menu stays. Without a hub address (no shell context), fall back
            // to the Code node's standalone page.
            var href = hubAddress != null
                ? new LayoutAreaReference(CodeArea) { Id = leaf.Node.Path }.ToHref(hubAddress)
                : new LayoutAreaReference(CodeLayoutAreas.OverviewArea).ToHref(leaf.Node.Path);
            return parent.WithView(new NavLinkControl(leaf.Node.Name ?? leaf.Node.Id, CustomIcons.CSharp(), href));
        }

        var folder = (CodeTreeFolder)node;
        var (label, current) = CompressChain(folder);

        var group = new NavGroupControl(label)
            .WithIcon(folder.PackagePath is not null ? FluentIcons.Box() : FluentIcons.Folder())
            .WithSkin(s => s.WithExpanded(true));
        // A package folder's header navigates to the package's own page — the
        // user asked for "which package is this from, and take me there".
        if (folder.PackagePath is not null)
            group = group.WithUrl($"/{folder.PackagePath}");
        foreach (var child in current.OrderedChildren())
            group = AppendCodeTreeNode(group, child, hubAddress);
        return parent.WithGroup(group);
    }

    /// <summary>
    /// Path-compresses a pure pass-through chain: a folder with no files and a
    /// single sub-folder renders as one "A/B" group instead of two nesting
    /// levels ("SampleData → Source → file" becomes "SampleData/Source" — the
    /// children rendered are the chain end's). Package folders never compress —
    /// in neither direction — because their header IS the package identity
    /// (icon + link to the package page). Pure helper so <c>CodeTreeTest</c>
    /// can pin the contract without walking the UI control tree.
    /// </summary>
    internal static (string Label, CodeTreeFolder Effective) CompressChain(CodeTreeFolder folder)
    {
        var label = folder.Name;
        var current = folder;
        while (current.PackagePath is null
               && current.Leaves.Count == 0
               && current.Folders.Count == 1)
        {
            var only = current.Folders.Values.First();
            if (only.PackagePath is not null)
                break;
            label = label.Length == 0 ? only.Name : $"{label}/{only.Name}";
            current = only;
        }
        return (label, current);
    }

    /// <summary>
    /// Testable pure helper: builds a <see cref="CodeTreeFolder"/> representing the
    /// hierarchy a caller would render as a <see cref="NavGroupControl"/>. Filters by
    /// <c>subPrefix</c> exactly like <see cref="BuildCodeNavGroup"/> so
    /// tests can assert the bucketing (outside-namespace files filtered, nested folders
    /// grouped, alphabetical order) without walking the UI control tree.
    /// </summary>
    internal static CodeTreeFolder BuildCodeTree(string rootPath, string subNamespace, IReadOnlyCollection<MeshNode> nodes)
    {
        var subPrefix = $"{rootPath}/{subNamespace}/";
        var tree = new CodeTreeFolder("");
        foreach (var node in nodes
                     .Where(n => n.Path.StartsWith(subPrefix, StringComparison.Ordinal))
                     .OrderBy(n => n.Path, StringComparer.Ordinal))
        {
            var relative = node.Path.Substring(subPrefix.Length);
            tree.Insert(relative.Split('/'), 0, node);
        }
        return tree;
    }

    internal abstract class CodeTreeNode
    {
        public string Name { get; init; } = "";
    }

    internal sealed class CodeTreeLeaf : CodeTreeNode
    {
        public MeshNode Node { get; init; } = null!;
    }

    internal sealed class CodeTreeFolder : CodeTreeNode
    {
        private readonly Dictionary<string, CodeTreeFolder> _folders = new(StringComparer.Ordinal);
        private readonly List<CodeTreeLeaf> _leaves = new();

        public CodeTreeFolder(string name) { Name = name; }

        public IReadOnlyDictionary<string, CodeTreeFolder> Folders => _folders;
        public IReadOnlyList<CodeTreeLeaf> Leaves => _leaves;

        /// <summary>
        /// Set when this folder represents a PACKAGE — the partition root (first
        /// path segment) owning shared files pulled in from outside the NodeType's
        /// subtree. The renderer gives such folders the package icon and links
        /// their header to the package's page; they sort after local folders and
        /// never path-compress.
        /// </summary>
        public string? PackagePath { get; private set; }

        public void MarkAsPackage(string packagePath) => PackagePath = packagePath;

        /// <summary>
        /// Returns the sub-folder of that name, creating it when absent — the
        /// merge-safe way to attach package folders (a name collision with a
        /// local relative folder must merge, never replace).
        /// </summary>
        public CodeTreeFolder GetOrAddFolder(string name)
        {
            if (!_folders.TryGetValue(name, out var folder))
                _folders[name] = folder = new CodeTreeFolder(name);
            return folder;
        }

        public void Insert(string[] segments, int index, MeshNode node)
        {
            if (index == segments.Length - 1)
            {
                _leaves.Add(new CodeTreeLeaf { Name = segments[index], Node = node });
                return;
            }
            var folderName = segments[index];
            if (!_folders.TryGetValue(folderName, out var folder))
                _folders[folderName] = folder = new CodeTreeFolder(folderName);
            folder.Insert(segments, index + 1, node);
        }

        public IEnumerable<CodeTreeNode> OrderedChildren()
            => _folders.Values.Where(f => f.PackagePath is null).OrderBy(f => f.Name, StringComparer.Ordinal)
                .Concat(_folders.Values.Where(f => f.PackagePath is not null).OrderBy(f => f.Name, StringComparer.Ordinal))
                .Cast<CodeTreeNode>()
                .Concat(_leaves.OrderBy(l => l.Name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Renders the Releases area for a NodeType — the chronological list of
    /// <c>Release</c> MeshNodes at <c>{nodeTypePath}/Release/{version}</c>, written by
    /// the CompileWatcher on every successful compile. Each row shows the status,
    /// version, timestamp, source/test counts, and release-notes excerpt, and links to
    /// the release's own page (where the sources/tests as-of that version are
    /// navigable). The header carries the "Create Release" button — the canonical
    /// request-via-stream-update compile trigger.
    /// </summary>
    [Browsable(false)]
    public static UiControl Releases(LayoutAreaHost host, RenderingContext ctx)
        => Shell(host, ReleasesContent);

    private static IObservable<UiControl?> ReleasesContent(LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<UiControl?>(
            BuildReleasesTemplate(host.Hub.Address.ToString(), host.ViewerLocale())
                .PublishingTo(NodeTypeStatusView.DataId, StatusProjection(host)));

    /// <summary>
    /// The Releases pane as a TEMPLATE: header with the live status line and the Create Release
    /// trigger, the pending release notes, and the release history listed by the GUI
    /// (<see cref="ReleasesList"/>) — newest first, each linking to its Release page where the
    /// sources/tests as of that version are navigable. Nothing here reads the node; the status lines
    /// bind to <see cref="NodeTypeStatusView"/>.
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's path (the hub's own).</param>
    /// <param name="locale">The viewer's locale, for the chrome's strings.</param>
    internal static UiControl BuildReleasesTemplate(string nodeTypePath, string? locale)
    {
        var status = NodeTypeStatusView.DataContext;
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; gap: 16px;")
            .WithView(Controls.H2(LocalizationCatalog.Get("ui.releases", locale)).WithStyle("margin: 0;"))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("gap: 12px; align-items: center;")
                .WithView(Controls.Body(NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.ReleasesStatusBadge)))
                    .WithStyle("color: var(--neutral-foreground-hint); font-size: 13px;") with { DataContext = status })
                .WithView(CreateReleaseButton(nodeTypePath, locale)));

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 12px;")
            .WithView(headerRow)
            .WithView(Controls.Body(LocalizationCatalog.Get("ui.releasesIntro", locale))
                .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 8px;"))
            .WithView(Controls.Body(NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.PendingReleaseNotes))) with
            {
                Style = NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.PendingReleaseNotesStyle)),
                DataContext = status
            })
            .WithView(ReleasesList(nodeTypePath));
    }

    /// <summary>
    /// The release history of a NodeType, listed by the GUI — newest first. A query control, so the
    /// hub never loads the releases: the viewer's client runs <c>namespace:{type}/Release</c> and
    /// keeps the list live as the compile watcher writes new Release nodes.
    ///
    /// <para><c>sort:CreatedAt-desc</c> orders by the RELEASE's own time: a <see cref="MeshNode"/> has
    /// no <c>CreatedAt</c> (its field is <c>CreatedDate</c>), so the selector resolves in the node's
    /// content — <see cref="NodeTypeRelease.CreatedAt"/>, a required member the one release writer
    /// always stamps — on every backend. The query language carries ONE sort key, so there is no
    /// node-date fallback for a row whose content is not a release; such a row sorts by an absent key.</para>
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's path.</param>
    /// <param name="limit">How many releases to show, or <c>null</c> for all.</param>
    internal static MeshSearchControl ReleasesList(string nodeTypePath, int? limit = null)
    {
        var list = Controls.MeshSearch
            .WithHiddenQuery(
                $"namespace:{nodeTypePath}/{ReleaseNodeType.ReleaseSegment} nodeType:{ReleaseNodeType.NodeType} sort:CreatedAt-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.List)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithReactiveMode(true);
        return limit is { } n ? list.WithItemLimit(n).WithMaxRows(n) : list;
    }

    /// <summary>
    /// The "Create Release" trigger, shared by the Releases and Configuration panes. Routes through
    /// the canonical, permission-checked entry point <see cref="NodeTypeReleaseExtensions.RequestNodeTypeRelease(MeshWeaver.Messaging.IMessageHub,string,bool,string?,System.Action{string}?)"/>:
    /// it verifies the caller holds <c>Permission.Compile</c> and refuses cleanly otherwise;
    /// on success it flips <see cref="NodeTypeDefinition.RequestedReleaseAt"/> (forced) through the
    /// node stream and the per-NodeType hub's release watcher compiles. No bespoke request type.
    /// </summary>
    private static ButtonControl CreateReleaseButton(string nodeTypePath, string? locale)
        => Controls.Button(LocalizationCatalog.Get("ui.createRelease", locale))
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Play())
            .WithReactiveClickAction(ctx => ReleaseClick(ctx.Host.Hub, nodeTypePath));

    /// <summary>
    /// The compile buttons' one write, as the CLICK's own outcome: requests a FORCED release of
    /// <paramref name="nodeTypePath"/> as the clicking user and answers the click with what became
    /// of it. Forced, so "Recompile" on an up-to-date type still compiles; on a type with changed
    /// sources forcing changes nothing.
    ///
    /// <para>🚨 <b>A refusal reaches the person who clicked.</b> The release request checks
    /// <c>Permission.Compile</c> and ANSWERS a refusal (it never faults — a caller batching many
    /// releases must not lose the rest to one of them). A click is not a batch: a viewer who holds
    /// <c>Update</c> but not <c>Compile</c> would press an enabled button and see nothing happen,
    /// with the reason only in the hub's log. So the answered refusal is turned into the click's
    /// error here, and the host refuses the click to the client with that sentence
    /// (<c>WithReactiveClickAction</c> → <c>LayoutAreaHost.FailClick</c>; Doc/GUI/ButtonPendingState).
    /// Completion is the trigger write having landed — never the compile it starts, which the
    /// per-NodeType hub's watcher runs and the page's status projection shows.</para>
    /// </summary>
    /// <param name="hub">The hub the click arrived on; its AccessContext is the clicking user.</param>
    /// <param name="nodeTypePath">The NodeType to release.</param>
    /// <returns>Completes when the release was requested; errors with the refusal's reason otherwise.</returns>
    internal static IObservable<System.Reactive.Unit> ReleaseClick(IMessageHub hub, string nodeTypePath)
        => Observable.Defer(() =>
        {
            string? refusal = null;
            return hub.ObserveNodeTypeRelease(
                    nodeTypePath,
                    force: true,
                    releaseNotes: null,
                    onError: reason =>
                    {
                        refusal = reason;
                        hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                            .CreateLogger(typeof(NodeTypeLayoutAreas))
                            .LogWarning("Release request for {Path} refused: {Reason}", nodeTypePath, reason);
                    })
                .SelectMany(requested => requested
                    ? Observable.Return(System.Reactive.Unit.Default)
                    : Observable.Throw<System.Reactive.Unit>(new InvalidOperationException(
                        refusal ?? $"The release of '{nodeTypePath}' was not requested.")));
        });



    /// <summary>
    /// The Configuration pane as a TEMPLATE: an editable settings form for the NodeType (Name,
    /// Icon, ChildrenQuery, DefaultNamespace, PageMaxWidth, Description, ReleaseNotes) bound
    /// DIRECTLY to the node, the live compile status and compile log, and a read-only preview of the
    /// Configuration lambda with an Edit button that opens the dedicated editor.
    ///
    /// <para>🚨 <b>Nothing here reads the node.</b> The pane used to be built only once the node
    /// had arrived — title, configuration preview and status lines interpolated from it, the
    /// preview copied into a <c>/data</c> slot per render, and a sources query issued per render to
    /// colour the release button. Now the form fields bind to the node (resolved on the GUI side
    /// through <c>IMeshNodeStreamCache</c>) and every derived line binds to the
    /// <see cref="NodeTypeStatusView"/> projection (Doc/GUI/DataBinding → "Templates first, data
    /// later").</para>
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's path (the hub's own).</param>
    /// <param name="locale">The viewer's locale, for the chrome's strings.</param>
    internal static UiControl BuildConfigurationTemplate(string nodeTypePath, string? locale)
    {
        string L(string key) => LocalizationCatalog.Get(key, locale);
        var status = NodeTypeStatusView.DataContext;
        var editHref = new LayoutAreaReference(HubConfigEditArea).ToHref(nodeTypePath);

        var runTestsButton = Controls.Button(L("ui.runTests"))
            .WithAppearance(Appearance.Outline)
            .WithIconStart(FluentIcons.Play())
            .WithClickAction(ctx =>
            {
                ctx.Host.Hub.Observe(new RunTestsRequest(),
                    o => o.WithTarget(ctx.Host.Hub.Address))
                    .Subscribe(
                        _ => { },
                        ex => ctx.Host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(NodeTypeLayoutAreas))
                            .LogWarning(ex, "RunTestsRequest failed on {Hub}", ctx.Host.Hub.Address));
                return Task.CompletedTask;
            });

        // Header row: the type's name + the live status line + Create Release + Run Tests.
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: space-between; align-items: center; gap: 16px;")
            .WithView(Controls.H2(NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.Title)))
                .WithStyle("margin: 0;") with { DataContext = status })
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("gap: 12px; align-items: center;")
                .WithView(Controls.Body(NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.StatusBadge)))
                    .WithStyle("color: var(--neutral-foreground-hint); font-size: 13px;") with { DataContext = status })
                .WithView(CreateReleaseButton(nodeTypePath, locale))
                .WithView(runTestsButton));

        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 20px;")
            .WithView(headerRow)
            .WithView(BuildCompileLogTemplate());

        // Editable settings form — bound DIRECTLY to the node stream (IMeshNodeStreamCache), ONE
        // source of truth. Display Name / Icon are node TOP-LEVEL fields (fields-mode DataContext);
        // the NodeTypeDefinition settings live in node.Content (content-mode DataContext). Each
        // control's edit writes straight back to the matching field on the node — no /data replica,
        // no debounced save subscription. (Pointer resolution against the node is case-insensitive,
        // so the camelCase node/Content JSON binds from these PascalCase pointers.)
        var nodeFieldsContext = LayoutAreaReference.GetMeshNodeDataContext(nodeTypePath, bindContent: false);
        var contentContext = LayoutAreaReference.GetMeshNodeDataContext(nodeTypePath, bindContent: true);

        var formGrid = Controls.Stack
            .WithStyle("display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 16px;")
            .WithView(new TextFieldControl(new JsonPointerReference(nameof(MeshNode.Name)))
            {
                Label = "Display Name",
                Immediate = true,
                DataContext = nodeFieldsContext
            })
            .WithView(new TextFieldControl(new JsonPointerReference(nameof(MeshNode.Icon)))
            {
                Label = "Icon",
                Placeholder = "content:icon.svg, <svg>…</svg>, or URL",
                Immediate = true,
                DataContext = nodeFieldsContext
            })
            .WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeDefinition.ChildrenQuery)))
            {
                Label = "Children Query",
                Placeholder = "e.g. nodeType:Person scope:descendants",
                Immediate = true,
                DataContext = contentContext
            })
            .WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeDefinition.DefaultNamespace)))
            {
                Label = "Default Namespace",
                Placeholder = "Pre-selected namespace in Create form",
                Immediate = true,
                DataContext = contentContext
            })
            .WithView(new TextFieldControl(new JsonPointerReference(nameof(NodeTypeDefinition.PageMaxWidth)))
            {
                Label = "Page Max Width",
                Placeholder = "e.g. 1200px or 100%",
                Immediate = true,
                DataContext = contentContext
            });

        stack = stack
            .WithView(formGrid)
            .WithView(new TextAreaControl(new JsonPointerReference(nameof(NodeTypeDefinition.Description)))
            {
                Label = "Description",
                Placeholder = "Long-form description shown in the Overview and Create dialog.",
                Immediate = true,
                DataContext = contentContext
            }.WithRows(4))
            // Release notes — what changed in the next compile, bound straight to
            // NodeTypeDefinition.ReleaseNotes; the release trigger reads them off the node.
            .WithView(new TextAreaControl(new JsonPointerReference(nameof(NodeTypeDefinition.ReleaseNotes)))
            {
                Label = "Release notes",
                Placeholder = "What changed in the next compile? Shown on each row in the Releases pane.",
                Immediate = true,
                DataContext = contentContext
            }.WithRows(3));

        // Configuration lambda — read-only preview, with a button to open the dedicated editor.
        stack = stack
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("justify-content: space-between; align-items: center; margin-top: 8px;")
                .WithView(Controls.H3(L("ui.configurationLambda")).WithStyle("margin: 0;"))
                .WithView(Controls.Button(L("common.edit"))
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Edit())
                    .WithNavigateToHref(editHref)))
            .WithView(ConfigurationPreview("280px", L("ui.noConfigLambda")));

        return stack;
    }

    /// <summary>
    /// The Configuration lambda, read-only, bound to <see cref="NodeTypeStatusView.ConfigurationCode"/>.
    /// An empty lambda shows <paramref name="placeholder"/>.
    /// </summary>
    private static CodeEditorControl ConfigurationPreview(string height, string placeholder)
        => new CodeEditorControl()
            .WithLanguage("csharp")
            .WithHeight(height)
            .WithLineNumbers(true)
            .WithMinimap(false)
            .WithWordWrap(true)
            .WithReadonly(true)
            .WithPlaceholder(placeholder) with
        {
            DataContext = NodeTypeStatusView.DataContext,
            Value = NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.ConfigurationCode))
        };

    /// <summary>
    /// The compile-log panel beneath the Configuration header, bound to the
    /// <see cref="NodeTypeStatusView"/> projection: hidden until a compile has been requested or
    /// recorded; then the running / failed / undetermined / published headline, the error text, and
    /// links to the latest release and the compile activity log
    /// (Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md → "Live progress").
    /// </summary>
    private static UiControl BuildCompileLogTemplate()
    {
        var status = NodeTypeStatusView.DataContext;
        return (Controls.Stack with
            {
                Style = NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.LogStyle)),
                DataContext = status
            })
            .WithView(Controls.Body(NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.LogHeadline))) with
            {
                Style = NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.LogHeadlineStyle)),
                DataContext = status
            })
            .WithView(Controls.Markdown(NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.LogDetail))) with
            {
                DataContext = status
            })
            .WithView(Controls.Markdown(NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.LogLinks)))
                .WithStyle("font-size: 12px; color: var(--neutral-foreground-hint);") with { DataContext = status });
    }

    /// <summary>
    /// Renders the HubConfiguration view: a TEMPLATE whose read-only lambda preview binds to the
    /// <see cref="NodeTypeStatusView"/> projection — the page is drawn at once and the code fills in.
    /// </summary>
    [Browsable(false)]
    public static UiControl HubConfigView(LayoutAreaHost host, RenderingContext ctx)
        => BuildHubConfigViewTemplate(host.Hub.Address.ToString(), host.ViewerLocale())
            .PublishingTo(NodeTypeStatusView.DataId, StatusProjection(host));

    /// <summary>
    /// The HubConfiguration view's template: title, the lambda read-only (an empty lambda shows
    /// "No Configuration defined."), Edit, and Back to the Configuration area.
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's path (the hub's own).</param>
    /// <param name="locale">The viewer's locale, for the chrome's strings.</param>
    internal static UiControl BuildHubConfigViewTemplate(string nodeTypePath, string? locale)
    {
        string L(string key) => LocalizationCatalog.Get(key, locale);
        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px;")
            .WithView(Controls.H2(L("ui.configuration")).WithStyle("margin-bottom: 16px;"))
            .WithView(Controls.Body("Lambda expression: Func<MessageHubConfiguration, MessageHubConfiguration>")
                .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 16px;"))
            .WithView(ConfigurationPreview("400px", L("ui.noConfiguration")))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("margin-top: 16px; gap: 8px;")
                .WithView(Controls.Button(L("common.edit"))
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Edit())
                    .WithNavigateToHref(new LayoutAreaReference(HubConfigEditArea).ToHref(nodeTypePath)))
                .WithView(Controls.Button(L("common.back"))
                    .WithAppearance(Appearance.Neutral)
                    .WithNavigateToHref(new LayoutAreaReference(ConfigurationArea).ToHref(nodeTypePath))));
    }


    /// <summary>
    /// Renders the Monaco editor for editing Configuration.
    /// Returns static structure with data-bound editor.
    /// </summary>
    [Browsable(false)]
    public static UiControl HubConfigEdit(LayoutAreaHost host, RenderingContext ctx)
    {
        // Subscribe to data streams
        host.SubscribeToDataStream(DefinitionDataId, GetNodeStream(host));
        host.SubscribeToDataStream(CodeFileDataId, host.Workspace.GetSingle<CodeConfiguration>());

        // Return structure with nested observable view
        return Controls.Stack
            .WithWidth("100%")
            .WithView(
                (h, c) => h.GetDataStream<MeshNode>(DefinitionDataId)
                    .CombineLatest(h.GetDataStream<CodeConfiguration>(CodeFileDataId))
                    .Select(tuple =>
                    {
                        var (node, codeFile) = tuple;
                        if (node == null)
                            return RenderLoading("Loading...");
                        var allCode = codeFile?.Code ?? "";
                        return BuildHubConfigEditContent(host, node, allCode);
                    }),
                "Editor"
            );
    }

    private static UiControl BuildHubConfigEditContent(LayoutAreaHost host, MeshNode node, string allCodeForAutocomplete)
    {
        var content = node.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);
        var hubAddress = host.Hub.Address;
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        // ID comes from hub address, not from content
        var nodeId = hubAddress.Segments.LastOrDefault() ?? "Unknown";

        // Data IDs for each editable field
        var displayNameDataId = Guid.NewGuid().AsString();
        var descriptionDataId = Guid.NewGuid().AsString();
        var iconNameDataId = Guid.NewGuid().AsString();
        var orderDataId = Guid.NewGuid().AsString();
        var childrenQueryDataId = Guid.NewGuid().AsString();
        var dependenciesDataId = Guid.NewGuid().AsString();
        var configurationDataId = Guid.NewGuid().AsString();

        // Initialize data streams
        host.UpdateData(displayNameDataId, node.Name ?? "");
        host.UpdateData(descriptionDataId, content?.Description ?? "");
        host.UpdateData(iconNameDataId, node.Icon ?? "");
        host.UpdateData(orderDataId, (node.Order ?? 0).ToString());
        host.UpdateData(childrenQueryDataId, content?.ChildrenQuery ?? "");
        host.UpdateData(dependenciesDataId, content?.Dependencies != null ? string.Join(", ", content.Dependencies) : "");
        host.UpdateData(configurationDataId, content?.Configuration ?? "config => config");

        // Header
        stack = stack.WithView(Controls.H2($"Edit: {node.Name ?? nodeId}").WithStyle("margin-bottom: 16px;"));

        // Form fields
        var formStyle = "display: grid; grid-template-columns: 150px 1fr; gap: 12px; align-items: center; margin-bottom: 12px;";

        // Display Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label(host.Localize("ui.displayName")).WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter display name...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(displayNameDataId) }));

        // Description
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label(host.Localize("ui.description")).WithStyle("font-weight: 500;"))
            .WithView(new TextAreaControl(new JsonPointerReference(""))
                .WithPlaceholder("Enter description...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(descriptionDataId) }));

        // Icon Name
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label(host.Localize("ui.iconName")).WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("e.g., Document, Folder...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(iconNameDataId) }));

        // Display Order
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label(host.Localize("ui.displayOrder")).WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("0")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(orderDataId) }));

        // Children Query
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label(host.Localize("ui.childrenQuery")).WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Query for children (e.g., nodeType:Person)")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(childrenQueryDataId) }));

        // Dependencies
        stack = stack.WithView(Controls.Stack
            .WithStyle(formStyle)
            .WithView(Controls.Label(host.Localize("ui.dependencies")).WithStyle("font-weight: 500;"))
            .WithView(new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Comma-separated node type paths...")
                .WithImmediate(true) with
            { DataContext = LayoutAreaReference.GetDataPointer(dependenciesDataId) }));

        // Configuration (code editor)
        stack = stack.WithView(Controls.H3(host.Localize("ui.configuration")).WithStyle("margin: 24px 0 8px 0;"));
        stack = stack.WithView(Controls.Body("Lambda expression: config => config.AddData(...)").WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 8px;"));

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

        // Cancel button
        var viewHref = new LayoutAreaReference(ConfigurationArea).ToHref(hubAddress);
        buttonRow = buttonRow.WithView(Controls.Button(host.Localize("common.cancel"))
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(viewHref));

        // Save button - sync click action: snapshot the form, then write THROUGH the node stream.
        // The form values are applied to the node's CURRENT state inside Update — the owning hub
        // serialises the write and merges it as a patch, so fields this form does not edit (a
        // compile status landing meanwhile, a concurrent rename) are not clobbered by a stale copy
        // of the whole node. This used to read the node once and post it back wholesale as a
        // DataChangeRequest — see Doc/Architecture/DataPlaneMessagesAreStreamPlumbing.
        buttonRow = buttonRow.WithView(Controls.Button(host.Localize("common.save"))
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(actx =>
            {
                // 🚨 The identity is captured HERE, on the click's delivery turn. The write below
                // runs after the form streams emit — possibly on another thread, where the
                // AsyncLocal AccessContext is gone — so it is issued under this captured caller.
                var access = host.Hub.ServiceProvider.GetService<AccessService>();
                var caller = access?.Context ?? access?.CircuitContext;
                Observable.CombineLatest(
                    host.Stream.GetDataStream<string>(displayNameDataId).Take(1),
                    host.Stream.GetDataStream<string>(descriptionDataId).Take(1),
                    host.Stream.GetDataStream<string>(iconNameDataId).Take(1),
                    host.Stream.GetDataStream<string>(orderDataId).Take(1),
                    host.Stream.GetDataStream<string>(childrenQueryDataId).Take(1),
                    host.Stream.GetDataStream<string>(dependenciesDataId).Take(1),
                    host.Stream.GetDataStream<string>(configurationDataId).Take(1),
                    (displayName, description, iconName, orderStr, childrenQuery, dependenciesStr, configuration) =>
                        new HubConfigForm(displayName, description, iconName, orderStr, childrenQuery, dependenciesStr, configuration))
                    .Take(1)
                    .SelectMany(form => access.RunAs(caller, () => host.Workspace.GetMeshNodeStream()
                        .Update<NodeTypeDefinition>((currentNode, currentDefinition) =>
                            ApplyHubConfigForm(currentNode, currentDefinition, form))))
                    .Take(1)
                    .Subscribe(
                        _ =>
                        {
                            var configNavHref = new LayoutAreaReference(ConfigurationArea).ToHref(hubAddress);
                            actx.Host.UpdateArea(actx.Area, new RedirectControl(configNavHref));
                        },
                        ex =>
                        {
                            var errorDialog = Controls.Dialog(
                                Controls.Markdown($"**Error saving:**\n\n{ex.Message}"),
                                "Save Failed"
                            ).WithSize("M");
                            actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                        });
                return Task.CompletedTask;
            }));

        stack = stack.WithView(buttonRow);

        return stack;
    }

    /// <summary>The Hub Configuration edit form's values, as snapshotted when Save is clicked.</summary>
    internal sealed record HubConfigForm(
        string? DisplayName,
        string? Description,
        string? IconName,
        string? Order,
        string? ChildrenQuery,
        string? Dependencies,
        string? Configuration);

    /// <summary>
    /// Applies the Hub Configuration edit form to the node's CURRENT state — the update lambda the
    /// Save button hands to the typed <c>GetMeshNodeStream().Update&lt;NodeTypeDefinition&gt;</c>. Only the fields the form edits are
    /// replaced; everything else on the node and its <see cref="NodeTypeDefinition"/> is carried
    /// through from <paramref name="currentNode"/>, never from a copy taken when the form rendered.
    /// </summary>
    /// <param name="currentNode">The node as the owner holds it now.</param>
    /// <param name="currentDefinition">Its content, read by the TYPED write: <c>null</c> only when the
    /// node has no content yet — content that is present but unreadable fails the write before this
    /// runs, so a default definition can never overwrite a real one.</param>
    /// <param name="form">The snapshotted form values.</param>
    internal static MeshNode ApplyHubConfigForm(MeshNode currentNode, NodeTypeDefinition? currentDefinition, HubConfigForm form)
    {
        if (!int.TryParse(form.Order, out var order)) order = 0;
        List<string>? dependencies = null;
        if (!string.IsNullOrWhiteSpace(form.Dependencies))
        {
            dependencies = form.Dependencies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (dependencies.Count == 0) dependencies = null;
        }
        var updatedDefinition = (currentDefinition ?? new NodeTypeDefinition()) with
        {
            Description = string.IsNullOrWhiteSpace(form.Description) ? null : form.Description,
            ChildrenQuery = string.IsNullOrWhiteSpace(form.ChildrenQuery) ? null : form.ChildrenQuery,
            Dependencies = dependencies,
            Configuration = string.IsNullOrWhiteSpace(form.Configuration) ? null : form.Configuration
        };
        return currentNode with
        {
            Name = string.IsNullOrWhiteSpace(form.DisplayName) ? null : form.DisplayName,
            Icon = string.IsNullOrWhiteSpace(form.IconName) ? null : form.IconName,
            Order = order,
            Content = updatedDefinition
        };
    }

    private static UiControl BuildInfoRow(string label, string value)
    {
        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 0; border-bottom: 1px solid var(--neutral-stroke-divider);")
            .WithView(Controls.Label($"{label}:").WithStyle("width: 150px; flex-shrink: 0; font-weight: 600;"))
            .WithView(Controls.Body(value));
    }

    /// <summary>
    /// Compile-state panel rendered at the top of <see cref="Overview"/>, as a TEMPLATE bound to the
    /// <see cref="NodeTypeStatusView"/> projection: the state chip (compiling / failed / undetermined
    /// / never compiled / source changed / up to date), the compile button — disabled while a compile
    /// runs — and a link to the latest release. Hidden for a NodeType with no code, which never
    /// participates in compilation. The decisions live in <see cref="NodeTypeStatusView.From"/>.
    /// The button routes through the permission-checked release request (<see cref="ReleaseClick"/>).
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's path.</param>
    internal static UiControl BuildCompileStatusPanel(string nodeTypePath)
    {
        var status = NodeTypeStatusView.DataContext;
        return (Controls.Stack.WithOrientation(Orientation.Horizontal) with
            {
                Style = NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.PanelStyle)),
                DataContext = status
            })
            .WithView(Controls.Body(NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.PanelChip))) with
            {
                Style = NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.PanelChipStyle)),
                DataContext = status
            })
            .WithView(new ButtonControl(NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.CompileLabel)))
                {
                    Disabled = NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.CompileDisabled)),
                    DataContext = status
                }
                .WithAppearance(Appearance.Accent)
                .WithReactiveClickAction(ctx => ReleaseClick(ctx.Host.Hub, nodeTypePath)))
            .WithView(Controls.Markdown(NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.LatestReleaseLink)))
                .WithStyle("margin-left: auto; font-size: 12px;") with { DataContext = status });
    }

    private static UiControl RenderLoading(string message)
        => Controls.Stack
            .WithStyle("padding: 24px; display: flex; align-items: center; justify-content: center;")
            .WithView(Controls.Progress(message, 0));

}

/// <summary>
/// Form DTO for the NodeType Configuration pane — carries the subset of MeshNode
/// and NodeTypeDefinition fields that the user can edit directly inline (Name, Icon,
/// Description, ChildrenQuery, DefaultNamespace, PageMaxWidth). The Configuration
/// lambda and Dependencies are edited in the dedicated HubConfigEdit Monaco view.
/// </summary>
public record NodeTypeConfigForm
{
    /// <summary>The display name of the NodeType.</summary>
    public string? Name { get; init; }
    /// <summary>The icon associated with the NodeType.</summary>
    public string? Icon { get; init; }
    /// <summary>The human-readable description of the NodeType.</summary>
    public string? Description { get; init; }
    /// <summary>The query used to list instances/children of this NodeType.</summary>
    public string? ChildrenQuery { get; init; }
    /// <summary>The default namespace applied to new instances of this NodeType.</summary>
    public string? DefaultNamespace { get; init; }
    /// <summary>The maximum page width used when rendering instances of this NodeType.</summary>
    public string? PageMaxWidth { get; init; }
    /// <summary>The pending release notes for the next NodeType release.</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>
    /// Builds a NodeTypeConfigForm from a mesh node and its optional NodeType definition.
    /// </summary>
    /// <param name="node">The mesh node to read values from.</param>
    /// <param name="def">The optional NodeType definition to read definition-level values from; may be null.</param>
    /// <returns>The populated configuration form.</returns>
    public static NodeTypeConfigForm FromNode(MeshNode node, NodeTypeDefinition? def) => new()
    {
        Name = node.Name,
        Icon = node.Icon,
        Description = def?.Description,
        ChildrenQuery = def?.ChildrenQuery,
        DefaultNamespace = def?.DefaultNamespace,
        PageMaxWidth = def?.PageMaxWidth,
        ReleaseNotes = def?.ReleaseNotes,
    };
}
