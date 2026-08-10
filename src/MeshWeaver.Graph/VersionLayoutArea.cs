using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout areas for version history: list of versions and diff view.
/// </summary>
public static class VersionLayoutArea
{
    /// <summary>
    /// Returns the Versions menu item if the user has Read permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Read))
            return null;
        return new("Versions", MeshNodeLayoutAreas.VersionsArea, Order: 55,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.VersionsArea))
            { LabelKey = "menu.versions" };
    }
    /// <summary>
    /// Renders the Versions list showing all historical versions of the current node.
    /// Each row has version number, timestamp, and Compare/Restore buttons.
    /// </summary>
    /// <summary>
    /// The well-known path of the central Collaboration plugin's review workspace. When it exists
    /// on the mesh, the Versions page DELEGATES to its TrackChanges area — the whole track-changes
    /// experience then lives in a git-synced plugin and evolves without core image rolls. Core
    /// keeps only this thin hook plus the built-in fallback (also reachable via ?builtin=1).
    /// </summary>
    internal const string CollaborationWorkspacePath = "Collaboration/Workspace";

    [Browsable(false)]
    public static IObservable<UiControl?> Versions(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var versionQuery = host.Hub.ServiceProvider.GetService<IVersionQuery>();
        var access = host.Hub.ServiceProvider.GetService<AccessService>();

        if (versionQuery == null)
        {
            return Observable.Return<UiControl?>(
                Controls.Stack.WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host))
                    .WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">Version history is not available.</p>")));
        }

        var mesh = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (host.GetQueryStringParamValue("builtin") != "1" && mesh is not null)
        {
            return HasCollaborationWorkspace(mesh)
                .Select(hasWorkspace => hasWorkspace
                    ? Observable.Return<UiControl?>(DelegatedVersions(host, hubPath))
                    : BuiltInVersions(host, hubPath, versionQuery, access))
                .Switch();
        }

        return BuiltInVersions(host, hubPath, versionQuery, access);
    }

    /// <summary>Embeds the Collaboration workspace's TrackChanges area for this node, with a
    /// lightweight link back to the built-in list.</summary>
    private static UiControl DelegatedVersions(LayoutAreaHost host, string hubPath) =>
        Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host))
            .WithView(Controls.LayoutArea(CollaborationWorkspacePath, "TrackChanges", hubPath)
                .WithShowProgress(false))
            .WithView(Controls.Html(
                $"<p style=\"margin-top: 8px;\"><a href=\"{MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.VersionsArea, "builtin=1")}\" " +
                "style=\"color: var(--neutral-foreground-hint); font-size: .8rem;\">Use the built-in version list</a></p>"));

    /// <summary>One bounded existence probe over the query index (no per-node hub activation —
    /// probing a MISSING node's hub would cost the full activation timeout).</summary>
    private static IObservable<bool> HasCollaborationWorkspace(IMeshService mesh) =>
        mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{CollaborationWorkspacePath}"))
            .Scan(false, (found, change) =>
                found || change.Items.Any(n => n.Path == CollaborationWorkspacePath))
            .StartWith(false)
            .Throttle(TimeSpan.FromMilliseconds(800))
            .Take(1);

    /// <summary>Data-stream id holding the chosen FROM version for this node's compare picker.</summary>
    internal static string FromStreamId(string hubPath) => $"compareFrom_{hubPath.Replace("/", "_")}";

    /// <summary>Data-stream id holding the chosen TO version for this node's compare picker.</summary>
    internal static string ToStreamId(string hubPath) => $"compareTo_{hubPath.Replace("/", "_")}";

    private static IObservable<long> VersionSelection(LayoutAreaHost host, string id) =>
        host.GetDataStream<string>(id)
            .Select(s => long.TryParse(s, out var parsed) ? parsed : 0L)
            .StartWith(0L)
            .DistinctUntilChanged();

    /// <summary>
    /// The version list IS the picker. A comparison needs two endpoints, so the page never guesses
    /// one: each row can be claimed as <b>From</b> or <b>To</b>, and Compare stays disabled until
    /// both are named. Alongside that, every row except the current one carries the one-click
    /// "compare with current" — by far the most common question ("what has happened since?"), which
    /// should never cost two clicks and a mental note of a version number.
    /// <para>
    /// The list re-reads whenever the node's version moves, so an edit made in another tab shows up
    /// as a new row rather than leaving the reader picking from a stale history.
    /// </para>
    /// </summary>
    private static IObservable<UiControl?> BuiltInVersions(
        LayoutAreaHost host, string hubPath, IVersionQuery versionQuery, AccessService? access)
    {
        // Both endpoints ride data streams (as strings — data streams are reference-typed).
        var fromId = FromStreamId(hubPath);
        var toId = ToStreamId(hubPath);

        var live = host.Workspace.GetMeshNodeStream()
            .Where(node => node is not null)
            .Select(node => node!.Version)
            .DistinctUntilChanged();

        return live
            .Select(currentVersion => versionQuery.GetVersions(hubPath).ToList()
                .Select(versions => (currentVersion, versions)))
            .Switch()
            .CombineLatest(
                VersionSelection(host, fromId),
                VersionSelection(host, toId),
                (state, fromVersion, toVersion) =>
        {
            var (currentVersion, rows) = state;
            // One row per version. A store that hands back the same version twice must not turn the
            // picker into a crash (ToDictionary) or a duplicated row.
            var versions = rows.DistinctBy(v => v.Version).OrderByDescending(v => v.Version).ToList();
            var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

            var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea);
            stack = stack.WithView(
                Controls.Stack.WithOrientation(Orientation.Horizontal)
                    .WithStyle("align-items: center; gap: 8px; margin-bottom: 16px;")
                    .WithView(Controls.Button(host.Localize("common.back"))
                        .WithAppearance(Appearance.Lightweight)
                        .WithIconStart(FluentIcons.ArrowLeft())
                        .WithNavigateToHref(backHref)));

            stack = stack.WithView(Controls.Title(host.Localize("versions.title"), 2)
                .WithStyle("margin: 0 0 16px 0;"));

            if (versions.Count == 0)
            {
                stack = stack.WithView(Controls.Body(host.Localize("versions.none"))
                    .WithStyle("color: var(--neutral-foreground-hint);"));
                return (UiControl?)stack;
            }

            var byVersion = versions.ToDictionary(v => v.Version);
            stack = stack.WithView(BuildCompareBar(host, hubPath, byVersion, access, fromVersion, toVersion, fromId, toId));

            foreach (var version in versions)
                stack = stack.WithView(BuildVersionRow(
                    host, hubPath, version, access, currentVersion, fromVersion, toVersion, fromId, toId));

            return (UiControl?)stack;
        });
    }

    /// <summary>
    /// The standing statement of what will be compared, with Compare disabled until both endpoints
    /// are named. A disabled button that says WHY beats a button that silently does nothing.
    /// </summary>
    private static UiControl BuildCompareBar(
        LayoutAreaHost host, string hubPath, IReadOnlyDictionary<long, MeshNodeVersion> byVersion,
        AccessService? access, long fromVersion, long toVersion, string fromId, string toId)
    {
        var ready = fromVersion > 0 && toVersion > 0;
        // No endpoints, no destination: the button is disabled anyway, but it never carries a URL
        // that would compare v0 with v0 if some future skin ignored that.
        var compare = Controls.Button(host.Localize("ui.compare"))
            .WithAppearance(ready ? Appearance.Accent : Appearance.Outline)
            .WithDisabled(!ready);
        if (ready)
            compare = compare.WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(
                hubPath, MeshNodeLayoutAreas.VersionDiffArea,
                $"from={Math.Min(fromVersion, toVersion)}&to={Math.Max(fromVersion, toVersion)}"));

        var bar = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 12px; flex-wrap: wrap; padding: 12px 16px; " +
                       "margin-bottom: 16px; border-radius: 6px; background: var(--neutral-layer-2);")
            .WithView(Controls.Body(host.Localize("versions.from"))
                .WithStyle("color: var(--neutral-foreground-hint); font-weight: 600;"))
            .WithView(Controls.Badge(Endpoint(host, byVersion, access, fromVersion)))
            .WithView(Controls.Body("→").WithStyle("color: var(--neutral-foreground-hint);"))
            .WithView(Controls.Body(host.Localize("versions.to"))
                .WithStyle("color: var(--neutral-foreground-hint); font-weight: 600;"))
            .WithView(Controls.Badge(Endpoint(host, byVersion, access, toVersion)))
            .WithView(compare);

        // Say WHY Compare is dead, in place. A tooltip on a disabled control frequently never
        // fires, which is exactly when the reader most needs the sentence.
        if (!ready)
            bar = bar.WithView(Controls.Body(host.Localize("versions.pickBoth"))
                .WithStyle("flex-basis: 100%; color: var(--neutral-foreground-hint);"));

        if (fromVersion > 0 || toVersion > 0)
            bar = bar.WithView(Controls.Button(host.Localize("versions.clear"))
                .WithAppearance(Appearance.Lightweight)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(fromId, "");
                    ctx.Host.UpdateData(toId, "");
                    return Task.CompletedTask;
                }));

        return bar;
    }

    private static string Endpoint(
        LayoutAreaHost host, IReadOnlyDictionary<long, MeshNodeVersion> byVersion,
        AccessService? access, long version)
    {
        if (version <= 0)
            return host.Localize("versions.notChosen");
        return byVersion.TryGetValue(version, out var summary)
            ? $"v{version} · {access.ToDisplayTime(summary.LastModified):g}"
            : $"v{version}";
    }

    /// <summary>
    /// One version, and the three things a reader wants to do with it: make it the baseline, make it
    /// the target, or just see what has happened since. Picking an endpoint that would invert the
    /// pair clears the other one instead of offering an impossible comparison — the picker cannot be
    /// driven into a state that Compare would have to reject.
    /// </summary>
    private static UiControl BuildVersionRow(
        LayoutAreaHost host, string hubPath, MeshNodeVersion version, AccessService? access,
        long currentVersion, long fromVersion, long toVersion, string fromId, string toId)
    {
        var thisVersion = version.Version;
        var isFrom = thisVersion == fromVersion;
        var isTo = thisVersion == toVersion;
        var isCurrent = thisVersion == currentVersion;
        var selected = isFrom || isTo;

        var label = isCurrent
            ? $"v{thisVersion} · {host.Localize("versions.current")}"
            : $"v{thisVersion}";

        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 16px; flex-wrap: wrap; padding: 12px 16px; border: 1px solid " +
                       (selected ? "var(--accent-fill-rest)" : "var(--neutral-stroke-rest)") +
                       "; border-radius: 6px; margin-bottom: 8px;")
            .WithView(Controls.Body(label).WithStyle("min-width: 110px; font-weight: 600;"))
            .WithView(Controls.Body($"{access.ToDisplayTime(version.LastModified):g}")
                .WithStyle("flex: 1; min-width: 140px; color: var(--neutral-foreground-hint);"))
            .WithView(Controls.Body(version.ChangedBy ?? "—")
                .WithStyle("min-width: 120px; color: var(--neutral-foreground-hint);"));

        // The ✓ marks the claimed endpoint without a second translated word, and the same button
        // releases it — one control, both directions.
        row = row.WithView(Controls.Button(Claimed(host.Localize("versions.from"), isFrom))
            .WithAppearance(isFrom ? Appearance.Accent : Appearance.Outline)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(fromId, isFrom ? "" : thisVersion.ToString());
                // A From at or after the current To would invert the pair — drop the To rather than
                // silently compare backwards.
                if (!isFrom && toVersion > 0 && toVersion <= thisVersion)
                    ctx.Host.UpdateData(toId, "");
                return Task.CompletedTask;
            }));

        row = row.WithView(Controls.Button(Claimed(host.Localize("versions.to"), isTo))
            .WithAppearance(isTo ? Appearance.Accent : Appearance.Outline)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(toId, isTo ? "" : thisVersion.ToString());
                if (!isTo && fromVersion > 0 && fromVersion >= thisVersion)
                    ctx.Host.UpdateData(fromId, "");
                return Task.CompletedTask;
            }));

        // "What changed since this version?" — the question people actually arrive with. Absent on
        // the current version, where it would compare a version to itself.
        if (!isCurrent)
            row = row.WithView(Controls.Button(host.Localize("versions.compareWithCurrent"))
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.BranchCompare())
                .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(
                    hubPath, MeshNodeLayoutAreas.VersionDiffArea, $"version={thisVersion}")));

        return row;
    }

    private static string Claimed(string label, bool isClaimed) => isClaimed ? $"{label} ✓" : label;

    /// <summary>
    /// Renders the diff view for a node. Supports three modes:
    ///   <list type="bullet">
    ///     <item><c>?from=X&amp;to=Y</c> — compare two historical versions.</item>
    ///     <item><c>?version=X</c> — compare a historical version to the current node.</item>
    ///     <item>no parameters — compare the previous version to the current node.</item>
    ///   </list>
    /// <para>
    /// This is also the ONE place the markdown redline ("what changed, by whom") is switched on:
    /// prose renders as an inline tracked-change view of the stated version pair, other content as
    /// the Monaco side-by-side diff. <c>?view=source</c> forces the Monaco view for prose too, so
    /// the raw markdown diff (front matter, link syntax) stays one click away.
    /// </para>
    /// Emits the diff once per permission state — the diff editor is expensive to re-create, so we
    /// avoid re-emitting on every node-stream tick.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> VersionDiff(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var versionQuery = host.Hub.ServiceProvider.GetService<IVersionQuery>();
        if (versionQuery == null)
            return Observable.Return<UiControl?>(Controls.Body(host.Localize("versions.unavailable")));

        // Restoring and reverting are writes: offer them only to a user who could carry them out.
        return host.Hub.GetEffectivePermissions(hubPath)
            .Select(perms => perms.HasFlag(Permission.Update))
            .DistinctUntilChanged()
            .Select(canEdit => BuildDiff(host, hubPath, versionQuery, canEdit))
            .Switch();
    }

    private static IObservable<UiControl?> BuildDiff(
        LayoutAreaHost host, string hubPath, IVersionQuery versionQuery, bool canEdit)
    {
        var options = host.Hub.JsonSerializerOptions;
        var fromStr = host.GetQueryStringParamValue("from");
        var toStr = host.GetQueryStringParamValue("to");

        // Mode 1: from=X&to=Y — compare two historical versions.
        if (long.TryParse(fromStr, out var fromVersion) && long.TryParse(toStr, out var toVersion))
        {
            return versionQuery.GetVersion(hubPath, fromVersion, options)
                .Zip(versionQuery.GetVersion(hubPath, toVersion, options),
                    (fromNode, toNode) => (fromNode, toNode))
                .Select(pair =>
                {
                    var (fromNode, toNode) = pair;
                    if (fromNode == null)
                        return (UiControl?)Controls.Body(host.Localize("versions.notFound", fromVersion));
                    if (toNode == null)
                        return (UiControl?)Controls.Body(host.Localize("versions.notFound", toVersion));

                    return (UiControl?)BuildDiffStack(host, hubPath, fromNode, toNode, options, canEdit,
                        $"v{fromVersion}", $"v{toVersion}",
                        host.Localize("versions.comparingVersions", fromVersion, toVersion),
                        restoreVersion: fromVersion,
                        // Both endpoints are historical, so the redline is pinned: it shows the
                        // document AS OF toVersion, not as it stands now.
                        compareToVersion: toVersion);
                });
        }

        var versionStr = host.GetQueryStringParamValue("version");

        // Mode 3: NO parameters — show the changes since the LAST version. Clicking into the diff
        // without picking anything should immediately show what changed most recently, not an
        // "invalid parameter" notice.
        if (string.IsNullOrEmpty(versionStr))
        {
            return host.Hub.GetMeshNode(hubPath)
                .SelectMany(currentNode =>
                {
                    if (currentNode == null)
                        return Observable.Return<UiControl?>(Controls.Body(host.Localize("versions.nodeNotFound", hubPath)));
                    return versionQuery.GetVersionBefore(hubPath, currentNode.Version, options)
                        .Select(previousNode =>
                        {
                            if (previousNode == null)
                                return (UiControl?)Controls.Body(host.Localize("versions.noEarlierVersion"))
                                    .WithStyle("color: var(--neutral-foreground-hint);");
                            return (UiControl?)BuildDiffStack(host, hubPath, previousNode, currentNode, options, canEdit,
                                $"v{previousNode.Version}", host.Localize("versions.current"),
                                host.Localize("versions.changesSince", previousNode.Version),
                                restoreVersion: previousNode.Version,
                                compareToVersion: null);
                        });
                });
        }

        // Mode 2: version=X — compare historical version to current.
        if (!long.TryParse(versionStr, out var targetVersion))
            return Observable.Return<UiControl?>(Controls.Body(host.Localize("versions.invalidParameter")));

        // One-shot read of the current node via GetDataRequest — true request/response,
        // no live workspace subscription. Render once with the snapshot; the diff view
        // doesn't need to re-render on subsequent stream ticks.
        return host.Hub.GetMeshNode(hubPath)
            .SelectMany(currentNode =>
            {
                if (currentNode == null)
                    return Observable.Return<UiControl?>(Controls.Body(host.Localize("versions.nodeNotFound", hubPath)));

                return versionQuery.GetVersion(hubPath, targetVersion, options)
                    .Select(historicalNode =>
                    {
                        if (historicalNode == null)
                            return (UiControl?)Controls.Body(host.Localize("versions.notFound", targetVersion));

                        return (UiControl?)BuildDiffStack(host, hubPath, historicalNode, currentNode, options, canEdit,
                            $"v{targetVersion}", host.Localize("versions.current"),
                            host.Localize("versions.comparingWithCurrent", targetVersion),
                            restoreVersion: targetVersion,
                            // The target is the LIVE document: the redline follows further edits and
                            // each hunk can be reverted out of it.
                            compareToVersion: null);
                    });
            });
    }

    private static UiControl BuildDiffStack(
        LayoutAreaHost host, string hubPath,
        MeshNode originalNode, MeshNode modifiedNode,
        JsonSerializerOptions options, bool canEdit,
        string originalLabel, string modifiedLabel,
        string title, long restoreVersion, long? compareToVersion)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.VersionsArea);
        stack = stack.WithView(
            Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px; margin-bottom: 16px;")
                .WithView(Controls.Button(host.Localize("ui.backToVersions"))
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(backHref)));

        stack = stack.WithView(Controls.Title(title, 2).WithStyle("margin: 0 0 16px 0;"));

        var isProse = IsMarkdownContent(originalNode, options) || IsMarkdownContent(modifiedNode, options);
        var isMarkdown = HoldsMarkdown(originalNode) || HoldsMarkdown(modifiedNode);
        var wantsSource = string.Equals(host.GetQueryStringParamValue("view"), "source", StringComparison.OrdinalIgnoreCase);

        stack = isMarkdown && !wantsSource
            ? stack
                .WithView(BuildRedline(hubPath, modifiedNode, options, originalNode.Version, compareToVersion, canEdit))
                .WithView(ViewSwitchLink(host, hubPath, toSource: true))
            : stack
                .WithView(new DiffEditorControl
                {
                    OriginalContent = ExtractDiffContent(originalNode, options),
                    ModifiedContent = ExtractDiffContent(modifiedNode, options),
                    OriginalLabel = originalLabel,
                    ModifiedLabel = modifiedLabel,
                    Language = isProse ? "markdown" : "json",
                    Height = "600px"
                });

        if (isMarkdown && wantsSource)
            stack = stack.WithView(ViewSwitchLink(host, hubPath, toSource: false));

        if (canEdit)
            stack = stack.WithView(
                Controls.Stack.WithStyle("margin-top: 16px;")
                    .WithView(Controls.Button(host.Localize("versions.restore", restoreVersion))
                        .WithAppearance(Appearance.Accent)
                        .WithIconStart(FluentIcons.ArrowUndo())
                        .WithClickAction(ctx =>
                        {
                            ctx.Hub.Post(new RollbackNodeRequest(hubPath, restoreVersion));
                            return Task.CompletedTask;
                        })));

        return stack;
    }

    /// <summary>
    /// Whether the redline can speak for this node: the projection reads markdown SPECIFICALLY
    /// (<see cref="ChangeProjection.CleanTextOf"/>), so a type whose prose lives in some other text
    /// field is prose for the diff editor's syntax highlighting but would redline nothing.
    /// <para>
    /// A Markdown node counts even when it is EMPTY at both ends — an emptied or not-yet-written
    /// document is still a document, and sending it to the source diff would show two blank panes
    /// where the redline correctly shows "no content". The node type is the authority; the content
    /// probe additionally catches markdown-shaped content under some other type.
    /// </para>
    /// </summary>
    private static bool HoldsMarkdown(MeshNode node) =>
        node.NodeType == MarkdownNodeType.NodeType
        || !string.IsNullOrEmpty(MarkdownOverviewLayoutArea.GetMarkdownContent(node));

    /// <summary>
    /// The tracked-change redline for the stated version pair: the document as of the TO version,
    /// with every hunk introduced since the FROM version marked up inline and carded with its
    /// author. Comments stay on the document's own page — this view answers one question.
    /// </summary>
    private static UiControl BuildRedline(
        string hubPath, MeshNode modifiedNode, JsonSerializerOptions options,
        long fromVersion, long? compareToVersion, bool canEdit) =>
        new CollaborativeMarkdownControl()
            // GetMarkdownContent, NOT ExtractDiffContent: the projection derives its hunks from
            // exactly this reader, and ExtractDiffContent falls back to serialized JSON when a
            // markdown node is empty — which would put a JSON envelope on screen under a redline
            // computed from prose.
            .WithValue(MarkdownOverviewLayoutArea.GetMarkdownContent(modifiedNode))
            .WithNodePath(hubPath)
            .WithHubAddress(hubPath)
            .WithCanComment(false)
            // Reverting a hunk writes to the LIVE document, so it is offered only when the live
            // document is what is being compared to.
            .WithCanEdit(canEdit && compareToVersion is null)
            .WithComparison(fromVersion, compareToVersion);

    /// <summary>Toggles between the inline redline and the raw source diff, preserving the version
    /// parameters that say WHAT is being compared.</summary>
    private static UiControl ViewSwitchLink(LayoutAreaHost host, string hubPath, bool toSource)
    {
        var carried = new[] { "from", "to", "version" }
            .Select(key => (key, value: host.GetQueryStringParamValue(key)))
            .Where(p => !string.IsNullOrEmpty(p.value))
            .Select(p => $"{p.key}={p.value}")
            .ToList();
        if (toSource)
            carried.Add("view=source");

        return Controls.Button(host.Localize(toSource ? "versions.viewSource" : "versions.viewRedline"))
            .WithAppearance(Appearance.Lightweight)
            .WithStyle("margin-top: 8px;")
            .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(
                hubPath, MeshNodeLayoutAreas.VersionDiffArea, string.Join("&", carried)));
    }

    /// <summary>
    /// Handles RollbackNodeRequest by fetching the historical version and posting it as a DataChangeRequest.
    /// Sync handler — composes via <c>IObservable</c>; no <c>await</c>.
    /// </summary>
    internal static IMessageDelivery HandleRollbackNodeRequest(
        IMessageHub hub,
        IMessageDelivery<RollbackNodeRequest> request)
    {
        var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
        if (versionQuery == null)
        {
            hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Rollback").Fail("Version history not available")),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var msg = request.Message;
        var options = hub.JsonSerializerOptions;

        versionQuery.GetVersion(msg.Path, msg.TargetVersion, options)
            .Subscribe(historicalNode =>
            {
                if (historicalNode == null)
                {
                    hub.Post(new DataChangeResponse(hub.Version,
                        new ActivityLog("Rollback").Fail($"Version {msg.TargetVersion} not found for {msg.Path}")),
                        o => o.ResponseFor(request));
                    return;
                }

                // 🚨 Restore through GetMeshNodeStream(path).Update — the ONE mutation API — NOT a
                // raw DataChangeRequest post.
                //
                // The old shape posted the historical node with `Version = 0` ("forces a new save")
                // straight at the hub, bypassing the owner's version mint. That raw node reached
                // MonotonicWriteGuardStorageAdapter, which REFUSES a backward write — and rightly
                // so: it cannot tell a rollback from the stale-snapshot corruption it exists to
                // stop ("do not relax this guard"). The refusal returns the STORED node rather than
                // an error, and the post was fire-and-forget, so the rollback silently did nothing:
                // the node never reverted, and any caller polling for the restored content waited
                // until its harness killed it (VersionHistoryTest.RollbackNode_RestoresHistoricalState,
                // a CI-only hang — the guard's fast path lets the write through while its
                // high-water mark is cold, so it only bites once earlier writes have warmed it).
                //
                // Update stamps Version = NextVersion(Math.Max(current.Version, updated.Version)),
                // so restoring HISTORICAL CONTENT becomes an ordinary FORWARD write: the node's
                // content goes back, its revision counter keeps climbing, and the guard is
                // untouched. A rollback is a new revision that happens to carry old content — it
                // was never a backward write.
                hub.GetMeshNodeStream(msg.Path)
                    .Update(_ => historicalNode with { Version = 0 })
                    .Subscribe(
                        _ => hub.Post(new DataChangeResponse(hub.Version, new ActivityLog("Rollback")),
                            o => o.ResponseFor(request)),
                        updateEx => hub.Post(new DataChangeResponse(hub.Version,
                            new ActivityLog("Rollback").Fail($"Rollback write failed: {updateEx.Message}")),
                            o => o.ResponseFor(request)));
            },
            ex => hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Rollback").Fail($"Rollback error: {ex.Message}")),
                o => o.ResponseFor(request)));

        return request.Processed();
    }

    /// <summary>
    /// Handles UndoActivityRequest by restoring all affected nodes to their pre-activity state.
    /// Sync handler — composes via <c>IObservable</c>; no <c>await</c>.
    /// Persistence allowed: handler runs on the affected node's owning hub.
    /// </summary>
    internal static IMessageDelivery HandleUndoActivityRequest(
        IMessageHub hub,
        IMessageDelivery<UndoActivityRequest> request)
    {
        var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
        if (versionQuery == null)
        {
            hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Undo").Fail("Version history not available")),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var msg = request.Message;
        var hubPath = hub.Address.ToString();
        var options = hub.JsonSerializerOptions;
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var activityNodePath = $"{hubPath}/_activity/{msg.ActivityLogId}";

        // Read the activity-log node via one-shot GetDataRequest — true request/response,
        // no SubscribeRequest+immediate-unsubscribe. Single-node-by-path content reads
        // MUST NOT use Query (read-side index lags); see
        // Doc/Architecture/AsynchronousCalls.md "Never use QueryAsync to obtain a MeshNode".
        hub.GetMeshNode(activityNodePath, TimeSpan.FromSeconds(15))
            .SelectMany(activityNode =>
            {
                if (activityNode?.Content is not ActivityLog activityLog)
                {
                    hub.Post(new DataChangeResponse(hub.Version,
                        new ActivityLog("Undo").Fail($"Activity log {msg.ActivityLogId} not found")),
                        o => o.ResponseFor(request));
                    return Observable.Empty<IReadOnlyCollection<MeshNode>>();
                }

                if (activityLog.AffectedPaths.Count == 0)
                {
                    hub.Post(new DataChangeResponse(hub.Version,
                        new ActivityLog("Undo").Fail("No affected paths recorded for this activity")),
                        o => o.ResponseFor(request));
                    return Observable.Empty<IReadOnlyCollection<MeshNode>>();
                }

                // For each affected path, fetch the version just before StartVersion in parallel.
                // No await — GetVersionBefore is already reactive, so each path's lookup is
                // merged straight in via SelectMany (no Task bridge, no Observable.FromAsync).
                return activityLog.AffectedPaths
                    .ToObservable()
                    .SelectMany(path =>
                        versionQuery.GetVersionBefore(path, activityLog.StartVersion, options))
                    .Where(node => node != null)
                    .Select(node => node! with { Version = 0 })
                    .Aggregate(
                        ImmutableList<MeshNode>.Empty,
                        (acc, n) => acc.Add(n))
                    .Select(list => (IReadOnlyCollection<MeshNode>)list);
            })
            .Subscribe(restoredNodes =>
            {
                if (restoredNodes.Count > 0)
                {
                    hub.Post(
                        new DataChangeRequest { ChangedBy = "undo" }.WithUpdates(restoredNodes.ToArray()),
                        o => o.WithTarget(hub.Address));
                }
            },
            ex => hub.Post(new DataChangeResponse(hub.Version,
                new ActivityLog("Undo").Fail($"Undo error: {ex.Message}")),
                o => o.ResponseFor(request)));

        return request.Processed();
    }

    /// <summary>
    /// Extracts content for diff display. Uses markdown for markdown content, JSON for everything else.
    /// </summary>
    /// <summary>
    /// The content-shape text probe order for the diff: dynamic node types carry their prose in
    /// differently-named string fields (a SocialMedia/Post uses <c>text</c>; markdown-shaped
    /// content uses <c>content</c>; some types use <c>body</c>).
    /// </summary>
    internal static readonly ImmutableArray<string> TextProperties = ["content", "text", "body"];

    internal static string ExtractDiffContent(MeshNode? node, JsonSerializerOptions options)
    {
        if (node == null)
            return "";

        // Try markdown first
        var markdown = MarkdownOverviewLayoutArea.GetMarkdownContent(node);
        if (!string.IsNullOrEmpty(markdown))
            return markdown;

        // Shape-tolerant text probe on the CONTENT: a dynamic type's content arrives TYPED on its
        // own hub (a runtime-compiled object — not MarkdownContent, not a JsonElement), and its
        // prose may live in a field GetMarkdownContent doesn't know (a SocialMedia/Post's `text`).
        // Coerce to an element via the CONCRETE runtime type (never the object overload — that
        // adopts foreign types into the registry as a read side effect) and probe.
        var element = ContentElement(node.Content, options);
        if (element is { ValueKind: JsonValueKind.Object })
        {
            foreach (var property in TextProperties)
            {
                if (element.Value.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(value.GetString()))
                    return value.GetString()!;
            }
        }

        // Fall back to the CONTENT as indented JSON — diffing the whole node envelope buries the
        // actual change under version/lastModified noise.
        var indentedOptions = new JsonSerializerOptions(options) { WriteIndented = true };
        return element is null
            ? ""
            : JsonSerializer.Serialize(element.Value, indentedOptions);
    }

    /// <summary>
    /// Checks if a node's diff content is prose (markdown or a text-bearing content field) rather
    /// than a JSON fallback — drives the diff editor's language.
    /// </summary>
    internal static bool IsMarkdownContent(MeshNode? node, JsonSerializerOptions options)
    {
        if (node == null) return false;
        if (!string.IsNullOrEmpty(MarkdownOverviewLayoutArea.GetMarkdownContent(node)))
            return true;
        var element = ContentElement(node.Content, options);
        return element is { ValueKind: JsonValueKind.Object }
            && TextProperties.Any(p =>
                element.Value.TryGetProperty(p, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(value.GetString()));
    }

    /// <summary>Coerces content of any shape (typed record, dynamic object, JsonElement, JsonNode)
    /// into a JsonElement; null when there is no content or it cannot serialize.</summary>
    private static JsonElement? ContentElement(object? content, JsonSerializerOptions options)
    {
        switch (content)
        {
            case null:
                return null;
            case JsonElement je:
                return je;
            case System.Text.Json.Nodes.JsonNode jn:
                return JsonSerializer.Deserialize<JsonElement>(jn, options);
            default:
                try
                {
                    return JsonSerializer.SerializeToElement(content, content.GetType(), options);
                }
                catch (Exception)
                {
                    return null;
                }
        }
    }
}
