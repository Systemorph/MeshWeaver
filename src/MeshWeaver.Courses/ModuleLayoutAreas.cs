using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Courses;

/// <summary>
/// Layout views for Module nodes. The default <see cref="ContentArea"/> renders
/// the whole module page: the summary markdown, the ordered
/// <c>Theory/*</c> children embedded via <see cref="LayoutAreaControl"/>
/// (default area — live: a tutor edit to a theory node re-renders its embed),
/// the <c>Example/*</c> children, a tab strip of the module's exercises (each
/// tab the exercise's <see cref="ExerciseLayoutAreas.WorkspaceArea"/>), and
/// Prev/Next navigation across the course's ordered sibling modules.
/// </summary>
public static class ModuleLayoutAreas
{
    /// <summary>Area name of the module page (the default area).</summary>
    public const string ContentArea = "Content";

    /// <summary>Area id of the module summary markdown.</summary>
    public const string SummaryArea = "Summary";
    /// <summary>Area id of the theory-blocks section.</summary>
    public const string TheorySection = "Theory";
    /// <summary>Area id of the worked-examples section.</summary>
    public const string ExampleSection = "Examples";
    /// <summary>Area id of the exercise tab strip.</summary>
    public const string ExerciseTabsArea = "Exercises";
    /// <summary>Area id of the Prev/Next sibling navigation row.</summary>
    public const string ModuleNavArea = "ModuleNav";

    /// <summary>
    /// Registers the Module views on the hub configuration: the framework
    /// defaults plus <see cref="Content"/> as the default area.
    /// </summary>
    public static MessageHubConfiguration AddModuleViews(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithDefaultArea(ContentArea)
                .WithView(ContentArea, Content));

    /// <summary>
    /// The module page, reactive off the module's own node stream and the
    /// synced children queries (theory, examples, exercises, sibling modules) —
    /// pure observable composition, re-renders when any child appears,
    /// disappears or reorders.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
    {
        var hub = host.Hub;
        var modulePath = hub.Address.ToString();
        var options = hub.JsonSerializerOptions;
        var nodeStream = host.Workspace.GetMeshNodeStream();

        // The course path (parent namespace) drives the sibling-module query
        // for Prev/Next navigation.
        var lastSlash = modulePath.LastIndexOf('/');
        var coursePath = lastSlash > 0 ? modulePath[..lastSlash] : modulePath;

        var theoryStream = hub.GetQuery(
            $"module-theory:{modulePath}",
            $"path:{modulePath}/{ModuleNodeType.TheorySubNamespace} scope:children");
        var exampleStream = hub.GetQuery(
            $"module-examples:{modulePath}",
            $"path:{modulePath}/{ModuleNodeType.ExampleSubNamespace} scope:children");
        var exerciseStream = hub.GetQuery(
            $"module-exercises:{modulePath}",
            $"path:{modulePath}/{ExerciseNodeType.ExerciseSubNamespace} scope:children nodeType:{ExerciseNodeType.NodeType}");
        var siblingStream = hub.GetQuery(
            $"course-modules:{coursePath}",
            $"path:{coursePath} scope:children nodeType:{ModuleNodeType.NodeType}");

        return nodeStream.CombineLatest(theoryStream, exampleStream, exerciseStream, siblingStream,
            (node, theory, examples, exercises, siblings) =>
                (UiControl?)BuildContent(node, modulePath, options, theory, examples, exercises, siblings));
    }

