using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The <b>edu module</b> — course/exercise layout areas shipped as standard functionality.
///
/// <para>Two features, sharing one "course structure" model (the exercise's parent <em>module</em>
/// subtree):</para>
/// <list type="bullet">
///   <item><b><see cref="StartExercise"/></b> — the "Your Turn" landing. Resolve-or-copy: if the learner
///   already has a personal copy of the module under their own partition, go there; otherwise copy the
///   module subtree (Exercises + Solutions + Source) into <c>{user}/{course}</c> and go there. So each
///   learner works on their OWN writable copy — mirrors <c>CodeLayoutAreas.CopyToHomeAndNavigate</c>
///   (<see cref="NodeCopyHelper.CopyNodeTree"/> + <see cref="RedirectControl"/>), but as a
///   navigation-time resolve rather than a button.</item>
///   <item><b><see cref="Learn"/></b> — a reader shell (<c>Splitter</c>) that puts the course side-nav
///   (<see cref="CourseNav"/>) on the left and the page's own Overview on the right, so a learner always
///   sees WHERE THEY ARE in the module. StartExercise lands the learner in this shell, and every nav link
///   points back into it, so the side-nav persists as they move between pages. Same shape as
///   <c>CodeLayoutAreas.Overview</c>.</item>
///   <item><b><see cref="GoToMyCopy"/></b> — the same resolve-or-copy as <see cref="StartExercise"/>, but
///   as an EMBEDDABLE button (<c>@@("area/GoToMyCopy")</c>) instead of an auto-redirect. A markdown course
///   page drops it inline: the learner sees a "Go to Exercise" button; the first press copies the module
///   into their home and opens it, the second press just goes to the copy. This is the shape a page embed
///   needs — an embedded <c>StartExercise</c> renders a <see cref="RedirectControl"/> that only drives
///   navigation as a top-level route, so inline it appears to "do nothing".</item>
/// </list>
///
/// <para>Registered on every node via <see cref="AddEducationLayoutAreas"/> (called from
/// <c>MeshNodeLayoutAreas.AddDefaultLayoutAreas</c>), so any course page exposes <c>/StartExercise</c>,
/// <c>/GoToMyCopy</c>, <c>/Learn</c>, and <c>/CourseNav</c>.</para>
/// </summary>
public static class EducationLayoutAreas
{
    /// <summary>Area name for the resolve-or-copy "Your Turn" landing (auto-redirect).</summary>
    public const string StartExerciseArea = "StartExercise";
    /// <summary>Area name for the embeddable resolve-or-copy "Go to Exercise" button.</summary>
    public const string GoToMyCopyArea = "GoToMyCopy";
    /// <summary>Area name for the course side-nav (table of contents).</summary>
    public const string CourseNavArea = "CourseNav";
    /// <summary>Area name for the reader shell (side-nav + page content).</summary>
    public const string LearnArea = "Learn";

    /// <summary>Registers the edu module's layout areas onto a layout definition.</summary>
    public static LayoutDefinition AddEducationLayoutAreas(this LayoutDefinition layout)
        => layout
            .WithView(StartExerciseArea, StartExercise)
            .WithView(GoToMyCopyArea, GoToMyCopy)
            .WithView(CourseNavArea, CourseNav)
            .WithView(LearnArea, Learn);

    /// <summary>
    /// The "Your Turn" landing: ensure the viewing learner has a personal, writable copy of this
    /// exercise's module (<see cref="EnsurePersonalCopy"/>), then open it in the <see cref="Learn"/>
    /// reader shell via an auto-redirect. Idempotent — a second visit just navigates to the existing copy.
    /// <para>Use <see cref="GoToMyCopy"/> instead to surface the same behaviour as a button that a
    /// markdown page can embed inline (an embedded <see cref="RedirectControl"/> does not navigate).</para>
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> StartExercise(LayoutAreaHost host, RenderingContext ctx)
        => EnsurePersonalCopy(host.Hub, host.Hub.Address.ToString())
            .Select(landing => (UiControl?)RedirectToLearn(landing));

