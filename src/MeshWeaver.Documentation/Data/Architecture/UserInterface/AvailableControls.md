---
Name: Available Controls Reference
Category: Documentation
Description: Complete reference for MeshWeaver UI controls — layout, input, display, navigation, and container components, all created server-side via the Controls factory.
Icon: /static/DocContent/Architecture/UserInterface/AvailableControls/icon.svg
---

MeshWeaver's UI is built from a library of composable controls defined entirely in C# on the server. The browser renders whatever the server describes — there is no client-side component code to write. Every control is created through the `Controls` factory class and composed into trees that update reactively as your data changes.

<svg viewBox="0 0 760 340" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="12">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity="0.55"/>
    </marker>
  </defs>
  <rect x="300" y="8" width="160" height="36" rx="10" fill="#1e88e5"/>
  <text x="380" y="31" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Controls factory</text>
  <line x1="380" y1="44" x2="100" y2="92" stroke="currentColor" stroke-opacity="0.45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="44" x2="248" y2="92" stroke="currentColor" stroke-opacity="0.45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="44" x2="380" y2="92" stroke="currentColor" stroke-opacity="0.45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="44" x2="512" y2="92" stroke="currentColor" stroke-opacity="0.45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="44" x2="660" y2="92" stroke="currentColor" stroke-opacity="0.45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="20" y="92" width="160" height="34" rx="8" fill="#5c6bc0"/>
  <text x="100" y="114" text-anchor="middle" fill="#fff" font-weight="bold">Layout</text>
  <rect x="168" y="92" width="160" height="34" rx="8" fill="#43a047"/>
  <text x="248" y="114" text-anchor="middle" fill="#fff" font-weight="bold">Input</text>
  <rect x="300" y="92" width="160" height="34" rx="8" fill="#1e88e5"/>
  <text x="380" y="114" text-anchor="middle" fill="#fff" font-weight="bold">Display</text>
  <rect x="432" y="92" width="160" height="34" rx="8" fill="#f57c00"/>
  <text x="512" y="114" text-anchor="middle" fill="#fff" font-weight="bold">Action</text>
  <rect x="580" y="92" width="160" height="34" rx="8" fill="#8e24aa"/>
  <text x="660" y="114" text-anchor="middle" fill="#fff" font-weight="bold">Navigation</text>
  <line x1="100" y1="126" x2="56" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="100" y1="126" x2="100" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="100" y1="126" x2="144" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <rect x="14" y="168" width="84" height="28" rx="6" fill="#5c6bc0" fill-opacity="0.7"/>
  <text x="56" y="187" text-anchor="middle" fill="#fff" font-size="11">Stack</text>
  <rect x="58" y="168" width="84" height="28" rx="6" fill="#5c6bc0" fill-opacity="0.7"/>
  <text x="100" y="187" text-anchor="middle" fill="#fff" font-size="11">Grid</text>
  <rect x="102" y="168" width="84" height="28" rx="6" fill="#5c6bc0" fill-opacity="0.7"/>
  <text x="144" y="187" text-anchor="middle" fill="#fff" font-size="11">LayoutArea</text>
  <line x1="248" y1="126" x2="204" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="248" y1="126" x2="248" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="248" y1="126" x2="292" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <rect x="162" y="168" width="84" height="28" rx="6" fill="#43a047" fill-opacity="0.7"/>
  <text x="204" y="187" text-anchor="middle" fill="#fff" font-size="11">TextField</text>
  <rect x="206" y="168" width="84" height="28" rx="6" fill="#43a047" fill-opacity="0.7"/>
  <text x="248" y="187" text-anchor="middle" fill="#fff" font-size="11">Select</text>
  <rect x="250" y="168" width="84" height="28" rx="6" fill="#43a047" fill-opacity="0.7"/>
  <text x="292" y="187" text-anchor="middle" fill="#fff" font-size="11">DatePicker</text>
  <line x1="380" y1="126" x2="336" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="380" y1="126" x2="380" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="380" y1="126" x2="424" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <rect x="294" y="168" width="84" height="28" rx="6" fill="#1e88e5" fill-opacity="0.7"/>
  <text x="336" y="187" text-anchor="middle" fill="#fff" font-size="11">Text</text>
  <rect x="338" y="168" width="84" height="28" rx="6" fill="#1e88e5" fill-opacity="0.7"/>
  <text x="380" y="187" text-anchor="middle" fill="#fff" font-size="11">DataGrid</text>
  <rect x="382" y="168" width="84" height="28" rx="6" fill="#1e88e5" fill-opacity="0.7"/>
  <text x="424" y="187" text-anchor="middle" fill="#fff" font-size="11">Chart</text>
  <line x1="512" y1="126" x2="468" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="512" y1="126" x2="512" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="512" y1="126" x2="556" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <rect x="426" y="168" width="84" height="28" rx="6" fill="#f57c00" fill-opacity="0.7"/>
  <text x="468" y="187" text-anchor="middle" fill="#fff" font-size="11">Button</text>
  <rect x="470" y="168" width="84" height="28" rx="6" fill="#f57c00" fill-opacity="0.7"/>
  <text x="512" y="187" text-anchor="middle" fill="#fff" font-size="11">IconButton</text>
  <rect x="514" y="168" width="84" height="28" rx="6" fill="#f57c00" fill-opacity="0.7"/>
  <text x="556" y="187" text-anchor="middle" fill="#fff" font-size="11">Menu</text>
  <line x1="660" y1="126" x2="616" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="660" y1="126" x2="660" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="660" y1="126" x2="704" y2="168" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <rect x="574" y="168" width="84" height="28" rx="6" fill="#8e24aa" fill-opacity="0.7"/>
  <text x="616" y="187" text-anchor="middle" fill="#fff" font-size="11">Tabs</text>
  <rect x="618" y="168" width="84" height="28" rx="6" fill="#8e24aa" fill-opacity="0.7"/>
  <text x="660" y="187" text-anchor="middle" fill="#fff" font-size="11">Breadcrumb</text>
  <rect x="662" y="168" width="84" height="28" rx="6" fill="#8e24aa" fill-opacity="0.7"/>
  <text x="704" y="187" text-anchor="middle" fill="#fff" font-size="11">Link</text>
  <line x1="380" y1="228" x2="248" y2="266" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="380" y1="228" x2="380" y2="266" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <line x1="380" y1="228" x2="512" y2="266" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.2" marker-end="url(#arr)"/>
  <rect x="270" y="228" width="220" height="28" rx="8" fill="#26a69a"/>
  <text x="380" y="247" text-anchor="middle" fill="#fff" font-weight="bold">Container + Utility</text>
  <rect x="162" y="266" width="172" height="28" rx="6" fill="#26a69a" fill-opacity="0.7"/>
  <text x="248" y="285" text-anchor="middle" fill="#fff" font-size="11">Dialog · EditForm · ExpansionPanel</text>
  <rect x="294" y="266" width="172" height="28" rx="6" fill="#26a69a" fill-opacity="0.7"/>
  <text x="380" y="285" text-anchor="middle" fill="#fff" font-size="11">Card · Spinner · Alert</text>
  <rect x="426" y="266" width="172" height="28" rx="6" fill="#26a69a" fill-opacity="0.7"/>
  <text x="512" y="285" text-anchor="middle" fill="#fff" font-size="11">Divider · Checkbox · Number</text>
  <line x1="380" y1="44" x2="380" y2="228" stroke="currentColor" stroke-opacity="0.2" stroke-width="1" stroke-dasharray="4 3"/>
</svg>

*Control taxonomy — all controls are created via `Controls.*` and compose into reactive UI trees.*

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

### MeshNodePickerControl

Searchable picker for mesh nodes. Renders search results as cards (via `MeshSearchView`) and stores the **selected node's path** as the form value. The standard surface behind the [`[MeshNode]` attribute](/Doc/GUI/Attributes) — prefer the attribute on data models; construct the control directly only in hand-built layout areas.

```csharp
new MeshNodePickerControl(new JsonPointerReference("/assigneePath"))
    .WithQueries("nodeType:User namespace:acme", "nodeType:Group path:acme scope:selfAndAncestors")
    .WithMaxResults(10)
    .WithEmptyOption("Root (top-level)")
    .WithLayout(MeshNodePickerLayout.Thin)
    .WithOpenDirection(MeshNodePickerOpenDirection.Up)
    .WithDefaultToFirst()
```

| Property | Type | Description |
|---|---|---|
| `Queries` | `string[]?` | Query strings run in parallel and merged; the user's typed text is appended to each (see [Query Syntax](/Doc/DataMesh/QuerySyntax)) |
| `Namespace` | `object?` | Namespace to scope the search to |
| `MaxResults` | `object?` | Maximum results shown in the dropdown |
| `Items` | `object[]?` | Fixed `MeshNode` set merged with query results (deduplicated by path) — for small known sets like creatable types or roles |
| `EmptyOptionLabel` | `string?` | When set, adds a top option that selects the empty value `""` |
| `Layout` | `MeshNodePickerLayout` | `Default` (full card) or `Thin` (compact row) |
| `Open` | `MeshNodePickerOpenDirection` | `Down` (default) or `Up` for bottom-anchored fields (chat composer) |
| `DefaultToFirst` | `bool` | Auto-select (and persist) the first result when no value is set |

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
