---
Name: Controlling Form Fields Through Attributes
Category: Documentation
Description: Standard .NET attributes that control how properties are rendered, labelled, validated, and hidden in the Editor control
Icon: /static/DocContent/GUI/Attributes/icon.svg
---

The Editor control reads standard .NET attributes on your record properties and automatically adjusts how each field is labelled, validated, hidden, or rendered. You annotate the data model once and every form that binds to it picks up the behaviour — no per-field UI code required.
<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="320" rx="14" fill="none"/>
  <text x="380" y="26" text-anchor="middle" font-size="14" font-weight="bold" fill="currentColor" fill-opacity=".75">Attribute → Editor Field Rendering Pipeline</text>
  <rect x="20" y="50" width="160" height="220" rx="10" fill="#1e1e2e" stroke="currentColor" stroke-opacity=".25" stroke-width="1.2"/>
  <text x="100" y="72" text-anchor="middle" font-size="12" font-weight="bold" fill="#90caf9">Data Model</text>
  <rect x="32" y="82" width="136" height="28" rx="6" fill="#1565c0"/>
  <text x="100" y="101" text-anchor="middle" fill="#fff" font-size="11">[DisplayName]</text>
  <rect x="32" y="116" width="136" height="28" rx="6" fill="#1565c0"/>
  <text x="100" y="135" text-anchor="middle" fill="#fff" font-size="11">[Description]</text>
  <rect x="32" y="150" width="136" height="28" rx="6" fill="#1565c0"/>
  <text x="100" y="169" text-anchor="middle" fill="#fff" font-size="11">[Browsable(false)]</text>
  <rect x="32" y="186" width="136" height="28" rx="6" fill="#2e7d32"/>
  <text x="100" y="205" text-anchor="middle" fill="#fff" font-size="11">[Required]  [Range]</text>
  <rect x="32" y="220" width="136" height="28" rx="6" fill="#2e7d32"/>
  <text x="100" y="239" text-anchor="middle" fill="#fff" font-size="11">[Editable(false)]</text>
  <rect x="32" y="254" width="136" height="28" rx="6" fill="#6a1a9a"/>
  <text x="100" y="268" text-anchor="middle" fill="#fff" font-size="10">[UiControl&lt;T&gt;]</text>
  <text x="100" y="280" text-anchor="middle" fill="#fff" font-size="10">[Dimension&lt;T&gt;]</text>
  <rect x="290" y="50" width="180" height="62" rx="10" fill="#37474f" stroke="currentColor" stroke-opacity=".25" stroke-width="1.2"/>
  <text x="380" y="73" text-anchor="middle" font-size="12" font-weight="bold" fill="#b0bec5">Editor Control</text>
  <text x="380" y="91" text-anchor="middle" font-size="11" fill="#90a4ae">reads attributes at render time</text>
  <text x="380" y="107" text-anchor="middle" font-size="11" fill="#90a4ae">applies behaviour per property</text>
  <line x1="196" y1="160" x2="286" y2="100" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="530" y="50" width="210" height="220" rx="10" fill="#1e1e2e" stroke="currentColor" stroke-opacity=".25" stroke-width="1.2"/>
  <text x="635" y="72" text-anchor="middle" font-size="12" font-weight="bold" fill="#90caf9">Rendered Form Field</text>
  <rect x="542" y="82" width="186" height="28" rx="6" fill="#1565c0"/>
  <text x="635" y="101" text-anchor="middle" fill="#fff" font-size="11">Custom label / help text</text>
  <rect x="542" y="116" width="186" height="28" rx="6" fill="#37474f"/>
  <text x="635" y="135" text-anchor="middle" fill="#b0bec5" font-size="11">Field hidden entirely</text>
  <rect x="542" y="150" width="186" height="28" rx="6" fill="#2e7d32"/>
  <text x="635" y="169" text-anchor="middle" fill="#fff" font-size="11">Inline validation errors</text>
  <rect x="542" y="184" width="186" height="28" rx="6" fill="#37474f"/>
  <text x="635" y="203" text-anchor="middle" fill="#b0bec5" font-size="11">Read-only display</text>
  <rect x="542" y="218" width="186" height="28" rx="6" fill="#6a1a9a"/>
  <text x="635" y="237" text-anchor="middle" fill="#fff" font-size="11">Custom control / dropdown</text>
  <line x1="474" y1="100" x2="526" y2="130" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="380" y="285" text-anchor="middle" font-size="11" fill="currentColor" fill-opacity=".5">type-to-control defaults apply when no override is present</text>
