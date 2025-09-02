using System.Collections.Immutable;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.DataGrid;

public record DataGridControl(object Data)
    : UiControl<DataGridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public ImmutableList<object> Columns { get; init; } = [];

    protected override DataGridControl PrepareRendering(RenderingContext context)
    => base.PrepareRendering(context) with
    {
        Style = Style ?? $"min-width: {Columns.Count * 120}px"
    };

    public DataGridControl WithColumn<TColumn>(params DataColumnControl<TColumn>[] columns)
        where TColumn : DataColumnControl<TColumn> => This with { Columns = Columns.AddRange(columns) };

    // Core DataGrid Properties
    public object? Virtualize { get; init; }
    public object? ItemSize { get; init; } = 50;
    public object? ResizableColumns { get; init; } = true;
    public object? GenerateHeader { get; init; } = "Sticky";
    public object? GridTemplateColumns { get; init; }
    public object? Loading { get; init; }
    public object? LoadingTemplate { get; init; }
    public object? EmptyContent { get; init; }
    
    // Selection
    public object? SelectionMode { get; init; }
    public object? SelectedItems { get; init; }
    public object? OnRowFocus { get; init; }
    public object? OnRowClick { get; init; }
    
    // Pagination
    public object? Pagination { get; init; }
    public object? ItemsProvider { get; init; }
    
    // Accessibility
    public object? AriaLabel { get; init; }
    public object? Role { get; init; }
    
    // Row Properties
    public object? RowClass { get; init; }
    public object? RowStyle { get; init; }
    public object? ItemKey { get; init; }
    
    // Column Management
    public object? ShowHover { get; init; } = true;
    public object? TotalItemCount { get; init; }
    public object? AutoFit { get; init; }
    public object? AutoFocus { get; init; }
    public object? AutoItemsPerPage { get; init; }
    public object? ItemsPerPage { get; init; }

    // Fluent Methods for Configuration
    public DataGridControl WithVirtualize(object virtualize) => This with { Virtualize = virtualize };
    public DataGridControl WithItemSize(object itemSize) => This with { ItemSize = itemSize };
    public DataGridControl Resizable(object? resizable = null) => This with { ResizableColumns = resizable ?? true };
    public DataGridControl WithGenerateHeader(object generateHeader) => This with { GenerateHeader = generateHeader };
    public DataGridControl WithGridTemplateColumns(object gridTemplateColumns) => This with { GridTemplateColumns = gridTemplateColumns };
    public DataGridControl WithLoading(object loading) => This with { Loading = loading };
    public DataGridControl WithLoadingTemplate(object loadingTemplate) => This with { LoadingTemplate = loadingTemplate };
    public DataGridControl WithEmptyContent(object emptyContent) => This with { EmptyContent = emptyContent };
    public DataGridControl WithSelectionMode(object selectionMode) => This with { SelectionMode = selectionMode };
    public DataGridControl WithSelectedItems(object selectedItems) => This with { SelectedItems = selectedItems };
    public DataGridControl WithRowFocusHandler(object onRowFocus) => This with { OnRowFocus = onRowFocus };
    public DataGridControl WithRowClickHandler(object onRowClick) => This with { OnRowClick = onRowClick };
    public DataGridControl WithPagination(object pagination) => This with { Pagination = pagination };
    public DataGridControl WithItemsProvider(object itemsProvider) => This with { ItemsProvider = itemsProvider };
    public DataGridControl WithAriaLabel(object ariaLabel) => This with { AriaLabel = ariaLabel };
    public DataGridControl WithRole(object role) => This with { Role = role };
    public DataGridControl WithRowClass(object rowClass) => This with { RowClass = rowClass };
    public DataGridControl WithRowStyle(object rowStyle) => This with { RowStyle = rowStyle };
    public DataGridControl WithItemKey(object itemKey) => This with { ItemKey = itemKey };
    public DataGridControl WithShowHover(object? showHover = null) => This with { ShowHover = showHover ?? true };
    public DataGridControl WithTotalItemCount(object totalItemCount) => This with { TotalItemCount = totalItemCount };
    public DataGridControl WithAutoFit(object? autoFit = null) => This with { AutoFit = autoFit ?? true };
    public DataGridControl WithAutoFocus(object? autoFocus = null) => This with { AutoFocus = autoFocus ?? true };
    public DataGridControl WithAutoItemsPerPage(object? autoItemsPerPage = null) => This with { AutoItemsPerPage = autoItemsPerPage ?? true };
    public DataGridControl WithItemsPerPage(object itemsPerPage) => This with { ItemsPerPage = itemsPerPage };
}

public record ContextProperty(string Property);

public abstract record DataColumnControl<TColumn>() : UiControl<TColumn>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    where TColumn : DataColumnControl<TColumn>
{
    public object? Title { get; init; }
    public object? Tooltip { get; init; }
    public object? Width { get; init; }
    public object? MinWidth { get; init; }
    public object? MaxWidth { get; init; }
    public object? Align { get; init; }
    public object? HeaderAlign { get; init; }
    public object? Resizable { get; init; } = true;
    public object? Sortable { get; init; } = true;
    public object? Filterable { get; init; }
    public object? Visible { get; init; } = true;
    public object? Frozen { get; init; }
    public object? HeaderClass { get; init; }
    public object? HeaderStyle { get; init; }
    public object? CellClass { get; init; }
    public object? CellStyle { get; init; }
    
