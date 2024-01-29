namespace OpenSmc.GridModel
{
    public record ColDef
    {
        // ReSharper disable once EmptyConstructor
        public ColDef()
        {
        }

        // The field of the row to get the cells data from
        public string Field { get; init; }

        public string ColId { get; init; }

        // The name to render in the column header. If not specified and field is specified, the field name will be used as the header name.
        public string HeaderName { get; init; }
        public object HeaderClass { get; init; }

        // Function or expression. Gets the value from your data for display. See ValueGetterParams for data bindings
        public string ValueGetter { get; init; }

        // Whether to show the column when the group is open / closed - accepted values "open" and "closed"
        public string ColumnGroupShow { get; init; } //= "open"; // closed in group

        // Styling class and options: https://www.ag-grid.com/angular-data-grid/excel-export-data-types/#strings-number-and-booleans
        public string CellClass { get; init; }

        public object CellStyle { get; init; }

        // Function or expression. Formats the value for display
        public string ValueFormatter { get; init; }

        // undefined / null: Grid renders the value as a string.
        // String: The name of a cell renderer component.
        // Class: Direct reference to a cell renderer component.
        // Function: A function that returns either an HTML string or DOM element for display.
        public string CellRenderer { get; init; }

        public CellRendererParams CellRendererParams { get; init; }

        // Set to true for this column to be hidden
        public bool? Hide { get; init; }

        // Width
        public int? Width { get; init; }
        public int? MinWidth { get; init; }
        public int? MaxWidth { get; init; }
        public int? Flex { get; init; }
        public bool? Resizable { get; init; }

        // Pin a column to one side: "left", "right"
        public string Pinned { get; init; }

        // Set to true to allow sorting on this column
        public bool? Sortable { get; init; }

        // Set to true if you do not want this column to be movable via dragging
        public bool? SuppressMovable { get; init; }

        // Set to sort this column. Options: null, 'asc', 'desc'
        public string Sort { get; init; }

        // true for default (text), string for custom, e.g. "agTextColumnFilter" or "agNumberColumnFilter"
        public object Filter { get; init; }
        public bool? FloatingFilter { get; init; }

        public bool? Editable { get; init; }

        // Set to true if no menu should be shown for this column header
        public bool? SuppressMenu { get; init; }

        // Group rows
        public bool? RowGroup { get; init; }

        // Set this in columns you want to group by. If only grouping by one column, set this to any number (e.g. 0). If grouping by multiple columns, set this to where you want this column to be in the group (e.g. 0 for first, 1 for second, and so on).
        public int? RowGroupIndex { get; init; }

        // TODO V10: not sure we need this. if we keep it, we might need properties of RowGroup to be moved back (2021/09/29, Ekaterina Mishina)
        // Set to true if you want to be able to row group by this column via the GUI. This will not block the API or properties being used to achieve row grouping.
        public bool? EnableRowGroup { get; init; }

        // Aggregation
        public string AggFunc { get; init; }
        public IReadOnlyCollection<string> AllowedAggFuncs { get; init; }
        public bool? EnableValue { get; init; }

        // To allow a column to be used as pivot column via the Tool Panel, set enablePivot=true on the required columns.
        // Otherwise you won't be able to drag and drop the columns to the pivot drop zone from the Tool Panel.
        public bool? EnablePivot { get; init; }

        // Set to true to pivot by this column
        public bool? Pivot { get; init; }
        public int? PivotIndex { get; init; }
        public string PivotComparator { get; init; }
    }

    // params for the agGroupCellRenderer CellRenderer
    public record CellRendererParams
    {
        // The value getter for the footer text. 
        public string FooterValueGetter { get; init; }

        // The renderer to use for inside the cell
        public string InnerRenderer { get; init; }

        // If true, count is not displayed beside the name.
        public bool SuppressCount { get; init; }
    }

    public record CellStyle
    {
        public string Color { get; init; }
        public string BackgroundColor { get; init; }
        public string Width { get; init; }
        public string Left { get; init; }
        public string FontWeight { get; set; }
        public string TextAlign { get; set; }
    }

    public static class ColumnGroupShow
    {
        public const string Closed = "closed";
        public const string Open = "open";
    }
}
