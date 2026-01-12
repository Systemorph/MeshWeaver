---
Name: Available Controls
Category: Documentation
Description: Complete reference for MeshWeaver UI controls including layout, input, display, and navigation components
Icon: /static/storage/content/MeshWeaver/Documentation/Architecture/UserInterface/AvailableControls/icon.svg
---

# Available Controls Reference

MeshWeaver provides a comprehensive library of UI controls defined server-side and rendered in the browser. All controls are created using the `Controls` factory class.

## Layout Controls

Controls for organizing and structuring UI content.

### StackControl

Arranges children vertically or horizontally.

```csharp
Controls.Stack()
    .WithOrientation(Orientation.Vertical)
    .WithChildren(
        Controls.Text("Item 1"),
        Controls.Text("Item 2")
    )
```

| Property | Type | Description |
|----------|------|-------------|
| `Orientation` | `Orientation` | Vertical or Horizontal layout |
| `Spacing` | `string` | Gap between children (CSS value) |
| `Children` | `IEnumerable<UiControl>` | Child controls |

### GridControl

CSS Grid-based layout for complex arrangements.

```csharp
Controls.Grid()
    .WithColumns("1fr 2fr 1fr")
    .WithRows("auto 1fr auto")
    .WithChildren(
        Controls.Text("Header").WithGridArea("1 / 1 / 2 / 4"),
        Controls.Text("Sidebar").WithGridArea("2 / 1 / 3 / 2"),
        Controls.Text("Content").WithGridArea("2 / 2 / 3 / 3")
    )
```

| Property | Type | Description |
|----------|------|-------------|
| `Columns` | `string` | CSS grid-template-columns |
| `Rows` | `string` | CSS grid-template-rows |
| `Gap` | `string` | Gap between grid cells |

### LayoutAreaControl

Defines a named region that can be updated independently.

```csharp
Controls.LayoutArea("detail-panel")
    .WithView(context => RenderDetailView(context))
```

| Property | Type | Description |
|----------|------|-------------|
| `Area` | `string` | Unique area identifier |
| `View` | `Func<LayoutAreaContext, UiControl>` | View rendering function |

## Input Controls

Controls for user data entry.

### TextFieldControl

Single-line text input with validation.

```csharp
Controls.TextField("username")
    .WithLabel("Username")
    .WithPlaceholder("Enter username")
    .WithRequired(true)
    .WithMaxLength(50)
```

| Property | Type | Description |
|----------|------|-------------|
| `DataContext` | `string` | Binding path for the value |
| `Label` | `string` | Field label |
| `Placeholder` | `string` | Placeholder text |
| `Required` | `bool` | Whether field is required |
| `MaxLength` | `int?` | Maximum character length |
| `Disabled` | `bool` | Disable input |

### NumberFieldControl

Numeric input with formatting and validation.

```csharp
Controls.NumberField("amount")
    .WithLabel("Amount")
    .WithMin(0)
    .WithMax(1000000)
    .WithStep(0.01)
    .WithFormat("N2")
```

| Property | Type | Description |
|----------|------|-------------|
| `Min` | `double?` | Minimum value |
| `Max` | `double?` | Maximum value |
| `Step` | `double?` | Increment step |
| `Format` | `string` | Number format string |

### SelectControl

Dropdown selection from a list of options.

```csharp
Controls.Select("status")
    .WithLabel("Status")
    .WithOptions(new[] {
        new SelectOption("draft", "Draft"),
        new SelectOption("active", "Active"),
        new SelectOption("closed", "Closed")
    })
```

| Property | Type | Description |
|----------|------|-------------|
| `Options` | `IEnumerable<SelectOption>` | Available choices |
| `Multiple` | `bool` | Allow multiple selection |
| `Searchable` | `bool` | Enable search filtering |

### CheckboxControl

Boolean toggle input.

```csharp
Controls.Checkbox("agreed")
    .WithLabel("I agree to the terms")
```

### DatePickerControl

Date selection with calendar popup.

