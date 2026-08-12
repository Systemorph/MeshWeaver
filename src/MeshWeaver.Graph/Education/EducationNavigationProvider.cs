using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Registration for <see cref="EducationNavigationProvider"/>.
/// </summary>
public static class EducationNavigationExtensions
{
    /// <summary>
    /// Registers the provider — mesh-scoped singleton, resolved by every per-node hub, so any page
    /// of any node-native course gets the whole-course index. Mirrors
    /// <c>MeshWeaver.Courses.AddCourses</c>' registration of its own provider.
    /// </summary>
    public static TBuilder AddEducationNavigation<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
        => (TBuilder)builder.ConfigureServices(services =>
            services.AddSingleton<INodeNavigationProvider, EducationNavigationProvider>());
}

/// <summary>
/// Supplies the whole-course left-hand navigation for pages of a NODE-NATIVE course — a partition
/// whose pages are typed by an education plugin (<c>Edu/Lesson</c>, <c>Edu/Module</c>,
/// <c>Edu/Exercise</c>, …) rather than by core's own Courses module (those have
/// <c>CourseNavigationProvider</c> in MeshWeaver.Courses).
///
/// <para><b>Why core's default is wrong here, again.</b> The default menu lists the current node's
/// children, so a learner reading lesson 2 of a nine-lesson course sees lesson 2's sub-pages and
/// nothing else — they cannot tell how long the course is, what comes next, or where they stand.
/// The 2026-08-08 fix ("the course index showed one branch, never the course") repaired this for
/// core-Courses courses only; every GitSync'd plugin course kept the one-branch menu, which is what
/// kept getting reported. This provider claims those by SHAPE — the node types themselves say
/// "lesson" — since core cannot reference a live-compiled plugin's types.</para>
///
/// <para><b>The learner's own copy indexes ITSELF.</b> A page inside <c>{viewer}/{course}/…</c>
/// gets the index of <c>{viewer}/{course}</c>, not of the central course: the copy is where the
/// learner reads, edits and runs their material, so its menu must navigate the copy (a link out to
/// the template would abandon their own work). Whatever the copy holds is what the menu shows —
/// an older exercises-only install simply shows its exercises.</para>
///
/// <para>Pure at its core: <see cref="BuildNavigation"/> turns one subtree snapshot into the
/// index, so structure, ordering, exclusions and position marking are pinned without a mesh.</para>
/// </summary>
public sealed class EducationNavigationProvider : INodeNavigationProvider
{
    /// <inheritdoc />
    public IObservable<NodeNavigation?>? GetNavigation(LayoutAreaHost host)
    {
        var currentPath = host.Hub.Address.ToString();
        if (string.IsNullOrEmpty(currentPath))
            return null;

        var segments = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // A satellite (_Access, _Thread, …) is never a course page. Cheap decline, no query.
        if (segments.Length == 0 || segments.Any(s => s[0] == '_'))
            return null;

        // The indexed root: the page's partition — or, inside the signed-in viewer's own home
        // ({viewer}/{course}/…), the course copy one level down (see class remarks).
        var viewer = EducationLayoutAreas.ResolveViewerHome(
            host.Hub.ServiceProvider.GetService<AccessService>());
        var root = !string.IsNullOrEmpty(viewer)
                   && string.Equals(segments[0], viewer, StringComparison.Ordinal)
                   && segments.Length >= 2
            ? $"{segments[0]}/{segments[1]}"
            : segments[0];

        var hub = host.Hub;
        var courseSlug = CourseProgress.CourseSlug(root);

        // The learner's visit markers decorate the index (✓ on visited lessons). Starts empty so
        // a slow or absent record can never hold the menu back.
        var visitedStream = string.IsNullOrEmpty(viewer)
            ? Observable.Return(NoLessons)
            : hub.GetQuery(
                    $"course-progress:{viewer}:{courseSlug}",
                    $"path:{CourseProgress.VisitedPath(viewer, courseSlug)} scope:descendants select:path,id")
                .Select(markers => CourseProgress.VisitedSlugs(markers))
                .Catch((Exception _) => Observable.Return(NoLessons))
                .StartWith(NoLessons);

        // The visit WRITE is prepared HERE, on the render turn, where the viewer's identity is
        // ambient — CreateOrUpdateNode captures the AccessContext when it is CALLED, so building
        // it inside a delayed callback would stamp whatever identity that scheduler hop carries
        // (the resume record's writer documents the same rule). It is an idempotent no-read
        // upsert of a self-describing marker, so there is no read-modify-write to race.
        var visitWrite = PrepareVisitWrite(host, viewer, root, courseSlug, currentPath);

        // ONE subtree query per course, shared by every page of it (synced + RLS-wrapped).
        // Defer wraps a per-subscription flag: the first CLAIMED index this page renders arms the
        // visit write — once per page open, and only after CourseProgress.VisitDwell so a page the
        // reader merely paged through is never marked read. Leaving CANCELS the dwell: the
        // subscription is registered for disposal with the area.
        return Observable.Defer(() =>
        {
            var recorded = false;
            return hub.GetQuery(
                    $"edu-course-index:{root}",
                    $"path:{root} scope:subtree is:main select:path,id,name,order,nodeType,icon")
                .CombineLatest(visitedStream, (nodes, visited) =>
                    DecorateVisited(BuildNavigation(root, nodes.ToList(), currentPath), visited))
                .Do(navigation =>
                {
                    if (navigation is null || recorded || visitWrite is null)
                        return;
                    recorded = true;
                    host.RegisterForDisposal(visitWrite
                        .DelaySubscription(CourseProgress.VisitDwell)
                        .Take(1)
                        .Subscribe(_ => { }, _ => { }));
                });
        });
    }

