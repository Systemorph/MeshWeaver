using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// The data-bound chat composer layout areas. Both areas bind the form controls DIRECTLY to the
/// <see cref="ThreadComposer"/> state of THIS node via a node-bound DataContext
/// (<see cref="ComposerContext"/>) — ONE source of truth, the node stream
/// (<c>IMeshNodeStreamCache</c>). No <c>/data</c> replica, no debounced save subscription, no
/// re-seed loop — each field edit writes straight back to the composer's inline location on the node.
///
/// <para><b>Where the composer lives (and the rule):</b> the composer is read/written via
/// <see cref="ThreadComposerNodeType.ComposerOf"/> / <c>WithComposer</c> in ONE of two inline shapes,
/// and the binding always targets that SAME inline location — NEVER a separate node:
/// <list type="bullet">
///   <item><description><b>No thread yet</b> → a standalone <c>ThreadComposer</c> node in the user's
///   home (<c>{user}/_Thread/ThreadComposer</c>): the composer IS the node's whole <c>Content</c>
///   (content-mode binding).</description></item>
///   <item><description><b>Once a thread exists</b> → the composer is the Thread's INLINE
///   <see cref="Thread.Composer"/> object on the thread node itself (fields-mode binding with
///   sub-path <c>content/composer</c>). The thread always refers to its own embedded composer
///   object, never an outside node.</description></item>
/// </list>
/// Because writes route through the owning hub's serialised action block, concurrent fields the
/// composer carries (<see cref="ThreadComposer.ContextPath"/> / <see cref="ThreadComposer.OpenThreadPath"/>,
/// set by the side panel) are never clobbered by a field edit.</para>
/// </summary>
public static class ThreadComposerView
{
    /// <summary>The composer area name — registered as the composer node's default area.</summary>
    public const string ComposerArea = "Composer";

    /// <summary>
    /// The selectors-only area: just the harness/agent/model <c>[MeshNode]</c> pickers, data-bound
    /// to THIS node's composer state and auto-persisting. The Blazor chat (<c>ThreadChatView</c>)
    /// embeds this so its harness/agent/model selection is 100% data-bound (no hand-rolled
    /// dropdowns) while keeping its own Monaco editor + attachments. Registered on BOTH the
    /// composer node type and thread hubs (binding <see cref="Thread.Composer"/>).
    /// </summary>
    public const string SelectorsArea = "Selectors";

    private const string LogCategory = "MeshWeaver.AI.ThreadComposerView";

