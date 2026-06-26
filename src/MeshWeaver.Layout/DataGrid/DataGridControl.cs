using System.Collections.Immutable;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.DataGrid;

/// <summary>
/// UI control that renders a data grid (table) bound to a collection.
/// Supports pagination, sorting, filtering, selection, virtualization, and column management.
/// </summary>
/// <param name="Data">The data source to display. Can be a collection, an observable, or a binding expression.</param>
public record DataGridControl(object Data)
    : UiControl<DataGridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The ordered list of column control definitions rendered in the grid.
    /// Defaults to an empty list; add columns via <see cref="WithColumn{TColumn}"/>.
    /// </summary>
    public ImmutableList<object> Columns { get; init; } = [];

    /// <summary>
    /// Applies default rendering preparation; sets a minimum inline width based on column count (120 px per column) when no explicit style is set.
    /// </summary>
    /// <param name="context">The rendering context supplied by the layout engine.</param>
    /// <returns>A copy of this control with rendering-time defaults applied.</returns>
    protected override DataGridControl PrepareRendering(RenderingContext context)
    => base.PrepareRendering(context) with
    {
        Style = Style ?? $"min-width: {Columns.Count * 120}px"
    };

    /// <summary>
    /// Returns a copy of the control with the given <paramref name="columns"/> appended to the column list.
    /// </summary>
    /// <typeparam name="TColumn">The concrete column control type, which must derive from <see cref="DataColumnControl{TColumn}"/>.</typeparam>
    /// <param name="columns">One or more column definitions to add.</param>
    /// <returns>A new <see cref="DataGridControl"/> with the columns appended.</returns>
    public DataGridControl WithColumn<TColumn>(params DataColumnControl<TColumn>[] columns)
        where TColumn : DataColumnControl<TColumn> => This with { Columns = Columns.AddRange(columns) };

    // Core DataGrid Properties
    /// <summary>
    /// When set, enables row virtualization so only visible rows are rendered in the DOM.
    /// Improves performance for large datasets. Accepts a boolean or a binding expression.
    /// </summary>
    public object? Virtualize { get; init; }
    /// <summary>
    /// The pixel height of each row used by the virtualizer to calculate scroll geometry. Defaults to 50.
    /// </summary>
    public object? ItemSize { get; init; } = 50;
    /// <summary>
    /// When true, columns can be resized by dragging their dividers. Defaults to <c>true</c>.
    /// </summary>
    public object? ResizableColumns { get; init; } = true;
    /// <summary>
    /// Controls whether and how the column header row is generated. Defaults to <c>"Sticky"</c>, which keeps the header visible while scrolling.
    /// </summary>
    public object? GenerateHeader { get; init; } = "Sticky";
    /// <summary>
    /// CSS <c>grid-template-columns</c> value used to override the automatically calculated column widths.
    /// </summary>
    public object? GridTemplateColumns { get; init; }
    /// <summary>
    /// When set, indicates that data is currently being loaded and the grid should display a loading indicator.
    /// </summary>
    public object? Loading { get; init; }
    /// <summary>
    /// Custom UI control or template rendered while the grid is in a loading state, replacing the default spinner.
    /// </summary>
    public object? LoadingTemplate { get; init; }
    /// <summary>
    /// Content rendered in place of the grid body when the data source contains no rows.
    /// </summary>
    public object? EmptyContent { get; init; }
    
    // Selection
    /// <summary>
    /// The row-selection mode (e.g. <c>"None"</c>, <c>"Single"</c>, <c>"Multiple"</c>).
    /// </summary>
    public object? SelectionMode { get; init; }
    /// <summary>
    /// The currently selected items; typically bound to a reactive property so the host can track selections.
    /// </summary>
    public object? SelectedItems { get; init; }
    
    // Pagination
    /// <summary>
    /// Pagination state object passed to the underlying grid component to enable paged navigation.
    /// </summary>
    public object? Pagination { get; init; }
    /// <summary>
    /// A delegate or observable that provides items on demand, used when the full dataset is not loaded up front.
    /// </summary>
    public object? ItemsProvider { get; init; }
    /// <summary>
    /// The set of page-size choices shown in the page-size selector. Defaults to <c>{ 5, 10, 25, 50, 100 }</c>.
    /// </summary>
    public object? PageSizeOptions { get; init; } = new[] { 5, 10, 25, 50, 100 };
    /// <summary>
    /// When true, a page-size selector dropdown is shown alongside the pagination controls. Defaults to <c>true</c>.
    /// </summary>
    public object? ShowPageSizeSelector { get; init; } = true;
    
    // Accessibility
    /// <summary>
    /// ARIA label applied to the grid element for screen-reader accessibility.
    /// </summary>
    public object? AriaLabel { get; init; }
    /// <summary>
    /// ARIA role override for the grid element (e.g. <c>"grid"</c>, <c>"treegrid"</c>).
    /// </summary>
    public object? Role { get; init; }
    
    // Row Properties
    /// <summary>
    /// CSS class name(s) applied to each data row. Accepts a string or a row-contextual binding expression.
    /// </summary>
    public object? RowClass { get; init; }
    /// <summary>
    /// Inline CSS style applied to each data row. Accepts a string or a row-contextual binding expression.
    /// </summary>
    public object? RowStyle { get; init; }
    
    // Column Management
    /// <summary>
    /// When true, rows are highlighted on mouse hover. Defaults to <c>true</c>.
    /// </summary>
    public object? ShowHover { get; init; } = true;
    /// <summary>
    /// When true, columns automatically size to fit their content.
    /// </summary>
    public object? AutoFit { get; init; }
    /// <summary>
    /// When true, the grid claims focus on initial render.
    /// </summary>
    public object? AutoFocus { get; init; }
    /// <summary>
    /// When true, the page size is automatically adjusted to fit the available vertical space.
    /// </summary>
    public object? AutoItemsPerPage { get; init; }
    /// <summary>
    /// The fixed number of rows displayed per page when pagination is enabled.
    /// </summary>
    public object? ItemsPerPage { get; init; }

    // Fluent Methods for Configuration
    /// <summary>Returns a copy of the control with <paramref name="virtualize"/> as its <see cref="Virtualize"/> value.</summary>
    /// <param name="virtualize">The virtualization setting to apply.</param>
    public DataGridControl WithVirtualize(object virtualize) => This with { Virtualize = virtualize };
    /// <summary>Returns a copy of the control with <paramref name="itemSize"/> as the row height used by the virtualizer.</summary>
    /// <param name="itemSize">Row height in pixels.</param>
    public DataGridControl WithItemSize(object itemSize) => This with { ItemSize = itemSize };
    /// <summary>Returns a copy of the control with column resizing enabled or set to <paramref name="resizable"/>. Defaults to <c>true</c>.</summary>
    /// <param name="resizable">The resizable value; defaults to <c>true</c> when omitted.</param>
    public DataGridControl Resizable(object? resizable = null) => This with { ResizableColumns = resizable ?? true };
    /// <summary>Returns a copy of the control with <paramref name="generateHeader"/> as the header-generation mode.</summary>
    /// <param name="generateHeader">Header generation mode (e.g. <c>"Sticky"</c>).</param>
    public DataGridControl WithGenerateHeader(object generateHeader) => This with { GenerateHeader = generateHeader };
    /// <summary>Returns a copy of the control with <paramref name="gridTemplateColumns"/> as the CSS grid-template-columns override.</summary>
    /// <param name="gridTemplateColumns">CSS grid-template-columns value.</param>
    public DataGridControl WithGridTemplateColumns(object gridTemplateColumns) => This with { GridTemplateColumns = gridTemplateColumns };
    /// <summary>Returns a copy of the control with <paramref name="loading"/> as its loading indicator state.</summary>
    /// <param name="loading">The loading state value.</param>
    public DataGridControl WithLoading(object loading) => This with { Loading = loading };
    /// <summary>Returns a copy of the control with <paramref name="loadingTemplate"/> as the custom loading template.</summary>
    /// <param name="loadingTemplate">The UI control or template to show while loading.</param>
    public DataGridControl WithLoadingTemplate(object loadingTemplate) => This with { LoadingTemplate = loadingTemplate };
    /// <summary>Returns a copy of the control with <paramref name="emptyContent"/> displayed when the data source is empty.</summary>
    /// <param name="emptyContent">The content to render for an empty grid.</param>
    public DataGridControl WithEmptyContent(object emptyContent) => This with { EmptyContent = emptyContent };
    /// <summary>Returns a copy of the control with <paramref name="selectionMode"/> as the row-selection mode.</summary>
    /// <param name="selectionMode">The selection mode (e.g. <c>"Single"</c>, <c>"Multiple"</c>).</param>
    public DataGridControl WithSelectionMode(object selectionMode) => This with { SelectionMode = selectionMode };
    /// <summary>Returns a copy of the control with <paramref name="selectedItems"/> as the selected-items binding.</summary>
    /// <param name="selectedItems">The selected items value or binding.</param>
    public DataGridControl WithSelectedItems(object selectedItems) => This with { SelectedItems = selectedItems };
    /// <summary>Returns a copy of the control with <paramref name="pagination"/> as the pagination state object.</summary>
    /// <param name="pagination">The pagination state to apply.</param>
    public DataGridControl WithPagination(object pagination) => This with { Pagination = pagination };
    /// <summary>Returns a copy of the control with <paramref name="itemsProvider"/> as the on-demand items provider.</summary>
    /// <param name="itemsProvider">The items-provider delegate or observable.</param>
    public DataGridControl WithItemsProvider(object itemsProvider) => This with { ItemsProvider = itemsProvider };
    /// <summary>Returns a copy of the control with <paramref name="ariaLabel"/> as the ARIA label for accessibility.</summary>
    /// <param name="ariaLabel">The ARIA label text.</param>
    public DataGridControl WithAriaLabel(object ariaLabel) => This with { AriaLabel = ariaLabel };
    /// <summary>Returns a copy of the control with <paramref name="role"/> as the ARIA role override.</summary>
    /// <param name="role">The ARIA role value.</param>
    public DataGridControl WithRole(object role) => This with { Role = role };
    /// <summary>Returns a copy of the control with <paramref name="rowClass"/> as the per-row CSS class expression.</summary>
    /// <param name="rowClass">The CSS class name or binding expression for rows.</param>
    public DataGridControl WithRowClass(object rowClass) => This with { RowClass = rowClass };
    /// <summary>Returns a copy of the control with <paramref name="rowStyle"/> as the per-row inline style expression.</summary>
    /// <param name="rowStyle">The inline CSS style or binding expression for rows.</param>
    public DataGridControl WithRowStyle(object rowStyle) => This with { RowStyle = rowStyle };
    /// <summary>Returns a copy of the control with hover highlighting enabled or set to <paramref name="showHover"/>. Defaults to <c>true</c>.</summary>
    /// <param name="showHover">The hover highlight setting; defaults to <c>true</c> when omitted.</param>
    public DataGridControl WithShowHover(object? showHover = null) => This with { ShowHover = showHover ?? true };
    /// <summary>Returns a copy of the control with auto-fit columns enabled or set to <paramref name="autoFit"/>. Defaults to <c>true</c>.</summary>
    /// <param name="autoFit">The auto-fit setting; defaults to <c>true</c> when omitted.</param>
    public DataGridControl WithAutoFit(object? autoFit = null) => This with { AutoFit = autoFit ?? true };
    /// <summary>Returns a copy of the control with auto-focus enabled or set to <paramref name="autoFocus"/>. Defaults to <c>true</c>.</summary>
    /// <param name="autoFocus">The auto-focus setting; defaults to <c>true</c> when omitted.</param>
    public DataGridControl WithAutoFocus(object? autoFocus = null) => This with { AutoFocus = autoFocus ?? true };
    /// <summary>Returns a copy of the control with automatic items-per-page sizing enabled or set to <paramref name="autoItemsPerPage"/>. Defaults to <c>true</c>.</summary>
    /// <param name="autoItemsPerPage">The auto-items-per-page setting; defaults to <c>true</c> when omitted.</param>
    public DataGridControl WithAutoItemsPerPage(object? autoItemsPerPage = null) => This with { AutoItemsPerPage = autoItemsPerPage ?? true };
    /// <summary>Returns a copy of the control with <paramref name="itemsPerPage"/> as the fixed page size.</summary>
    /// <param name="itemsPerPage">The number of rows per page.</param>
    public DataGridControl WithItemsPerPage(object itemsPerPage) => This with { ItemsPerPage = itemsPerPage };
    /// <summary>Returns a copy of the control with <paramref name="pageSizeOptions"/> as the page-size selector choices.</summary>
    /// <param name="pageSizeOptions">An array or binding of integer page-size values.</param>
    public DataGridControl WithPageSizeOptions(object pageSizeOptions) => This with { PageSizeOptions = pageSizeOptions };
    /// <summary>Returns a copy of the control with the page-size selector shown or set to <paramref name="showPageSizeSelector"/>. Defaults to <c>true</c>.</summary>
    /// <param name="showPageSizeSelector">Whether to display the page-size selector; defaults to <c>true</c> when omitted.</param>
    public DataGridControl WithShowPageSizeSelector(object? showPageSizeSelector = null) => This with { ShowPageSizeSelector = showPageSizeSelector ?? true };
}