    /// <summary>
    /// Resolve-or-copy: ensures the signed-in viewer has a personal, writable copy of this node's parent
    /// MODULE subtree under their own partition, and emits the path to OPEN — the personal copy once one is
    /// (or gets) made, or the source path itself when there is nothing to copy: no writable home, the node
    /// already sits inside the viewer's own partition, the viewer AUTHORS the template (holds Update), or
    /// the path is too shallow to have a module. Idempotent: an existing copy is reused, never duplicated.
    /// <para>Cold + reactive end-to-end — the copy runs on <c>Subscribe</c>. Both <see cref="StartExercise"/>
    /// and <see cref="GoToMyCopy"/>'s click action route through this one helper, so their behaviour cannot
    /// drift.</para>
    /// <para>Reuses <see cref="IMeshService.CopyNode"/> (ONE mesh copy request: Read on the source subtree
    /// + Create under the learner's own partition — an RLS auto-grant the learner always holds). NOT
    /// <see cref="NodeCopyHelper.CopyNodeTree"/>'s per-node <c>CreateNodeRequest</c>s routed through the
    /// source node's own hub, which the access pipeline would evaluate as Create on the READ-ONLY source
    /// and deny a learner.</para>
    /// </summary>
    /// <param name="hub">The hub whose address is the source node and whose ambient <see cref="AccessService"/> resolves the viewer.</param>
    /// <param name="sourcePath">The full path of the node the viewer is looking at.</param>
    public static IObservable<string> EnsurePersonalCopy(IMessageHub hub, string sourcePath)
    {
        var viewer = ResolveViewerHome(hub.ServiceProvider.GetService<AccessService>());

        // No writable home (anonymous / system / hub principal) → nothing to copy INTO.
        // Already inside the viewer's own partition → they ARE in their copy; no redirect loop.
        if (string.IsNullOrEmpty(viewer)
            || sourcePath.StartsWith(viewer + "/", StringComparison.Ordinal))
            return Observable.Return(sourcePath);

        var segs = sourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Need at least {course}/{page}: a bare partition root has no module subtree to copy.
        if (segs.Length < 2)
            return Observable.Return(sourcePath);

        // Copy unit = the node's parent MODULE subtree; target = {viewer}/{module}, so the module's
        // course-relative sub-path is preserved.
        var copyRoot = string.Join("/", segs[..^1]);       // the node's MODULE (its parent)
        var targetRoot = $"{viewer}/{copyRoot}";           // the module's copy under the learner
        var landingPath = $"{viewer}/{sourcePath}";        // the node itself inside that copy

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Graph.Education");

        // Evaluate the VIEWER's permission explicitly (by id) — NOT the ambient AccessService context,
        // which on a hub action block can be the system/owner identity rather than the viewer.
        return hub.GetEffectivePermissions(sourcePath, viewer).Take(1).SelectMany(perms =>
        {
            // Author / maintainer of the template (has Update): keep them on the template so authoring and
            // preview keep working. Only read-only LEARNERS get a personal copy.
            if (perms.HasFlag(Permission.Update))
                return Observable.Return(sourcePath);

            // Existence check via query (returns empty for a missing node — no NotFound OnError, unlike a
            // point read). A prior-session copy is long indexed; skipping the copy when it exists makes the
            // whole flow idempotent.
            return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{landingPath}"))
                .Take(1)
                .SelectMany(change =>
                {
                    if (change.Items is { Count: > 0 })
                        return Observable.Return(landingPath);

                    return meshService.CopyNode(copyRoot, targetRoot, includeDescendants: true)
                        .Select(_ =>
                        {
                            logger?.LogInformation(
                                "EnsurePersonalCopy: copied {Source} -> {Target} for {Viewer}", copyRoot, targetRoot, viewer);
                            return landingPath;
                        });
                });
        });
    }

    /// <summary>
    /// The embeddable "Go to Exercise" button — the resolve-or-copy of <see cref="EnsurePersonalCopy"/>
    /// surfaced as a control a markdown course page drops inline with <c>@@("area/GoToMyCopy")</c>. Reactive
    /// render:
    /// <list type="bullet">
    ///   <item>no writable home → a gentle "sign in to take this" message;</item>
    ///   <item>the copy already exists (or the viewer owns/authors the template) → a "Go to Exercise →"
    ///   button;</item>
    ///   <item>otherwise → a "Go to Exercise" button.</item>
    /// </list>
    /// The label is cosmetic; the CLICK always routes through <see cref="EnsurePersonalCopy"/> (idempotent),
    /// so it copies-then-navigates on the first press and just navigates on the second — and works even if
    /// the render-time existence snapshot was stale.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> GoToMyCopy(LayoutAreaHost host, RenderingContext ctx)
    {
        var hub = host.Hub;
        var sourcePath = hub.Address.ToString();
        var viewer = ResolveViewerHome(hub.ServiceProvider.GetService<AccessService>());

        if (string.IsNullOrEmpty(viewer))
            return Observable.Return<UiControl?>(SignInHint());

        var segs = sourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Already the viewer's own copy (or too shallow to have a module subtree) → the copy IS the source:
        // a plain "Go to Exercise →" that opens it in the reader shell.
        if (segs.Length < 2 || sourcePath.StartsWith(viewer + "/", StringComparison.Ordinal))
            return Observable.Return<UiControl?>(GoButton(sourcePath, alreadyMine: true));

        var landingPath = $"{viewer}/{sourcePath}";
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        return hub.GetEffectivePermissions(sourcePath, viewer).Take(1).SelectMany(perms =>
        {
            // Author / maintainer (holds Update on the template): open the template itself, no personal copy.
            if (perms.HasFlag(Permission.Update))
                return Observable.Return<UiControl?>(GoButton(sourcePath, alreadyMine: true));

            // Existence drives the arrow on the LABEL only; the click always ensures-then-navigates.
            return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{landingPath}"))
                .Take(1)
                .Select(change => (UiControl?)GoButton(sourcePath, alreadyMine: change.Items is { Count: > 0 }));
        });
    }