    private static UiControl BuildContent(
        MeshNode? node, string modulePath, JsonSerializerOptions options,
        IEnumerable<MeshNode> theory, IEnumerable<MeshNode> examples,
        IEnumerable<MeshNode> exercises, IEnumerable<MeshNode> siblings)
    {
        var module = node.ContentAs<ModuleConfiguration>(options);
        var stack = Controls.Stack.WithWidth("100%")
            .WithView(Controls.H1(node?.Name ?? node?.Id ?? "Module").WithStyle("margin: 0 0 8px 0;"));

        if (!string.IsNullOrWhiteSpace(module?.Summary))
            stack = stack.WithView(Controls.Markdown(module!.Summary!), SummaryArea);

        // Theory blocks — ordered, each embedded via its default area so the
        // module page IS the live rendering of each child node.
        var orderedTheory = Ordered(theory).ToList();
        if (orderedTheory.Count > 0)
        {
            var section = Controls.Stack.WithWidth("100%");
            foreach (var block in orderedTheory)
                section = section.WithView(
                    new LayoutAreaControl(new Address(block.Path), new LayoutAreaReference("")),
                    Embed(block));
            stack = stack.WithView(section, TheorySection);
        }

        // Worked examples — same treatment (typically Code nodes rendering
        // their notebook cell).
        var orderedExamples = Ordered(examples).ToList();
        if (orderedExamples.Count > 0)
        {
            var section = Controls.Stack.WithWidth("100%")
                .WithView(Controls.H2("Examples").WithStyle("margin: 16px 0 8px 0;"));
            foreach (var example in orderedExamples)
                section = section.WithView(
                    new LayoutAreaControl(new Address(example.Path), new LayoutAreaReference("")),
                    Embed(example));
            stack = stack.WithView(section, ExampleSection);
        }

        // Exercises — a tab per exercise, each tab embedding the exercise's
        // workspace area (statement + fork + validate).
        var orderedExercises = Ordered(exercises).ToList();
        if (orderedExercises.Count > 0)
        {
            var tabs = Controls.Tabs.WithSkin(s => s.WithWidth("100%"));
            foreach (var exercise in orderedExercises)
                tabs = tabs.WithView(
                    new LayoutAreaControl(
                        new Address(exercise.Path),
                        new LayoutAreaReference(ExerciseLayoutAreas.WorkspaceArea)),
                    s => s.WithLabel(exercise.Name ?? exercise.Id));
            stack = stack
                .WithView(Controls.H2("Exercises").WithStyle("margin: 16px 0 8px 0;"))
                .WithView(tabs, ExerciseTabsArea);
        }

        var nav = BuildModuleNav(modulePath, siblings);
        if (nav is not null)
            stack = stack.WithView(nav, ModuleNavArea);

        return stack;
    }

    /// <summary>
    /// Prev/Next navigation across the course's ordered modules
    /// (<c>WithNavigateToHref</c> — the <c>CodeLayoutAreas.Overview</c> nav
    /// pattern). Null when the module has no siblings.
    /// </summary>
    private static UiControl? BuildModuleNav(string modulePath, IEnumerable<MeshNode> siblings)
    {
        var ordered = Ordered(siblings).ToList();
        var index = ordered.FindIndex(n => n.Path == modulePath);
        if (index < 0 || ordered.Count < 2)
            return null;

        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; justify-content: space-between; width: 100%; margin-top: 24px;");
        row = row.WithView(index > 0
            ? Controls.Button($"← {ordered[index - 1].Name ?? ordered[index - 1].Id}")
                .WithNavigateToHref($"/{ordered[index - 1].Path}")
            : Controls.Spacer);
        row = row.WithView(index < ordered.Count - 1
            ? Controls.Button($"{ordered[index + 1].Name ?? ordered[index + 1].Id} →")
                .WithAppearance(Appearance.Accent)
                .WithNavigateToHref($"/{ordered[index + 1].Path}")
            : Controls.Spacer);
        return row;
    }

    /// <summary>
    /// Canonical child ordering: <see cref="MeshNode.Order"/> ascending (null
    /// last), then id — stable across re-renders.
    /// </summary>
    internal static IEnumerable<MeshNode> Ordered(IEnumerable<MeshNode> nodes)
        => nodes
            .OrderBy(n => n.Order ?? int.MaxValue)
            .ThenBy(n => n.Id, StringComparer.Ordinal);

    /// <summary>Stable per-child embed area id (the child's node id).</summary>
    private static string Embed(MeshNode node) => $"Embed-{node.Id}";
}
