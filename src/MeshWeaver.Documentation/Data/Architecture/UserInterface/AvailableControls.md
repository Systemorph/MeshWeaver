---
Name: Available Controls Reference
Category: Documentation
Description: Catalog of the control families — display, containers, inputs, data grids, charts, feedback, editors, and mesh controls — one live example per family, with links to each deep-dive page.
Icon: /static/DocContent/Architecture/UserInterface/AvailableControls/icon.svg
---

A [layout area](/Doc/GUI/LayoutAreas) renders a tree of **controls** — immutable C# records declared server-side through the `Controls` factory (`MeshWeaver.Layout`) and streamed to the browser as data. There is no client-side component code to write: you compose controls, the portal renders them, and they update reactively as the underlying data changes.

Two shapes cover the whole factory surface: parameterless controls are **property getters** (`Controls.Stack`, `Controls.Tabs` — no parentheses), and parameterised ones are methods (`Controls.Label("…")`, `Controls.DataGrid(rows)`). Children are attached with `WithView(...)` — one call per child — never with a children list. Every code cell on this page is executable: the result renders directly below the code, and **Run** re-executes it.

<svg viewBox="0 0 760 300" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="12">
  <defs>
    <marker id="acarr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity="0.55"/>
    </marker>
  </defs>
  <rect x="255" y="8" width="250" height="38" rx="10" fill="#1e88e5"/>
  <text x="380" y="26" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Controls factory</text>
  <text x="380" y="40" text-anchor="middle" fill="#bbdefb" font-size="10">MeshWeaver.Layout</text>
  <line x1="380" y1="46" x2="97" y2="86" stroke="currentColor" stroke-opacity="0.45" stroke-width="1.5" marker-end="url(#acarr)"/>
  <line x1="380" y1="46" x2="286" y2="86" stroke="currentColor" stroke-opacity="0.45" stroke-width="1.5" marker-end="url(#acarr)"/>
  <line x1="380" y1="46" x2="474" y2="86" stroke="currentColor" stroke-opacity="0.45" stroke-width="1.5" marker-end="url(#acarr)"/>
  <line x1="380" y1="46" x2="663" y2="86" stroke="currentColor" stroke-opacity="0.45" stroke-width="1.5" marker-end="url(#acarr)"/>
  <rect x="9" y="88" width="178" height="76" rx="8" fill="#1e88e5" fill-opacity="0.85"/>
  <text x="98" y="108" text-anchor="middle" fill="#fff" font-weight="bold">Display</text>
  <text x="98" y="126" text-anchor="middle" fill="#e3f2fd" font-size="10">Label · Badge · Icon</text>
  <text x="98" y="141" text-anchor="middle" fill="#e3f2fd" font-size="10">Markdown · Html</text>
  <rect x="197" y="88" width="178" height="76" rx="8" fill="#5c6bc0" fill-opacity="0.85"/>
  <text x="286" y="108" text-anchor="middle" fill="#fff" font-weight="bold">Containers</text>
  <text x="286" y="126" text-anchor="middle" fill="#e8eaf6" font-size="10">Stack · LayoutGrid · Tabs</text>
  <text x="286" y="141" text-anchor="middle" fill="#e8eaf6" font-size="10">Splitter · Toolbar</text>
  <rect x="385" y="88" width="178" height="76" rx="8" fill="#43a047" fill-opacity="0.85"/>
  <text x="474" y="108" text-anchor="middle" fill="#fff" font-weight="bold">Inputs</text>
  <text x="474" y="126" text-anchor="middle" fill="#c8e6c9" font-size="10">Text · Number · Select</text>
  <text x="474" y="141" text-anchor="middle" fill="#c8e6c9" font-size="10">CheckBox · DateTime</text>
  <rect x="573" y="88" width="178" height="76" rx="8" fill="#f57c00" fill-opacity="0.85"/>
  <text x="662" y="108" text-anchor="middle" fill="#fff" font-weight="bold">Data &amp; charts</text>
  <text x="662" y="126" text-anchor="middle" fill="#ffe0b2" font-size="10">DataGrid · Charts</text>
  <text x="662" y="141" text-anchor="middle" fill="#ffe0b2" font-size="10">PivotGrid</text>
  <line x1="380" y1="46" x2="135" y2="188" stroke="currentColor" stroke-opacity="0.3" stroke-width="1.2" marker-end="url(#acarr)"/>
  <line x1="380" y1="46" x2="380" y2="188" stroke="currentColor" stroke-opacity="0.3" stroke-width="1.2" marker-end="url(#acarr)"/>
  <line x1="380" y1="46" x2="625" y2="188" stroke="currentColor" stroke-opacity="0.3" stroke-width="1.2" marker-end="url(#acarr)"/>
  <rect x="15" y="190" width="240" height="76" rx="8" fill="#26a69a" fill-opacity="0.85"/>
  <text x="135" y="210" text-anchor="middle" fill="#fff" font-weight="bold">Feedback</text>
  <text x="135" y="228" text-anchor="middle" fill="#b2dfdb" font-size="10">Progress · Exception</text>
  <rect x="260" y="190" width="240" height="76" rx="8" fill="#8e24aa" fill-opacity="0.85"/>
  <text x="380" y="210" text-anchor="middle" fill="#fff" font-weight="bold">Editors</text>
  <text x="380" y="228" text-anchor="middle" fill="#e1bee7" font-size="10">Edit macro · MarkdownEditor</text>
  <text x="380" y="243" text-anchor="middle" fill="#e1bee7" font-size="10">CodeEditor</text>
  <rect x="505" y="190" width="240" height="76" rx="8" fill="#e53935" fill-opacity="0.85"/>
  <text x="625" y="210" text-anchor="middle" fill="#fff" font-weight="bold">Mesh &amp; navigation</text>
  <text x="625" y="228" text-anchor="middle" fill="#ffcdd2" font-size="10">MeshNodePicker · MeshSearch</text>
  <text x="625" y="243" text-anchor="middle" fill="#ffcdd2" font-size="10">NavMenu · NavLink · LayoutArea</text>
