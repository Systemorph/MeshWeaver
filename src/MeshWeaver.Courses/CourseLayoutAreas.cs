using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Courses;

/// <summary>
/// One row of the course progress table: a module, its exercise count and how
/// many the viewer has passed.
/// </summary>
/// <param name="Module">The module's display name.</param>
/// <param name="Exercises">Number of exercises in the module.</param>
/// <param name="Passed">Number of those the viewer has passed.</param>
public record CourseProgressRow(string Module, int Exercises, int Passed);

/// <summary>
/// Layout views for Course nodes. The default <see cref="ContentArea"/> renders
/// the course overview: the description markdown, a card per module (ordered,
/// navigating into the module page), and the VIEWER's progress — a
/// module × exercises × passed table plus an overall progress bar, computed by
/// combining the course's exercise subtree with the viewer's attempt subtree.
/// </summary>
public static class CourseLayoutAreas
{
    /// <summary>Area name of the course overview (the default area).</summary>
    public const string ContentArea = "Content";

    /// <summary>Area id of the course description markdown.</summary>
    public const string DescriptionArea = "Description";
    /// <summary>Area id of the module-cards section.</summary>
    public const string ModulesArea = "Modules";
    /// <summary>Area id of the progress section (grid + bar).</summary>
    public const string ProgressArea = "Progress";
    /// <summary>Area id of the overall progress bar inside the progress section.</summary>
    public const string ProgressBarArea = "ProgressBar";
    /// <summary>Area id of the per-module progress grid inside the progress section.</summary>
    public const string ProgressGridArea = "ProgressGrid";

    /// <summary>
    /// Registers the Course views on the hub configuration: the framework
    /// defaults plus <see cref="Content"/> as the default area.
    /// </summary>
    public static MessageHubConfiguration AddCourseViews(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithDefaultArea(ContentArea)
                .WithView(ContentArea, Content));

    /// <summary>
    /// The course overview, reactive off the course's own node stream, the
    /// module children, the exercise subtree and — when a real user is signed
    /// in — the viewer's attempt subtree under
    /// <c>{home}/Courses/{Escape(coursePath)}</c>.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
    {
        var hub = host.Hub;
        var coursePath = hub.Address.ToString();
        var options = hub.JsonSerializerOptions;
        var nodeStream = host.Workspace.GetMeshNodeStream();

        var moduleStream = hub.GetQuery(
            $"course-modules:{coursePath}",
            $"path:{coursePath} scope:children nodeType:{ModuleNodeType.NodeType}");
        var exerciseStream = hub.GetQuery(
            $"course-exercises:{coursePath}",
            $"path:{coursePath} scope:descendants nodeType:{ExerciseNodeType.NodeType}");

        // The viewer's attempts for THIS course live under the canonical
        // escaped root in their home partition (see AttemptPathFor). Anonymous
        // viewers get an empty attempt set (no progress section).
        var viewerHome = ExerciseLayoutAreas.ResolveViewerHome(
            hub.ServiceProvider.GetService<AccessService>());
        var attemptStream = viewerHome is null
            ? Observable.Return(Enumerable.Empty<MeshNode>())
            : hub.GetQuery(
                $"course-attempts:{viewerHome}:{coursePath}",
                $"path:{viewerHome}/{ExerciseAttemptNodeType.CoursesSubNamespace}/{PathEscaping.Escape(coursePath)} "
                + $"scope:descendants nodeType:{ExerciseAttemptNodeType.NodeType}");

        return nodeStream.CombineLatest(moduleStream, exerciseStream, attemptStream,
            (node, modules, exercises, attempts) =>
                (UiControl?)BuildContent(node, options, modules, exercises, attempts, viewerHome is not null));
    }

