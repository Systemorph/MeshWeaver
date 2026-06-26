using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.GridModel
{
    /// <summary>
    /// Top-level configuration for a data grid (ag-Grid style <c>gridOptions</c>): the columns, row
    /// data, grouping/pivot/aggregation behaviour, layout sizing and event callbacks.
    /// </summary>
    public record GridOptions
    {

        /// <summary>The column definitions for the grid.</summary>
        public IReadOnlyCollection<ColDef> ColumnDefs { get; init; } = [];

        /// <summary>The data rows displayed in the grid.</summary>
        public IReadOnlyCollection<object> RowData { get; init; } = [];

        /// <summary>Default column definition whose properties apply to every column.</summary>
        public ColDef? DefaultColDef { get; init; }

        /// <summary>Default column group definition whose properties apply to every column group.</summary>
        public ColGroupDef? DefaultColGroupDef { get; init; }

        /// <summary>Definition of the auto-generated group column used when row grouping.</summary>
        public ColDef? AutoGroupColumnDef { get; init; }

        /// <summary>
        /// Side bar configuration: <c>true</c> for the default side bar, <c>"columns"</c> or
        /// <c>"filters"</c> for a single tool panel, or a side-bar definition object for detail.
        /// </summary>
        public object? SideBar { get; init; }

        /// <summary>When <c>true</c>, enables pivot mode.</summary>
        public bool? PivotMode { get; init; }

        /// <summary>When to show the pivot panel at the top; options are <c>"never"</c>, <c>"always"</c> and <c>"onlyWhenPivoting"</c>.</summary>
        public bool? PivotPanelShow { get; init; }

        /// <summary>When <c>true</c>, omits the aggregation function name from column headers (e.g. <c>"Bank Balance"</c> instead of <c>"sum(Bank Balance)"</c>).</summary>
        public bool? SuppressAggFuncInHeader { get; init; }

        /// <summary>When <c>true</c>, aggregations are not computed for the grid's root node.</summary>
        public bool? SuppressAggAtRootLevel { get; init; }

        /// <summary>When <c>true</c>, pivot column groups appear fixed and cannot be expanded or collapsed.</summary>
        public bool? SuppressExpandablePivotGroups { get; init; }

        /// <summary>When <c>true</c>, the grid does not swap in the grouping column while pivoting.</summary>
        public bool? PivotSuppressAutoColumn { get; init; }

        /// <summary>Enables pivot row totals; accepted values are <c>"before"</c> and <c>"after"</c>.</summary>
        public string? PivotRowTotals { get; init; }

        /// <summary>Enables pivot column group totals; accepted values are <c>"before"</c> and <c>"after"</c>.</summary>
        public string? PivotColumnGroupTotals { get; init; }

        /// <summary>When <c>true</c>, adds per-group subtotal footer rows.</summary>
        public bool? GroupIncludeFooter { get; init; }

        /// <summary>When <c>true</c>, adds a grand-total footer row.</summary>
        public bool? GroupIncludeTotalFooter { get; init; }

        /// <summary>When <c>true</c>, enables the integrated charting feature.</summary>
        public bool? EnableCharts { get; init; }

        /// <summary>When <c>true</c>, enables range (cell) selection.</summary>
        public bool? EnableRangeSelection { get; init; }

        /// <summary>
        /// How row grouping is displayed; options are <c>"singleColumn"</c>, <c>"multipleColumns"</c>,
        /// <c>"groupRows"</c> and <c>"custom"</c>.
        /// </summary>
        public string? GroupDisplayType { get; init; }

        /// <summary>Number of group levels expanded by default; <c>0</c> for none, <c>-1</c> to expand everything.</summary>
        public int? GroupDefaultExpanded { get; init; }

        /// <summary>When <c>true</c>, shows the open group in the group column for non-group rows.</summary>
        public bool? ShowOpenedGroup { get; init; }

        /// <summary>When <c>true</c>, collapses groups that have only a single child.</summary>
        public bool? GroupRemoveSingleChildren { get; init; }

        /// <summary>When <c>true</c>, collapses lowest-level groups that have only a single child.</summary>
        public bool? GroupRemoveLowestSingleChildren { get; init; }

        /// <summary>Map of custom component names to their registered renderer/editor implementations.</summary>
        public IImmutableDictionary<string, string> Components { get; init; } = ImmutableDictionary<string, string>.Empty;

        /// <summary>When <c>true</c>, enables tree-data mode; requires <see cref="GetDataPath"/> and is incompatible with pivot or row grouping.</summary>
        public bool? TreeData { get; init; }

        /// <summary>Callback returning the hierarchy path (one element per tree level) for each row in tree-data mode.</summary>
        public string? GetDataPath { get; init; }

        /// <summary>Excel cell styles available when exporting to Excel.</summary>
        public IReadOnlyCollection<string>? ExcelStyles { get; init; }

        /// <summary>Height in pixels of group header rows.</summary>
        public int? GroupHeaderHeight { get; init; }

        /// <summary>Height in pixels of the column header row.</summary>
        public int? HeaderHeight { get; init; }

        /// <summary>Height in pixels of data rows.</summary>
        public int? RowHeight { get; init; }

        /// <summary>When <c>true</c>, disables the hover highlight on rows.</summary>
        public bool? SuppressRowHoverHighlight { get; init; }

        /// <summary>Inline style applied to every row.</summary>
        public CellStyle? RowStyle { get; init; }

        /// <summary>Callback expression returning a per-row style.</summary>
        public string? GetRowStyle { get; init; }

        /// <summary>JavaScript handler invoked when the grid is ready.</summary>
        public string? OnGridReady { get; init; }

        /// <summary>Callback that post-processes secondary (pivot) column definitions before they are applied.</summary>
        public string? ProcessSecondaryColDef { get; init; }

        /// <summary>JavaScript handler invoked after the grid first renders its data.</summary>
        public string? OnFirstDataRendered { get; init; }

        /// <summary>When <c>true</c> (the default), highlights the column under the mouse on hover.</summary>
        public bool ColumnHoverHighlight { get; init; } = true;

        /// <summary>DOM layout mode; e.g. <c>"normal"</c>, <c>"autoHeight"</c> or <c>"print"</c>.</summary>
        public string? DomLayout { get; set; }

        /// <summary>Returns a copy of these options with the framework's default column, header and sizing settings applied.</summary>
        /// <returns>A new <see cref="GridOptions"/> with the default settings merged in.</returns>
        public GridOptions WithDefaultSettings()
        {
            var gridOptions = this with
            {
                DefaultColDef = new()
                {
                    Resizable = true,
                    CellStyle = new CellStyle { TextAlign = "right" },
                    ValueFormatter = GridConst.DefaultValueFormatter,
                    // TODO: figure out what are this properties. In case following lines are uncommented => all reporting tests are broken (2022/07/18, Andrei Sirotenko)
                    //SuppressMovable = true,
                    //SuppressMenu = true
                },
                DefaultColGroupDef = new() { OpenByDefault = true },
                HeaderHeight = 35,
                RowHeight = 35,
                // TODO V10: this seem to take the length of the original number before ValueFormatter, find proper way for auto size (2021/11/04, Ekaterina Mishina)
                OnGridReady = GridConst.AutoSizeColumns
            };
            return gridOptions;
        }

        /// <summary>Determines value equality, comparing every option including deep/structural comparison of columns and row data.</summary>
        /// <param name="other">The other instance to compare against.</param>
        /// <returns><c>true</c> if all options are equal; otherwise <c>false</c>.</returns>
        public virtual bool Equals(GridOptions? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(other, this))
                return true;

            return (ColumnDefs.SequenceEqual(other.ColumnDefs)) &&
                   RowData.SequenceEqual(other.RowData, JsonObjectEqualityComparer.Instance) &&
                   EqualityComparer<ColDef>.Default.Equals(DefaultColDef, other.DefaultColDef) &&
                   EqualityComparer<ColGroupDef>.Default.Equals(DefaultColGroupDef, other.DefaultColGroupDef) &&
                   EqualityComparer<ColDef>.Default.Equals(AutoGroupColumnDef, other.AutoGroupColumnDef) &&
                   JsonObjectEqualityComparer.Instance.Equals(SideBar, other.SideBar) &&
                   PivotMode == other.PivotMode &&
                   PivotPanelShow == other.PivotPanelShow &&
                   SuppressAggFuncInHeader == other.SuppressAggFuncInHeader &&
                   SuppressAggAtRootLevel == other.SuppressAggAtRootLevel &&
                   SuppressExpandablePivotGroups == other.SuppressExpandablePivotGroups &&
                   PivotSuppressAutoColumn == other.PivotSuppressAutoColumn &&
                   PivotRowTotals == other.PivotRowTotals &&
                   PivotColumnGroupTotals == other.PivotColumnGroupTotals &&
                   GroupIncludeFooter == other.GroupIncludeFooter &&
                   GroupIncludeTotalFooter == other.GroupIncludeTotalFooter &&
                   EnableCharts == other.EnableCharts &&
                   EnableRangeSelection == other.EnableRangeSelection &&
                   GroupDisplayType == other.GroupDisplayType &&
                   GroupDefaultExpanded == other.GroupDefaultExpanded &&
                   ShowOpenedGroup == other.ShowOpenedGroup &&
                   GroupRemoveSingleChildren == other.GroupRemoveSingleChildren &&
                   GroupRemoveLowestSingleChildren == other.GroupRemoveLowestSingleChildren &&
                   Components.SequenceEqual(other.Components) &&
                   TreeData == other.TreeData &&
                   GetDataPath == other.GetDataPath &&
                   (ExcelStyles?.SequenceEqual(other.ExcelStyles ?? Enumerable.Empty<string>()) ?? other.ExcelStyles is null) &&
                   GroupHeaderHeight == other.GroupHeaderHeight &&
                   HeaderHeight == other.HeaderHeight &&
                   RowHeight == other.RowHeight &&
                   SuppressRowHoverHighlight == other.SuppressRowHoverHighlight &&
                   EqualityComparer<CellStyle>.Default.Equals(RowStyle, other.RowStyle) &&
                   GetRowStyle == other.GetRowStyle &&
                   OnGridReady == other.OnGridReady &&
                   ProcessSecondaryColDef == other.ProcessSecondaryColDef &&
                   OnFirstDataRendered == other.OnFirstDataRendered &&
                   ColumnHoverHighlight == other.ColumnHoverHighlight &&
                   DomLayout == other.DomLayout;
        }

        /// <summary>Computes a hash code consistent with <c>Equals</c> across all options.</summary>
        /// <returns>A hash code for this instance.</returns>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(ColumnDefs);
            hash.Add(RowData, JsonObjectEqualityComparer.Instance);
            hash.Add(DefaultColDef);
            hash.Add(DefaultColGroupDef);
            hash.Add(AutoGroupColumnDef);
            hash.Add(SideBar, JsonObjectEqualityComparer.Instance);
            hash.Add(PivotMode);
            hash.Add(PivotPanelShow);
            hash.Add(SuppressAggFuncInHeader);
            hash.Add(SuppressAggAtRootLevel);
            hash.Add(SuppressExpandablePivotGroups);
            hash.Add(PivotSuppressAutoColumn);
            hash.Add(PivotRowTotals);
            hash.Add(PivotColumnGroupTotals);
            hash.Add(GroupIncludeFooter);
            hash.Add(GroupIncludeTotalFooter);
            hash.Add(EnableCharts);
            hash.Add(EnableRangeSelection);
            hash.Add(GroupDisplayType);
            hash.Add(GroupDefaultExpanded);
            hash.Add(ShowOpenedGroup);
            hash.Add(GroupRemoveSingleChildren);
            hash.Add(GroupRemoveLowestSingleChildren);
            hash.Add(Components);
            hash.Add(TreeData);
            hash.Add(GetDataPath);
            hash.Add(ExcelStyles);
            hash.Add(GroupHeaderHeight);
            hash.Add(HeaderHeight);
            hash.Add(RowHeight);
            hash.Add(SuppressRowHoverHighlight);
            hash.Add(RowStyle);
            hash.Add(GetRowStyle);
            hash.Add(OnGridReady);
            hash.Add(ProcessSecondaryColDef);
            hash.Add(OnFirstDataRendered);
            hash.Add(ColumnHoverHighlight);
            hash.Add(DomLayout?.GetHashCode() ?? 0);
            return hash.ToHashCode();
        }
    }
}
