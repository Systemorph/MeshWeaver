﻿using System.Collections.Immutable;

namespace OpenSmc.GridModel
{
    public record GridOptions
    {
        // ReSharper disable once EmptyConstructor
        public GridOptions()
        {
        }

        // Columns
        public IReadOnlyCollection<ColDef> ColumnDefs { get; init; }
        // Rows: set the data to be displayed as rows in the grid.
        public IReadOnlyCollection<object> RowData { get; init; } = Array.Empty<object>();

        // A default column definition. Set properties for all columns

        public ColDef DefaultColDef { get; init; }

        // A default column group definition
        public ColGroupDef DefaultColGroupDef { get; init; }

        public ColDef AutoGroupColumnDef { get; init; }

        // true to display the side bar with default configuration;
        // 'columns' or 'filters' to display side bar with just one of Columns or Filters tool panels;
        // an object of type SideBarDef(explained below) to allow detailed configuration of the side bar
        public object SideBar { get; init; }

        // Set to true to enable pivot mode.
        public bool? PivotMode { get; init; }

        // When to show the 'pivot panel' (where you drag rows to pivot) at the top. Options: 'never', 'always', 'onlyWhenPivoting'
        public bool? PivotPanelShow { get; init; }
        // When true, column headers won't include the aggFunc name, e.g. 'sum(Bank Balance)' will just be 'Bank Balance'.
        public bool? SuppressAggFuncInHeader { get; init; }
        // When true, the aggregations won't be computed for the root node of the grid
        public bool? SuppressAggAtRootLevel { get; init; }
        // When enabled, pivot column groups will appear 'fixed', without the ability to expand and collapse the column groups.
        public bool? SuppressExpandablePivotGroups { get; init; }
        // If true, the grid will not swap in the grouping column when pivoting.
        public bool? PivotSuppressAutoColumn { get; init; }
        // To enable Pivot Row Totals, declare the following property: gridOption.pivotRowTotals = 'before' | 'after'
        public string PivotRowTotals { get; init; }
        // To enable total columns set gridOptions.pivotColumnGroupTotals = 'before' | 'after'
        public string PivotColumnGroupTotals { get; init; }

        // adds subtotals
        public bool? GroupIncludeFooter { get; init; }
        // includes grand total
        public bool? GroupIncludeTotalFooter { get; init; }

        public bool? EnableCharts { get; init; }
        public bool? EnableRangeSelection { get; init; }

        // Specifies how the results of row grouping should be displayed. The options are:
        // 'singleColumn': single group column automatically added by the grid.
        // 'multipleColumns': a group column per row group is added automatically.
        // 'groupRows': group rows are automatically added instead of group columns.
        // 'custom': informs the grid that group columns will be provided.
        public string GroupDisplayType { get; init; }

        // If grouping, set to the number of levels to expand by default, e.g. 0 for none, 1 for first level only, etc. Set to -1 to expand everything.
        public int? GroupDefaultExpanded { get; init; }
        // Shows the open group in the group column for non-group rows.
        public bool? ShowOpenedGroup { get; init; }
        // Set to true to collapse groups that only have one child.
        public bool? GroupRemoveSingleChildren { get; init; }
        // Set to true to collapse lowest level groups that only have one child.
        public bool? GroupRemoveLowestSingleChildren { get; init; }

        public IImmutableDictionary<string, string> Components { get; init; } = ImmutableDictionary<string, string>.Empty;

        // Set to true to enable the Grid to work with Tree Data. You must also implement the getDataPath(data) callback. It is not possible to do pivot or row grouping while using tree data
        public bool? TreeData { get; init; }
        // When providing tree data to the grid you implement the gridOptions.getDataPath(data) callback to tell the grid the hierarchy for each row. The callback returns back a string[] with each element specifying a level of the tree.
        public string GetDataPath { get; init; }

        public IReadOnlyCollection<string> ExcelStyles { get; init; }

        // Way to reset height of the grid.

        public int? GroupHeaderHeight { get; init; }
        public int? HeaderHeight { get; init; }
        public int? RowHeight { get; init; }

        public bool? SuppressRowHoverHighlight { get; init; }

        public CellStyle RowStyle { get; init; }

        public string GetRowStyle { get; init; }

        public string OnGridReady { get; init; }
        public string ProcessSecondaryColDef { get; init; }
        public string OnFirstDataRendered { get; init; }

        public bool ColumnHoverHighlight { get; init; } = true;
        public string DomLayout { get; set; }

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

    }
}