/// <summary>
/// Identifies a property on the row data object to use as a column binding or context reference.
/// </summary>
/// <param name="Property">The name of the property on the row type.</param>
public record ContextProperty(string Property);

/// <summary>
/// Abstract base record for all data-grid column controls, providing common layout, style, and behavior options.
/// </summary>
/// <typeparam name="TColumn">The concrete column type; used so fluent <c>WithXxx</c> methods return the derived type.</typeparam>
public abstract record DataColumnControl<TColumn>() : UiControl<TColumn>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    where TColumn : DataColumnControl<TColumn>
{
    /// <summary>Column header text displayed in the header row.</summary>
    public object? Title { get; init; }
    /// <summary>Tooltip text shown when the user hovers over the column header.</summary>
    public object? Tooltip { get; init; }
    /// <summary>Explicit column width (e.g. <c>"200px"</c> or <c>"20%"</c>). When null the grid calculates width automatically.</summary>
    public object? Width { get; init; }
    /// <summary>Minimum column width to enforce when the user resizes columns.</summary>
    public object? MinWidth { get; init; }
    /// <summary>Maximum column width to enforce when the user resizes columns.</summary>
    public object? MaxWidth { get; init; }
    /// <summary>Horizontal alignment of cell content (e.g. <c>"left"</c>, <c>"center"</c>, <c>"right"</c>).</summary>
    public object? Align { get; init; }
    /// <summary>Horizontal alignment of the column header text.</summary>
    public object? HeaderAlign { get; init; }
    /// <summary>When true, the user can drag the column divider to resize this column. Defaults to <c>true</c>.</summary>
    public object? Resizable { get; init; } = true;
    /// <summary>When true, clicking the column header sorts the grid by this column. Defaults to <c>true</c>.</summary>
    public object? Sortable { get; init; } = true;
    /// <summary>When true, a filter input is rendered for this column, allowing the user to filter rows.</summary>
    public object? Filterable { get; init; }
    /// <summary>When true, the column is visible. Set to false to hide the column while keeping it in the definition. Defaults to <c>true</c>.</summary>
    public object? Visible { get; init; } = true;
    /// <summary>When true, the column is frozen (sticky) at the left edge while the grid scrolls horizontally.</summary>
    public object? Frozen { get; init; }
    /// <summary>CSS class name(s) applied to the column header cell.</summary>
    public object? HeaderClass { get; init; }
    /// <summary>Inline CSS style applied to the column header cell.</summary>
    public object? HeaderStyle { get; init; }
    /// <summary>CSS class name(s) applied to every data cell in this column.</summary>
    public object? CellClass { get; init; }
    /// <summary>Inline CSS style applied to every data cell in this column.</summary>
    public object? CellStyle { get; init; }
    