</svg>

*Attributes on the data model are read once by the Editor at render time and map directly to visual field behaviour.*

---

## Display Attributes

### `[Description]`

Adds explanatory help text directly below the field, giving users the context they need without cluttering the label.

```csharp
public record Person
{
    [Description("Enter your full legal name as it appears on official documents")]
    public string Name { get; init; }
}
```

The description appears as a subtle hint beneath the input.

---

### `[DisplayName]`

Overrides the label that would otherwise be derived from the property name. Useful when the code name is abbreviated, technical, or ambiguous.

```csharp
public record Settings
{
    [DisplayName("Enable Email Notifications")]
    public bool NotificationsEnabled { get; init; }
}
```

The field label shows **"Enable Email Notifications"** instead of the auto-generated *"Notifications Enabled"*.

---

### `[Browsable(false)]`

Completely hides a property from the rendered form. Internal IDs, computed fields, and implementation details that have no place in a user-facing editor belong here.

```csharp
public record Entity
{
    [Browsable(false)]
    public string InternalId { get; init; }

    public string Name { get; init; }
}
```

Only `Name` appears in the form; `InternalId` is invisible.

---

## Validation Attributes

### `[Required]`

Marks a field as mandatory. The Editor will show a validation error and prevent submission until the field has a value.

```csharp
public record User
{
    [Required]
    public string Email { get; init; }
}
```

---

### `[Range]`

Constrains a numeric field to a minimum and maximum. Works for integers, decimals, and doubles.

```csharp
public record Product
{
    [Range(0, 10000)]
    public decimal Price { get; init; }

    [Range(1, 100)]
    public int Quantity { get; init; }
}
```

Values outside the declared range surface inline validation errors immediately.

---

### `[Editable(false)]`

Renders a field as read-only — the value is visible but cannot be changed. Perfect for system-assigned fields like order numbers or record IDs that you still want users to see.

```csharp
public record Order
{
    [Editable(false)]
    public string OrderNumber { get; init; }

    public string Notes { get; init; }
}
```

`OrderNumber` is displayed but locked; `Notes` remains editable.

---

## Control Override Attributes

### `[UiControl<T>]`

Replaces the default control chosen by type inference with a specific control type. Pass options as named parameters when the target control accepts configuration.

```csharp
public record Preferences
{
    [UiControl<TextAreaControl>]
    public string Bio { get; init; }

    [UiControl<RadioGroupControl>(Options = new[] { "Light", "Dark", "System" })]
    public string Theme { get; init; }
}
```

`Bio` renders as a multi-line text area; `Theme` renders as a radio button group.

---

### `[Dimension<T>]`

Populates a dropdown from a typed data source. Decorate the foreign-key property with the entity type that holds the valid values. The Editor queries all records of that type and presents them as selectable options.

```csharp
public record Country
{
    [Key]
    public string Code { get; init; }
    public string Name { get; init; }
}

public record Address
{
    public string Street { get; init; }

    [Dimension<Country>]
    public string CountryCode { get; init; }
}
```

`CountryCode` renders as a dropdown pre-populated with every `Country` record in the mesh.

---

### `[MeshNode]`

Marks a `string` property as a reference to a **mesh node**. The Editor renders a searchable `MeshNodePickerControl`; the selected node's **path** is stored as the property value. This is the standard way to pick a node — never hand-build a select + search.

```csharp
public record TaskItem
{
    // Multiple queries run in parallel and merge; the user's typed text is appended to each.
    [MeshNode("nodeType:User namespace:{node.namespace}")]
    public string? AssigneePath { get; init; }

    // Compact picker opening upwards, auto-selecting the first result when unset —
    // the shape the chat composer uses for its agent/model pickers.
    [MeshNode("nodeType:Agent namespace:Agent",
        Layout = MeshNodePickerLayout.Thin,
        Open = MeshNodePickerOpenDirection.Up,
        DefaultToFirst = true)]
    public string? AgentPath { get; init; }
}
```

| Option | Effect |
|---|---|
| `Queries` (ctor args) | Query strings (see [Query Syntax](/Doc/DataMesh/QuerySyntax)) run in parallel and merged. Template variables `{node.namespace}` / `{node.path}` resolve against the editing context at render time; `{node.PropertyName}` resolves against the bound object. |
| `Layout` | `Default` (full card: avatar, name, node-type subtitle) or `Thin` (small icon + name, minimal padding for tight rows). |
| `Open` | `Down` (default) or `Up` — open the dropdown above the field when it is anchored to the bottom of the viewport. |
| `DefaultToFirst` | Opt-in: when no value is set, auto-select (and persist) the first available result. |

