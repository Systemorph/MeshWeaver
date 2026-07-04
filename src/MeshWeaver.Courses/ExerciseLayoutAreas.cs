using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Courses;

/// <summary>
/// Layout views for Exercise nodes. The default <see cref="WorkspaceArea"/> is
/// the trainee's whole exercise experience, reactive off the exercise node
/// stream AND the viewer's attempt:
/// <list type="bullet">
///   <item>No attempt yet → the statement plus a Start button that forks the
///   starter via <see cref="ExerciseAttemptNodeType.StartAttempt"/>; the view
///   re-renders into the workspace automatically when the attempt appears.</item>
///   <item>Attempt exists → a Splitter: left the statement + the Monaco edit
///   area of the attempt's working-copy Code node; right the working copy's
///   notebook cell (Run/Cancel live in the cell — never duplicated here), a
///   Validate button that flips the
///   <see cref="ExerciseAttemptStatus.ValidationRequestedAt"/> trigger via
///   <c>GetMeshNodeStream(attemptPath).Update(...)</c>, a live status badge,
///   the last validation activity's progress embed, and the solution-reveal
///   toggle with a conditional embed of the reference solution.</item>
/// </list>
/// </summary>
public static class ExerciseLayoutAreas
{
    /// <summary>Area name of the exercise workspace (the default area).</summary>
    public const string WorkspaceArea = "Workspace";

    /// <summary>Area id of the statement markdown inside the workspace.</summary>
    public const string StatementArea = "Statement";
    /// <summary>Area id of the Start button (no-attempt state).</summary>
    public const string StartButtonArea = "StartExercise";
    /// <summary>Area id of the attempt working copy's Monaco edit embed (left pane).</summary>
    public const string EditorArea = "AttemptEditor";
    /// <summary>Area id of the attempt working copy's notebook-cell embed (right pane).</summary>
    public const string AttemptCellArea = "AttemptCell";
    /// <summary>Area id of the Validate button.</summary>
    public const string ValidateButtonArea = "Validate";
    /// <summary>Area id of the live attempt-status badge.</summary>
    public const string StatusBadgeArea = "AttemptStatus";
    /// <summary>Area id of the last validation activity's progress embed.</summary>
    public const string ValidationOutputArea = "ValidationOutput";
    /// <summary>Area id of the solution-reveal toggle button.</summary>
    public const string RevealSolutionButtonArea = "RevealSolution";
    /// <summary>Area id of the conditional reference-solution embed.</summary>
    public const string SolutionArea = "Solution";

    private const string LoggerCategory = "MeshWeaver.Courses.ExerciseLayoutAreas";

    /// <summary>
    /// Registers the Exercise views on the hub configuration:
    /// the framework defaults plus <see cref="Workspace"/> as the default area
    /// (clone of <c>AddMarkdownViews</c>).
    /// </summary>
    public static MessageHubConfiguration AddExerciseViews(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithDefaultArea(WorkspaceArea)
                .WithView(WorkspaceArea, Workspace));