    /// <summary>Returns a copy of the column with <paramref name="title"/> as the header text.</summary>
    /// <param name="title">The header label to display.</param>
    public TColumn WithTitle(object title) => This with { Title = title };
    /// <summary>Returns a copy of the column with <paramref name="tooltipText"/> as the header tooltip.</summary>
    /// <param name="tooltipText">The tooltip text shown on header hover.</param>
    public TColumn WithTooltipText(object tooltipText) => This with { Tooltip = tooltipText };
    /// <summary>Returns a copy of the column with <paramref name="width"/> as the explicit column width.</summary>
    /// <param name="width">The CSS width value (e.g. <c>"150px"</c>).</param>
    public TColumn WithWidth(object width) => This with { Width = width };
    /// <summary>Returns a copy of the column with <paramref name="minWidth"/> as the minimum column width.</summary>
    /// <param name="minWidth">The CSS minimum-width value.</param>
    public TColumn WithMinWidth(object minWidth) => This with { MinWidth = minWidth };
    /// <summary>Returns a copy of the column with <paramref name="maxWidth"/> as the maximum column width.</summary>
    /// <param name="maxWidth">The CSS maximum-width value.</param>
    public TColumn WithMaxWidth(object maxWidth) => This with { MaxWidth = maxWidth };
    /// <summary>Returns a copy of the column with <paramref name="align"/> as the cell content alignment.</summary>
    /// <param name="align">Alignment value (e.g. <c>"left"</c>, <c>"center"</c>, <c>"right"</c>).</param>
    public TColumn WithAlign(object align) => This with { Align = align };
    /// <summary>Returns a copy of the column with <paramref name="headerAlign"/> as the header text alignment.</summary>
    /// <param name="headerAlign">Alignment value for the header cell.</param>
    public TColumn WithHeaderAlign(object headerAlign) => This with { HeaderAlign = headerAlign };
    /// <summary>Returns a copy of the column with resizing enabled or set to <paramref name="resizable"/>. Defaults to <c>true</c>.</summary>
    /// <param name="resizable">Whether the column is resizable; defaults to <c>true</c> when omitted.</param>
    public TColumn WithResizable(object? resizable = null) => This with { Resizable = resizable ?? true };
    /// <summary>Returns a copy of the column with sorting enabled or set to <paramref name="sortable"/>. Defaults to <c>true</c>.</summary>
    /// <param name="sortable">Whether clicking the header sorts the grid; defaults to <c>true</c> when omitted.</param>
    public TColumn WithSortable(object? sortable = null) => This with { Sortable = sortable ?? true };
    /// <summary>Returns a copy of the column with filtering enabled or set to <paramref name="filterable"/>. Defaults to <c>true</c>.</summary>
    /// <param name="filterable">Whether a filter input is shown for this column; defaults to <c>true</c> when omitted.</param>
    public TColumn WithFilterable(object? filterable = null) => This with { Filterable = filterable ?? true };
    /// <summary>Returns a copy of the column with visibility set to <paramref name="visible"/>. Defaults to <c>true</c>.</summary>
    /// <param name="visible">Whether the column is visible; defaults to <c>true</c> when omitted.</param>
    public TColumn WithVisible(object? visible = null) => This with { Visible = visible ?? true };
    /// <summary>Returns a copy of the column with frozen (sticky) state set to <paramref name="frozen"/>. Defaults to <c>false</c>.</summary>
    /// <param name="frozen">Whether the column is frozen at the left edge; defaults to <c>false</c> when omitted.</param>
    public TColumn WithFrozen(object? frozen = null) => This with { Frozen = frozen ?? false };
    /// <summary>Returns a copy of the column with <paramref name="headerClass"/> as the header cell CSS class.</summary>
    /// <param name="headerClass">CSS class name(s) for the header cell.</param>
    public TColumn WithHeaderClass(object headerClass) => This with { HeaderClass = headerClass };
    /// <summary>Returns a copy of the column with <paramref name="headerStyle"/> as the header cell inline style.</summary>
    /// <param name="headerStyle">Inline CSS style for the header cell.</param>
    public TColumn WithHeaderStyle(object headerStyle) => This with { HeaderStyle = headerStyle };
    /// <summary>Returns a copy of the column with <paramref name="cellClass"/> as the data cell CSS class.</summary>
    /// <param name="cellClass">CSS class name(s) applied to every data cell in this column.</param>
    public TColumn WithCellClass(object cellClass) => This with { CellClass = cellClass };
    /// <summary>Returns a copy of the column with <paramref name="cellStyle"/> as the data cell inline style.</summary>
    /// <param name="cellStyle">Inline CSS style applied to every data cell in this column.</param>
    public TColumn WithCellStyle(object cellStyle) => This with { CellStyle = cellStyle };
}
/// <summary>
/// Abstract base for data-grid columns that bind to a named property of the row object,
/// supporting formatting, sorting, editing, validation, and display customization.
/// </summary>
public abstract record PropertyColumnControl : DataColumnControl<PropertyColumnControl>
{
    /// <summary>Name of the property on the row data object that this column displays and edits.</summary>
    public string? Property { get; init; }
    /// <summary>Format string (e.g. <c>"N2"</c>, <c>"yyyy-MM-dd"</c>) applied when rendering the cell value.</summary>
    public object? Format { get; init; }
    /// <summary>When true, this column is the default sort column when the grid first renders.</summary>
    public object? IsDefaultSortColumn { get; init; }
    /// <summary>The initial sort direction (<c>"Ascending"</c> or <c>"Descending"</c>) applied when <see cref="IsDefaultSortColumn"/> is true.</summary>
    public object? InitialSortDirection { get; init; }
    /// <summary>When true, cells in this column can be edited inline by the user.</summary>
    public object? IsEditable { get; init; }
    /// <summary>A key selector or comparer used when sorting by this column, overriding the default property comparison.</summary>
    public object? SortBy { get; init; }
    /// <summary>Placeholder text displayed in the edit input when the cell value is empty.</summary>
    public object? PlaceholderText { get; init; }
    /// <summary>The <c>StringComparison</c> mode used when sorting or filtering string values in this column.</summary>
    public object? StringComparison { get; init; }
    /// <summary>The culture used for parsing and formatting numeric or date values in this column.</summary>
    public object? Culture { get; init; }
    
