namespace MeshWeaver.Maui.Abstractions;

/// <summary>
/// The parity ratchet's source of truth: which framework <c>UiControl</c>s the native MAUI view pack
/// renders with an explicit native view (<see cref="SupportedLeafControls"/>) and which are still
/// PLANNED (<see cref="PlannedControls"/>). Container controls (anything implementing
/// <c>IContainerControl</c>) are rendered generically by the pack's <c>ContainerView</c> fallback and
/// are NOT listed here — the coverage test detects them by reflection.
///
/// <para>The coverage test (<c>MauiControlCoverageTest</c>) enumerates every concrete <c>UiControl</c>
/// and asserts it is accounted for: <c>SupportedLeafControls ∪ PlannedControls ∪ {containers}</c> must
/// cover them all, with no overlap and no stale entries. So adding a new framework control FAILS the
/// test until it is classified here; implementing a planned control's native view = move its name from
/// <see cref="PlannedControls"/> to <see cref="SupportedLeafControls"/> (and register it in the view
/// pack's <c>BuildRegistry</c>). Names are the control type's <c>Name</c> (with the <c>Control</c> suffix).</para>
/// </summary>
public static class MauiControlManifest
{
    /// <summary>Controls with an explicit native <c>MauiView</c> registered in the view pack.</summary>
    public static readonly IReadOnlySet<string> SupportedLeafControls = new HashSet<string>(StringComparer.Ordinal)
    {
        // Wave 1 leaves
        "LabelControl", "ButtonControl", "HtmlControl", "MarkdownControl", "CollaborativeMarkdownControl",
        "IconControl", "ProgressControl", "NamedAreaControl",
        // Form controls
        "TextFieldControl", "TextAreaControl", "CheckBoxControl", "SwitchControl", "SelectControl",
        "NumberFieldControl", "DateTimeControl", "DateControl", "ComboboxControl", "ListboxControl",
        "RadioGroupControl", "SliderControl", "SpacerControl", "ExceptionControl",
        // Data + layout (some are containers too, but they have explicit views)
        "DataGridControl", "LayoutGridControl", "TabsControl",
        // Query-driven + node-bound editor
        "MeshNodePickerControl", "MeshSearchControl", "MeshNodeContentEditorControl",
        // Embedded area + nav + badges
        "LayoutAreaControl", "BadgeControl", "NavLinkControl", "MenuItemControl",
        // Agent-backed chat
        "ThreadChatControl", "ThreadMessageBubbleControl",
        // Phase 3 — node display cards + grouped catalog + query-driven collection + search box
        "MeshNodeCardControl", "MeshNodeThumbnailControl", "CatalogControl", "MeshNodeCollectionControl",
        "SearchBoxControl",
        // Phase 3 — redirect + code sample + dialog
        "RedirectControl", "CodeSampleControl", "DialogControl",
    };

    /// <summary>Concrete controls not yet given a native view — the remaining parity work (Phases 3-5).</summary>
    public static readonly IReadOnlySet<string> PlannedControls = new HashSet<string>(StringComparer.Ordinal)
    {
        // Phase 3 — node management / misc
        "MeshNodeEditorControl", "MeshNodeRoleEditorControl", "EditFormControl",
        "EditorControl", "ItemTemplateControl", "AppearanceControl",
        "UserProfileControl", "FileBrowserControl", "NodeImportControl", "NodeExportControl",
        "ExportDocumentControl", "LayoutAreaDefinitionControl",
        // Phase 4 — rich data + editors (OSS libs)
        "ChartControl", "PivotGridControl", "CodeEditorControl", "DiffEditorControl",
        "MarkdownEditorControl",
        // DataGrid column control (rendered by the grid, not standalone). PropertyColumnControl is
        // generic (PropertyColumnControl<T>) so it isn't a concrete control in the coverage scan.
        "TemplateColumnControl",
    };
}