    private static UiControl BuildContent(
        MeshNode? node, JsonSerializerOptions options,
        IEnumerable<MeshNode> modules, IEnumerable<MeshNode> exercises,
        IEnumerable<MeshNode> attempts, bool signedIn)
    {
        var course = node.ContentAs<CourseConfiguration>(options);
        var stack = Controls.Stack.WithWidth("100%")
            .WithView(Controls.H1(node?.Name ?? node?.Id ?? "Course").WithStyle("margin: 0 0 8px 0;"));

        if (!string.IsNullOrWhiteSpace(course?.Description))
            stack = stack.WithView(Controls.Markdown(course!.Description!), DescriptionArea);

        var orderedModules = ModuleLayoutAreas.Ordered(modules).ToList();
        if (orderedModules.Count > 0)
            stack = stack.WithView(BuildModuleCards(orderedModules, options), ModulesArea);

        // Progress: exercise subtree × the viewer's passed attempts. The
        // attempt records the exercise it forks on
        // ExerciseAttemptStatus.ExercisePath — the join key.
        var exerciseList = exercises.ToList();
        if (signedIn && exerciseList.Count > 0)
            stack = stack.WithView(
                BuildProgress(orderedModules, exerciseList, attempts, options), ProgressArea);

        return stack;
    }

    /// <summary>
    /// A card per module: name, summary, and an Open button navigating to the
    /// module page.
    /// </summary>
    private static UiControl BuildModuleCards(
        IReadOnlyList<MeshNode> modules, JsonSerializerOptions options)
    {
        var cards = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; flex-wrap: wrap; gap: 16px; margin: 16px 0;");
        foreach (var module in modules)
        {
            var summary = module.ContentAs<ModuleConfiguration>(options)?.Summary;
            var card = Controls.Stack
                .WithStyle("border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; " +
                           "padding: 16px; width: 280px; background: var(--neutral-layer-1);")
                .WithView(Controls.H3(module.Name ?? module.Id).WithStyle("margin: 0 0 8px 0;"));
            if (!string.IsNullOrWhiteSpace(summary))
                card = card.WithView(Controls.Markdown(summary!)
                    .WithStyle("color: var(--neutral-foreground-hint); font-size: 0.9rem;"));
            card = card.WithView(Controls.Button("Open module")
                .WithAppearance(Appearance.Accent)
                .WithNavigateToHref($"/{module.Path}"));
            cards = cards.WithView(card, $"Module-{module.Id}");
        }
        return cards;
    }

    /// <summary>
    /// The viewer's progress: an overall <c>Controls.Progress</c> bar plus a
    /// module | exercises | passed <c>DataGrid</c> over
    /// <see cref="CourseProgressRow"/> rows.
    /// </summary>
    private static UiControl BuildProgress(
        IReadOnlyList<MeshNode> modules, IReadOnlyList<MeshNode> exercises,
        IEnumerable<MeshNode> attempts, JsonSerializerOptions options)
    {
        // Exercise paths the viewer passed (via the attempt's recorded
        // ExercisePath — robust against where the attempt physically lives).
        var passedExercises = attempts
            .Select(a => a.ContentAs<ExerciseAttemptStatus>(options))
            .Where(s => s?.Status == AttemptStatus.Passed)
            .Select(s => s!.ExercisePath)
            .ToHashSet(StringComparer.Ordinal);

        var rows = modules
            .Select(module =>
            {
                var modulePrefix = module.Path + "/";
                var moduleExercises = exercises
                    .Where(e => e.Path.StartsWith(modulePrefix, StringComparison.Ordinal))
                    .ToList();
                return new CourseProgressRow(
                    module.Name ?? module.Id,
                    moduleExercises.Count,
                    moduleExercises.Count(e => passedExercises.Contains(e.Path)));
            })
            .Where(row => row.Exercises > 0)
            .ToArray();

        var total = rows.Sum(r => r.Exercises);
        var passed = rows.Sum(r => r.Passed);
        var percent = total == 0 ? 0 : (int)Math.Round(100.0 * passed / total);

        var section = Controls.Stack.WithWidth("100%")
            .WithView(Controls.H2("Your progress").WithStyle("margin: 16px 0 8px 0;"))
            .WithView(Controls.Progress($"{passed} of {total} exercises passed", percent),
                ProgressBarArea);

        if (rows.Length > 0)
            section = section.WithView(Controls.DataGrid(rows)
                    .WithColumn(new PropertyColumnControl<string>
                        { Property = nameof(CourseProgressRow.Module).ToCamelCase() }.WithTitle("Module"))
                    .WithColumn(new PropertyColumnControl<int>
                        { Property = nameof(CourseProgressRow.Exercises).ToCamelCase() }.WithTitle("Exercises"))
                    .WithColumn(new PropertyColumnControl<int>
                        { Property = nameof(CourseProgressRow.Passed).ToCamelCase() }.WithTitle("Passed")),
                ProgressGridArea);

        return section;
    }
}
