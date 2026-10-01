using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// The NodeType pages' compile state as DATA — every string the Configuration, Releases and Overview
/// templates show about the type's compile, already decided and localized. The templates bind their
/// controls to pointers into this record at <c>/data/{<see cref="DataId"/>}</c>; nothing in a template
/// reads the node (Doc/GUI/DataBinding → "Templates first, data later").
///
/// <para>🚨 Why a projection and not a field binding: what the page shows is not a FIELD of the
/// definition but a DECISION over several (status × build presence × dirty flag), and the words are
/// the viewer's language. Pure (<see cref="From"/>), so the decisions are tested without a renderer; the
/// stream that feeds it (<see cref="NodeTypeLayoutAreas"/>.<c>StatusProjection</c>) is the only part
/// that touches the node.</para>
/// </summary>
/// <param name="Title">The type's display name, falling back to its id.</param>
/// <param name="StatusBadge">The Configuration pane's one-line compile status.</param>
/// <param name="ReleasesStatusBadge">The Releases pane's one-line compile status (silent on Ok).</param>
/// <param name="ConfigurationCode">The Configuration lambda, or empty.</param>
/// <param name="PendingReleaseNotes">"Pending release notes: …", or empty.</param>
/// <param name="PendingReleaseNotesStyle">Hides the release-notes line when there are none.</param>
/// <param name="LogStyle">The compile-log panel's style — hidden while nothing has been compiled.</param>
/// <param name="LogHeadline">The compile-log panel's headline.</param>
/// <param name="LogHeadlineStyle">The headline's colour.</param>
/// <param name="LogDetail">Markdown under the headline: the error text, or empty.</param>
/// <param name="LogLinks">Markdown links: the latest release and the compile activity log.</param>
/// <param name="PanelStyle">The Overview compile panel's style — hidden for a type with no code.</param>
/// <param name="PanelChip">The Overview compile panel's state chip.</param>
/// <param name="PanelChipStyle">The chip's style.</param>
/// <param name="CompileLabel">The compile button's label.</param>
/// <param name="CompileDisabled">Whether the compile button is disabled (a compile is running).</param>
/// <param name="LatestReleaseLink">Markdown link to the latest release, or empty.</param>
public sealed record NodeTypeStatusView(
    string Title,
    string StatusBadge,
    string ReleasesStatusBadge,
    string ConfigurationCode,
    string PendingReleaseNotes,
    string PendingReleaseNotesStyle,
    string LogStyle,
    string LogHeadline,
    string LogHeadlineStyle,
    string LogDetail,
    string LogLinks,
    string PanelStyle,
    string PanelChip,
    string PanelChipStyle,
    string CompileLabel,
    bool CompileDisabled,
    string LatestReleaseLink)
{
    /// <summary>The id under the layout area's <c>/data</c> the projection is published at.</summary>
    public const string DataId = "nodeTypeStatus";

    /// <summary>The DataContext a template's bound controls carry.</summary>
    public static string DataContext => LayoutAreaReference.GetDataPointer(DataId);

    /// <summary>A pointer to <paramref name="member"/> of this record, as it is serialized (camelCase).</summary>
    /// <param name="member">The member name — pass <c>nameof(NodeTypeStatusView.X)</c>.</param>
    public static JsonPointerReference Pointer(string member)
        => new(char.ToLowerInvariant(member[0]) + member[1..]);

    private const string Hidden = "display: none;";
    private const string PanelBase =
        "align-items: center; gap: 12px; padding: 12px 16px; margin: 16px 0; border-radius: 6px; "
        + "border: 1px solid var(--neutral-stroke-rest); ";
    private const string LogBase =
        "padding: 12px 16px; background: var(--neutral-layer-2); border-radius: 4px; gap: 8px; margin-bottom: 8px;";

    /// <summary>
    /// Decides every word and style the NodeType templates show for <paramref name="node"/>. Pure.
    /// </summary>
    /// <param name="node">The NodeType node, or <c>null</c> while it has not arrived.</param>
    /// <param name="def">Its definition, read with <c>ContentAs</c> by the caller.</param>
    /// <param name="nodeTypePath">The type's path — the title's fallback.</param>
    /// <param name="locale">The viewer's locale.</param>
    public static NodeTypeStatusView From(MeshNode? node, NodeTypeDefinition? def, string nodeTypePath, string? locale)
    {
        string L(string key, params object?[] args) => LocalizationCatalog.Get(key, locale, args);
        var status = def?.CompilationStatus;

        var title = node?.Name is { Length: > 0 } name ? name : nodeTypePath.Split('/').LastOrDefault() ?? nodeTypePath;

        var statusBadge = status switch
        {
            CompilationStatus.Pending or CompilationStatus.Compiling => L("ui.compiling"),
            CompilationStatus.Ok => L("ui.lastCompileOk"),
            CompilationStatus.Error => L("ui.lastCompileError"),
            CompilationStatus.Unavailable => L("ui.compileStateUnknown"),
            _ => ""
        };
        var releasesBadge = status == CompilationStatus.Ok ? "" : statusBadge;

        var notes = def?.ReleaseNotes;
        var hasNotes = !string.IsNullOrWhiteSpace(notes);

        var (logStyle, logHeadline, logHeadlineStyle, logDetail, logLinks) = CompileLog(def, L);
        var (panelStyle, chip, chipStyle, compileLabel, compileDisabled) = CompilePanel(def, L);

        var latestRelease = def?.LatestReleasePath is { Length: > 0 } release
            ? $"[{release.Split('/').LastOrDefault() ?? release}](/{release})"
            : "";

        return new NodeTypeStatusView(
            Title: title,
            StatusBadge: statusBadge,
            ReleasesStatusBadge: releasesBadge,
            ConfigurationCode: def?.Configuration ?? "",
            PendingReleaseNotes: hasNotes ? L("ui.pendingReleaseNotes", notes!.Trim()) : "",
            PendingReleaseNotesStyle: hasNotes
                ? "color: var(--neutral-foreground-hint); font-size: 0.9rem; font-style: italic;"
                : Hidden,
            LogStyle: logStyle,
            LogHeadline: logHeadline,
            LogHeadlineStyle: logHeadlineStyle,
            LogDetail: logDetail,
            LogLinks: logLinks,
            PanelStyle: panelStyle,
            PanelChip: chip,
            PanelChipStyle: chipStyle,
            CompileLabel: compileLabel,
            CompileDisabled: compileDisabled,
            LatestReleaseLink: latestRelease);
    }

    /// <summary>
    /// The compile-log panel beneath the Configuration header: hidden until a compile has been
    /// requested or recorded; then the running / failed / undetermined / published headline, the
    /// error text, and links to the latest release and the compile activity log.
    /// </summary>
    private static (string Style, string Headline, string HeadlineStyle, string Detail, string Links) CompileLog(
        NodeTypeDefinition? def, Func<string, object?[], string> L)
    {
        var hasState = def is not null
            && (def.CompilationStatus is not null
                || !string.IsNullOrEmpty(def.LastCompilationActivityPath)
                || !string.IsNullOrEmpty(def.LatestReleasePath));
        if (!hasState)
            return (Hidden, "", "", "", "");

        string headline = "", headlineStyle = "", detail = "";
        var links = new List<string>();
        switch (def!.CompilationStatus)
        {
            case CompilationStatus.Pending or CompilationStatus.Compiling:
                headline = L("ui.compiling", []);
                headlineStyle = "color: var(--accent-fill-rest); font-weight: 600;";
                break;
            case CompilationStatus.Error when !string.IsNullOrEmpty(def.CompilationError):
                headline = L("ui.compileFailed", []);
                headlineStyle = "color: var(--error); font-weight: 600;";
                detail = Fenced(def.CompilationError!);
                break;
            case CompilationStatus.Unavailable:
                // Availability problem, not a compile failure — the text is the "could not
                // determine" message, never Roslyn diagnostics.
                headline = L("ui.compileStateUnknown", []);
                headlineStyle = "color: var(--warning-foreground); font-weight: 600;";
                if (!string.IsNullOrEmpty(def.CompilationError))
                    detail = Fenced(def.CompilationError!);
                break;
            case CompilationStatus.Ok when !string.IsNullOrEmpty(def.LatestReleasePath):
                headline = L("ui.releasePublished", []);
                headlineStyle = "color: var(--accent-fill-rest); font-weight: 600;";
                links.Add($"[→ {def.LatestReleasePath}](/{def.LatestReleasePath})");
                break;
        }

        if (!string.IsNullOrEmpty(def.LastCompilationActivityPath))
            links.Add($"[{L("ui.viewCompileLog", [])}](/{def.LastCompilationActivityPath})");

        return (LogBase, headline, headline.Length == 0 ? Hidden : headlineStyle, detail, string.Join("  \n", links));
    }

    /// <summary>
    /// The Overview's compile panel: hidden for a type with no code (it never participates in
    /// compilation); otherwise the state chip and the compile button's label, with the button
    /// disabled while a compile is running.
    /// </summary>
    private static (string Style, string Chip, string ChipStyle, string Label, bool Disabled) CompilePanel(
        NodeTypeDefinition? def, Func<string, object?[], string> L)
    {
        var hasCode = def is not null
            && (!string.IsNullOrWhiteSpace(def.Configuration)
                || !string.IsNullOrWhiteSpace(def.HubConfiguration)
                || (def.CurrentSourceVersions?.Count ?? 0) > 0);
        if (!hasCode)
            return (Hidden, "", "", L("ui.compile", []), true);

        var status = def!.CompilationStatus;
        // "Never compiled" = no assembly metadata persisted. Framework versions are deliberately not
        // compared here (that is HasUsableBuild's concern): a build that exists reads "Up to date"
        // until the status flips Dirty / Error / Compiling.
        var hasBuild = !string.IsNullOrEmpty(def.LatestAssemblyCollection)
            && !string.IsNullOrEmpty(def.LatestAssemblyPath);
        var neverCompiled = !hasBuild
            && status != CompilationStatus.Compiling
            && status != CompilationStatus.Error
            // "Could not determine" is not "never compiled" — claiming the latter hides the real
            // (retryable) cause behind a wrong first build.
            && status != CompilationStatus.Unavailable;

        const string warning = "background: var(--warning-fill-rest); border-color: var(--warning-stroke-rest);";
        if (status == CompilationStatus.Compiling)
            return (PanelBase + "background: var(--neutral-fill-stealth-rest);",
                L("ui.compiling", []), "font-weight: 600;", L("ui.compile", []), true);
        if (status == CompilationStatus.Error)
            return (PanelBase + "background: var(--error-fill-rest); border-color: var(--error-stroke-rest);",
                L("ui.compilationFailed", []), "font-weight: 600; color: var(--error-foreground);",
                L("ui.retryCompile", []), false);
        if (status == CompilationStatus.Unavailable)
            // Undetermined, not failed: the warning tint, never the error tint that reads as
            // "your code is broken".
            return (PanelBase + warning, L("ui.compileStateUnknown", []),
                "font-weight: 600; color: var(--warning-foreground);", L("ui.retryCompile", []), false);
        if (neverCompiled)
            return (PanelBase + warning, L("ui.neverCompiled", []),
                "font-weight: 600; color: var(--warning-foreground);", L("ui.compile", []), false);
        if (def.IsDirty)
            return (PanelBase + warning, L("ui.sourceChanged", []),
                "font-weight: 600; color: var(--warning-foreground);", L("ui.compile", []), false);
        return (PanelBase + "background: var(--neutral-fill-stealth-rest);",
            L("ui.upToDate", []), "font-weight: 600;", L("ui.recompile", []), false);
    }

    private static string Fenced(string text) => $"```text\n{text}\n```";
}