    // Validation properties
    /// <summary>When true, the cell edit field is marked as required and blocks saving when empty.</summary>
    public object? Required { get; init; }
    /// <summary>Regular-expression pattern the edited value must match for validation to pass.</summary>
    public object? ValidationPattern { get; init; }
    /// <summary>Error message displayed when the edited value fails validation.</summary>
    public object? ValidationMessage { get; init; }
    
    // Editing properties  
    /// <summary>Custom UI control or template used for the cell edit input instead of the default text field.</summary>
    public object? EditTemplate { get; init; }
    /// <summary>Expression evaluated per row that, when true, renders the cell as read-only even when <see cref="IsEditable"/> is set.</summary>
    public object? ReadOnlyExpression { get; init; }
    
    // Display properties
    /// <summary>Composite format string for display (e.g. <c>"{0:C2}"</c>), distinct from the raw <see cref="Format"/> applied during binding.</summary>
    public object? DisplayFormat { get; init; }
    /// <summary>Text shown in the cell when the property value is null.</summary>
    public object? NullDisplayText { get; init; }
    /// <summary>When true, an empty string entered by the user is treated as null before saving. Defaults to <c>true</c>.</summary>
    public object? ConvertEmptyStringToNull { get; init; } = true;
    /// <summary>When true, the cell value is HTML-encoded before rendering to prevent XSS. Defaults to <c>true</c>.</summary>
    public object? HtmlEncode { get; init; } = true;
    
