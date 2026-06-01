---
Name: Available Controls Reference
Category: Documentation
Description: Complete reference for MeshWeaver UI controls — layout, input, display, navigation, and container components, all created server-side via the Controls factory.
Icon: /static/DocContent/Architecture/UserInterface/AvailableControls/icon.svg
---

MeshWeaver's UI is built from a library of composable controls defined entirely in C# on the server. The browser renders whatever the server describes — there is no client-side component code to write. Every control is created through the `Controls` factory class and composed into trees that update reactively as your data changes.

---

## Layout Controls

Layout controls determine how other controls are spatially arranged on the page.

### StackControl

The workhorse layout control. Arranges children in a single axis — vertical by default, or horizontal when you need a toolbar or button row.

```csharp
Controls.Stack()
    .WithOrientation(Orientation.Vertical)
    .WithChildren(
        Controls.Text("Item 1"),
        Controls.Text("Item 2")
    )
```

| Property | Type | Description |
|---|---|---|
| `Orientation` | `Orientation` | `Vertical` (default) or `Horizontal` |
| `Spacing` | `string` | Gap between children — any CSS value |
| `Children` | `IEnumerable<UiControl>` | Child controls |

---

### GridControl

When a single axis isn't enough, `GridControl` exposes the full power of CSS Grid for arbitrary two-dimensional layouts.

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
|---|---|---|
| `Columns` | `string` | CSS `grid-template-columns` value |
| `Rows` | `string` | CSS `grid-template-rows` value |
| `Gap` | `string` | Gap between grid cells |

---

### LayoutAreaControl

A named region that can be updated independently of its surroundings. Use it to carve a page into independently-refreshable zones — a detail panel that reloads on row selection, for example.

```csharp
Controls.LayoutArea("detail-panel")
    .WithView(context => RenderDetailView(context))
```

| Property | Type | Description |
|---|---|---|
| `Area` | `string` | Unique area identifier |
| `View` | `Func<LayoutAreaContext, UiControl>` | View rendering function |

---

## Input Controls

Input controls bind to your data model and surface user edits through the reactive data pipeline.

### TextFieldControl

Single-line text input with built-in validation support.

```csharp
Controls.TextField("username")
    .WithLabel("Username")
    .WithPlaceholder("Enter username")
    .WithRequired(true)
    .WithMaxLength(50)
```

| Property | Type | Description |
|---|---|---|
| `DataContext` | `string` | Binding path for the value |
| `Label` | `string` | Field label |
| `Placeholder` | `string` | Placeholder text |
| `Required` | `bool` | Whether the field is required |
| `MaxLength` | `int?` | Maximum character length |
| `Disabled` | `bool` | Disables the input |

---

### NumberFieldControl

Numeric input with formatting options and min/max/step constraints.

```csharp
Controls.NumberField("amount")
    .WithLabel("Amount")
    .WithMin(0)
    .WithMax(1000000)
    .WithStep(0.01)
    .WithFormat("N2")
```

| Property | Type | Description |
|---|---|---|
| `Min` | `double?` | Minimum value |
| `Max` | `double?` | Maximum value |
| `Step` | `double?` | Increment step |
| `Format` | `string` | .NET number format string |

---

### SelectControl

Dropdown selection from a static or dynamic list of options.

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
|---|---|---|
| `Options` | `IEnumerable<SelectOption>` | Available choices |
| `Multiple` | `bool` | Allow multi-selection |
| `Searchable` | `bool` | Enable search filtering |

---

### CheckboxControl

A simple boolean toggle, typically used inside a form.

```csharp
Controls.Checkbox("agreed")
    .WithLabel("I agree to the terms")
```

---

### DatePickerControl

Date selection backed by a calendar popup, with optional min/max bounds.

```csharp
Controls.DatePicker("startDate")
    .WithLabel("Start Date")
    .WithMinDate(DateTime.Today)
    .WithFormat("yyyy-MM-dd")
```

