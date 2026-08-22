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
    {
        // 🌍 Resolve the viewer's language ONCE, on the render turn, and pass it down. Reading it
        // inside the Select would read an AsyncLocal that the node-stream emission has already left
        // behind, so a German viewer would get a German page frame around an English skill card.
        var locale = host.ViewerLocale();
        return host.Workspace.GetMeshNodeStream()
            .Select(node => (UiControl?)BuildOverview(host, node, locale));
    }

    private const string ContainerStyle = "max-width: 1080px; margin: 0 auto; padding: 24px; gap: 16px;";

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node, string? locale)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle(ContainerStyle);

        var def = node.ContentAs<SkillDefinition>(host.Hub.JsonSerializerOptions);
        // 🌍 Display text only. The INSTRUCTION body below is the skill's procedure — model-facing
        // prompt text — and is rendered exactly as authored in every language.
        var text = NodeTextTranslations.For(def, locale);

        // Title — the skill name (falls back to a humanised id).
        var displayName = Localized(text?.Name, node?.Name) ?? node?.Id?.Wordify() ?? "Skill";
        container = container.WithView(Controls.Title(displayName, 1));

        // Subtitle — the slash word (`/id`) and the node's help text (description).
        var subtitle = BuildSubtitleMarkdown(node, Localized(text?.Description, node?.Description));
        if (!string.IsNullOrWhiteSpace(subtitle))
            container = container.WithView(Controls.Markdown(subtitle));

        if (def is null)
            return container;

        // The instruction body (SKILL.md) is the skill's main content — rendered as markdown.
        if (!string.IsNullOrWhiteSpace(def.Instructions))
            container = container.WithView(Controls.Markdown(def.Instructions!));
        else if (def.Action is null)
            container = container.WithView(Controls.Markdown(host.Localize("ui.mdSkillNoInstructions")));

        // Compact metadata table at the foot.
        var metadata = BuildMetadataMarkdown(def);
        if (!string.IsNullOrWhiteSpace(metadata))
            container = container.WithView(Controls.Markdown(metadata));

        return container;
    }

    /// <summary>Per-FIELD fallback: a translation that sets only one field keeps the authored rest.</summary>
    private static string? Localized(string? translated, string? authored)
        => string.IsNullOrWhiteSpace(translated) ? authored : translated;

    private static string BuildSubtitleMarkdown(MeshNode? node, string? localizedDescription)
    {
        // 🚨 The slash word is node.Id — the INVOCATION token — so it renders identically in every
        // language. Localizing it would print a command the router cannot resolve.
        var slash = string.IsNullOrWhiteSpace(node?.Id) ? null : $"`/{node!.Id}`";
        var description = string.IsNullOrWhiteSpace(localizedDescription) ? null : localizedDescription;
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