```csharp
Controls.DatePicker("startDate")
    .WithLabel("Start Date")
    .WithMinDate(DateTime.Today)
    .WithFormat("yyyy-MM-dd")
```

| Property | Type | Description |
|----------|------|-------------|
| `MinDate` | `DateTime?` | Earliest selectable date |
| `MaxDate` | `DateTime?` | Latest selectable date |
| `Format` | `string` | Date display format |

## Display Controls

Controls for presenting data.

### TextControl

Text display with optional styling.

```csharp
Controls.Text("Welcome to MeshWeaver")
    .WithTypography(Typography.H1)
    .WithColor("primary")
```

| Property | Type | Description |
|----------|------|-------------|
| `Content` | `string` | Text content |
| `Typography` | `Typography` | Text style (H1-H6, Body, Caption) |
| `Color` | `string` | Text color |

### DataGridControl

Tabular data display with sorting, filtering, and pagination.

```csharp
Controls.DataGrid(claimsData)
    .WithColumns(
        Column.For("claimNumber").WithTitle("Claim #"),
        Column.For("status").WithTitle("Status"),
        Column.For("amount").WithTitle("Amount").WithFormat("C2")
    )
    .WithPaging(pageSize: 25)
    .WithSorting(true)
    .WithRowClick(OnRowClick)
```

| Property | Type | Description |
|----------|------|-------------|
| `Data` | `object` | Data source (collection) |
| `Columns` | `IEnumerable<Column>` | Column definitions |
| `PageSize` | `int` | Rows per page |
| `Sortable` | `bool` | Enable column sorting |
| `Filterable` | `bool` | Enable column filtering |

### CardControl

Content container with optional header and actions.

```csharp
Controls.Card()
    .WithTitle("Claim Summary")
    .WithSubtitle("CLM-2024-001")
    .WithContent(
        Controls.Stack().WithChildren(...)
    )
    .WithActions(
        Controls.Button("Edit").WithClickAction(OnEdit)
    )
```

### ChartControl

Data visualization with various chart types.

```csharp
Controls.Chart()
    .WithType(ChartType.Bar)
    .WithData(salesData)
    .WithXAxis("month")
    .WithYAxis("revenue")
```

| Property | Type | Description |
|----------|------|-------------|
| `Type` | `ChartType` | Bar, Line, Pie, Area, etc. |
| `Data` | `object` | Chart data source |
| `XAxis` | `string` | X-axis data field |
| `YAxis` | `string` | Y-axis data field |

## Action Controls

Controls for user interactions.

### ButtonControl

Clickable button with action handler.

```csharp
Controls.Button("Submit")
    .WithVariant(ButtonVariant.Primary)
    .WithIcon("Send")
    .WithClickAction(async context => {
        await context.Hub.Post(new SubmitRequest());
    })
```

| Property | Type | Description |
|----------|------|-------------|
| `Label` | `string` | Button text |
| `Variant` | `ButtonVariant` | Primary, Secondary, Outlined, Text |
| `Icon` | `string` | Icon name |
| `Disabled` | `bool` | Disable button |
| `ClickAction` | `Func<ClickContext, Task>` | Click handler |

### IconButtonControl

Icon-only button for compact actions.

```csharp
Controls.IconButton("Delete")
    .WithIcon("delete")
    .WithTooltip("Delete item")
    .WithClickAction(OnDelete)
```

### MenuControl

Dropdown menu with action items.

```csharp
Controls.Menu()
    .WithTrigger(Controls.IconButton("more_vert"))
    .WithItems(
        MenuItem.Action("Edit", OnEdit),
        MenuItem.Action("Duplicate", OnDuplicate),
        MenuItem.Divider(),
        MenuItem.Action("Delete", OnDelete).WithColor("error")
    )
```

## Navigation Controls

Controls for navigation and structure.

### TabsControl

Tabbed content navigation.

```csharp
Controls.Tabs()
    .WithTabs(
        Tab.Create("overview", "Overview", overviewContent),
        Tab.Create("details", "Details", detailsContent),
        Tab.Create("history", "History", historyContent)
    )
```