| Property | Type | Description |
|---|---|---|
| `MinDate` | `DateTime?` | Earliest selectable date |
| `MaxDate` | `DateTime?` | Latest selectable date |
| `Format` | `string` | Date display format string |

---

## Display Controls

Display controls present data to the user without expecting direct edits.

### TextControl

Styled text with configurable typography and colour. Covers headings, body copy, captions, and everything in between.

```csharp
Controls.Text("Welcome to MeshWeaver")
    .WithTypography(Typography.H1)
    .WithColor("primary")
```

| Property | Type | Description |
|---|---|---|
| `Content` | `string` | Text content |
| `Typography` | `Typography` | H1–H6, Body, Caption, and more |
| `Color` | `string` | Text colour |

---

### DataGridControl

Full-featured tabular display with sorting, filtering, pagination, and row-click actions — the standard way to render any collection.

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
|---|---|---|
| `Data` | `object` | Data source (any collection) |
| `Columns` | `IEnumerable<Column>` | Column definitions |
| `PageSize` | `int` | Rows per page |
| `Sortable` | `bool` | Enable column sorting |
| `Filterable` | `bool` | Enable column filtering |

---

### CardControl

A visually distinct container with an optional title, subtitle, body, and action strip — useful for summary panels and entity detail views.

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

---

### ChartControl

Data visualisation powered by Chart.js, supporting bar, line, pie, area, and other chart types.

```csharp
Controls.Chart()
    .WithType(ChartType.Bar)
    .WithData(salesData)
    .WithXAxis("month")
    .WithYAxis("revenue")
```

| Property | Type | Description |
|---|---|---|
| `Type` | `ChartType` | Bar, Line, Pie, Area, etc. |
| `Data` | `object` | Chart data source |
| `XAxis` | `string` | X-axis data field |
| `YAxis` | `string` | Y-axis data field |

---

## Action Controls

Action controls let the user trigger operations. Their click handlers run server-side — you always have direct access to the hub and the full mesh.

### ButtonControl

The primary interaction control. Supports variants, icons, and a synchronous click handler.

```csharp
Controls.Button("Submit")
    .WithVariant(ButtonVariant.Primary)
    .WithIcon("Send")
    .WithClickAction(context => {
        context.Hub.Post(new SubmitRequest());
        return Task.CompletedTask;
    })
```

| Property | Type | Description |
|---|---|---|
| `Label` | `string` | Button text |
| `Variant` | `ButtonVariant` | Primary, Secondary, Outlined, Text |
| `Icon` | `string` | Icon name |
| `Disabled` | `bool` | Disables the button |
| `ClickAction` | `Func<ClickContext, Task>` | Click handler |

---

### IconButtonControl

A compact, icon-only button suited to inline actions such as delete, edit, or expand. Always pair with a tooltip for accessibility.

```csharp
Controls.IconButton("Delete")
    .WithIcon("delete")
    .WithTooltip("Delete item")
    .WithClickAction(OnDelete)
```

---

### MenuControl

A contextual dropdown menu triggered by any control — typically an `IconButton` with a "more_vert" icon.

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

---

## Navigation Controls

### TabsControl

Switches between named content panels without a full page reload.

```csharp
Controls.Tabs()
    .WithTabs(
        Tab.Create("overview", "Overview", overviewContent),
        Tab.Create("details", "Details", detailsContent),
        Tab.Create("history", "History", historyContent)
    )
```

---

### BreadcrumbControl

Shows the user's position in the mesh hierarchy and lets them jump back up.

```csharp
Controls.Breadcrumb()
    .WithItems(
        BreadcrumbItem.Link("Home", "/"),
        BreadcrumbItem.Link("Claims", "/claims"),
        BreadcrumbItem.Current("CLM-2024-001")
    )
```

---

### LinkControl

A navigational link to any path within the mesh.

```csharp
Controls.Link("View Claim")
    .WithPath("Insurance/Claims/CLM-2024-001")
```

---

## Container Controls

