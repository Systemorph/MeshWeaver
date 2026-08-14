using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Which lessons a learner has OPENED, captured in their own home partition — one idempotent
/// marker node per lesson at <c>{viewer}/_Progress/{course}/Visited/{lesson}</c>.
/// <see cref="EducationNavigationProvider"/> writes a marker when a course page has been open for
/// <see cref="VisitDwell"/> and decorates the course index from the same markers (✓ on visited
/// lessons), so "what have I done" lives with the learner, not with the course.
///
/// <para><b>Deliberately BESIDE the resume record, never inside it.</b> The Edu module keeps the
/// learner's "where did I stop" as a single node at <c>{viewer}/_Progress/{course}</c> written as a
/// blind later-visit-wins upsert (MeshWeaver.Plugins #428) — a shape that cannot carry an
/// accumulating map without becoming a read-modify-write and inheriting its lost-update race. The
/// visited history therefore accumulates as CHILD markers under that record: each visit is its own
/// self-describing node, each write is a no-read idempotent upsert, and the two writers can never
/// clobber each other because they never touch the same node.</para>
///
/// <para><b>One record per course, whichever copy is read.</b> The course key is the course root's
/// LAST segment and the lesson key the lesson folder's — identical for the central course and the
/// learner's own copy (<c>{viewer}/{course}/…</c>), so progress does not fork when a learner
/// installs mid-course. (The same keying the resume record uses.)</para>
///
/// <para>Pure functions only — the WRITE lives in the provider, where the render turn's ambient
/// viewer identity is available (a marker in the learner's own partition is the learner's write).</para>
/// </summary>
public static class CourseProgress
{
    /// <summary>The satellite namespace under the viewer's home that holds all course progress.</summary>
    public const string ProgressNamespace = "_Progress";

    /// <summary>The sub-namespace holding the per-lesson visit markers, beside the resume record.</summary>
    public const string VisitedNamespace = "Visited";

    /// <summary>
    /// How long a course page must stay open before the visit is remembered — a DWELL threshold,
    /// same value and same reasoning as the resume record's: paging through an index renders
    /// pages for a fraction of a second each, and "visited" must mean read, not passed through.
    /// </summary>
    public static readonly TimeSpan VisitDwell = TimeSpan.FromSeconds(3);

    /// <summary>The container of a course's visit markers: <c>{viewer}/_Progress/{course}/Visited</c>.</summary>
    /// <param name="viewer">The viewer's home partition.</param>
    /// <param name="courseSlug">The course key — the course root's last segment (<see cref="CourseSlug"/>).</param>
    public static string VisitedPath(string viewer, string courseSlug)
        => $"{viewer}/{ProgressNamespace}/{courseSlug}/{VisitedNamespace}";

    /// <summary>
    /// The course key of an indexed root — its LAST segment, so the central course
    /// (<c>ThinkInStreams</c>) and the learner's copy (<c>{viewer}/ThinkInStreams</c>) share one
    /// progress subtree. Pure.
    /// </summary>
    /// <param name="root">The indexed course root (central or the viewer's copy).</param>
    public static string CourseSlug(string root)
        => new string(EducationHelpers.LastSegment(root));

    /// <summary>
    /// The lesson key of the page being read: the first segment UNDER the course root — the lesson
    /// folder both trees share — or <c>null</c> when the page IS the root (nothing lesson-shaped to
    /// mark). Pure.
    /// </summary>
    /// <param name="root">The indexed course root.</param>
    /// <param name="currentPath">The page being read (under that root).</param>
    public static string? LessonSlug(string root, string currentPath)
    {
        if (!currentPath.StartsWith(root + "/", StringComparison.Ordinal))
            return null;
        var tail = currentPath[(root.Length + 1)..];
        var slash = tail.IndexOf('/');
        var slug = slash < 0 ? tail : tail[..slash];
        return slug.Length == 0 || slug[0] == '_' ? null : slug;
    }

    /// <summary>
    /// The visit marker — a complete, self-describing node, so recording a visit is an IDEMPOTENT
    /// no-read upsert: no read-modify-write, therefore no lost-update race to design around, and a
    /// re-visit rewrites the same statement. Human-readable (a Markdown node), like the resume
    /// record beside it. Pure.
    /// </summary>
    /// <param name="viewer">The viewer's home partition.</param>
    /// <param name="courseSlug">The course key (<see cref="CourseSlug"/>).</param>
    /// <param name="lessonSlug">The lesson key (<see cref="LessonSlug"/>).</param>
    /// <param name="pagePath">The page whose render records the visit (for the record's own text).</param>
    /// <param name="at">The visit instant (UTC, ISO-8601).</param>
    public static MeshNode VisitMarker(
        string viewer, string courseSlug, string lessonSlug, string pagePath, string at)
        => new(lessonSlug, VisitedPath(viewer, courseSlug))
        {
            Name = $"Visited — {lessonSlug}",
            NodeType = "Markdown",
            MainNode = $"{VisitedPath(viewer, courseSlug)}/{lessonSlug}",
            Content = new JsonObject
            {
                ["$type"] = "MarkdownContent",
                ["course"] = courseSlug,
                ["lesson"] = lessonSlug,
                ["lastPath"] = pagePath,
                ["lastVisitedAt"] = at,
                ["content"] = $"Visited [{pagePath}](/{pagePath}) — {at}.\n",
            },
        };

    /// <summary>
    /// The lesson keys a set of visit markers says the learner has opened — their node ids (the
    /// marker path's last segment). Tolerates anything else living in the container. Pure.
    /// </summary>
    /// <param name="markers">The nodes under a course's <see cref="VisitedPath"/>.</param>
    public static IReadOnlySet<string> VisitedSlugs(IEnumerable<MeshNode> markers)
        => markers
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal);
}