    /// <summary>Adds the data-bound composer + selectors views; <see cref="ComposerArea"/> is the default area.</summary>
    public static MessageHubConfiguration AddThreadComposerView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(ComposerArea)
            .WithView(ComposerArea, Composer)
            .WithView(SelectorsArea, ComposerSelectors));

    /// <summary>The selector properties shown in <see cref="SelectorsArea"/>, in display order.</summary>
    private static readonly string[] SelectorPropertyNames =
        [nameof(ThreadComposer.Harness), nameof(ThreadComposer.AgentName), nameof(ThreadComposer.ModelName)];

    /// <summary>
    /// Renders ONLY the harness picker (no label — just the combobox), bound DIRECTLY to THIS node's
    /// composer state (<see cref="ComposerContext"/>) so the pick persists straight to the node.
    /// Agent + model are NOT shown here: the chat footer stays compact (harness + context + Send on
    /// one row), and agent/model are chosen via the <c>/agent</c> and <c>/model</c> slash-commands
    /// (which write the same composer). The control is built ONCE from the first composer emission;
    /// all later updates flow through the node-bound data binding.
    /// </summary>
    public static UiControl ComposerSelectors(LayoutAreaHost host, RenderingContext context)
        => Controls.Stack.WithWidth("100%")
            .WithView((h, _) => h.Workspace.GetMeshNodeStream()
                .Where(n => ThreadComposerNodeType.ComposerOf(n, h.Hub.JsonSerializerOptions, Logger(h)) is not null)
                .Take(1)
                // All THREE framework MeshNodePickerControls (harness · agent · model) — search,
                // keyboard nav, icons, default-to-first for free. Replaces the hand-rolled command
                // picker widget (a regression): a node-pick command now just surfaces the same nice
                // control the composer already uses. Each is data-bound to its [MeshNode] property
                // and auto-persists to the composer node.
                .Select(node => (UiControl?)BuildSelectorRow(
                    h, EditLayoutArea.GetDataId(h.Hub.Address.ToString()), ComposerContext(h, node))));

    /// <summary>
    /// The composer node's default area: data-bound message editor + selector row + Send.
    /// Send submits via the canonical <see cref="HubThreadExtensions.StartThread"/>, copying the
    /// composer onto the created thread, emptying the draft, and stamping
    /// <see cref="ThreadComposer.OpenThreadPath"/> so the side panel navigates — all data-bound,
    /// no circuit access from this server-side hub.
    /// </summary>
    public static UiControl Composer(LayoutAreaHost host, RenderingContext context)
        => Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;")
            .WithView((h, _) => h.Workspace.GetMeshNodeStream()
                .Where(n => ThreadComposerNodeType.ComposerOf(n, h.Hub.JsonSerializerOptions, Logger(h)) is not null)
                .Take(1)
                .Select(node =>
                {
                    var ctx = ComposerContext(h, node);
                    var dataId = EditLayoutArea.GetDataId(h.Hub.Address.ToString());

                    var messageProp = typeof(ThreadComposer).GetProperty(nameof(ThreadComposer.MessageContent))!;
                    var editor = Controls.Stack.WithWidth("100%")
                        .WithView(h.Hub.ServiceProvider.MapToToggleableControl(
                            messageProp, dataId, canEdit: true, h, isToggleable: false, boundDataContext: ctx));

                    var bottomRow = Controls.Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithStyle("gap: 8px; flex-wrap: nowrap; align-items: flex-end; width: 100%;")
                        .WithView(Controls.Stack.WithStyle("flex: 1 1 auto; min-width: 0;")
                            .WithView(BuildSelectorRow(h, dataId, ctx)))
                        .WithView(Controls.Stack.WithStyle("flex: 0 0 auto; margin-left: auto;")
                            .WithView(BuildSendButton()));

                    return (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;")
                        .WithView(editor)
                        .WithView(bottomRow);
                }));

    private static ILogger? Logger(LayoutAreaHost host)
        => host.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);

    /// <summary>
    /// The node-bound DataContext the composer form binds against — ONE source of truth, the node
    /// stream (IMeshNodeStreamCache). The composer lives in one of TWO shapes
    /// (<see cref="ThreadComposerNodeType.ComposerOf"/>), and this resolves the binding root to the
    /// SAME inline location each shape reads from — it NEVER references a separate node:
    /// <list type="bullet">
    ///   <item><description><b>standalone <c>ThreadComposer</c> node</b> (the user-home new-chat
    ///   composer, used when no thread exists yet): the composer IS the node's whole
    ///   <c>Content</c> → content-mode, no sub-path. Field pointers (<c>messageContent</c>,
    ///   <c>harness</c>, …) resolve against the Content.</description></item>
    ///   <item><description><b><c>Thread</c> node</b> (once a thread exists): the composer is the
    ///   thread's INLINE <see cref="Thread.Composer"/> object — fields-mode with sub-path
    ///   <c>content/composer</c>, so a <c>harness</c> pointer resolves to
    ///   <c>content/composer/harness</c> on the thread node ITSELF. The thread always binds its own
    ///   embedded composer object, never an external composer node.</description></item>
    /// </list>
    /// Each field edit writes straight back to that location on the node stream — no <c>/data</c>
    /// replica, no debounced save subscription. The owning hub serialises writes, so concurrent
    /// fields (ContextPath / OpenThreadPath set by the side panel) are never clobbered.
    /// </summary>
    private static string ComposerContext(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = host.Hub.Address.ToString();
        return node?.NodeType == ThreadNodeType.NodeType
            ? LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false, subPath: $"{nameof(MeshNode.Content).ToCamelCase()}/{nameof(Thread.Composer).ToCamelCase()}")
            : LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: true);
    }

    /// <summary>
    /// Horizontal row of the three selector pickers sized to share width (flex: 1 1 0), kept on
    /// ONE line (nowrap) and bottom-aligned so the chat footer fits pickers + chips + Send on a
    /// single bottom row. Each picker shrinks rather than wrapping; min-width keeps them usable.
    /// </summary>
    private static UiControl BuildSelectorRow(LayoutAreaHost host, string dataId, string boundContext)
    {
        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 6px; flex-wrap: nowrap; align-items: flex-end; width: 100%;");
        foreach (var prop in SelectorProperties())
            row = row.WithView(
                Controls.Stack.WithStyle("flex: 1 1 0; min-width: 70px; max-width: 220px;")
                    .WithView(host.Hub.ServiceProvider.MapToToggleableControl(
                        prop, dataId, canEdit: true, host, isToggleable: false, boundDataContext: boundContext)));
        return row;
    }

    private static IEnumerable<PropertyInfo> SelectorProperties() =>
        SelectorPropertyNames
            .Select(typeof(ThreadComposer).GetProperty)
            .Where(p => p is not null)!;

    /// <summary>
    /// Builds JUST the harness <see cref="MeshNodePickerControl"/> — no label, just the combobox —
    /// data-bound to the composer's <see cref="ThreadComposer.Harness"/> field via the node-bound
    /// <paramref name="boundContext"/>. Constructed directly from the property's <c>[MeshNode]</c>
    /// attribute (the same query/layout/open/default the standard editor would build), bypassing
    /// <c>MapToToggleableControl</c> so no "Harness" label row is rendered. The picker mutates the
    /// <c>harness</c> pointer, which writes straight back to the composer on the node stream.
    /// </summary>
    private static UiControl BuildHarnessPicker(LayoutAreaHost host, string boundContext)
    {
        var harnessProp = typeof(ThreadComposer).GetProperty(nameof(ThreadComposer.Harness))!;
        var meshNodeAttr = harnessProp.GetCustomAttribute<MeshNodeAttribute>()!;
        var nodeNamespace = host.Hub.Address.ToString();
        var picker = new MeshNodePickerControl(
            new JsonPointerReference(nameof(ThreadComposer.Harness).ToCamelCase()!))
        {
            Queries = MeshNodeAttribute.ResolveQueries(meshNodeAttr.Queries, nodeNamespace, nodeNamespace),
            Layout = meshNodeAttr.Layout,
            Open = meshNodeAttr.Open,
            DefaultToFirst = meshNodeAttr.DefaultToFirst,
            DataContext = boundContext
        };
        return Controls.Stack
            .WithStyle("min-width: 90px; max-width: 220px;")
            .WithView(picker);
    }

    /// <summary>
    /// Send button — one-shot read of the composer straight off the node stream (ONE source of
    /// truth), then the canonical <see cref="HubThreadExtensions.StartThread"/>. Identity is
    /// resolved at CLICK time from the click delivery's <see cref="AccessContext"/> (hub/system
    /// principals filtered) — never captured at render time, where the ambient context can be the
    /// hub itself.
    /// </summary>
    private static UiControl BuildSendButton()
        => Controls.Button("Send")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                var host = ctx.Host;
                var logger = Logger(host);
                var user = ResolveUser(host.Hub.ServiceProvider.GetService<AccessService>());
                host.Workspace.GetMeshNodeStream()
                    .Select(n => ThreadComposerNodeType.ComposerOf(n, host.Hub.JsonSerializerOptions, logger))
                    .Where(c => c is not null)
                    .Take(1)
                    .Subscribe(
                        edited => Send(host, edited, user, logger),
                        ex => logger?.LogWarning(ex, "[ThreadComposer] Send: composer read failed"));
                return Task.CompletedTask;
            });

    /// <summary>
    /// The submit pipeline: thread under <c>{MainNodeOf(ContextPath) ?? user}/_Thread/…</c>,
    /// composer copied onto the thread, then ONE composer-node write that empties the draft and
    /// stamps <see cref="ThreadComposer.OpenThreadPath"/> = the created thread's path (the side
    /// panel observes the composer node, opens the thread, and clears the signal).
    /// </summary>
    private static void Send(LayoutAreaHost host, ThreadComposer? edited, string? user, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(edited?.MessageContent))
            return;

        var contextPath = string.IsNullOrEmpty(edited!.ContextPath) ? null : edited.ContextPath;
        var ns = contextPath is null ? user : ThreadNodeType.MainNodeOf(contextPath);
        if (string.IsNullOrEmpty(ns))
        {
            logger?.LogWarning(
                "[ThreadComposer] Send ignored — no resolvable namespace (no user identity and no context)");
            return;
        }

        host.Hub.StartThread(
            namespacePath: ns!,
            userText: edited.MessageContent!,
            agentName: edited.AgentName,
            modelName: edited.ModelName,
            harness: edited.Harness,
            contextPath: contextPath ?? ns,
            attachments: edited.Attachments,
            createdBy: user,
            composer: edited,
            onCreated: node => host.Workspace.GetMeshNodeStream()
                .Update(n =>
                {
                    var c = ThreadComposerNodeType.ComposerOf(n, host.Hub.JsonSerializerOptions, logger);
                    if (n.Content is not null && c is null)
                        return n; // unreadable → leave alone, never clobber
                    return ThreadComposerNodeType.WithComposer(
                        n,
                        (c ?? new ThreadComposer()) with
                        {
                            MessageContent = null,
                            Attachments = null,
                            OpenThreadPath = node.Path
                        },
                        host.Hub.JsonSerializerOptions, logger);
                })
                .Subscribe(
                    _ => { },
                    ex => logger?.LogWarning(ex,
                        "[ThreadComposer] post-send clear/navigate-stamp failed for {Thread}", node.Path)),
            onError: err => logger?.LogWarning("[ThreadComposer] StartThread failed: {Error}", err));
    }

    /// <summary>
    /// The submitting user's identity, filtered the same way as every other resolver
    /// (<c>system-security</c> and hub principals are NOT users). AsyncLocal context first —
    /// at click time it carries the click delivery's <see cref="AccessContext"/>.
    /// </summary>
    private static string? ResolveUser(AccessService? access)
    {
        if (access is null) return null;
        foreach (var candidate in new[] { access.Context?.ObjectId, access.CircuitContext?.ObjectId })
        {
            if (!string.IsNullOrEmpty(candidate)
                && candidate != WellKnownUsers.System
                && !AccessService.LooksLikeHubPrincipal(candidate))
                return candidate;
        }
        return null;
    }
}
