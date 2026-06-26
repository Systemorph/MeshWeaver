#nullable enable
namespace MeshWeaver.GridModel
{
    /// <summary>
    /// Definition of a single grid column (ag-Grid style <c>ColDef</c>), describing how a field
    /// of each row is bound, rendered, formatted, sorted, filtered, grouped and aggregated.
    /// </summary>
    public record ColDef
    {
        /// <summary>Initializes a new, empty column definition.</summary>
        // ReSharper disable once EmptyConstructor
        public ColDef() { }

        /// <summary>Name of the row field whose value supplies this column's cell data.</summary>
        public string? Field { get; init; }

        /// <summary>Unique identifier for the column; defaults to <see cref="Field"/> when not set.</summary>
        public object? ColId { get; init; }

        /// <summary>Text shown in the column header; falls back to <see cref="Field"/> when omitted.</summary>
        public string? HeaderName { get; init; }

        /// <summary>CSS class(es) applied to the column header cell.</summary>
        public object? HeaderClass { get; init; }

        /// <summary>JavaScript function or expression that derives the display value from the row data.</summary>
        public string? ValueGetter { get; init; }

        /// <summary>Whether the column is shown when its group is open or closed; accepted values are <c>"open"</c> and <c>"closed"</c>.</summary>
        public string? ColumnGroupShow { get; init; } //= "open"; // closed in group

        /// <summary>CSS class(es) applied to the data cells of this column.</summary>
        public string? CellClass { get; init; }

        /// <summary>Inline style for the data cells; typically a <see cref="MeshWeaver.GridModel.CellStyle"/> or a style-returning expression.</summary>
        public object? CellStyle { get; init; }

        /// <summary>JavaScript function or expression that formats the cell value for display.</summary>
        public string? ValueFormatter { get; init; }

        /// <summary>
        /// How cells are rendered: null renders the value as a string; a string names a cell
        /// renderer component; a function returns an HTML string or DOM element for display.
        /// </summary>
        public string? CellRenderer { get; init; }

        /// <summary>Extra parameters passed to the configured <see cref="CellRenderer"/>.</summary>
        public CellRendererParams? CellRendererParams { get; init; }

        /// <summary>When <c>true</c>, the column is hidden.</summary>
        public bool? Hide { get; init; }

        /// <summary>Fixed column width in pixels.</summary>
        public int? Width { get; init; }

        /// <summary>Minimum column width in pixels.</summary>
        public int? MinWidth { get; init; }

        /// <summary>Maximum column width in pixels.</summary>
        public int? MaxWidth { get; init; }

        /// <summary>Flex ratio used to distribute remaining horizontal space across columns.</summary>
        public int? Flex { get; init; }

        /// <summary>When <c>true</c>, the user can resize the column.</summary>
        public bool? Resizable { get; init; }

        /// <summary>Pins the column to a side of the grid; accepted values are <c>"left"</c> and <c>"right"</c>.</summary>
        public string? Pinned { get; init; }

        /// <summary>When <c>true</c>, sorting is allowed on this column.</summary>
        public bool? Sortable { get; init; }

        /// <summary>When <c>true</c>, the column cannot be moved by dragging.</summary>
        public bool? SuppressMovable { get; init; }

        /// <summary>Initial sort direction; accepted values are <c>null</c>, <c>"asc"</c> and <c>"desc"</c>.</summary>
        public string? Sort { get; init; }

        /// <summary>Filter to use: <c>true</c> for the default text filter, or a string naming a custom filter such as <c>"agNumberColumnFilter"</c>.</summary>
        public object? Filter { get; init; }

        /// <summary>When <c>true</c>, shows a floating filter row beneath the header.</summary>
        public bool? FloatingFilter { get; init; }

        /// <summary>When <c>true</c>, cells in this column are editable.</summary>
        public bool? Editable { get; init; }

        /// <summary>When <c>true</c>, no column menu is shown for this header.</summary>
        public bool? SuppressMenu { get; init; }

        /// <summary>When <c>true</c>, rows are grouped by this column.</summary>
        public bool? RowGroup { get; init; }

        /// <summary>Zero-based position of this column within the row-group order when grouping by multiple columns.</summary>
        public int? RowGroupIndex { get; init; }

        // TODO V10: not sure we need this. if we keep it, we might need properties of RowGroup to be moved back (2021/09/29, Ekaterina Mishina)
        /// <summary>When <c>true</c>, the user may row-group by this column via the GUI; does not block API-driven grouping.</summary>
        public bool? EnableRowGroup { get; init; }

        /// <summary>Name of the aggregation function applied to this column's values (e.g. <c>"sum"</c>, <c>"avg"</c>).</summary>
        public string? AggFunc { get; init; }

        /// <summary>Aggregation functions the user is allowed to choose for this column.</summary>
        public IReadOnlyCollection<string>? AllowedAggFuncs { get; init; }

        /// <summary>When <c>true</c>, the column can be used as a value (aggregated) column.</summary>
        public bool? EnableValue { get; init; }

        /// <summary>When <c>true</c>, the column may be dragged onto the pivot drop zone in the Tool Panel.</summary>
        public bool? EnablePivot { get; init; }

        /// <summary>When <c>true</c>, the grid pivots by this column.</summary>
        public bool? Pivot { get; init; }

        /// <summary>Zero-based position of this column within the pivot order when pivoting by multiple columns.</summary>
        public int? PivotIndex { get; init; }

        /// <summary>JavaScript comparator used to order the generated pivot columns for this field.</summary>
        public string? PivotComparator { get; init; }
    }

    /// <summary>Parameters for the built-in group cell renderer (<c>agGroupCellRenderer</c>).</summary>
    public record CellRendererParams
    {
        /// <summary>Value getter expression supplying the footer text.</summary>
        public string? FooterValueGetter { get; init; }

        /// <summary>Name of the renderer used to render the value inside the group cell.</summary>
        public string? InnerRenderer { get; init; }

        /// <summary>When <c>true</c>, the child count is not displayed next to the group name.</summary>
        public bool SuppressCount { get; init; }
    }

    /// <summary>Inline CSS style applied to a grid cell or row.</summary>
    public record CellStyle
    {
        /// <summary>Text color (CSS <c>color</c>).</summary>
        public string? Color { get; init; }

        /// <summary>Background color (CSS <c>background-color</c>).</summary>
        public string? BackgroundColor { get; init; }

        /// <summary>Width (CSS <c>width</c>).</summary>
        public string? Width { get; init; }

        /// <summary>Left offset (CSS <c>left</c>).</summary>
        public string? Left { get; init; }

        /// <summary>Font weight (CSS <c>font-weight</c>).</summary>
        public string? FontWeight { get; set; }

        /// <summary>Horizontal text alignment (CSS <c>text-align</c>).</summary>
        public string? TextAlign { get; set; }
    }

    /// <summary>String constants for the <see cref="ColDef.ColumnGroupShow"/> values that control visibility within a column group.</summary>
    public static class ColumnGroupShow
    {
        /// <summary>Show the column only when the group is collapsed.</summary>
        public const string Closed = "closed";

        /// <summary>Show the column only when the group is expanded.</summary>
        public const string Open = "open";
    }
}