</svg>

*The control families — every control is created via `Controls.*` (plus `Charts.*` for charts) and composed with `WithView`.*

## The catalog at a glance

| Family | Factory members | Deep dive |
|---|---|---|
| Display | `Label` (+ typography helpers `H1`…`H6`, `Body`, `Header`), `Badge`, `Icon`, `Markdown`, `Html`, `Title`, `CodeSample`, `Spacer` | [Badges, Icons & Status](/Doc/GUI/DisplayControls) |
| Containers & skins | `Stack`, `LayoutGrid`, `Tabs`, `Splitter`, `Toolbar`, `Layout`, `Skins.Card` | [Container Controls](/Doc/GUI/ContainerControl) · [Layout Grid](/Doc/GUI/LayoutGrid) |
| Inputs | `Text`, `Number`, `Date`, `DateTime`, `CheckBox`, `Switch`, `Select`, `Combobox`, `Listbox`, `RadioGroup`, `Slider` | [Form Input Controls](/Doc/GUI/InputControls) |
| Actions | `Button`, `MenuItem` | [User Interface](/Doc/Architecture/UserInterface) — click handlers |
| Data | `DataGrid` + `PropertyColumnControl` / `TemplateColumnControl` | [DataGrid](/Doc/GUI/DataGrid) |
| Charts & pivots | `Charts.*` (`MeshWeaver.Layout.Chart`), `ToPivotGrid` (`MeshWeaver.Layout.Pivot`) | [Charts at a glance](/Doc/GUI/ChartGallery) · [Pivot tricks](/Doc/GUI/PivotTricks) |
| Feedback | `Progress`, `Exception` | [Badges, Icons & Status](/Doc/GUI/DisplayControls) |
| Editors | `Edit` macro (`EditorControl`), `MarkdownEditorControl`, `CodeEditorControl` | [Editor](/Doc/GUI/Editor) · [Code Editor](/Doc/GUI/CodeEditor) |
| Mesh & navigation | `MeshNodePicker`, `MeshSearch`, `SearchBox`, `FileBrowser`, `NavMenu`, `NavLink`, `NavGroup`, `LayoutArea`, `NamedArea` | [Mesh Search](/Doc/GUI/MeshSearch) · [Navigation Menus](/Doc/GUI/Navigation) |

> **One naming gotcha.** `Controls.Text(...)` is a text **input** (`TextFieldControl`), not display text. Read-only text goes through `Controls.Label` / the typography helpers, `Controls.Markdown`, or — for genuinely pre-rendered markup only — `Controls.Html`.

## Display — Label, Badge, Icon, Markdown

Display controls present read-only content: `Label` with typography helpers for text, `Badge` for status pills, `Icon` for the `FluentIcons` catalog, and `Markdown` for formatted prose. They are the leaves of most control trees.

