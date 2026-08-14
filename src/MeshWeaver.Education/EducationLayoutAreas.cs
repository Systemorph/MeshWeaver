using System.Collections.Immutable;
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
/// <para>Two features, sharing one "course structure" model — the learner's personal copy is the exercise's
/// parent <em>module</em> subtree under <c>{viewer}/…</c>, and the reader shell's rail is the whole
/// <em>course</em> (<see cref="BuildCourseNavModel"/>):</para>
/// <list type="bullet">
///   <item><b><see cref="StartExercise"/></b> — the "Your Turn" landing. Resolve-or-copy: if the learner
///   already has a personal copy of the module under their own partition, go there; otherwise copy the
///   module subtree (Exercises + Solutions + Source) into <c>{user}/{course}</c> and go there. So each
///   learner works on their OWN writable copy — mirrors <c>CodeLayoutAreas.CopyToHomeAndNavigate</c>
///   (<see cref="NodeCopyHelper.CopyNodeTree"/> + <see cref="RedirectControl"/>), but as a
///   navigation-time resolve rather than a button.</item>
///   <item><b><see cref="Learn"/></b> — a reader shell (<c>Splitter</c>) that puts the course side-nav
///   (<see cref="CourseNav"/>) on the left and the page's own Overview on the right, so a learner always
///   sees WHERE THEY ARE in the COURSE. StartExercise lands the learner in this shell, and every nav link
///   points back into it, so the side-nav persists as they move between pages — including while they work
///   inside their own copy of an exercise. Same shape as <c>CodeLayoutAreas.Overview</c>.</item>
///   <item><b><see cref="GoToMyCopy"/></b> — the same resolve-or-copy as <see cref="StartExercise"/>, but
///   as an EMBEDDABLE button (<c>@@("area/GoToMyCopy")</c>) instead of an auto-redirect. A markdown course
///   page drops it inline: the learner sees a "Go to Exercise" button; the first press copies the module
///   into their home and opens it, the second press just goes to the copy. This is the shape a page embed
///   needs — an embedded <c>StartExercise</c> renders a <see cref="RedirectControl"/> that only drives
///   navigation as a top-level route, so inline it appears to "do nothing".</item>
/// </list>
///
/// <para>Registered on every node via <see cref="AddEducationLayoutAreas(MessageHubConfiguration)"/>
/// (chained in the portal's node-hub configuration since the MeshWeaver.Education pack extraction),
/// so any course page exposes <c>/StartExercise</c>, <c>/GoToMyCopy</c>, <c>/Learn</c>, and
/// <c>/CourseNav</c>.</para>
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
    /// Hub-configuration overload for composition-root chains (the shape the portal's node-hub
    /// config uses): since the MeshWeaver.Education pack extraction, these areas are registered
    /// here rather than inside core's <c>AddDefaultLayoutAreas</c>.
    /// </summary>
    public static MessageHubConfiguration AddEducationLayoutAreas(this MessageHubConfiguration config)
        => config.AddLayout(layout => layout.AddEducationLayoutAreas());

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
    /// The course side-nav (table of contents) — the WHOLE course: every module as a collapsible group,
    /// the current module expanded and the current position marked active, so the learner sees where they
    /// are AND what else the course holds. The index is the SAME at every depth — drilling into a module,
    /// an exercise index or a page inside the learner's own copy only moves the highlight, it never
    /// re-scopes the rail. Standalone area so it can also be embedded.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> CourseNav(LayoutAreaHost host, RenderingContext ctx)
        => CourseNavStream(host).Select(c => (UiControl?)c);

    // The nav-menu stream, non-null (the Splitter left pane consumes this directly).
    private static IObservable<UiControl> CourseNavStream(LayoutAreaHost host)
    {
        var currentPath = host.Hub.Address.ToString();
        var viewer = ResolveViewerHome(host.Hub.ServiceProvider.GetService<AccessService>());
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();

        // Root the TOC at the COURSE (the top-level partition), NOT at the current page's module — the rail
        // is the course index, so all modules are listed however deep the learner has navigated. When the
        // learner is working inside their own copy ({viewer}/{course}/…) the viewer prefix is stripped, so
        // the rail still resolves to — and indexes — the CENTRAL course.
        var courseRoot = ResolveCourseRoot(currentPath, viewer);

        if (meshService is null)
            return Observable.Return<UiControl>(Controls.NavMenu);

        // Course + its main descendants in one live query; BuildCourseNavModel turns it into the module
        // groups (reactive — the nav follows adds/renames). Internal nodes are filtered in CODE
        // (SelectCoursePages), not with a `-nodeType:` term: a negation-only exclusion is not honoured by
        // every query provider — on the in-memory/static provider it empties the whole result, which would
        // silently blank the rail.
        var course = meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{courseRoot} scope:subtree is:main"))
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, ApplyQueryChange)
            .Select(nodes => (IReadOnlyCollection<MeshNode>)nodes.Values.ToList());

        // The viewer's OWN copies of this course, live: an exercise the learner already copied gets a direct
        // link into that copy; one they have not gets the resolve-or-copy click (see BuildCourseNavModel).
        // Starts empty so a slow/denied personal query can never hold the rail back — the click path is
        // idempotent, so an under-reported copy costs nothing.
        var personal = string.IsNullOrEmpty(viewer)
            ? Observable.Return(NoPaths)
            : meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{viewer}/{courseRoot} scope:subtree is:main"))
                .Scan(ImmutableDictionary<string, MeshNode>.Empty, ApplyQueryChange)
                .Select(nodes => (IReadOnlySet<string>)nodes.Keys.ToHashSet(StringComparer.Ordinal))
                .Catch((Exception _) => Observable.Return(NoPaths))
                .StartWith(NoPaths);

        return course.CombineLatest(personal, (nodes, copies) =>
            (UiControl)RenderCourseNav(
                BuildCourseNavModel(courseRoot, currentPath, viewer, nodes, copies)));
    }

    // A query stream reports Initial/Reset as the FULL result but Added/Updated/Removed as ONLY the changed
    // items — so the rail folds the changes instead of re-rendering from a delta, which would blank every
    // page the delta does not mention the moment someone adds or renames one.
    internal static ImmutableDictionary<string, MeshNode> ApplyQueryChange(
        ImmutableDictionary<string, MeshNode> nodes, QueryResultChange<MeshNode> change)
        => change.ChangeType switch
        {
            QueryChangeType.Initial or QueryChangeType.Reset => ImmutableDictionary<string, MeshNode>.Empty
                .SetItems(change.Items.Select(n => new KeyValuePair<string, MeshNode>(n.Path, n))),
            QueryChangeType.Removed => nodes.RemoveRange(change.Items.Select(n => n.Path)),
            _ => nodes.SetItems(change.Items.Select(n => new KeyValuePair<string, MeshNode>(n.Path, n))),
        };

    // The empty personal-copy set — shared, never mutated (no static mutable state).
    private static readonly IReadOnlySet<string> NoPaths = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The sub-namespace under a module where exercises live — <c>{course}/{module}/Exercise/{n}</c>, the
    /// convention <c>MeshWeaver.Courses.Configuration.ExerciseNodeType.ExerciseSubNamespace</c> declares and
    /// the GitSync'd courses follow. Declared here rather than referenced because MeshWeaver.Graph must not
    /// depend on MeshWeaver.Courses. The plural spelling is accepted too.
    /// </summary>
    public const string ExerciseSubNamespace = "Exercise";

    /// <summary>
    /// The CENTRAL course a page belongs to: the first path segment — except inside a learner's personal
    /// copy (<c>{viewer}/{course}/…</c>), where the viewer prefix is stripped first so the rail indexes the
    /// shared course rather than the learner's partition. Pure.
    /// </summary>
    /// <param name="currentPath">The full path of the page being viewed (template or personal copy).</param>
    /// <param name="viewer">The viewer's home partition, or null/empty when there is none.</param>
    public static string ResolveCourseRoot(string currentPath, string? viewer)
    {
        var source = ToSourcePath(currentPath, viewer);
        var slash = source.IndexOf('/');
        return slash < 0 ? source : source[..slash];
    }

    /// <summary>
    /// The CENTRAL (template) path a page corresponds to: <paramref name="path"/> itself, or — when it sits
    /// in the viewer's personal copy (<c>{viewer}/…</c>) — the same page in the shared course. The inverse of
    /// <see cref="PersonalExercisePath"/>; used to mark the rail's active link and expand the current module
    /// no matter which of the two copies the learner is reading. Pure.
    /// </summary>
    /// <param name="path">A page path, in the course or in the viewer's copy of it.</param>
    /// <param name="viewer">The viewer's home partition, or null/empty when there is none.</param>
    public static string ToSourcePath(string path, string? viewer)
        => !string.IsNullOrEmpty(viewer) && path.StartsWith(viewer + "/", StringComparison.Ordinal)
            ? path[(viewer.Length + 1)..]
            : path;

    /// <summary>
    /// The path of the viewer's OWN copy of an exercise — the same target
    /// <see cref="EnsurePersonalCopy"/> resolves to (<c>{viewer}/{exercisePath}</c>). Returns
    /// <c>null</c> when there is no such path (no signed-in home, or the exercise is not inside
    /// <paramref name="coursePath"/>) so a caller can never fall back to the template: <b>the rail must
    /// never link the source exercise</b>. An already-personal path is returned unchanged (idempotent).
    /// Pure — this is the invariant, so it is unit-testable without a mesh.
    /// </summary>
    /// <param name="coursePath">The central course root the exercise must live under.</param>
    /// <param name="exercisePath">The exercise's path in the central course (or already in the copy).</param>
    /// <param name="viewer">The viewer's home partition, or null/empty when there is none.</param>
    public static string? PersonalExercisePath(string coursePath, string exercisePath, string? viewer)
    {
        if (string.IsNullOrEmpty(viewer) || string.IsNullOrEmpty(exercisePath))
            return null;
        if (exercisePath.StartsWith(viewer + "/", StringComparison.Ordinal))
            return exercisePath;                     // already the learner's own copy
        if (string.IsNullOrEmpty(coursePath)
            || !exercisePath.StartsWith(coursePath + "/", StringComparison.Ordinal))
            return null;                             // not part of this course — no copy to point at
        return $"{viewer}/{exercisePath}";
    }

    /// <summary>
    /// Whether a node is an EXERCISE — a page the learner works on in their own copy rather than reads from
    /// the template. Two signals, both grounded in how courses are authored: an exercise node type
    /// (<c>Exercise</c>, <c>Edu/Exercise</c>, …), or a node sitting directly in a module's
    /// <see cref="ExerciseSubNamespace"/> folder (<c>{course}/{module}/Exercise/{n}</c>). Pure.
    /// </summary>
    /// <param name="node">The node to classify.</param>
    public static bool IsExercise(MeshNode node)
        => IsExerciseNodeType(node.NodeType) || IsExerciseSegment(ParentSegment(node.Path));

    private static bool IsExerciseNodeType(string? nodeType)
        => nodeType is not null
           && (nodeType.Equals(ExerciseSubNamespace, StringComparison.OrdinalIgnoreCase)
               || nodeType.EndsWith("/" + ExerciseSubNamespace, StringComparison.OrdinalIgnoreCase));

    // The 'Exercise' (or 'Exercises') folder segment courses group their your-turn pages under.
    internal static bool IsExerciseSegment(ReadOnlySpan<char> segment)
        => segment.Equals(ExerciseSubNamespace, StringComparison.OrdinalIgnoreCase)
           || segment.Equals(ExerciseSubNamespace + "s", StringComparison.OrdinalIgnoreCase);

    private static ReadOnlySpan<char> ParentSegment(string path)
    {
        var last = path.LastIndexOf('/');
        if (last <= 0)
            return [];
        var parent = path.AsSpan(0, last);
        var beforeParent = parent.LastIndexOf('/');
        return beforeParent < 0 ? parent : parent[(beforeParent + 1)..];
    }

    internal static ReadOnlySpan<char> LastSegment(string path)
    {
        var last = path.LastIndexOf('/');
        return last < 0 ? path.AsSpan() : path.AsSpan(last + 1);
    }

    /// <summary>
    /// The pages a course side-nav lists under <paramref name="parentPath"/>: its DIRECT children
    /// (deeper support nodes such as <c>Source/…</c> are excluded to keep the TOC a clean page list), ordered
    /// by <see cref="MeshNode.Order"/> then <see cref="MeshNode.Name"/> (case-insensitive). Internal /
    /// satellite nodes — any whose final path segment starts with <c>_</c> (<c>_GitSync</c>, <c>_Access</c>,
    /// <c>_Thread</c>, <c>_Activity</c>, …) and any <c>GitHubSyncConfig</c> (a real main node that <c>is:main</c>
    /// keeps) — are filtered out: they are plumbing, never learner-facing pages.
    /// Applied at every level of the rail (course → modules, module → pages, module → exercises), so one
    /// ordering contract governs the whole index. Pure — the nav-rendering and the ordering contract are the
    /// same function, so a test can pin the order without reaching through the render/serialization layer.
    /// </summary>
    /// <param name="parentPath">The containing node (course, module, or exercise folder) whose children are listed.</param>
    /// <param name="mainNodes">The course + its main descendants (the <c>scope:subtree is:main</c> query result).</param>
    public static IReadOnlyList<MeshNode> SelectCoursePages(
        string parentPath, IReadOnlyCollection<MeshNode> mainNodes)
    {
        var prefix = parentPath + "/";
        return mainNodes
            .Where(n => !string.IsNullOrEmpty(n.Path)
                        && IsDirectChildPage(n.Path.AsSpan(), prefix)
                        && !GitHubSyncConfigNodeType.Equals(n.NodeType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Order)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // `{space}/_GitSync` is a non-satellite internal config node — it's a real main node (MainNode == Path),
    // so `is:main` keeps it; drop it by type so it never shows up as a learner-facing page in the rail. (See
    // SatelliteEntityPatterns.md — the READER filters internal main-nodes; we do NOT forge a satellite
    // MainNode on the node itself.) Mirrors GitHubSyncService.ConfigNodeType, which MeshWeaver.Graph cannot
    // reference.
    private const string GitHubSyncConfigNodeType = "GitHubSyncConfig";

    // A direct-child PAGE of the parent: sits immediately under it (no deeper), and its own segment
    // is not an internal/satellite node (leading '_' — _GitSync, _Access, _Thread, …).
    private static bool IsDirectChildPage(ReadOnlySpan<char> path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var segment = path[prefix.Length..];
        return segment.Length > 0 && !segment.Contains('/') && segment[0] != '_';
    }

    /// <summary>
    /// One entry in the course rail. Either a plain <b>page</b> link into the shared course, or an
    /// <b>exercise</b> that resolves into the learner's OWN copy — never the template
    /// (<see cref="PersonalExercisePath"/>).
    /// </summary>
    /// <param name="Title">The label shown in the rail.</param>
    /// <param name="SourcePath">The node in the CENTRAL course this entry stands for (its identity).</param>
    /// <param name="Href">Where the link navigates, or <c>null</c> when the target must be resolved on click.</param>
    /// <param name="ResolveFromPath">The source path to run <see cref="EnsurePersonalCopy"/> on when clicked
    /// (set only when <paramref name="Href"/> is null); <c>null</c> together with a null Href means the entry
    /// is listed but not navigable — the rail shows the exercise exists without ever linking the template.</param>
    /// <param name="IsActive">Whether this entry is where the learner currently stands — the page being read,
    /// or (when that page is not itself a rail entry) the deepest entry containing it. See
    /// <see cref="ResolveActivePath"/>.</param>
    /// <param name="IsExercise">Whether this entry is an exercise (personal-copy target) rather than a page.</param>
    /// <param name="Icon">The node's icon, or null.</param>
    public sealed record CourseNavLink(
        string Title,
        string SourcePath,
        string? Href,
        string? ResolveFromPath,
        bool IsActive,
        bool IsExercise,
        string? Icon);

    /// <summary>One module of the course rail: a collapsible group of page + exercise links.</summary>
    /// <param name="Title">The module's name (the group heading).</param>
    /// <param name="Path">The module's path in the central course.</param>
    /// <param name="Expanded">Whether the group renders expanded — true only for the module the learner is
    /// inside (at ANY depth below it), so a long course stays scannable. Collapsing the others hides links,
    /// never modules: the group is still there and one click away.</param>
    /// <param name="Links">The module home, its pages, then its exercises (each ordered by Order then Name).</param>
    public sealed record CourseNavModule(
        string Title,
        string Path,
        bool Expanded,
        IReadOnlyList<CourseNavLink> Links);

    /// <summary>
    /// The whole course index the rail renders: the course home, any course-level pages, and every module.
    /// A pure value — <see cref="BuildCourseNavModel"/> computes it and <see cref="RenderCourseNav"/> renders
    /// it, so the structure, ordering, expansion and link-target invariants are testable without a renderer.
    /// </summary>
    /// <param name="CoursePath">The central course root the rail is indexed on.</param>
    /// <param name="Home">The course home link.</param>
    /// <param name="Pages">Course-level pages that are not modules (childless direct children, e.g. a cover).</param>
    /// <param name="Modules">Every module of the course, ordered by Order then Name.</param>
    public sealed record CourseNavModel(
        string CoursePath,
        CourseNavLink Home,
        IReadOnlyList<CourseNavLink> Pages,
        IReadOnlyList<CourseNavModule> Modules);

    /// <summary>
    /// Builds the whole course index from one <c>scope:subtree</c> query result. Pure and total:
    /// <list type="bullet">
    ///   <item><b>The index does not depend on where the learner stands.</b> EVERY module of the course is
    ///   listed (a direct child that has children), ordered by <see cref="MeshNode.Order"/> then name, with
    ///   the same links under it, whatever <paramref name="currentPath"/> is — course root, module page,
    ///   a page inside a module, a module's <see cref="ExerciseSubNamespace"/> index, an exercise inside
    ///   <c>{viewer}/…</c>, or anything deeper. Navigating in only moves the <em>markers</em>
    ///   (<see cref="CourseNavLink.IsActive"/> / <see cref="CourseNavModule.Expanded"/>); it never re-scopes
    ///   or shrinks the rail.</item>
    ///   <item>Only the module containing the current page is <see cref="CourseNavModule.Expanded"/>, and
    ///   exactly one entry is <see cref="CourseNavLink.IsActive"/> — the page itself, or the deepest entry
    ///   containing it when the page is not a rail entry of its own (<see cref="ResolveActivePath"/>). Both
    ///   are computed on the CENTRAL path, so they hold while the learner reads their own copy.</item>
    ///   <item>Exercises (<see cref="IsExercise"/>, including everything in a module's
    ///   <see cref="ExerciseSubNamespace"/> folder) link into the learner's own copy: a direct href when the
    ///   copy exists (<paramref name="personalPaths"/>), otherwise a resolve-or-copy click. NEVER the
    ///   template — at course level too, where a childless exercise is still an exercise and not a page.</item>
    /// </list>
    /// </summary>
    /// <param name="coursePath">The central course root (see <see cref="ResolveCourseRoot"/>).</param>
    /// <param name="currentPath">The page being read — the template page or the learner's copy of it.</param>
    /// <param name="viewer">The viewer's home partition, or null/empty when there is none.</param>
    /// <param name="mainNodes">The course + its main descendants (<c>path:{course} scope:subtree is:main</c>).</param>
    /// <param name="personalPaths">The paths that exist in the viewer's own copy of the course (may be empty).</param>
    public static CourseNavModel BuildCourseNavModel(
        string coursePath,
        string currentPath,
        string? viewer,
        IReadOnlyCollection<MeshNode> mainNodes,
        IReadOnlySet<string> personalPaths)
    {
        // Everything is compared on the CENTRAL path: a learner reading {viewer}/{course}/… is on the same
        // page of the same course, so their rail highlights and expands exactly like the template's.
        var current = ToSourcePath(currentPath, viewer);

        var root = mainNodes.FirstOrDefault(n => string.Equals(n.Path, coursePath, StringComparison.Ordinal));
        var home = new CourseNavLink(
            root?.Name ?? new string(LastSegment(coursePath)),
            coursePath,
            LearnHref(coursePath),
            null,
            false,
            false,
            root?.Icon);

        var pages = new List<CourseNavLink>();
        var modules = new List<CourseNavModule>();

        foreach (var child in SelectCoursePages(coursePath, mainNodes))
        {
            var childPages = SelectCoursePages(child.Path, mainNodes);
            if (childPages.Count == 0)
            {
                // A leaf directly under the course. Usually a cover/outline page — but an EXERCISE leaf
                // (by node type, or a course-level Exercise/ folder that holds nothing) is an exercise
                // wherever it sits, so it resolves into the learner's copy instead of becoming a plain page
                // link at the template. That is the one thing the rail never emits.
                pages.Add(IsExercise(child) || IsExerciseSegment(LastSegment(child.Path))
                    ? ExerciseLink(child, coursePath, viewer, personalPaths)
                    : PageLink(child));
                continue;
            }

            modules.Add(BuildModule(child, childPages, coursePath, current, viewer, mainNodes, personalPaths));
        }

        // The index above is built WITHOUT any position marker — it is the same for every page of the course.
        // Only now is "you are here" resolved and stamped onto exactly one entry, so drilling down can move
        // the highlight but can never re-scope or shrink the rail.
        return MarkActive(new CourseNavModel(coursePath, home, pages, modules), current);
    }

    /// <summary>
    /// Which rail entry marks "you are here": the entry whose path IS the current page, or — when that page
    /// is not a rail entry of its own — the DEEPEST entry that contains it. That fallback is what makes the
    /// rail work at any depth: a module's <see cref="ExerciseSubNamespace"/> index page (whose own entry is
    /// replaced by the exercises it holds, e.g. <c>{course}/{module}/Exercises</c>), a sub-page below the
    /// level the rail lists, or a page inside an exercise all highlight the nearest thing that IS listed
    /// instead of leaving the learner with no position at all. Returns <c>null</c> when nothing contains the
    /// page. Pure.
    /// </summary>
    /// <param name="currentPath">The CENTRAL path of the page being read (see <see cref="ToSourcePath"/>).</param>
    /// <param name="entryPaths">The <see cref="CourseNavLink.SourcePath"/> of every entry the rail emits.</param>
    public static string? ResolveActivePath(string currentPath, IEnumerable<string> entryPaths)
    {
        string? deepestAncestor = null;
        foreach (var entry in entryPaths)
        {
            if (string.IsNullOrEmpty(entry))
                continue;
            if (string.Equals(entry, currentPath, StringComparison.Ordinal))
                return entry;                                       // an exact entry always wins
            if (currentPath.StartsWith(entry + "/", StringComparison.Ordinal)
                && (deepestAncestor is null || entry.Length > deepestAncestor.Length))
                deepestAncestor = entry;
        }
        return deepestAncestor;
    }

    // Stamps IsActive onto the single entry ResolveActivePath picks. A post-pass over the finished index
    // rather than a flag threaded through the build, so there is exactly ONE rule for "where am I" and the
    // index itself is provably independent of the current page.
    private static CourseNavModel MarkActive(CourseNavModel model, string current)
    {
        var active = ResolveActivePath(current, EnumerateLinks(model).Select(l => l.SourcePath));
        if (active is null)
            return model;

        CourseNavLink Mark(CourseNavLink link)
            => string.Equals(link.SourcePath, active, StringComparison.Ordinal)
                ? link with { IsActive = true }
                : link;

        return model with
        {
            Home = Mark(model.Home),
            Pages = model.Pages.Select(Mark).ToList(),
            Modules = model.Modules
                .Select(m => m with { Links = m.Links.Select(Mark).ToList() })
                .ToList(),
        };
    }

    /// <summary>Every link the rail emits, in render order — the course home, its course-level pages, then
    /// each module's links. The set a caller checks invariants over (position, never-link-the-template).</summary>
    /// <param name="model">The course index.</param>
    public static IEnumerable<CourseNavLink> EnumerateLinks(CourseNavModel model)
        => new[] { model.Home }.Concat(model.Pages).Concat(model.Modules.SelectMany(m => m.Links));

    private static CourseNavModule BuildModule(
        MeshNode module,
        IReadOnlyList<MeshNode> modulePages,
        string coursePath,
        string current,
        string? viewer,
        IReadOnlyCollection<MeshNode> mainNodes,
        IReadOnlySet<string> personalPaths)
    {
        var title = module.Name ?? module.Id;
        var links = new List<CourseNavLink>
        {
            // Module home first. It is also the entry that carries the highlight for any page of the module
            // the rail does not list individually — the module's Exercise/ index, or a deeper sub-page
            // (MarkActive / ResolveActivePath).
            new(title, module.Path, LearnHref(module.Path), null, false, false, module.Icon)
        };

        // Exercises are collected separately so they always land at the END of the module — the reading
        // pages first, then the "your turn" list.
        var exercises = new List<CourseNavLink>();

        foreach (var page in modulePages)
        {
            if (IsExerciseSegment(LastSegment(page.Path)))
            {
                // The module's Exercise/ folder: its children ARE the exercises, so the folder's own index
                // page is replaced by them rather than listed alongside. With no children the node is not a
                // folder at all — it IS the module's single exercise.
                var contained = SelectCoursePages(page.Path, mainNodes);
                exercises.AddRange(contained.Count > 0
                    ? contained.Select(x => ExerciseLink(x, coursePath, viewer, personalPaths))
                    : [ExerciseLink(page, coursePath, viewer, personalPaths)]);
                continue;
            }

            if (IsExercise(page))
            {
                exercises.Add(ExerciseLink(page, coursePath, viewer, personalPaths));
                continue;
            }

            links.Add(PageLink(page));
        }

        links.AddRange(exercises);

        // Expanded only for the module the learner is inside (its own page, one of its pages, or one of its
        // exercises) — an 18-module course stays a scannable list rather than a wall of links.
        var expanded = string.Equals(current, module.Path, StringComparison.Ordinal)
                       || current.StartsWith(module.Path + "/", StringComparison.Ordinal);

        return new CourseNavModule(title, module.Path, expanded, links);
    }

    private static CourseNavLink PageLink(MeshNode page)
        => new(page.Name ?? page.Id, page.Path, LearnHref(page.Path), null, false, false, page.Icon);

    // An exercise entry. NEVER links the template: it points at the learner's own copy when that copy
    // exists, and otherwise carries the resolve-or-copy source for the click (the same EnsurePersonalCopy
    // GoToMyCopy runs), so the first click creates the copy and lands there.
    private static CourseNavLink ExerciseLink(
        MeshNode exercise,
        string coursePath,
        string? viewer,
        IReadOnlySet<string> personalPaths)
    {
        var personal = PersonalExercisePath(coursePath, exercise.Path, viewer);
        var hasCopy = personal is not null && personalPaths.Contains(personal);
        return new CourseNavLink(
            exercise.Name ?? exercise.Id,
            exercise.Path,
            hasCopy ? LearnHref(personal!) : null,
            hasCopy || personal is null ? null : exercise.Path,
            false,
            true,
            exercise.Icon);
    }

    private static string LearnHref(string path) => new LayoutAreaReference(LearnArea).ToHref(path);

    /// <summary>
    /// Renders a <see cref="CourseNavModel"/> as the rail: the course home, its course-level pages, then one
    /// collapsible <see cref="NavGroupControl"/> per module. A link with no href but a
    /// <see cref="CourseNavLink.ResolveFromPath"/> becomes a click that runs
    /// <see cref="EnsurePersonalCopy"/> and navigates into the resulting copy — so an exercise the learner
    /// has not started yet is still one click away, without the rail ever emitting a template href.
    /// </summary>
    /// <param name="model">The course index to render.</param>
    public static NavMenuControl RenderCourseNav(CourseNavModel model)
    {
        var menu = Controls.NavMenu
            .WithSkin(s => s.WithWidth(300).WithCollapsible(false))
            .WithNavLink(RenderNavLink(model.Home));

        foreach (var page in model.Pages)
            menu = menu.WithNavLink(RenderNavLink(page));

        foreach (var module in model.Modules)
        {
            var group = new NavGroupControl(module.Title).WithSkin(s => s.WithExpanded(module.Expanded));
            foreach (var link in module.Links)
                group = group.WithView(RenderNavLink(link));
            menu = menu.WithNavGroup(group);
        }

        return menu;
    }

    private static NavLinkControl RenderNavLink(CourseNavLink link)
    {
        var control = new NavLinkControl(link.Title, link.Icon, link.Href).WithIsActive(link.IsActive);
        if (link.Href is not null || link.ResolveFromPath is null)
            return control;

        // No copy yet: resolve-or-copy on click (idempotent), exactly like the GoToMyCopy button — and with
        // no href, so the rail cannot navigate to the template even by accident.
        var source = link.ResolveFromPath;
        return control.WithClickAction(ctx =>
        {
            var hub = ctx.Host.Hub;
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Graph.Education");
            EnsurePersonalCopy(hub, source).Subscribe(
                landing => ctx.NavigateTo(LearnHref(landing)),
                ex =>
                {
                    logger?.LogWarning(ex, "CourseNav: could not open the personal copy of {Source}", source);
                    ctx.Host.UpdateArea(DialogControl.DialogArea, Controls.Dialog(
                            Controls.Markdown($"**Couldn't open your copy:**\n\n{ex.Message}"),
                            "Go to Exercise")
                        .WithSize("M").WithClosable(true));
                });
            return Task.CompletedTask;
        });
    }

    private static UiControl RedirectToLearn(string path) =>
        new RedirectControl(new LayoutAreaReference(LearnArea).ToHref(path));

    /// <summary>
    /// The signed-in viewer's HOME partition (their <c>AccessContext.ObjectId</c>, per-delivery first
    /// then durable circuit) — mirrors <c>CodeLayoutAreas.ResolveViewerHome</c>. System / anonymous /
    /// hub-shaped principals yield <c>null</c>: they have no home partition to copy into.
    /// </summary>
    internal static string? ResolveViewerHome(AccessService? accessService)
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
