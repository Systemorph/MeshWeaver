namespace MeshWeaver.Layout;

/// <summary>
/// A control that displays a collection of MeshNodes loaded from queries.
/// Supports "+" button to add items (via dialog) and trash button to delete items.
/// Items are rendered as compact cards (avatar + name + type).
/// </summary>
public record MeshNodeCollectionControl()
    : UiControl<MeshNodeCollectionControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Queries to list existing items. Multiple queries are run in parallel and results merged.
    /// Supports template variables via MeshNodeCollectionAttribute.ResolveQueries.
    /// </summary>
    public string[] Queries { get; init; } = [];

    /// <summary>
    /// Whether items show a trash/delete button.
    /// </summary>
    public bool Deletable { get; init; }

    /// <summary>
    /// Whether to show the "+" add button. Default true.
    /// </summary>
    public bool ShowAdd { get; init; } = true;

    /// <summary>
    /// Title for the add dialog.
    /// </summary>
    public string? AddDialogTitle { get; init; }

    /// <summary>
    /// Picker queries for the add dialog's MeshNodePickerControl.
    /// Multiple queries supported.
    /// </summary>
    public string[]? AddPickerQueries { get; init; }

    /// <summary>
    /// Label for the picker in the add dialog.
    /// </summary>
    public string? AddPickerLabel { get; init; }

    public MeshNodeCollectionControl WithQueries(params string[] queries) => this with { Queries = queries };
    public MeshNodeCollectionControl WithDeletable(bool deletable) => this with { Deletable = deletable };
    public MeshNodeCollectionControl WithShowAdd(bool show) => this with { ShowAdd = show };
    public MeshNodeCollectionControl WithAddDialogTitle(string title) => this with { AddDialogTitle = title };
    public MeshNodeCollectionControl WithAddPickerQueries(params string[] queries) => this with { AddPickerQueries = queries };
    public MeshNodeCollectionControl WithAddPickerLabel(string label) => this with { AddPickerLabel = label };
}