    /// <summary>
    /// The exercise workspace, reactive off the exercise's own node stream and
    /// the VIEWER's attempt. Attempt existence is observed through the synced
    /// query surface (<c>hub.GetQuery</c> — empty-on-absent), never a point
    /// <c>GetMeshNodeStream</c> on a maybe-missing path (which would trip the
    /// stream cache's storm breaker); once the attempt exists its LIVE state
    /// comes off the authoritative node stream.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Workspace(LayoutAreaHost host, RenderingContext _)
    {
        var hub = host.Hub;
        var exercisePath = hub.Address.ToString();
        var options = hub.JsonSerializerOptions;
        var nodeStream = host.Workspace.GetMeshNodeStream();

        var viewerHome = ResolveViewerHome(hub.ServiceProvider.GetService<AccessService>());
        string? attemptPath = null;
        if (viewerHome is not null)
        {
            try
            {
                attemptPath = ExerciseAttemptNodeType.AttemptPathFor(viewerHome, exercisePath);
            }
            catch (ArgumentException)
            {
                // The exercise doesn't live under a {module}/Exercise/{id} shape
                // (e.g. previewed standalone) — no attempt mapping exists; render
                // the statement without the fork workflow.
            }
        }

        if (attemptPath is null)
            return nodeStream.Select(node => (UiControl?)BuildStatementOnly(node, options, viewerHome));

        var resolvedAttemptPath = attemptPath;

        // Empty-on-absent existence + live state: the exact-path synced query
        // flips existence; the node stream carries the authoritative live state
        // once the node is there.
        var attemptStream = hub
            .GetQuery($"exercise-attempt:{resolvedAttemptPath}", $"path:{resolvedAttemptPath}")
            .Select(nodes => nodes.Any(n => n.Path == resolvedAttemptPath))
            .DistinctUntilChanged()
            .Select(exists => exists
                ? host.Workspace.GetMeshNodeStream(resolvedAttemptPath)
                    .Select(n => n.ContentAs<ExerciseAttemptStatus>(options))
                    .Where(s => s is not null)
                : Observable.Return<ExerciseAttemptStatus?>(null))
            .Switch();

        return nodeStream.CombineLatest(attemptStream,
            (node, attempt) => (UiControl?)(attempt is null
                ? BuildStartView(node, exercisePath, options)
                : BuildWorkspaceView(node, exercisePath, resolvedAttemptPath, attempt, options)));
    }

    /// <summary>
    /// Statement-only rendering for viewers without an attempt mapping
    /// (anonymous, or an exercise outside the canonical path shape).
    /// </summary>
    private static UiControl BuildStatementOnly(
        MeshNode? node, JsonSerializerOptions options, string? viewerHome)
    {
        var exercise = node.ContentAs<ExerciseConfiguration>(options);
        var stack = Controls.Stack.WithWidth("100%")
            .WithView(Controls.H1(node?.Name ?? node?.Id ?? "Exercise").WithStyle("margin: 0 0 16px 0;"))
            .WithView(Controls.Markdown(exercise?.Statement ?? "*No statement yet.*"), StatementArea);
        if (viewerHome is null)
            stack = stack.WithView(Controls.Body(
                    "Sign in to start this exercise and get your own working copy.")
                .WithStyle("display: block; color: var(--neutral-foreground-hint); font-style: italic;"));
        return stack;
    }

    /// <summary>
    /// The no-attempt state: statement + Start button. The click forks the
    /// starter via <see cref="ExerciseAttemptNodeType.StartAttempt"/> (cold —
    /// subscribed with an error sink surfacing failures as a dialog); the
    /// synced attempt query then re-renders the area into the workspace.
    /// </summary>
    private static UiControl BuildStartView(
        MeshNode? node, string exercisePath, JsonSerializerOptions options)
    {
        var exercise = node.ContentAs<ExerciseConfiguration>(options);
        return Controls.Stack.WithWidth("100%")
            .WithView(Controls.H1(node?.Name ?? node?.Id ?? "Exercise").WithStyle("margin: 0 0 16px 0;"))
            .WithView(Controls.Markdown(exercise?.Statement ?? "*No statement yet.*"), StatementArea)
            .WithView(Controls.Button("Start exercise")
                    .WithIconStart(FluentIcons.Play())
                    .WithAppearance(Appearance.Accent)
                    .WithClickAction(ctx =>
                    {
                        var logger = ctx.Host.Hub.ServiceProvider
                            .GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
                        ExerciseAttemptNodeType.StartAttempt(ctx.Host.Hub, exercisePath)
                            .Subscribe(
                                attemptPath => logger.LogDebug(
                                    "Started attempt {AttemptPath} for {ExercisePath}",
                                    attemptPath, exercisePath),
                                ex =>
                                {
                                    logger.LogWarning(ex,
                                        "StartAttempt failed for {ExercisePath}", exercisePath);
                                    ctx.Host.UpdateArea(DialogControl.DialogArea, Controls.Dialog(
                                            Controls.Markdown($"**Could not start the exercise:**\n\n{ex.Message}"),
                                            "Start failed")
                                        .WithSize("M").WithClosable(true));
                                });
                        return Task.CompletedTask;
                    }),
                StartButtonArea);
    }

