using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The learner's course progress, captured IN THEIR OWN HOME partition — one record per course at
/// <c>{viewer}/_Progress/{course}</c>: where they last stood (<c>lastPath</c> /
/// <c>lastVisitedAt</c>) and which lessons they have opened (<c>visited</c>, lesson path → first
/// visit). <see cref="EducationNavigationProvider"/> writes a visit whenever a course page renders
/// its index and decorates the menu from the same record (✓ on visited lessons), so "where am I,
/// what have I done" lives with the learner, not with the course.
///
/// <para><b>One record per course, whichever copy is read.</b> A page in the learner's own copy
/// (<c>{viewer}/{course}/…</c>) and the same page on the central course record into the SAME node —
/// paths are stored in their CENTRAL form (<see cref="EducationLayoutAreas.ToSourcePath"/>), so
/// progress does not fork when a learner installs mid-course.</para>
///
/// <para><b>The content is plain camelCase JSON</b> (<see cref="JsonObject"/>), not a registered
/// content type: the record round-trips through any hub without polymorphic type registration, and
/// every reader goes through the shape-tolerant <see cref="Read"/> (typed / JsonElement /
/// JsonObject — the three shapes node content arrives in).</para>
///
/// <para><b>Writes are idempotent and render-safe.</b> <see cref="MergeVisit"/> returns null when
/// the record already says everything this visit would say — so re-renders write nothing, and the
/// menu re-render a write triggers converges instead of looping. The write itself is
/// fire-and-forget off the render path, bounded, and runs under the SYSTEM identity
/// (<c>AccessService.ImpersonateAsSystem</c>) because ambient identity on deferred reactive writes
/// is a race (the billing incident's shape); the record lands in the viewer's own partition either
/// way.</para>
/// </summary>
public static class CourseProgress
{
    /// <summary>The satellite namespace under the viewer's home that holds the records.</summary>
    public const string ProgressNamespace = "_Progress";

    /// <summary>The node type of a progress record.</summary>
    public const string NodeType = "CourseProgress";

    /// <summary>The record path for a viewer + central course: <c>{viewer}/_Progress/{course}</c>.</summary>
    /// <param name="viewer">The viewer's home partition.</param>
    /// <param name="centralCourse">The course's CENTRAL root (a top-level partition id).</param>
    public static string RecordPath(string viewer, string centralCourse)
        => $"{viewer}/{ProgressNamespace}/{centralCourse}";

    /// <summary>
    /// Merges one visit into a record's content: stamps <c>lastPath</c>/<c>lastVisitedAt</c> and
    /// first-visit timestamps for the lesson being read. Returns <c>null</c> when the record
    /// already says everything this visit would say — the caller then writes NOTHING, which is
    /// what makes render-driven capture safe. Pure.
    /// </summary>
    /// <param name="existing">The record's current content in whatever shape it arrived, or null.</param>
    /// <param name="centralCourse">The course's central root.</param>
    /// <param name="centralPage">The page being read, in central form.</param>
    /// <param name="centralLesson">The index entry containing the page (central form), or null when
    /// the page is above/outside every lesson (the course home).</param>
    /// <param name="now">The visit instant (UTC).</param>
    public static JsonObject? MergeVisit(
        object? existing, string centralCourse, string centralPage, string? centralLesson,
        DateTimeOffset now)
    {
        var record = Read(existing);
        var visited = record.TryGetPropertyValue("visited", out var v) && v is JsonObject vo
            ? vo
            : [];

        var lessonAlreadyVisited = centralLesson is null || visited.ContainsKey(centralLesson);
        if (lessonAlreadyVisited
            && record.TryGetPropertyValue("lastPath", out var last)
            && string.Equals(last?.GetValue<string>(), centralPage, StringComparison.Ordinal))
            return null;                                    // nothing new — no write

        var mergedVisited = new JsonObject();
        foreach (var (key, value) in visited)
            mergedVisited[key] = value?.DeepClone();
        if (centralLesson is not null && !mergedVisited.ContainsKey(centralLesson))
            mergedVisited[centralLesson] = now.ToString("O");

        return new JsonObject
        {
            ["coursePath"] = centralCourse,
            ["lastPath"] = centralPage,
            ["lastVisitedAt"] = now.ToString("O"),
            ["visited"] = mergedVisited,
        };
    }

