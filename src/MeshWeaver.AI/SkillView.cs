using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.AI;

/// <summary>
/// Read-only overview (landing page) for <see cref="SkillNodeType"/> nodes. Renders a clean,
/// markdown-formatted page: the skill's name as a title, its slash word + help text, the
/// <c>SKILL.md</c> instruction body rendered as markdown, and a compact metadata table
/// (kind, harness, behaviour action, advertised/sub-thread).
///
/// <para>Composed entirely from framework controls — <see cref="Controls.Stack"/>,
/// <see cref="Controls.Title"/> and <see cref="Controls.Markdown"/> — never hand-built HTML
/// strings. Binds reactively to the OWN node's stream, exactly like
/// <c>DocumentLayoutAreas.Overview</c> / <c>MarkdownOverviewLayoutArea.Overview</c>.</para>
/// </summary>
public static class SkillView
{
    /// <summary>
    /// Overrides the default <see cref="MeshNodeLayoutAreas.OverviewArea"/> with the Skill overview
    /// while keeping every other default area/menu. Mirrors <c>DocumentNodeType</c>'s registration:
    /// <c>AddDefaultLayoutAreas()</c> (idempotent) then a single <c>WithView</c> override.
    /// </summary>
    public static MessageHubConfiguration AddSkillView(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.OverviewArea, Overview));

    /// <summary>
    /// Reactive overview — reads the OWN node off the per-node hub's stream
    /// (<c>host.Workspace.GetMeshNodeStream()</c>) and re-renders on every change. No await, no
    /// <c>Take(1)</c> on the live stream.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .Select(node => (UiControl?)BuildOverview(host, node));

    private const string ContainerStyle = "max-width: 1080px; margin: 0 auto; padding: 24px; gap: 16px;";

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle(ContainerStyle);

        // Title — the skill name (falls back to a humanised id).
        var displayName = node?.Name ?? node?.Id?.Wordify() ?? "Skill";
        container = container.WithView(Controls.Title(displayName, 1));

        // Subtitle — the slash word (`/id`) and the node's help text (description).
        var subtitle = BuildSubtitleMarkdown(node);
        if (!string.IsNullOrWhiteSpace(subtitle))
            container = container.WithView(Controls.Markdown(subtitle));

        var def = node.ContentAs<SkillDefinition>(host.Hub.JsonSerializerOptions);
        if (def is null)
            return container;

        // The instruction body (SKILL.md) is the skill's main content — rendered as markdown.
        if (!string.IsNullOrWhiteSpace(def.Instructions))
            container = container.WithView(Controls.Markdown(def.Instructions!));
        else if (def.Action is null)
            container = container.WithView(Controls.Markdown("_This skill has no instructions yet._"));

        // Compact metadata table at the foot.
        var metadata = BuildMetadataMarkdown(def);
        if (!string.IsNullOrWhiteSpace(metadata))
            container = container.WithView(Controls.Markdown(metadata));

        return container;
    }

    private static string BuildSubtitleMarkdown(MeshNode? node)
    {
        var slash = string.IsNullOrWhiteSpace(node?.Id) ? null : $"`/{node!.Id}`";
        var description = string.IsNullOrWhiteSpace(node?.Description) ? null : node!.Description;
        return (slash, description) switch
        {
            ({ } s, { } d) => $"{s}\n\n{d}",
            ({ } s, null) => s,
            (null, { } d) => d!,
            _ => string.Empty
        };
    }

    private static string BuildMetadataMarkdown(SkillDefinition def)
    {
        var rows = MetadataRows(def).ToImmutableArray();
        if (rows.Length == 0)
            return string.Empty;

        var header = "---\n\n| Property | Value |\n|---|---|\n";
        var body = string.Join("\n", rows.Select(r => $"| **{r.Label}** | {r.Value} |"));
        return header + body + "\n";
    }

    private static IEnumerable<(string Label, string Value)> MetadataRows(SkillDefinition def)
    {
        var kinds = new[]
        {
            !string.IsNullOrWhiteSpace(def.Instructions) ? "Instruction" : null,
            def.Action is not null ? "Behaviour" : null,
        }.Where(k => k is not null);
        var kindText = string.Join(" · ", kinds);
        if (!string.IsNullOrEmpty(kindText))
            yield return ("Kind", kindText);

        if (!string.IsNullOrWhiteSpace(def.Harness))
            yield return ("Harness", Escape(def.Harness!));

        if (def.Action is { } action)
        {
            yield return ("Action", DescribeAction(action));
            if (action.Kind == SkillActionKind.Pick && !string.IsNullOrWhiteSpace(action.Field))
                yield return ("Sets composer field", $"`{action.Field}`");
        }

        yield return ("Advertised up-front", def.AutoMount ? "Yes" : "No");
        if (def.LaunchesSubThread)
            yield return ("Runs in", "a sub-thread");
    }

    private static string DescribeAction(SkillAction action) => action.Kind switch
    {
        SkillActionKind.Pick => string.IsNullOrWhiteSpace(action.Title)
            ? "Opens a picker"
            : $"Opens a picker — {Escape(action.Title!)}",
        SkillActionKind.OpenContent => string.IsNullOrWhiteSpace(action.ContentPath)
            ? "Opens content"
            : $"Opens `{action.ContentPath}`",
        SkillActionKind.Navigate => string.IsNullOrWhiteSpace(action.ContentPath)
            ? "Navigates the UI to a path (pane-aware, resilient)"
            : $"Navigates to `{action.ContentPath}` (pane-aware, resilient)",
        SkillActionKind.Connect => string.IsNullOrWhiteSpace(action.Provider)
            ? "Connects a provider"
            : $"Connects {Escape(action.Provider!)}",
        SkillActionKind.Disconnect => string.IsNullOrWhiteSpace(action.Provider)
            ? "Disconnects a provider"
            : $"Disconnects {Escape(action.Provider!)}",
        SkillActionKind.NewThread => "Starts a new, empty conversation",
        _ => action.Kind.ToString(),
    };

    // Escape markdown table-breaking pipes so a free-text value can't split a cell.
    private static string Escape(string value) => value.Replace("|", "\\|");
}