    public TColumn WithTitle(object title) => This with { Title = title };
    public TColumn WithTooltipText(object tooltipText) => This with { Tooltip = tooltipText };
    public TColumn WithWidth(object width) => This with { Width = width };
    public TColumn WithMinWidth(object minWidth) => This with { MinWidth = minWidth };
    public TColumn WithMaxWidth(object maxWidth) => This with { MaxWidth = maxWidth };
    public TColumn WithAlign(object align) => This with { Align = align };
    public TColumn WithHeaderAlign(object headerAlign) => This with { HeaderAlign = headerAlign };
    public TColumn WithResizable(object? resizable = null) => This with { Resizable = resizable ?? true };
    public TColumn WithSortable(object? sortable = null) => This with { Sortable = sortable ?? true };
    public TColumn WithFilterable(object? filterable = null) => This with { Filterable = filterable ?? true };
    public TColumn WithVisible(object? visible = null) => This with { Visible = visible ?? true };
    public TColumn WithFrozen(object? frozen = null) => This with { Frozen = frozen ?? false };
    public TColumn WithHeaderClass(object headerClass) => This with { HeaderClass = headerClass };
    public TColumn WithHeaderStyle(object headerStyle) => This with { HeaderStyle = headerStyle };
    public TColumn WithCellClass(object cellClass) => This with { CellClass = cellClass };
    public TColumn WithCellStyle(object cellStyle) => This with { CellStyle = cellStyle };
}
public abstract record PropertyColumnControl : DataColumnControl<PropertyColumnControl>
{
    public string? Property { get; init; }
    public object? Format { get; init; }
    public object? IsDefaultSortColumn { get; init; }
    public object? InitialSortDirection { get; init; }
    public object? IsEditable { get; init; }
    public object? SortBy { get; init; }
    public object? PlaceholderText { get; init; }
    public object? StringComparison { get; init; }
    public object? Culture { get; init; }
    
    // Validation properties
    public object? Required { get; init; }
    public object? ValidationPattern { get; init; }
    public object? ValidationMessage { get; init; }
    
    // Editing properties  
    public object? EditTemplate { get; init; }
    public object? ReadOnlyExpression { get; init; }
    
    // Display properties
    public object? DisplayFormat { get; init; }
    public object? NullDisplayText { get; init; }
    public object? ConvertEmptyStringToNull { get; init; } = true;
    public object? HtmlEncode { get; init; } = true;
    
    public PropertyColumnControl WithFormat(object format) => this with { Format = format };
    public PropertyColumnControl WithProperty(string property) => this with { Property = property };
    public PropertyColumnControl WithDefaultSort(object? isDefault = null) => this with { IsDefaultSortColumn = isDefault ?? true };
    public PropertyColumnControl WithInitialSortDirection(object sortDirection) => this with { InitialSortDirection = sortDirection };
    public PropertyColumnControl WithEditable(object? editable = null) => this with { IsEditable = editable ?? true };
    public PropertyColumnControl WithSortBy(object sortBy) => this with { SortBy = sortBy };
    public PropertyColumnControl WithPlaceholderText(object placeholderText) => this with { PlaceholderText = placeholderText };
    public PropertyColumnControl WithStringComparison(object stringComparison) => this with { StringComparison = stringComparison };
    public PropertyColumnControl WithCulture(object culture) => this with { Culture = culture };
    public PropertyColumnControl WithRequired(object? required = null) => this with { Required = required ?? true };
    public PropertyColumnControl WithValidationPattern(object validationPattern) => this with { ValidationPattern = validationPattern };
    public PropertyColumnControl WithValidationMessage(object validationMessage) => this with { ValidationMessage = validationMessage };
    public PropertyColumnControl WithEditTemplate(object editTemplate) => this with { EditTemplate = editTemplate };
    public PropertyColumnControl WithReadOnlyExpression(object readOnlyExpression) => this with { ReadOnlyExpression = readOnlyExpression };
    public PropertyColumnControl WithDisplayFormat(object displayFormat) => this with { DisplayFormat = displayFormat };
    public PropertyColumnControl WithNullDisplayText(object nullDisplayText) => this with { NullDisplayText = nullDisplayText };
    public PropertyColumnControl WithConvertEmptyStringToNull(object? convert = null) => this with { ConvertEmptyStringToNull = convert ?? true };
    public PropertyColumnControl WithHtmlEncode(object? htmlEncode = null) => this with { HtmlEncode = htmlEncode ?? true };
    
    public abstract Type GetPropertyType();
}

public record PropertyColumnControl<TProperty> : PropertyColumnControl
{
    public override Type GetPropertyType() => typeof(TProperty);
}


public record TemplateColumnControl(UiControl Template)
    : DataColumnControl<TemplateColumnControl>
{
    public object? SortBy { get; init; }
    public object? HeaderTemplate { get; init; }
    public object? FilterTemplate { get; init; }
    public object? FooterTemplate { get; init; }
    public object? GroupHeaderTemplate { get; init; }
    public object? IsRowHeader { get; init; }
    
    public TemplateColumnControl WithSortBy(object sortBy) => this with { SortBy = sortBy };
    public TemplateColumnControl WithHeaderTemplate(object headerTemplate) => this with { HeaderTemplate = headerTemplate };
    public TemplateColumnControl WithFilterTemplate(object filterTemplate) => this with { FilterTemplate = filterTemplate };
    public TemplateColumnControl WithFooterTemplate(object footerTemplate) => this with { FooterTemplate = footerTemplate };
    public TemplateColumnControl WithGroupHeaderTemplate(object groupHeaderTemplate) => this with { GroupHeaderTemplate = groupHeaderTemplate };
    public TemplateColumnControl WithRowHeader(object? isRowHeader = null) => this with { IsRowHeader = isRowHeader ?? true };
}

public record DataGridCellClick(object? Item, int Column);
