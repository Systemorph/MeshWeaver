using System.ComponentModel;
using System.Reactive.Linq;
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
/// </list>
///
/// <para>Registered on every node via <see cref="AddEducationLayoutAreas"/> (called from
/// <c>MeshNodeLayoutAreas.AddDefaultLayoutAreas</c>), so any course page exposes <c>/StartExercise</c>,
/// <c>/Learn</c>, and <c>/CourseNav</c>.</para>
/// </summary>
public static class EducationLayoutAreas
{
    /// <summary>Area name for the resolve-or-copy "Your Turn" landing.</summary>
    public const string StartExerciseArea = "StartExercise";
    /// <summary>Area name for the course side-nav (table of contents).</summary>
    public const string CourseNavArea = "CourseNav";
    /// <summary>Area name for the reader shell (side-nav + page content).</summary>
    public const string LearnArea = "Learn";

    /// <summary>Registers the edu module's layout areas onto a layout definition.</summary>
    public static LayoutDefinition AddEducationLayoutAreas(this LayoutDefinition layout)
        => layout
            .WithView(StartExerciseArea, StartExercise)
            .WithView(CourseNavArea, CourseNav)
            .WithView(LearnArea, Learn);

    /// <summary>
    /// The "Your Turn" landing: ensure the viewing learner has a personal, writable copy of this
    /// exercise's module, then open it in the <see cref="Learn"/> reader shell. Idempotent — a second
    /// visit just navigates to the existing copy.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> StartExercise(LayoutAreaHost host, RenderingContext ctx)
    {
        var hub = host.Hub;
        var sourcePath = hub.Address.ToString();
        var viewer = ResolveViewerHome(hub.ServiceProvider.GetService<AccessService>());

        // No writable home (anonymous / system / hub principal): nothing to copy INTO — just open the
        // shared exercise in the reader shell (read-only for them).
        // Already inside the viewer's own partition: they ARE in their copy — open it, no redirect loop.
        if (string.IsNullOrEmpty(viewer)
            || sourcePath.StartsWith(viewer + "/", StringComparison.Ordinal))
            return Observable.Return<UiControl?>(RedirectToLearn(sourcePath));

        var segs = sourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Need at least {course}/{page}: a bare partition root has no module subtree to copy.
        if (segs.Length < 2)
            return Observable.Return<UiControl?>(RedirectToLearn(sourcePath));

        // Copy unit = the exercise's parent MODULE subtree; target = {viewer}/{course}, so the module's
        // course-relative sub-path is preserved (course = the module's parent, "" for a 2-segment path).
        var copyRoot = string.Join("/", segs[..^1]);       // the exercise's MODULE (its parent)
        var targetRoot = $"{viewer}/{copyRoot}";           // the module's copy under the learner
        var landingPath = $"{viewer}/{sourcePath}";        // the exercise page inside that copy

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Graph.Education");

        // Evaluate the VIEWER's permission explicitly (by id) — NOT the ambient AccessService context,
        // which on a hub action block can be the system/owner identity rather than the viewer.
        return hub.GetEffectivePermissions(sourcePath, viewer).Take(1).SelectMany(perms =>
        {
            // Author / maintainer of the template (has Update): show the template directly so authoring
            // and preview keep working. Only read-only LEARNERS get redirected to a personal copy.
            if (perms.HasFlag(Permission.Update))
                return Observable.Return<UiControl?>(RedirectToLearn(sourcePath));

            // Existence check via query (returns empty for a missing node — no NotFound OnError, unlike a
            // point read). A prior-session copy is long indexed; skipping the copy when it exists makes the
            // whole flow idempotent.
            return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{landingPath}"))
                .Take(1)
                .SelectMany(change =>
                {
                    if (change.Items is { Count: > 0 })
                        return Observable.Return<UiControl?>(RedirectToLearn(landingPath));

                    // ONE CopyNodeRequest routed through the mesh copy handler (Read on the source subtree +
                    // Create on the TARGET under the learner's own partition). NOT per-node
                    // CreateNodeRequests posted through the source node's OWN hub — the access pipeline would
                    // check those as Create on the read-only source and deny the learner. The learner always
                    // holds Create under their own {viewer}/… partition (RLS auto-grant).
                    return meshService.CopyNode(copyRoot, targetRoot, includeDescendants: true)
                        .Select(_ =>
                        {
                            logger?.LogInformation(
                                "StartExercise: copied {Source} -> {Target} for {Viewer}", copyRoot, targetRoot, viewer);
                            return (UiControl?)RedirectToLearn(landingPath);
                        });
                });
        });
    }

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
        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{moduleRoot} scope:subtree is:main"))
            .Select(change => BuildCourseNav(moduleRoot, currentPath, change.Items));
    }

    private static UiControl BuildCourseNav(
        string moduleRoot, string currentPath, IReadOnlyCollection<MeshNode> mainNodes)
    {
        var root = mainNodes.FirstOrDefault(n => string.Equals(n.Path, moduleRoot, StringComparison.Ordinal));
        var groupTitle = root?.Name ?? moduleRoot.Split('/').Last();

        // Direct children of the module = the pages. Deeper support nodes (e.g. Source/…) are left out of
        // the TOC to keep it a clean page list; ordered by Order then Name.
        var prefix = moduleRoot + "/";
        var pages = mainNodes
            .Where(n => !string.IsNullOrEmpty(n.Path)
                        && n.Path.StartsWith(prefix, StringComparison.Ordinal)
                        && !n.Path.AsSpan(prefix.Length).Contains('/'))
            .OrderBy(n => n.Order)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