```csharp --render CatalogDisplay --show-code
Controls.Stack
    .WithView(Controls.H4("Display controls"))
    .WithView(Controls.Label("Every family on this page renders live in the kernel"))
    .WithView(Controls.Stack
        .WithOrientation(Orientation.Horizontal)
        .WithHorizontalGap("8px")
        .WithView(Controls.Badge("Released").WithAppearance("Accent"))
        .WithView(Controls.Icon(FluentIcons.CheckmarkCircle()))
        .WithView(Controls.Markdown("**Markdown** renders inline, too")))
```

See [Badges, Icons & Status](/Doc/GUI/DisplayControls) for the full display set, including `Spacer` and the `Skins.Card` skin.

## Containers — Stack, Tabs, Toolbar, Splitter, LayoutGrid

Containers arrange other controls. `WithView(control)` appends a child; the optional second argument (`s => s.WithLabel("…")`) configures the child's slot — that is how tabs get labels, splitter panes get sizes, and grid items get column spans. `Stack` stacks vertically or horizontally, `Tabs` shows one panel at a time, `Toolbar` lays out action buttons, `Splitter` creates resizable panes, and `LayoutGrid` is the responsive 12-column system.

```csharp --render CatalogContainers --show-code
Controls.Tabs
    .WithView(
        Controls.Stack
            .WithView(Controls.Markdown("`Stack` arranges children **vertically** by default; each `WithView` appends one child."))
            .WithView(Controls.Toolbar
                .WithView(Controls.Button("Refresh"))
                .WithView(Controls.Button("Export"))),
        s => s.WithLabel("Overview"))
    .WithView(Controls.Label("Tabs show one panel at a time."), s => s.WithLabel("Details"))
```

See [Container Controls](/Doc/GUI/ContainerControl) for all five containers and the full `WithView` overload table, and [Layout Grid](/Doc/GUI/LayoutGrid) for responsive breakpoints.

## Inputs — Text, Number, Select, CheckBox, DateTime

Input controls bind a value two-way. In a real layout area the first argument is a `JsonPointerReference` into the area's data store (see [Data Binding](/Doc/GUI/DataBinding)); in standalone demos it is a plain value. List selection takes `Option<T>` values; `Select`, `Combobox`, and `Listbox` share the same `(data, options)` shape and differ only in presentation.

```csharp --render CatalogInputs --show-code
var currencies = new[]
{
    new Option<string>("CHF", "Swiss Franc (CHF)"),
    new Option<string>("EUR", "Euro (EUR)")
};

Controls.Stack
    .WithView(Controls.Text("Alice Example").WithLabel("Name"))
    .WithView(Controls.Number(42, "Int32").WithLabel("Age"))
    .WithView(Controls.Select("CHF", currencies).WithLabel("Currency"))
    .WithView(Controls.CheckBox(true).WithLabel("Active"))
    .WithView(Controls.DateTime(DateTime.Today).WithLabel("Start date"))
```

See [Form Input Controls](/Doc/GUI/InputControls) for every input rendered live — including `Switch`, `TextArea`, `RadioGroup`, and the mesh-node picker — and [Property Attributes](/Doc/GUI/Attributes) for the attributes that pick these controls automatically.

## Data — DataGrid

`Controls.DataGrid(rows)` renders any collection as a sortable, resizable table — the standard way to render tabular data (never hand-built HTML). Columns are typed `PropertyColumnControl<T>` instances; **property names are camelCase** (`"instrument"` for `Instrument`). `TemplateColumnControl` puts an arbitrary control in every cell for action columns.

```csharp --render CatalogDataGrid --show-code
record Position(string Instrument, int Quantity, decimal Price);

var positions = new[]
{
    new Position("Bond A", 100, 102.50m),
    new Position("Equity B", 250, 48.30m),
    new Position("Fund C", 75, 210.00m)
};

Controls.DataGrid(positions)
    .WithColumn(new PropertyColumnControl<string>  { Property = "instrument" }.WithTitle("Instrument"))
    .WithColumn(new PropertyColumnControl<int>     { Property = "quantity"   }.WithTitle("Quantity"))
    .WithColumn(new PropertyColumnControl<decimal> { Property = "price"      }
        .WithTitle("Price").WithAlign("end").WithFormat("N2"))
```

See [DataGrid](/Doc/GUI/DataGrid) for pagination, virtualization, action columns, and the full option tables.

## Charts and pivots

Charts live in `MeshWeaver.Layout.Chart`: the `Charts.*` factories turn plain arrays into column, bar, line, and pie charts, and the `SliceBy(...).To*Chart(...)` pipeline charts sliced datasets. The pivot twin (`ToPivotGrid`) folds flat facts into rows-by-X, columns-by-Y tables.