### BreadcrumbControl

Navigation breadcrumb trail.

```csharp
Controls.Breadcrumb()
    .WithItems(
        BreadcrumbItem.Link("Home", "/"),
        BreadcrumbItem.Link("Claims", "/claims"),
        BreadcrumbItem.Current("CLM-2024-001")
    )
```

### LinkControl

Navigation link within the mesh.

```csharp
Controls.Link("View Claim")
    .WithPath("Insurance/Claims/CLM-2024-001")
```

## Container Controls

Controls for grouping and organization.

### DialogControl

Modal dialog with content and actions.

```csharp
Controls.Dialog()
    .WithTitle("Confirm Delete")
    .WithContent(Controls.Text("Are you sure?"))
    .WithActions(
        Controls.Button("Cancel").WithClickAction(OnCancel),
        Controls.Button("Delete").WithVariant(ButtonVariant.Primary).WithClickAction(OnConfirm)
    )
```

### EditFormControl

Form container with validation and submission.

```csharp
Controls.EditForm(claimData)
    .WithChildren(
        Controls.TextField("claimNumber").WithLabel("Claim Number"),
        Controls.DatePicker("lossDate").WithLabel("Loss Date"),
        Controls.NumberField("reserveAmount").WithLabel("Reserve")
    )
    .WithSubmitAction(OnSubmit)
```

| Property | Type | Description |
|----------|------|-------------|
| `Data` | `object` | Form data object |
| `Children` | `IEnumerable<UiControl>` | Form field controls |
| `SubmitAction` | `Func<SubmitContext, Task>` | Form submission handler |

### ExpansionPanelControl

Collapsible content section.

```csharp
Controls.ExpansionPanel()
    .WithTitle("Advanced Options")
    .WithContent(advancedOptionsContent)
    .WithExpanded(false)
```

## Utility Controls

### SpinnerControl

Loading indicator.

```csharp
Controls.Spinner()
    .WithSize(SpinnerSize.Large)
    .WithLabel("Loading...")
```

### AlertControl

Informational message display.

```csharp
Controls.Alert()
    .WithSeverity(AlertSeverity.Warning)
    .WithTitle("Review Required")
    .WithMessage("This claim requires manager approval.")
```

| Property | Type | Description |
|----------|------|-------------|
| `Severity` | `AlertSeverity` | Info, Success, Warning, Error |
| `Title` | `string` | Alert title |
| `Message` | `string` | Alert message |

### DividerControl

Visual separator.

```csharp
Controls.Divider()
    .WithOrientation(Orientation.Horizontal)
```

## Control Composition

Controls can be composed to build complex UIs:

```csharp
public UiControl BuildClaimView(Claim claim)
{
    return Controls.Stack()
        .WithChildren(
            // Header
            Controls.Card()
                .WithTitle(claim.ClaimNumber)
                .WithContent(
                    Controls.Grid()
                        .WithColumns("1fr 1fr 1fr")
                        .WithChildren(
                            LabelValue("Status", claim.Status),
                            LabelValue("Loss Date", claim.LossDate.ToShortDateString()),
                            LabelValue("Reserve", claim.ReserveAmount.ToString("C"))
                        )
                ),
            // Details
            Controls.Tabs()
                .WithTabs(
                    Tab.Create("notes", "Notes", BuildNotesTab(claim)),
                    Tab.Create("documents", "Documents", BuildDocsTab(claim)),
                    Tab.Create("history", "History", BuildHistoryTab(claim))
                ),
            // Actions
            Controls.Stack()
                .WithOrientation(Orientation.Horizontal)
                .WithChildren(
                    Controls.Button("Edit").WithClickAction(OnEdit),
                    Controls.Button("Close Claim").WithVariant(ButtonVariant.Primary).WithClickAction(OnClose)
                )
        );
}

private UiControl LabelValue(string label, string value)
{
    return Controls.Stack()
        .WithChildren(
            Controls.Text(label).WithTypography(Typography.Caption),
            Controls.Text(value).WithTypography(Typography.Body1)
        );
}
```