The read-only rendering is a plain label showing the stored path.

---

### `[MeshNodeCollection]`

The collection counterpart of `[MeshNode]`: marks a collection property as holding mesh-node references. The Editor renders a full-width inline collection section — existing entries as chips, with add/remove actions when the property is editable. Queries use the same syntax and template variables as `[MeshNode]`.

```csharp
public record Team
{
    [MeshNodeCollection("nodeType:User namespace:{node.namespace}")]
    public ImmutableList<string> MemberPaths { get; init; } = [];
}
```

---

### `[Markdown]`

Renders a `string` property as markdown: `MarkdownControl` for display, `MarkdownEditorControl` for editing (own edit button by default — `SeparateEditView = true`).

```csharp
public record Article
{
    [Markdown(EditorHeight = "400px", ShowPreview = true, TrackChanges = false,
        Placeholder = "Write the article body…")]
    public string Body { get; init; } = "";
}
```

| Option | Default | Effect |
|---|---|---|
| `EditorHeight` | `"300px"` | Height of the editor area |
| `ShowPreview` | `true` | Side-by-side preview while editing |
| `TrackChanges` | `false` | Enable tracked-changes annotations |
| `Placeholder` | "Enter content…" | Hint shown when empty |

---

### `[ContentItem]`

Marks a `string` property as a reference to a file in a **content collection** (image URL, attachment, …). The Editor renders a text field with a **Browse** button that opens a modal file browser over the node's content collection.

```csharp
public record Profile
{
    [ContentItem]               // browses the default "content" collection
    public string? AvatarUrl { get; init; }

    [ContentItem("uploads")]    // browse a specific collection
    public string? AttachmentPath { get; init; }
}
```

---

## Default Type-to-Control Mapping

When no override attribute is present, the Editor picks the most appropriate control for each property type:

| Property Type | Default Control |
|---|---|
| `string` | `TextFieldControl` |
| `int`, `double`, `decimal` | `NumberFieldControl` |
| `bool` | `CheckBoxControl` |
| `DateTime` | `DateTimeControl` |

These defaults apply **only when no override attribute is present** — `[UiControl<T>]`, `[Dimension<T>]`, `[MeshNode]`, `[MeshNodeCollection]`, `[Markdown]`, and `[ContentItem]` all take precedence.

---

## Combining Attributes

Attributes compose freely. Stack display, validation, and control-override attributes on the same property to express exactly the behaviour you need.

```csharp
public record Employee
{
    [Required]
    [Description("Full name as it appears on official documents")]
    public string FullName { get; init; }

    [Required]
    [Description("Work email address")]
    public string WorkEmail { get; init; }

    [Range(18, 100)]
    [Description("Must be at least 18")]
    public int Age { get; init; }

    [Browsable(false)]
    public string InternalCode { get; init; }
}
```

---

## Live Example

The snippet below renders a quick reference card summarising which attribute controls which aspect of a field. It runs directly in the kernel so you can experiment by modifying the markup.

```csharp --render AttributeSummaryCard --show-code
MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Markdown("### Attribute Quick Reference"))
    .WithView(MeshWeaver.Layout.Controls.Markdown(
        "| Attribute | Effect |\n" +
        "|---|---|\n" +
        "| `[Description(\"...\")]` | Help text below the field |\n" +
        "| `[DisplayName(\"...\")]` | Custom field label |\n" +
        "| `[Browsable(false)]` | Hides the field entirely |\n" +
        "| `[Required]` | Field must have a value |\n" +
        "| `[Range(min, max)]` | Numeric bounds validation |\n" +
        "| `[Editable(false)]` | Read-only display |\n" +
        "| `[UiControl<T>]` | Override rendered control type |\n" +
        "| `[Dimension<T>]` | Dropdown from data source |\n" +
        "| `[MeshNode(\"query\")]` | Searchable mesh-node picker (stores the path) |\n" +
        "| `[MeshNodeCollection(\"query\")]` | Inline chip collection of node references |\n" +
        "| `[Markdown]` | Markdown display + editor |\n" +
        "| `[ContentItem]` | Text field + Browse over a content collection |"
    ))
```

---

## See Also

- [Editor Control](../Editor) — how these attributes are consumed when rendering forms
- [DataBinding](../DataBinding) — how data flows into and out of the Editor