    // The marker upsert for this page, built on the render turn (see GetNavigation), or null when
    // there is nothing to record: no signed-in home, or the page is not under a lesson.
    private static IObservable<MeshNode>? PrepareVisitWrite(
        LayoutAreaHost host, string? viewer, string root, string courseSlug, string currentPath)
    {
        if (string.IsNullOrEmpty(viewer))
            return null;
        var lessonSlug = CourseProgress.LessonSlug(root, currentPath);
        if (lessonSlug is null)
            return null;

        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        return meshService?.CreateOrUpdateNode(CourseProgress.VisitMarker(
            viewer, courseSlug, lessonSlug, currentPath, DateTimeOffset.UtcNow.ToString("O")));
    }

    // The empty visited set — shared, never mutated.
    private static readonly IReadOnlySet<string> NoLessons = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Marks visited lessons with a leading ✓ — the glyph is language-neutral and rides the label,
    /// so it needs nothing from the renderer. <paramref name="visitedSlugs"/> holds LESSON KEYS
    /// (folder names — <see cref="CourseProgress.LessonSlug"/>), which the central course and the
    /// learner's copy share, so both decorate identically. Pure.
    /// </summary>
    /// <param name="navigation">The built index, or null (declined — passes through).</param>
    /// <param name="visitedSlugs">Lesson keys the learner has opened.</param>
    public static NodeNavigation? DecorateVisited(
        NodeNavigation? navigation, IReadOnlySet<string> visitedSlugs)
    {
        if (navigation is null || visitedSlugs.Count == 0)
            return navigation;

        return navigation with
        {
            Entries = navigation.Entries
                .Select(e => visitedSlugs.Contains(new string(EducationLayoutAreas.LastSegment(e.Path)))
                    ? e with { Label = $"✓ {e.Label}" }
                    : e)
                .ToList(),
        };
    }

    /// <summary>
    /// Turns one course-subtree snapshot into the whole-course index, or <c>null</c> when the
    /// subtree is not a course at all (no education-typed node — the provider then declines and
    /// core's default child list stands).
    ///
    /// <list type="bullet">
    ///   <item><b>Every lesson, whatever the page.</b> The index lists the course's direct children
    ///   (<see cref="EducationLayoutAreas.SelectCoursePages"/> — ordered by Order then name,
    ///   satellites and sync config excluded); a child with pages becomes a group holding them.
    ///   Where the reader stands only moves the marker, never the scope.</item>
    ///   <item><b>Lesson internals stay out.</b> A lesson's <c>Source</c>/<c>Test</c> folders hold
    ///   code that renders INSIDE its pages; listing them would bury the course structure. An
    ///   <c>Exercise</c>/<c>Exercises</c> folder is replaced by the exercises it holds, which land
    ///   at the END of their lesson — reading pages first, then the your-turn list (the same order
    ///   the reader shell's rail uses).</item>
    ///   <item><b>Exactly one entry is current</b> — the page itself, or the deepest listed entry
    ///   containing it (<see cref="EducationLayoutAreas.ResolveActivePath"/>), so a reader inside
    ///   an exercise's sub-page still has a position.</item>
    /// </list>
    /// Pure.
    /// </summary>
    /// <param name="root">The course root the index is built for.</param>
    /// <param name="nodes">The root's subtree (one <c>scope:subtree is:main</c> snapshot).</param>
    /// <param name="currentPath">The page being read.</param>
    public static NodeNavigation? BuildNavigation(
        string root, IReadOnlyCollection<MeshNode> nodes, string currentPath)
    {
        if (!LooksLikeCourse(nodes))
            return null;

        var rootNode = nodes.FirstOrDefault(n => string.Equals(n.Path, root, StringComparison.Ordinal));
        var entries = new List<NodeNavigationEntry>();

        foreach (var child in EducationLayoutAreas.SelectCoursePages(root, nodes))
        {
            var children = LessonPages(child.Path, nodes);
            entries.Add(new NodeNavigationEntry(
                child.Name ?? child.Id, child.Path, Icon: child.Icon)
            {
                Children = children,
            });
        }

        // Position is stamped LAST, onto exactly one entry, so the index itself is provably the
        // same wherever the reader stands.
        var active = EducationLayoutAreas.ResolveActivePath(
            currentPath,
            entries.SelectMany(e => new[] { e.Path }.Concat(e.Children.Select(c => c.Path))));

        return new NodeNavigation(
            rootNode?.Name ?? rootNode?.Id ?? new string(EducationLayoutAreas.LastSegment(root)),
            active is null ? entries : entries.Select(e => Marked(e, active)).ToList())
        {
            TitlePath = root,
        };
    }