    /// <summary>
    /// The "Go to Exercise" button. Its click action runs <see cref="EnsurePersonalCopy"/> and, on success,
    /// navigates into the resolved copy's <see cref="Learn"/> reader shell (so the learner lands WITH the
    /// collapsible course side-nav). Errors surface through a dialog rather than a silent no-op.
    /// </summary>
    private static UiControl GoButton(string sourcePath, bool alreadyMine) =>
        Controls.Button(alreadyMine ? "Go to Exercise →" : "Go to Exercise")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Play())
            .WithClickAction(ctx =>
            {
                var hub = ctx.Host.Hub;
                var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.Graph.Education");
                EnsurePersonalCopy(hub, sourcePath).Subscribe(
                    landing => ctx.NavigateTo(new LayoutAreaReference(LearnArea).ToHref(landing)),
                    ex =>
                    {
                        logger?.LogWarning(ex, "GoToMyCopy failed for {Source}", sourcePath);
                        ctx.Host.UpdateArea(DialogControl.DialogArea, Controls.Dialog(
                                Controls.Markdown($"**Couldn't open your copy:**\n\n{ex.Message}"),
                                "Go to Exercise")
                            .WithSize("M").WithClosable(true));
                    });
                return Task.CompletedTask;
            });

    /// <summary>
    /// The sign-in hint shown by <see cref="GoToMyCopy"/> when there is no writable home to copy into.
    /// </summary>
    private static UiControl SignInHint() =>
        Controls.Stack.WithStyle("padding: 12px;")
            .WithView(Controls.Markdown(
                "**Sign in to take this exercise.** We'll copy it into your own space so you can work on " +
                "your own version — that needs a signed-in account."));

    /// <summary>
    /// Reader shell: a horizontal <c>Splitter</c> with the course side-nav (<see cref="CourseNav"/>) on
    /// the left and this page's own Overview on the right. Learners land here from
    /// <see cref="StartExercise"/>, and every nav link targets <c>/Learn</c>, so the side-nav persists
    /// across page navigation. Mirrors <c>CodeLayoutAreas.Overview</c>.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Learn(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubAddress = host.Hub.Address;

        var splitter = Controls.Splitter
            .WithClass("shell-splitter")
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("100%"))
            .WithView(
                (h, c) => CourseNavStream(h),
                skin => skin.WithSize("300px").WithMin("220px").WithMax("420px").WithCollapsible(true))
            .WithView(
                new LayoutAreaControl(hubAddress, new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea)),
                skin => skin.WithSize("*"));

        return Observable.Return<UiControl?>(splitter);
    }

    /// <summary>
    /// The course side-nav (table of contents) for the current page's module, with the current page
    /// highlighted so the learner sees WHERE THEY ARE. Standalone area so it can also be embedded.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> CourseNav(LayoutAreaHost host, RenderingContext ctx)
        => CourseNavStream(host).Select(c => (UiControl?)c);

    // The nav-menu stream, non-null (the Splitter left pane consumes this directly).
    private static IObservable<UiControl> CourseNavStream(LayoutAreaHost host)
    {
        var currentPath = host.Hub.Address.ToString();
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();

        // Root the TOC at the current page's MODULE (its parent) — the same unit StartExercise copies, so
        // the nav shows exactly the pages the learner has in their copy.
        var segs = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var moduleRoot = segs.Length >= 2 ? string.Join("/", segs[..^1]) : currentPath;

        if (meshService is null)
            return Observable.Return<UiControl>(Controls.NavMenu);

        // Module + its main descendants in one live query; BuildCourseNav renders the module as the group
        // header and its direct children as links (reactive — the nav follows adds/renames).
        // GitHubSyncConfig (`{space}/_GitSync`) is a non-satellite internal config node — it's a real main
        // node (MainNode == Path), so `is:main` correctly keeps it; exclude it by type here so it never
        // appears as a learner-facing page in the rail. (See SatelliteEntityPatterns.md — the reader
        // filters internal main-nodes; we do NOT forge a satellite MainNode on the node itself.)
        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{moduleRoot} scope:subtree is:main -nodeType:GitHubSyncConfig"))
            .Select(change => BuildCourseNav(moduleRoot, currentPath, change.Items));
    }

    /// <summary>
    /// The pages a course side-nav lists for <paramref name="moduleRoot"/>: the module's DIRECT children
    /// (deeper support nodes such as <c>Source/…</c> are excluded to keep the TOC a clean page list), ordered
    /// by <see cref="MeshNode.Order"/> then <see cref="MeshNode.Name"/> (case-insensitive). Internal /
    /// satellite nodes — any whose final path segment starts with <c>_</c> (<c>_GitSync</c>, <c>_Access</c>,
    /// <c>_Thread</c>, <c>_Activity</c>, …) — are filtered out: they are plumbing, never learner-facing pages.
    /// Pure — the nav-rendering and the ordering contract are the same function, so a test can pin the order
    /// without reaching through the render/serialization layer.
    /// </summary>
    /// <param name="moduleRoot">The containing space (the current page's parent module) whose children are listed.</param>
    /// <param name="mainNodes">The module + its main descendants (the <c>scope:subtree is:main</c> query result).</param>
    public static IReadOnlyList<MeshNode> SelectCoursePages(
        string moduleRoot, IReadOnlyCollection<MeshNode> mainNodes)
    {
        var prefix = moduleRoot + "/";
        return mainNodes
            .Where(n => !string.IsNullOrEmpty(n.Path) && IsDirectChildPage(n.Path.AsSpan(), prefix))
            .OrderBy(n => n.Order)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // A direct-child PAGE of the module: sits immediately under moduleRoot (no deeper), and its own segment
    // is not an internal/satellite node (leading '_' — _GitSync, _Access, _Thread, …).
    private static bool IsDirectChildPage(ReadOnlySpan<char> path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var segment = path[prefix.Length..];
        return segment.Length > 0 && !segment.Contains('/') && segment[0] != '_';
    }

    private static UiControl BuildCourseNav(
        string moduleRoot, string currentPath, IReadOnlyCollection<MeshNode> mainNodes)
    {
        var root = mainNodes.FirstOrDefault(n => string.Equals(n.Path, moduleRoot, StringComparison.Ordinal));
        var groupTitle = root?.Name ?? moduleRoot.Split('/').Last();

        // Direct children of the module = the pages (ordered by Order then Name).
        var pages = SelectCoursePages(moduleRoot, mainNodes);

        var group = new NavGroupControl(groupTitle).WithSkin(s => s.WithExpanded(true));

        // Module home first (active when we're on the module root itself).
        group = group.WithView(
            new NavLinkControl(groupTitle, root?.Icon, new LayoutAreaReference(LearnArea).ToHref(moduleRoot))
                .WithIsActive(string.Equals(currentPath, moduleRoot, StringComparison.Ordinal)));

        foreach (var page in pages)
        {
            var href = new LayoutAreaReference(LearnArea).ToHref(page.Path);
            group = group.WithView(
                new NavLinkControl(page.Name ?? page.Id, page.Icon, href)
                    .WithIsActive(string.Equals(currentPath, page.Path, StringComparison.Ordinal)));
        }

        return Controls.NavMenu
            .WithSkin(s => s.WithWidth(300).WithCollapsible(false))
            .WithNavGroup(group);
    }

    private static UiControl RedirectToLearn(string path) =>
        new RedirectControl(new LayoutAreaReference(LearnArea).ToHref(path));

    /// <summary>
    /// The signed-in viewer's HOME partition (their <c>AccessContext.ObjectId</c>, per-delivery first
    /// then durable circuit) — mirrors <c>CodeLayoutAreas.ResolveViewerHome</c>. System / anonymous /
    /// hub-shaped principals yield <c>null</c>: they have no home partition to copy into.
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
}