```csharp --render CatalogChart --show-code
using MeshWeaver.Layout.Chart;

var revenue = new double[] { 480, 520, 610, 730 };
var quarters = new[] { "Q1", "Q2", "Q3", "Q4" };

Charts.Column(revenue, quarters, "Revenue (CHF k)")
    .WithTitle("Quarterly revenue")
```

See [Charts at a glance](/Doc/GUI/ChartGallery) for the whole gallery, [Pivot tricks](/Doc/GUI/PivotTricks) for the pivot side, and [Data Cubes](/Doc/DataMesh/DataCubes) for slicing real datasets.

## Feedback — Progress

`Controls.Progress(message, percentage)` is the standard way to surface long-running work — imports, compiles, exports — in a layout area. In real use the percentage comes from an observable, so the bar advances as the operation reports progress; `Controls.Exception(ex)` renders a failure where a result would have gone.

```csharp --render CatalogProgress --show-code
Controls.Stack
    .WithView(Controls.Progress("Exporting report…", 80))
    .WithView(Controls.Progress("Compiling node type…", 45))
```

See [Static vs Dynamic Views](/Doc/GUI/Observables) for feeding a control from a live stream.

## Editors and forms

The `Edit` macro generates a complete form from a plain record — each property becomes the input its type and attributes dictate (`string` → text field, `int` → number field, `bool` → checkbox), with validation from standard data annotations. Never hand-build a form field-by-field when a record describes the shape.

```csharp --render CatalogEditor --show-code
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

record ContactForm
{
    [Required]
    [DisplayName("Full name")]
    public string Name { get; init; } = "Alice Example";

    [Range(18, 120)]
    public int Age { get; init; } = 34;

    [DisplayName("Subscribe to updates")]
    public bool Subscribed { get; init; } = true;
}

Mesh.Edit(new ContactForm(), "catalogEditorDemo")
```

The rich editors are **node-bound**: they bind to a mesh node's content and auto-save through the node stream, so they need a layout-area host with a node path and cannot run standalone in a kernel cell:

```csharp
// Node-bound — not runnable standalone: requires a mesh node to bind to.
new MarkdownEditorControl { Value = markdown }
    .WithAutoSave(hubAddress, nodePath)                          // markdown w/ auto-save
MeshNodeContentEditorControl.ForType(nodePath, typeof(MyContent)) // typed content editor
```

See [Editor](/Doc/GUI/Editor) for the macro's attribute-driven mapping and reactive output, [Code Editor](/Doc/GUI/CodeEditor) for the Monaco-based code editor, and [Data Binding](/Doc/GUI/DataBinding) for how values flow.

## Mesh controls — MeshNodePicker, MeshSearch

Mesh controls work directly against mesh content. `MeshNodePicker` is the standard picker whenever content references other content — it searches with [query syntax](/Doc/DataMesh/QuerySyntax) and stores the selected node's **path**; never hand-build a select over node paths. `MeshSearch` and `SearchBox` provide free-text search surfaces, and `FileBrowser` navigates a content collection.

```csharp --render CatalogNodePicker --show-code
Controls.MeshNodePicker("Doc/GUI/DataGrid")
    .WithQueries("namespace:Doc/GUI scope:descendants nodeType:Markdown")
    .WithMaxResults(8)
    .WithLabel("Pick a documentation page")
```

See [Mesh Search](/Doc/GUI/MeshSearch) for the search surface and [Form Input Controls](/Doc/GUI/InputControls) for the picker's query options.

## Navigation — NavMenu, NavLink, NavGroup

Navigation menus compose from three controls: `NavMenu` (the container), `NavGroup` (a collapsible heading), and `NavLink` (a clickable link with optional `FluentIcons` icon). The URLs are ordinary mesh paths. To embed another hub's layout area inside a view, use `Controls.LayoutArea(address, area)`; to reference a named slot within the current area, `Controls.NamedArea(area)`.

```csharp --render CatalogNavMenu --show-code
Controls.NavMenu
    .WithNavLink("GUI Overview", "/Doc/GUI", FluentIcons.Home())
    .WithNavLink("Data Grid", "/Doc/GUI/DataGrid", FluentIcons.Table())
```

See [Navigation Menus](/Doc/GUI/Navigation) for grouped, collapsible menus and [Layout Areas](/Doc/GUI/LayoutAreas) for how areas nest.

## See also

- [User Interface](/Doc/Architecture/UserInterface) — how control trees travel to the browser and how click handlers run
- [GUI documentation](/Doc/GUI) — the full GUI area index
- [Data Binding](/Doc/GUI/DataBinding) — `JsonPointerReference` and the reactive data pipeline behind every input