    /// <summary>
    /// Whether a subtree IS a course this provider should claim: any node typed as a lesson,
    /// module or exercise of an education module (<c>Edu/Lesson</c>, <c>Edu/Module</c>,
    /// <c>{anything}/Lesson</c>, …, or the exercise shapes
    /// <see cref="EducationLayoutAreas.IsExercise"/> recognizes). Core's own Courses types
    /// (<c>Course</c>/<c>Module</c>, no module prefix) deliberately do NOT match — those pages
    /// belong to MeshWeaver.Courses' provider. Pure.
    /// </summary>
    /// <param name="nodes">The candidate subtree.</param>
    public static bool LooksLikeCourse(IEnumerable<MeshNode> nodes)
        => nodes.Any(n =>
            EndsWithSegment(n.NodeType, "Lesson")
            || EndsWithSegment(n.NodeType, "Module")
            || EducationLayoutAreas.IsExercise(n));

    private static bool EndsWithSegment(string? nodeType, string segment)
        => nodeType is not null
           && nodeType.Length > segment.Length + 1
           && nodeType.EndsWith("/" + segment, StringComparison.OrdinalIgnoreCase);

    // A lesson's menu entries: its learner-facing pages first, its exercises last. Source/Test
    // folders (page-embedded code) are dropped; an Exercise folder is replaced by its children.
    private static IReadOnlyList<NodeNavigationEntry> LessonPages(
        string lessonPath, IReadOnlyCollection<MeshNode> nodes)
    {
        var pages = new List<NodeNavigationEntry>();
        var exercises = new List<NodeNavigationEntry>();

        foreach (var page in EducationLayoutAreas.SelectCoursePages(lessonPath, nodes))
        {
            var segment = EducationLayoutAreas.LastSegment(page.Path);
            if (segment.Equals("Source", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Test", StringComparison.OrdinalIgnoreCase))
                continue;

            if (EducationLayoutAreas.IsExerciseSegment(segment))
            {
                // The Exercise/ folder: its children ARE the exercises — with none, the node
                // itself is the lesson's single exercise (mirrors the reader shell's rail).
                var contained = EducationLayoutAreas.SelectCoursePages(page.Path, nodes);
                exercises.AddRange(contained.Count > 0
                    ? contained.Select(Entry)
                    : [Entry(page)]);
                continue;
            }

            (EducationLayoutAreas.IsExercise(page) ? exercises : pages).Add(Entry(page));
        }

        pages.AddRange(exercises);
        return pages;
    }

    private static NodeNavigationEntry Entry(MeshNode node)
        => new(node.Name ?? node.Id, node.Path, Icon: node.Icon);

    // Stamps IsCurrent onto the one entry (or nested child) whose path is the resolved position.
    private static NodeNavigationEntry Marked(NodeNavigationEntry entry, string active)
    {
        if (string.Equals(entry.Path, active, StringComparison.Ordinal))
            return entry with { IsCurrent = true };
        if (entry.Children.Count == 0)
            return entry;
        return entry with
        {
            Children = entry.Children
                .Select(c => string.Equals(c.Path, active, StringComparison.Ordinal)
                    ? c with { IsCurrent = true }
                    : c)
                .ToList(),
        };
    }
}