    /// <summary>Returns a copy of the column with <paramref name="format"/> as the cell-value format string.</summary>
    /// <param name="format">The format string (e.g. <c>"N2"</c>).</param>
    public PropertyColumnControl WithFormat(object format) => this with { Format = format };
    /// <summary>Returns a copy of the column with <paramref name="property"/> as the bound property name.</summary>
    /// <param name="property">The name of the property on the row type to bind.</param>
    public PropertyColumnControl WithProperty(string property) => this with { Property = property };
    /// <summary>Returns a copy of the column designated as the default sort column, optionally with <paramref name="isDefault"/>. Defaults to <c>true</c>.</summary>
    /// <param name="isDefault">Whether this column is the default sort column; defaults to <c>true</c> when omitted.</param>
    public PropertyColumnControl WithDefaultSort(object? isDefault = null) => this with { IsDefaultSortColumn = isDefault ?? true };
    /// <summary>Returns a copy of the column with <paramref name="sortDirection"/> as the initial sort direction.</summary>
    /// <param name="sortDirection">The sort direction (e.g. <c>"Ascending"</c>).</param>
    public PropertyColumnControl WithInitialSortDirection(object sortDirection) => this with { InitialSortDirection = sortDirection };
    /// <summary>Returns a copy of the column with inline editing enabled or set to <paramref name="editable"/>. Defaults to <c>true</c>.</summary>
    /// <param name="editable">Whether cells are editable; defaults to <c>true</c> when omitted.</param>
    public PropertyColumnControl WithEditable(object? editable = null) => this with { IsEditable = editable ?? true };
    /// <summary>Returns a copy of the column with <paramref name="sortBy"/> as the sort key selector.</summary>
    /// <param name="sortBy">The sort key selector or comparer.</param>
    public PropertyColumnControl WithSortBy(object sortBy) => this with { SortBy = sortBy };
    /// <summary>Returns a copy of the column with <paramref name="placeholderText"/> as the edit-input placeholder.</summary>
    /// <param name="placeholderText">Placeholder text for the cell editor.</param>
    public PropertyColumnControl WithPlaceholderText(object placeholderText) => this with { PlaceholderText = placeholderText };
    /// <summary>Returns a copy of the column with <paramref name="stringComparison"/> as the string comparison mode.</summary>
    /// <param name="stringComparison">The <c>StringComparison</c> value to use.</param>
    public PropertyColumnControl WithStringComparison(object stringComparison) => this with { StringComparison = stringComparison };
    /// <summary>Returns a copy of the column with <paramref name="culture"/> as the formatting culture.</summary>
    /// <param name="culture">The culture info (or culture name string) for formatting and parsing.</param>
    public PropertyColumnControl WithCulture(object culture) => this with { Culture = culture };
    /// <summary>Returns a copy of the column with the required validation constraint enabled or set to <paramref name="required"/>. Defaults to <c>true</c>.</summary>
    /// <param name="required">Whether the field is required; defaults to <c>true</c> when omitted.</param>
    public PropertyColumnControl WithRequired(object? required = null) => this with { Required = required ?? true };
    /// <summary>Returns a copy of the column with <paramref name="validationPattern"/> as the regex validation pattern.</summary>
    /// <param name="validationPattern">The regular-expression pattern the edited value must match.</param>
    public PropertyColumnControl WithValidationPattern(object validationPattern) => this with { ValidationPattern = validationPattern };
    /// <summary>Returns a copy of the column with <paramref name="validationMessage"/> as the validation error message.</summary>
    /// <param name="validationMessage">The error message shown on validation failure.</param>
    public PropertyColumnControl WithValidationMessage(object validationMessage) => this with { ValidationMessage = validationMessage };
    /// <summary>Returns a copy of the column with <paramref name="editTemplate"/> as the custom edit-cell template.</summary>
    /// <param name="editTemplate">The UI control or template used for editing cells in this column.</param>
    public PropertyColumnControl WithEditTemplate(object editTemplate) => this with { EditTemplate = editTemplate };
    /// <summary>Returns a copy of the column with <paramref name="readOnlyExpression"/> as the per-row read-only condition.</summary>
    /// <param name="readOnlyExpression">An expression evaluated per row; when true, the cell is read-only.</param>
    public PropertyColumnControl WithReadOnlyExpression(object readOnlyExpression) => this with { ReadOnlyExpression = readOnlyExpression };
    /// <summary>Returns a copy of the column with <paramref name="displayFormat"/> as the display composite format string.</summary>
    /// <param name="displayFormat">The composite format string (e.g. <c>"{0:C2}"</c>) for rendering cell values.</param>
    public PropertyColumnControl WithDisplayFormat(object displayFormat) => this with { DisplayFormat = displayFormat };
    /// <summary>Returns a copy of the column with <paramref name="nullDisplayText"/> as the text shown for null values.</summary>
    /// <param name="nullDisplayText">Text displayed when the property value is null.</param>
    public PropertyColumnControl WithNullDisplayText(object nullDisplayText) => this with { NullDisplayText = nullDisplayText };
    /// <summary>Returns a copy of the column with empty-string-to-null conversion enabled or set to <paramref name="convert"/>. Defaults to <c>true</c>.</summary>
    /// <param name="convert">Whether empty strings are treated as null; defaults to <c>true</c> when omitted.</param>
    public PropertyColumnControl WithConvertEmptyStringToNull(object? convert = null) => this with { ConvertEmptyStringToNull = convert ?? true };
    /// <summary>Returns a copy of the column with HTML encoding enabled or set to <paramref name="htmlEncode"/>. Defaults to <c>true</c>.</summary>
    /// <param name="htmlEncode">Whether cell values are HTML-encoded before rendering; defaults to <c>true</c> when omitted.</param>
    public PropertyColumnControl WithHtmlEncode(object? htmlEncode = null) => this with { HtmlEncode = htmlEncode ?? true };
    