    /// <summary>
    /// The lesson paths a record marks visited, in CENTRAL form. Empty for a missing/foreign
    /// record. Pure and shape-tolerant — this is what decorates the course index. </summary>
    /// <param name="content">The record's content in whatever shape it arrived, or null.</param>
    public static IReadOnlySet<string> VisitedLessons(object? content)
    {
        var record = Read(content);
        return record.TryGetPropertyValue("visited", out var v) && v is JsonObject visited
            ? visited.Select(p => p.Key).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    // Node content arrives typed, as a JsonElement, or as a JsonObject depending on which hub
    // serialized it last — read it in WHATEVER shape it arrives (the silent-failure rule).
    private static JsonObject Read(object? content) => content switch
    {
        JsonObject o => o,
        JsonElement { ValueKind: JsonValueKind.Object } e =>
            JsonNode.Parse(e.GetRawText()) as JsonObject ?? [],
        string s when s.TrimStart().StartsWith('{') =>
            TryParse(s),
        _ => [],
    };

    private static JsonObject TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Records one page visit — fire-and-forget off the render path: read the record (a query, so
    /// absence is an empty result, never an error), merge, and write only when the merge says
    /// something changed. Bounded end to end; a slow or failing write is logged and dropped —
    /// progress capture is a nicety, the page render is not allowed to feel it.
    /// </summary>
    /// <param name="hub">The page's hub (any hub — services are mesh-scoped).</param>
    /// <param name="viewer">The viewer's home partition; null/empty records nothing.</param>
    /// <param name="root">The indexed course root (central or the viewer's copy).</param>
    /// <param name="currentPath">The page being read.</param>
    /// <param name="navigation">The index shown for the page (its entries locate the lesson).</param>
    internal static void RecordVisit(
        IMessageHub hub, string? viewer, string root, string currentPath, NodeNavigation navigation)
    {
        if (string.IsNullOrEmpty(viewer))
            return;

        var centralCourse = EducationLayoutAreas.ToSourcePath(root, viewer);
        var centralPage = EducationLayoutAreas.ToSourcePath(currentPath, viewer);
        // The lesson the page belongs to: the top-level index entry that is the page or contains it.
        var centralLesson = navigation.Entries
            .Select(e => EducationLayoutAreas.ToSourcePath(e.Path, viewer))
            .FirstOrDefault(p => string.Equals(p, centralPage, StringComparison.Ordinal)
                                 || centralPage.StartsWith(p + "/", StringComparison.Ordinal));

        var recordPath = RecordPath(viewer, centralCourse);
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Education");
        var now = DateTimeOffset.UtcNow;

        meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{recordPath}"))
            .Take(1)
            .SelectMany(change =>
            {
                var existing = change.Items?.FirstOrDefault();
                var merged = MergeVisit(existing?.Content, centralCourse, centralPage, centralLesson, now);
                if (merged is null)
                    return Observable.Empty<MeshNode>();     // idempotent: nothing new to say

                var node = existing is null
                    ? new MeshNode(centralCourse, $"{viewer}/{ProgressNamespace}")
                    {
                        Name = $"Progress — {centralCourse}",
                        NodeType = NodeType,
                        MainNode = viewer,                    // satellite of the viewer's home
                        Content = merged,
                    }
                    : existing with { Content = merged };

                // SYSTEM identity: ambient identity on a deferred reactive write is a race — and
                // the target is the viewer's own partition, so this grants nothing.
                return Observable.Using(
                    () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                    _ => meshService.CreateOrUpdateNode(node));
            })
            .Timeout(TimeSpan.FromSeconds(30))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "CourseProgress: could not record the visit to {Page} for {Viewer}",
                    currentPath, viewer));
    }
}
