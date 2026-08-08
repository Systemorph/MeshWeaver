using System.Reactive.Linq;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Graph;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Courses;

/// <summary>
/// Supplies the left-hand index for pages that sit inside a course: the WHOLE course, grouped by
/// module, with the reader's position marked — not the branch they happen to be standing in.
///
/// <para>Core's default lists the current node's children, so a reader in lesson 2 sees lesson 2's
/// sub-nodes and nothing else: they cannot tell how long the course is, what is coming, or that
/// they are nearly done. Courses owns the semantics core must not learn — that a course root is a
/// <c>Course</c> node, that its direct <c>Module</c> children are the chapters, and that lessons
/// live in the modules' <c>Theory</c> / <c>Example</c> / <c>Exercise</c> sub-namespaces — so it
/// hands core the finished index through <see cref="INodeNavigationProvider"/>.</para>
///
/// <para><b>Two shared queries, never a per-page probe.</b> Which course (if any) a page belongs to
/// comes from ONE query per PARTITION, keyed <c>course-roots:{partition}</c> — every page in the
/// partition subscribes to the same synced collection. The index itself is ONE
/// <c>scope:descendants</c> query per COURSE, keyed <c>course-index:{course}</c> and likewise
/// shared by every page of that course. A course with 200 lessons therefore costs two live
/// queries, not two hundred.</para>
///
/// <para>Both are live: adding, renaming or re-ordering a lesson re-emits and the index re-renders
/// under the reader without a reload.</para>
/// </summary>
public sealed class CourseNavigationProvider : INodeNavigationProvider
{
    /// <summary>
    /// The module sub-namespaces that hold lessons, in the order the module page renders them —
    /// theory, then worked examples, then exercises. The index reads top-to-bottom the way the
    /// module does.
    /// </summary>
    private static readonly string[] LessonSections =
    [
        ModuleNodeType.TheorySubNamespace,
        ModuleNodeType.ExampleSubNamespace,
        ExerciseNodeType.ExerciseSubNamespace,
    ];

    /// <summary>
    /// The whole-course index for the page <paramref name="host"/> renders, or <c>null</c> when the
    /// path shape rules a course out before any query is opened (a satellite, or a bare partition
    /// root — neither can sit under a course).
    /// </summary>
    /// <param name="host">The layout-area host; its hub address IS the page's path.</param>
    public IObservable<NodeNavigation?>? GetNavigation(LayoutAreaHost host)
    {
        var currentPath = host.Hub.Address.ToString();
        if (string.IsNullOrEmpty(currentPath))
            return null;

        var segments = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // A satellite (_Access, _Thread, _Comment, …) is never a course page, and a bare partition
        // root has no course ABOVE it. Decline both here — cheap, and it opens no query.
        if (segments.Length < 2 || segments.Any(s => s[0] == '_'))
            return null;

        var hub = host.Hub;
        var partition = segments[0];

        // ONE query per partition, shared by every page in it: where are this partition's courses?
        var roots = hub.GetQuery(
            $"course-roots:{partition}",
            $"path:{partition} scope:descendants nodeType:{CourseNodeType.NodeType} select:path,id,name,icon");

        return roots
            .Select(candidates => EnclosingCourse(candidates, currentPath))
            .DistinctUntilChanged(root => root?.Path ?? string.Empty)
            .Select(root => root is null
                ? Observable.Return<NodeNavigation?>(null)
                // ONE subtree query per course, shared by every page of it.
                : hub.GetQuery(
                        $"course-index:{root.Path}",
                        $"path:{root.Path} scope:descendants is:main select:path,id,name,order,nodeType,icon")
                    .Select(nodes => BuildNavigation(root, nodes, currentPath)))
            .Switch();
    }

    /// <summary>
    /// The course <paramref name="currentPath"/> sits in: the DEEPEST candidate that is the path
    /// itself or an ancestor of it, so a course nested inside another wins over the outer one.
    /// <c>null</c> when the page is not in a course at all. Pure — testable without a mesh.
    /// </summary>
    /// <param name="candidates">The partition's <c>Course</c> nodes.</param>
    /// <param name="currentPath">The page being read.</param>
    public static MeshNode? EnclosingCourse(IEnumerable<MeshNode> candidates, string currentPath)
        => candidates
            .Where(c => !string.IsNullOrEmpty(c.Path) && Contains(c.Path, currentPath))
            .OrderByDescending(c => c.Path.Length)
            .FirstOrDefault();

    private static bool Contains(string root, string path)
        => string.Equals(root, path, StringComparison.Ordinal)
           || path.StartsWith(root + "/", StringComparison.Ordinal);