    /// <summary>Returns the CLR type of the property this column is bound to, used by the grid to select an appropriate editor and formatter.</summary>
    /// <returns>The <see cref="Type"/> of the bound property.</returns>
    public abstract Type GetPropertyType();
}

/// <summary>
/// A strongly-typed property column that binds to a row property of type <typeparamref name="TProperty"/>.
/// The type parameter determines the editor, formatter, and sort comparer chosen by the grid.
/// </summary>
/// <typeparam name="TProperty">The CLR type of the property this column displays and edits.</typeparam>
public record PropertyColumnControl<TProperty> : PropertyColumnControl
{
    /// <summary>Returns <c>typeof(<typeparamref name="TProperty"/>)</c> so the grid can select the appropriate editor and formatter.</summary>
    /// <returns>The CLR type of the bound property.</returns>
    public override Type GetPropertyType() => typeof(TProperty);
}


/// <summary>
/// A data-grid column that renders cells and headers using fully custom UI control templates,
/// rather than binding to a named property.
/// </summary>
/// <param name="Template">The UI control used to render each data cell in this column.</param>
public record TemplateColumnControl(UiControl Template)
    : DataColumnControl<TemplateColumnControl>
{
    /// <summary>A key selector or comparer used when the user sorts by this column.</summary>
    public object? SortBy { get; init; }
    /// <summary>Custom UI control or template rendered in the column header, replacing the default title text.</summary>
    public object? HeaderTemplate { get; init; }
    /// <summary>Custom UI control or template rendered in the column filter row.</summary>
    public object? FilterTemplate { get; init; }
    /// <summary>Custom UI control or template rendered in the column footer row.</summary>
    public object? FooterTemplate { get; init; }
    /// <summary>Custom UI control or template rendered in the group header row for this column when grouping is active.</summary>
    public object? GroupHeaderTemplate { get; init; }
    /// <summary>When true, this column is designated as the row header for accessibility, giving its cells the <c>rowheader</c> ARIA role.</summary>
    public object? IsRowHeader { get; init; }
    
    /// <summary>Returns a copy of the column with <paramref name="sortBy"/> as the sort key selector.</summary>
    /// <param name="sortBy">The sort key selector or comparer for this template column.</param>
    public TemplateColumnControl WithSortBy(object sortBy) => this with { SortBy = sortBy };
    /// <summary>Returns a copy of the column with <paramref name="headerTemplate"/> as the custom header template.</summary>
    /// <param name="headerTemplate">The UI control or template rendered in the header cell.</param>
    public TemplateColumnControl WithHeaderTemplate(object headerTemplate) => this with { HeaderTemplate = headerTemplate };
    /// <summary>Returns a copy of the column with <paramref name="filterTemplate"/> as the custom filter-row template.</summary>
    /// <param name="filterTemplate">The UI control or template rendered in the filter row for this column.</param>
    public TemplateColumnControl WithFilterTemplate(object filterTemplate) => this with { FilterTemplate = filterTemplate };
    /// <summary>Returns a copy of the column with <paramref name="footerTemplate"/> as the custom footer template.</summary>
    /// <param name="footerTemplate">The UI control or template rendered in the footer cell.</param>
    public TemplateColumnControl WithFooterTemplate(object footerTemplate) => this with { FooterTemplate = footerTemplate };
    /// <summary>Returns a copy of the column with <paramref name="groupHeaderTemplate"/> as the group-header template.</summary>
    /// <param name="groupHeaderTemplate">The UI control or template rendered in the group header for this column.</param>
    public TemplateColumnControl WithGroupHeaderTemplate(object groupHeaderTemplate) => this with { GroupHeaderTemplate = groupHeaderTemplate };
    /// <summary>Returns a copy of the column with the row-header designation enabled or set to <paramref name="isRowHeader"/>. Defaults to <c>true</c>.</summary>
    /// <param name="isRowHeader">Whether this column is the row header; defaults to <c>true</c> when omitted.</param>
    public TemplateColumnControl WithRowHeader(object? isRowHeader = null) => this with { IsRowHeader = isRowHeader ?? true };
}

/// <summary>
/// Event payload raised when the user clicks a cell in the data grid.
/// </summary>
/// <param name="Item">The row data object corresponding to the clicked cell, or null if no item is associated.</param>
/// <param name="Column">The zero-based index of the column that was clicked.</param>
public record DataGridCellClick(object? Item, int Column);