Container controls group other controls and add behaviour — form validation, modal presentation, or collapsible visibility.

### DialogControl

A modal dialog with a content area and an action strip. Show it programmatically from a button's click handler.

```csharp
Controls.Dialog()
    .WithTitle("Confirm Delete")
    .WithContent(Controls.Text("Are you sure?"))
    .WithActions(
        Controls.Button("Cancel").WithClickAction(OnCancel),
        Controls.Button("Delete").WithVariant(ButtonVariant.Primary).WithClickAction(OnConfirm)
    )
```

---

### EditFormControl

Wraps input controls in a validated form container, giving you a single `SubmitAction` entry point instead of wiring each field individually.

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
|---|---|---|
| `Data` | `object` | Form data object |
| `Children` | `IEnumerable<UiControl>` | Form field controls |
| `SubmitAction` | `Func<SubmitContext, Task>` | Form submission handler |

---

### ExpansionPanelControl

A collapsible section that keeps the page tidy by hiding infrequently-needed content behind a clickable header.

```csharp
Controls.ExpansionPanel()
    .WithTitle("Advanced Options")
    .WithContent(advancedOptionsContent)
    .WithExpanded(false)
```

---

## Utility Controls

Small, single-purpose controls that fill common UI gaps.

### SpinnerControl

A loading indicator for asynchronous operations.

```csharp
Controls.Spinner()
    .WithSize(SpinnerSize.Large)
    .WithLabel("Loading...")
```

---

### AlertControl

An inline message banner — use it to surface warnings, errors, or success confirmations without a full dialog.

```csharp
Controls.Alert()
    .WithSeverity(AlertSeverity.Warning)
    .WithTitle("Review Required")
    .WithMessage("This claim requires manager approval.")
```

| Property | Type | Description |
|---|---|---|
| `Severity` | `AlertSeverity` | Info, Success, Warning, Error |
| `Title` | `string` | Alert title |
| `Message` | `string` | Alert message body |

---

### DividerControl

A thin visual rule for separating content regions.

```csharp
Controls.Divider()
    .WithOrientation(Orientation.Horizontal)
```

---

## Composing Controls

Controls are plain C# objects — compose them freely in helper methods to build rich, readable view code. The example below assembles a complete claim view from layout, display, navigation, and action controls:

```csharp
public UiControl BuildClaimView(Claim claim)
{
    return Controls.Stack()
        .WithChildren(
            // Header card with a 3-column summary grid
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
            // Tabbed detail sections
            Controls.Tabs()
                .WithTabs(
                    Tab.Create("notes", "Notes", BuildNotesTab(claim)),
                    Tab.Create("documents", "Documents", BuildDocsTab(claim)),
                    Tab.Create("history", "History", BuildHistoryTab(claim))
                ),
            // Horizontal action strip
            Controls.Stack()
                .WithOrientation(Orientation.Horizontal)
                .WithChildren(
                    Controls.Button("Edit").WithClickAction(OnEdit),
                    Controls.Button("Close Claim")
                        .WithVariant(ButtonVariant.Primary)
                        .WithClickAction(OnClose)
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

---

## Live Demo

The cell below renders a small stack of controls directly in this page, illustrating how layout, text, and button controls compose together at runtime:

```csharp --render ControlsDemo --show-code
MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Text("Layout Controls — live in the kernel"))
    .WithView(MeshWeaver.Layout.Controls.Html("<hr style='margin:4px 0'/>"))
    .WithView(MeshWeaver.Layout.Controls.Stack
        .WithView(MeshWeaver.Layout.Controls.Html("<b>Stack (Vertical, default)</b>"))
        .WithView(MeshWeaver.Layout.Controls.Html("Item A"))
        .WithView(MeshWeaver.Layout.Controls.Html("Item B"))
        .WithView(MeshWeaver.Layout.Controls.Html("Item C")))
    .WithView(MeshWeaver.Layout.Controls.Button("Example Button"))
```