    /// <summary>
    /// Turns one course-subtree snapshot into the index: the course's <c>Module</c> children as
    /// groups, each holding its lessons, everything ordered by <see cref="MeshNode.Order"/> then id.
    ///
    /// <para>Exclusions match core's: any node with a <c>_</c>-prefixed path segment (<c>_Access</c>,
    /// <c>_Thread</c>, <c>_Comment</c>, …) is plumbing, never a lesson. So are an exercise's own
    /// <c>Source</c> / <c>Test</c> / <c>Solution</c> code nodes — they are rendered INSIDE the
    /// exercise, and listing them would bury the course structure they belong to.</para>
    ///
    /// <para>Position is stamped last, on exactly one entry: the entry whose path IS the page, or —
    /// when the page is not itself an entry (a reader inside an exercise's starter code, say) — the
    /// deepest entry that contains it. So a reader always has a position, at any depth.</para>
    ///
    /// <para>Pure, so the structure and ordering can be pinned without a renderer.</para>
    /// </summary>
    /// <param name="course">The course root node.</param>
    /// <param name="descendants">The course's descendants (one <c>scope:descendants</c> result).</param>
    /// <param name="currentPath">The page being read.</param>
    public static NodeNavigation BuildNavigation(
        MeshNode course, IEnumerable<MeshNode> descendants, string currentPath)
    {
        var prefix = course.Path + "/";
        var pages = descendants
            .Where(n => !string.IsNullOrEmpty(n.Path)
                        && n.Path.StartsWith(prefix, StringComparison.Ordinal)
                        && !n.Path[prefix.Length..].Split('/').Any(s => s.Length > 0 && s[0] == '_'))
            .ToList();

        var modules = Ordered(pages.Where(n => !n.Path[prefix.Length..].Contains('/')
                                               && ModuleNodeType.NodeType.Equals(n.NodeType, StringComparison.Ordinal)));

        var entries = modules
            .Select(module => new NodeNavigationEntry(
                module.Name ?? module.Id, module.Path, Icon: module.Icon)
            {
                Children = LessonsOf(module, pages),
            })
            .ToList();

        return MarkCurrent(
            new NodeNavigation(course.Name ?? course.Id, entries) { TitlePath = course.Path },
            currentPath);
    }

    // A module's lessons: {module}/{section}/{id} for the known lesson sections, plus any direct
    // child of the module that is not one of those section folders (a course that keeps a page
    // straight under its module still shows up). Ordered by section, then Order, then id.
    private static IReadOnlyList<NodeNavigationEntry> LessonsOf(
        MeshNode module, IReadOnlyList<MeshNode> pages)
    {
        var modulePrefix = module.Path + "/";
        return pages
            .Where(n => n.Path.StartsWith(modulePrefix, StringComparison.Ordinal))
            .Select(n => (Node: n, Rank: LessonRank(n.Path[modulePrefix.Length..].Split('/'))))
            .Where(x => x.Rank >= 0)
            .GroupBy(x => x.Rank)
            .OrderBy(g => g.Key)
            .SelectMany(g => Ordered(g.Select(x => x.Node)))
            .Select(n => new NodeNavigationEntry(n.Name ?? n.Id, n.Path, Icon: n.Icon))
            .ToList();
    }

    // Where a module-relative path sits in the reading order, or -1 when it is not a lesson at all.
    // {section}/{id} ranks by section; a bare {id} that is not a section folder comes last; anything
    // deeper (an exercise's Source/Test/Solution code) is exercise-internal and never listed.
    private static int LessonRank(string[] segments)
    {
        if (segments.Length == 2)
        {
            var section = Array.FindIndex(LessonSections,
                s => s.Equals(segments[0], StringComparison.Ordinal));
            return section < 0 ? -1 : section;
        }
        if (segments.Length != 1)
            return -1;
        return LessonSections.Any(s => s.Equals(segments[0], StringComparison.Ordinal))
            ? -1                                   // the section folder itself, not a lesson
            : LessonSections.Length;               // a page kept straight under the module
    }

    // The canonical child ordering, shared with ModuleLayoutAreas: Order ascending (null last),
    // then id — stable across re-renders.
    private static List<MeshNode> Ordered(IEnumerable<MeshNode> nodes)
        => nodes
            .OrderBy(n => n.Order ?? int.MaxValue)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();

    // Stamps "you are here" onto exactly one entry AFTER the index is built, so the index itself is
    // provably the same wherever the reader stands — navigating moves the marker, it never re-scopes
    // or shrinks the list. An exact entry wins; otherwise the deepest entry containing the page does
    // (a reader inside an exercise's starter code is marked on the exercise).
    private static NodeNavigation MarkCurrent(NodeNavigation navigation, string currentPath)
    {
        var candidates = navigation.Entries
            .SelectMany(e => new[] { e.Path }.Concat(e.Children.Select(c => c.Path)))
            .Concat([navigation.TitlePath ?? string.Empty]);

        string? current = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            if (string.Equals(candidate, currentPath, StringComparison.Ordinal))
            {
                current = candidate;
                break;
            }
            if (currentPath.StartsWith(candidate + "/", StringComparison.Ordinal)
                && (current is null || candidate.Length > current.Length))
                current = candidate;
        }

        if (current is null)
            return navigation;

        return navigation with
        {
            Entries = navigation.Entries
                .Select(e => e with
                {
                    IsCurrent = string.Equals(e.Path, current, StringComparison.Ordinal),
                    Children = e.Children
                        .Select(c => c with { IsCurrent = string.Equals(c.Path, current, StringComparison.Ordinal) })
                        .ToList(),
                })
                .ToList(),
        };
    }
}
