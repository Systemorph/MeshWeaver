using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// The data-bound chat composer layout areas. Both areas bind the <see cref="ThreadComposer"/>
/// state of THIS node — the composer node's own content out of a thread, or the thread's
/// embedded <see cref="Thread.Composer"/> when registered on a thread hub
/// (<see cref="ThreadComposerNodeType.ComposerOf"/> discriminates by NodeType).
///
/// <para><b>Binding discipline (the mid-edit-clobber fix):</b> the form data is seeded ONCE per
/// layout session and auto-save is installed ONCE (<see cref="BindComposerData"/>). Later node
/// emissions re-seed the form ONLY when they differ from the last value this session saved or
/// seeded — the echo of our own save compares equal and is skipped, so a server echo can never
/// overwrite in-flight typing, and stacked auto-save subscriptions can't accumulate.</para>
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
    /// Renders just the harness/agent/model pickers, data-bound + auto-persisting against THIS
    /// node's composer state. The control row is built ONCE from the first composer emission;
    /// all later updates flow through the data binding (see <see cref="BindComposerData"/>).
    /// </summary>
    public static UiControl ComposerSelectors(LayoutAreaHost host, RenderingContext context)
        => Controls.Stack.WithWidth("100%")
            .WithView((h, _) => ComposerStream(h)
                .Take(1)
                .Select(composer =>
                {
                    var dataId = BindComposerData(h, composer);
                    return (UiControl?)BuildSelectorRow(h, dataId);
                }));

    /// <summary>
    /// The composer node's default area: data-bound message editor + selector row + Send.
    /// Send submits via the canonical <see cref="HubThreadExtensions.StartThread"/>, copying the
    /// composer onto the created thread, emptying the draft, and stamping
    /// <see cref="ThreadComposer.OpenThreadPath"/> so the side panel navigates — all data-bound,
    /// no circuit access from this server-side hub.
    /// </summary>
    public static UiControl Composer(LayoutAreaHost host, RenderingContext context)
        => Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;")
            .WithView((h, _) => ComposerStream(h)
                .Take(1)
                .Select(composer =>
                {
                    var dataId = BindComposerData(h, composer);

                    var messageProp = typeof(ThreadComposer).GetProperty(nameof(ThreadComposer.MessageContent))!;
                    var editor = Controls.Stack.WithWidth("100%")
                        .WithView(h.Hub.ServiceProvider.MapToToggleableControl(
                            messageProp, dataId, canEdit: true, h, isToggleable: false));

                    var bottomRow = Controls.Stack
                        .WithOrientation(Orientation.Horizontal)
                        .WithStyle("gap: 8px; flex-wrap: nowrap; align-items: flex-end; width: 100%;")
                        .WithView(Controls.Stack.WithStyle("flex: 1 1 auto; min-width: 0;")
                            .WithView(BuildSelectorRow(h, dataId)))
                        .WithView(Controls.Stack.WithStyle("flex: 0 0 auto; margin-left: auto;")
                            .WithView(BuildSendButton(dataId)));

                    return (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;")
                        .WithView(editor)
                        .WithView(bottomRow);
                }));

    /// <summary>
    /// This node's composer state as a live stream — <see cref="ThreadComposerNodeType.ComposerOf"/>
    /// over the OWN node stream (composer node content or the thread's embedded composer).
    /// </summary>
    private static IObservable<ThreadComposer> ComposerStream(LayoutAreaHost host)
    {
        var logger = Logger(host);
        return host.Workspace.GetMeshNodeStream()
            .Select(node => ThreadComposerNodeType.ComposerOf(node, host.Hub.JsonSerializerOptions, logger))
            .Where(c => c is not null)
            .Select(c => c!);
    }

    private static ILogger? Logger(LayoutAreaHost host)
        => host.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);

    /// <summary>
    /// Seeds the form data and installs the bidirectional composer binding EXACTLY ONCE per
    /// layout session (gated by <see cref="LayoutAreaHost.TryMarkEditStateInitialized"/>):
    ///
    /// <list type="bullet">
    ///   <item><description><b>data → node (auto-save)</b>: debounced; writes only the EDITABLE
    ///   fields (message, harness/agent/model, attachments), preserving the node's authoritative
    ///   <see cref="ThreadComposer.ContextPath"/> / <see cref="ThreadComposer.OpenThreadPath"/>
    ///   so a stale form snapshot can never re-arm the navigate signal or clobber the side
    ///   panel's context write.</description></item>
    ///   <item><description><b>node → data (re-seed)</b>: only when the node value differs from
    ///   the last value this session saved or seeded. The echo of our own save compares EQUAL
    ///   (value equality, attachments by sequence) and is skipped — no mid-typing clobber, the
    ///   defect of re-running <c>UpdateData</c> on every emission.</description></item>
    /// </list>
    /// </summary>
    internal static string BindComposerData(LayoutAreaHost host, ThreadComposer initial)
    {
        var nodePath = host.Hub.Address.ToString();
        var dataId = EditLayoutArea.GetDataId(nodePath);
        if (!host.TryMarkEditStateInitialized($"composer-bind_{dataId}"))
            return dataId; // already wired this session

        var logger = Logger(host);
        var gate = new object();
        var last = initial;
        host.UpdateData(dataId, initial);

        host.RegisterForDisposal($"composer-autosave_{dataId}",
            host.Stream.GetDataStream<ThreadComposer>(dataId)
                .Debounce(TimeSpan.FromMilliseconds(300))
                .Subscribe(
                    updated =>
                {
                    if (updated is null) return;
                    lock (gate)
                    {
                        if (Equals(last, updated)) return;
                        last = updated;
                    }
                    host.Workspace.GetMeshNodeStream()
                        .Update(node =>
                        {
                            var current = ThreadComposerNodeType.ComposerOf(node, host.Hub.JsonSerializerOptions, logger);
                            if (node.Content is not null && current is null)
                                return node; // unreadable → leave alone, never clobber
                            var merged = (current ?? new ThreadComposer()) with
                            {
                                MessageContent = updated.MessageContent,
                                Harness = updated.Harness,
                                AgentName = updated.AgentName,
                                ModelName = updated.ModelName,
                                Attachments = updated.Attachments
                            };
                            return Equals(current, merged)
                                ? node
                                : ThreadComposerNodeType.WithComposer(node, merged, host.Hub.JsonSerializerOptions, logger);
                        })
                        .Subscribe(
                            _ => { },
                            ex => logger?.LogWarning(ex,
                                "[ThreadComposer] auto-save failed for {Path}", nodePath));
                },
                    // If the data stream itself faults, auto-save dies for the session —
                    // log loudly so a dead binding is visible instead of silent draft loss.
                    ex => logger?.LogWarning(ex,
                        "[ThreadComposer] auto-save data stream FAULTED for {Path} — drafts no longer persist this session",
                        nodePath)));

        host.RegisterForDisposal($"composer-reseed_{dataId}",
            host.Workspace.GetMeshNodeStream()
                .Select(n => ThreadComposerNodeType.ComposerOf(n, host.Hub.JsonSerializerOptions, logger))
                .Where(c => c is not null)
                .Subscribe(
                    c =>
                    {
                        lock (gate)
                        {
                            if (Equals(last, c)) return;
                            last = c!;
                        }
                        host.UpdateData(dataId, c!);
                    },
                    ex => logger?.LogWarning(ex,
                        "[ThreadComposer] re-seed stream FAULTED for {Path} — external composer changes no longer reflect this session",
                        nodePath)));

        return dataId;
    }

    /// <summary>
    /// Horizontal row of the three selector pickers sized to share width (flex: 1 1 0), kept on
    /// ONE line (nowrap) and bottom-aligned so the chat footer fits pickers + chips + Send on a
    /// single bottom row. Each picker shrinks rather than wrapping; min-width keeps them usable.
    /// </summary>
    private static UiControl BuildSelectorRow(LayoutAreaHost host, string dataId)
    {
        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 6px; flex-wrap: nowrap; align-items: flex-end; width: 100%;");
        foreach (var prop in SelectorProperties())
            row = row.WithView(
                Controls.Stack.WithStyle("flex: 1 1 0; min-width: 70px; max-width: 220px;")
                    .WithView(host.Hub.ServiceProvider.MapToToggleableControl(
                        prop, dataId, canEdit: true, host, isToggleable: false)));
        return row;
    }

    private static IEnumerable<PropertyInfo> SelectorProperties() =>
        SelectorPropertyNames
            .Select(typeof(ThreadComposer).GetProperty)
            .Where(p => p is not null)!;

    /// <summary>
    /// Send button — one-shot read of the current form data, then the canonical
    /// <see cref="HubThreadExtensions.StartThread"/>. Identity is resolved at CLICK time from
    /// the click delivery's <see cref="AccessContext"/> (hub/system principals filtered) — never
    /// captured at render time, where the ambient context can be the hub itself.
    /// </summary>
    private static UiControl BuildSendButton(string dataId)
        => Controls.Button("Send")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                var host = ctx.Host;
                var logger = Logger(host);
                var user = ResolveUser(host.Hub.ServiceProvider.GetService<AccessService>());
                host.Stream.GetDataStream<ThreadComposer>(dataId)
                    .Take(1)
                    .Subscribe(
                        edited => Send(host, edited, user, logger),
                        ex => logger?.LogWarning(ex, "[ThreadComposer] Send: form read failed"));
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
