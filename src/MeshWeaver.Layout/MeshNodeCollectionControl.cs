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

    /// <summary>
    /// Minimum number of items the collection must keep. While the list is at the minimum the
    /// delete buttons are hidden, so the UI cannot take it below what the model requires.
    /// Default 0 — no lower bound.
    /// </summary>
    public int Min { get; init; }

    /// <summary>
    /// Maximum number of items the collection accepts. At the maximum the "+" button is hidden,
    /// so the UI cannot exceed it. Default <see cref="int.MaxValue"/> — unbounded.
    /// </summary>
    public int Max { get; init; } = int.MaxValue;

    /// <summary>Whether the "+" button shows at <paramref name="count"/> items: the author asked
    /// for it AND the maximum is not reached. Pure — the view's single add gate.</summary>
    public bool CanAdd(int count) => ShowAdd && count < Max;

    /// <summary>Whether delete buttons show at <paramref name="count"/> items: the author asked
    /// for them AND removing one would not breach the minimum. Pure — the view's single delete
    /// gate, so a required collection cannot be emptied by mis-clicking.</summary>
    public bool CanDelete(int count) => Deletable && count > Min;

    /// <summary>Returns a copy requiring at least <paramref name="min"/> items.</summary>
    public MeshNodeCollectionControl WithMin(int min) => this with { Min = min };

    /// <summary>Returns a copy accepting at most <paramref name="max"/> items.</summary>
    public MeshNodeCollectionControl WithMax(int max) => this with { Max = max };

    /// <summary>Returns a copy with <paramref name="queries"/> as the collection queries run in parallel to populate the list.</summary>
    /// <param name="queries">One or more mesh query strings whose results are merged and displayed as cards.</param>
    public MeshNodeCollectionControl WithQueries(params string[] queries) => this with { Queries = queries };
    /// <summary>Returns a copy with <paramref name="deletable"/> controlling whether each item shows a delete button.</summary>
    /// <param name="deletable">When <c>true</c>, a trash button is shown on each card.</param>
    public MeshNodeCollectionControl WithDeletable(bool deletable) => this with { Deletable = deletable };
    /// <summary>Returns a copy with <paramref name="show"/> controlling whether the "+" add button is visible.</summary>
    /// <param name="show">When <c>true</c>, the add button is rendered.</param>
    public MeshNodeCollectionControl WithShowAdd(bool show) => this with { ShowAdd = show };
    /// <summary>Returns a copy with <paramref name="title"/> as the title of the add dialog.</summary>
    /// <param name="title">Title displayed at the top of the add-item dialog.</param>
    public MeshNodeCollectionControl WithAddDialogTitle(string title) => this with { AddDialogTitle = title };
    /// <summary>Returns a copy with <paramref name="queries"/> as the picker queries used inside the add dialog.</summary>
    /// <param name="queries">Mesh query strings for the add-dialog's node picker.</param>
    public MeshNodeCollectionControl WithAddPickerQueries(params string[] queries) => this with { AddPickerQueries = queries };
    /// <summary>Returns a copy with <paramref name="label"/> as the picker label inside the add dialog.</summary>
    /// <param name="label">Label shown above the node picker in the add-item dialog.</param>
    public MeshNodeCollectionControl WithAddPickerLabel(string label) => this with { AddPickerLabel = label };
}