    /// <summary>
    /// The attempt workspace: Splitter with the statement + Monaco edit embed
    /// of the working copy on the left; the working copy's notebook cell, the
    /// validation controls and the solution reveal on the right.
    /// </summary>
    private static UiControl BuildWorkspaceView(
        MeshNode? node, string exercisePath, string attemptPath,
        ExerciseAttemptStatus attempt, JsonSerializerOptions options)
    {
        var exercise = node.ContentAs<ExerciseConfiguration>(options);
        var attemptCodePath =
            $"{attemptPath}/{ExerciseNodeType.SourceSubNamespace}/{ExerciseAttemptNodeType.AttemptCodeNodeId}";
        var solutionPath =
            $"{exercisePath}/{ExerciseNodeType.SolutionSubNamespace}/{ExerciseNodeType.SolutionNodeId}";

        // Left pane: what the trainee is asked to do + their editable working copy.
        var left = Controls.Stack.WithWidth("100%")
            .WithView(Controls.H1(node?.Name ?? node?.Id ?? "Exercise").WithStyle("margin: 0 0 16px 0;"))
            .WithView(Controls.Markdown(exercise?.Statement ?? "*No statement yet.*"), StatementArea)
            .WithView(new LayoutAreaControl(
                    new Address(attemptCodePath),
                    new LayoutAreaReference(CodeLayoutAreas.EditArea)),
                EditorArea);

        // Right pane: the notebook cell (Run/Cancel live INSIDE the cell — one
        // source of truth, never duplicated here), validation controls, status.
        var right = Controls.Stack.WithWidth("100%")
            .WithView(BuildStatusBadge(attempt), StatusBadgeArea)
            .WithView(new LayoutAreaControl(
                    new Address(attemptCodePath),
                    new LayoutAreaReference("")),
                AttemptCellArea);

        if (!string.IsNullOrEmpty(attempt.LastValidationActivityPath))
        {
            // The validation run's live output — the same Progress area the Code
            // cell embeds for its own runs (streams Running → terminal).
            right = right.WithView(new LayoutAreaControl(
                        new Address(attempt.LastValidationActivityPath!),
                        new LayoutAreaReference(ActivityLayoutAreas.ProgressArea))
                    .WithStyle("border-left: 3px solid var(--accent-fill-rest); " +
                               "background: var(--neutral-layer-2); padding: 10px 12px; margin-top: 8px;"),
                ValidationOutputArea);
        }

        right = right.WithView(BuildValidationToolbar(attemptPath, attempt), ValidateButtonArea);

        right = right.WithView(BuildRevealSolutionButton(attemptPath, attempt), RevealSolutionButtonArea);
        if (attempt.RevealedSolution)
            right = right.WithView(new LayoutAreaControl(
                        new Address(solutionPath),
                        new LayoutAreaReference(""))
                    .WithStyle("border: 1px dashed var(--neutral-stroke-rest); border-radius: 6px; " +
                               "padding: 8px; margin-top: 8px;"),
                SolutionArea);

        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("100%"))
            .WithView(left, skin => skin.WithSize("45%").WithMin("300px").WithCollapsible(true))
            .WithView(right, skin => skin.WithSize("*"));
    }

    /// <summary>
    /// The live attempt-status badge: Validating… while an unclaimed trigger is
    /// pending, otherwise In progress / Passed / Failed off the attempt stream.
    /// </summary>
    private static UiControl BuildStatusBadge(ExerciseAttemptStatus attempt)
    {
        var validating = attempt.ValidationRequestedAt is { } requested
            && (attempt.LastValidationHandledAt is null || requested > attempt.LastValidationHandledAt.Value);
        var (text, color) = validating
            ? ("Validating…", "var(--accent-fill-rest)")
            : attempt.Status switch
            {
                AttemptStatus.Passed => ("Passed", "var(--success, #107C10)"),
                AttemptStatus.Failed => ("Failed", "var(--error, #D13438)"),
                _ => ("In progress", "var(--neutral-foreground-hint)")
            };
        return Controls.Body(text).WithStyle(
            $"display: inline-block; padding: 2px 12px; border-radius: 10px; font-size: 0.85rem; " +
            $"border: 1px solid {color}; color: {color};");
    }

    /// <summary>
    /// The Validate button: flips the
    /// <see cref="ExerciseAttemptStatus.ValidationRequestedAt"/> /
    /// <see cref="ExerciseAttemptStatus.ValidationRequestedBy"/> trigger pair on
    /// the attempt node via the canonical <c>GetMeshNodeStream(path).Update</c>
    /// — no request message. The per-attempt hub's validation watcher reacts.
    /// </summary>
    private static UiControl BuildValidationToolbar(string attemptPath, ExerciseAttemptStatus attempt)
        => Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; align-items: center; gap: 8px; margin-top: 8px;")
            .WithView(Controls.Button("Validate")
                .WithIconStart(FluentIcons.CheckmarkCircle())
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    var hub = ctx.Host.Hub;
                    var logger = hub.ServiceProvider
                        .GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
                    var accessService = hub.ServiceProvider.GetService<AccessService>();
                    var requestedBy = accessService?.Context?.ObjectId
                        ?? accessService?.CircuitContext?.ObjectId;
                    hub.GetMeshNodeStream(attemptPath)
                        .Update(curr => curr.Content is ExerciseAttemptStatus status
                            ? curr with
                            {
                                Content = status with
                                {
                                    ValidationRequestedAt = DateTimeOffset.UtcNow,
                                    ValidationRequestedBy = requestedBy
                                }
                            }
                            : curr)
                        .Subscribe(
                            _ => { },
                            ex => logger.LogWarning(ex,
                                "Validation request failed for {AttemptPath}", attemptPath));
                    return Task.CompletedTask;
                }));

    /// <summary>
    /// The solution-reveal toggle: flips
    /// <see cref="ExerciseAttemptStatus.RevealedSolution"/> on the attempt node
    /// (a UX gate persisted with the attempt, so it survives navigation).
    /// </summary>
    private static UiControl BuildRevealSolutionButton(string attemptPath, ExerciseAttemptStatus attempt)
        => Controls.Button(attempt.RevealedSolution ? "Hide solution" : "Reveal solution")
            .WithIconStart(FluentIcons.Lightbulb())
            .WithStyle("margin-top: 8px;")
            .WithClickAction(ctx =>
            {
                var hub = ctx.Host.Hub;
                var logger = hub.ServiceProvider
                    .GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
                hub.GetMeshNodeStream(attemptPath)
                    .Update(curr => curr.Content is ExerciseAttemptStatus status
                        ? curr with { Content = status with { RevealedSolution = !status.RevealedSolution } }
                        : curr)
                    .Subscribe(
                        _ => { },
                        ex => logger.LogWarning(ex,
                            "Solution-reveal toggle failed for {AttemptPath}", attemptPath));
                return Task.CompletedTask;
            });

    /// <summary>
    /// The signed-in viewer's home partition, resolved from the ambient
    /// <see cref="AccessService"/> (per-delivery context first, then the durable
    /// circuit context — the <c>ThreadNodeType.BuildCreate</c> pattern). System /
    /// anonymous / hub-shaped principals yield <c>null</c>.
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
