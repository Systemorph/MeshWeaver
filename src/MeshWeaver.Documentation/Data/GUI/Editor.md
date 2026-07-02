---
Name: Adding Editable Forms to a UI
Category: Documentation
Description: Automatically generate editable forms from C# records, with type-driven field rendering, validation attributes, and reactive live output.
Icon: /static/DocContent/GUI/Editor/icon.svg
---

The Editor control turns a plain C# record into a fully interactive form — no markup, no field wiring. Annotate your properties with standard .NET attributes and the editor selects the right input control, applies validation, and streams live updates back to any reactive view you attach.
<svg viewBox="0 0 760 220" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="ed-arrow" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="220" rx="12" fill="#1a2030" opacity="0.55"/>
  <rect x="20" y="50" width="140" height="120" rx="10" fill="#1e3a5f" stroke="#1e88e5" stroke-width="1.5"/>
  <rect x="20" y="50" width="140" height="32" rx="10" fill="#1e88e5"/>
  <rect x="20" y="70" width="140" height="12" fill="#1e88e5"/>
  <text x="90" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">C# Record</text>
  <text x="36" y="100" fill="#90caf9" font-size="11">string Name</text>
  <text x="36" y="116" fill="#90caf9" font-size="11">int Age</text>
  <text x="36" y="132" fill="#90caf9" font-size="11">bool IsActive</text>
  <text x="36" y="148" fill="#90caf9" font-size="11">DateTime BirthDate</text>
  <rect x="200" y="10" width="150" height="80" rx="10" fill="#1b2a1e" stroke="#43a047" stroke-width="1.5"/>
  <rect x="200" y="10" width="150" height="32" rx="10" fill="#43a047"/>
  <rect x="200" y="30" width="150" height="12" fill="#43a047"/>
  <text x="275" y="32" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Attributes</text>
  <text x="216" y="62" fill="#a5d6a7" font-size="11">[Required]</text>
  <text x="216" y="78" fill="#a5d6a7" font-size="11">[DisplayName] [Range]</text>
  <text x="216" y="94" fill="#a5d6a7" font-size="11">[Browsable(false)]</text>
  <rect x="200" y="120" width="150" height="66" rx="10" fill="#2a1b3a" stroke="#8e24aa" stroke-width="1.5"/>
  <rect x="200" y="120" width="150" height="32" rx="10" fill="#8e24aa"/>
  <rect x="200" y="140" width="150" height="12" fill="#8e24aa"/>
  <text x="275" y="142" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Reflection</text>
  <text x="216" y="168" fill="#ce93d8" font-size="11">GetProperties() → MapToControl</text>
  <rect x="400" y="50" width="150" height="120" rx="10" fill="#1a2d3a" stroke="#5c6bc0" stroke-width="1.5"/>
  <rect x="400" y="50" width="150" height="32" rx="10" fill="#5c6bc0"/>
  <rect x="400" y="70" width="150" height="12" fill="#5c6bc0"/>
  <text x="475" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">EditorControl</text>
  <text x="416" y="100" fill="#b0bec5" font-size="11">TextFieldControl</text>
  <text x="416" y="116" fill="#b0bec5" font-size="11">NumberFieldControl</text>
  <text x="416" y="132" fill="#b0bec5" font-size="11">CheckBoxControl</text>
  <text x="416" y="148" fill="#b0bec5" font-size="11">DateTimeControl</text>
  <rect x="600" y="70" width="140" height="80" rx="10" fill="#1e2d1a" stroke="#26a69a" stroke-width="1.5"/>
  <rect x="600" y="70" width="140" height="32" rx="10" fill="#26a69a"/>
  <rect x="600" y="90" width="140" height="12" fill="#26a69a"/>
  <text x="670" y="92" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Reactive View</text>
  <text x="616" y="120" fill="#80cbc4" font-size="11">GetDataStream&lt;T&gt;()</text>
  <text x="616" y="136" fill="#80cbc4" font-size="11">live re-render</text>
  <line x1="160" y1="110" x2="198" y2="155" stroke="#90a4ae" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#ed-arrow)"/>
  <line x1="160" y1="90" x2="198" y2="55" stroke="#90a4ae" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#ed-arrow)"/>
  <line x1="350" y1="60" x2="398" y2="90" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#ed-arrow)"/>
  <line x1="350" y1="148" x2="398" y2="120" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#ed-arrow)"/>
  <line x1="550" y1="110" x2="598" y2="110" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#ed-arrow)"/>
  <text x="556" y="105" fill="#90a4ae" font-size="10">stream</text>
</svg>
*How the Editor works: record properties and attributes feed reflection, which maps each property to a typed field control; form changes stream to any attached reactive view.*

---

# Basic Usage

## Simple form

Declare a record and call `host.Edit(...)`. The editor reflects over every public property and renders an appropriate input:

```csharp
public record Person
{
    public string Name { get; init; }     // → TextFieldControl
    public int Age { get; init; }         // → NumberFieldControl
    public bool IsActive { get; init; }   // → CheckBoxControl
}

host.Edit(new Person { Name = "Alice", Age = 30, IsActive = true })
```

The three properties above produce a text field, a number field, and a checkbox — in that order.

## Form with reactive output

Pass a second argument to attach a live view that re-renders whenever the form data changes:

```csharp
public record Calculator
{
    public double X { get; init; }   // first operand
    public double Y { get; init; }   // second operand
}

host.Edit(
    new Calculator { X = 10, Y = 5 },
    calc => Controls.Label($"Sum: {calc.X + calc.Y}")   // updates as you type
)
```

The label below the two number fields recalculates on every keystroke. The reactive variant wraps the `EditorControl` in a `StackControl` and subscribes to form changes via `GetDataStream<T>()`.

## Form with validation attributes

Standard .NET data-annotation attributes are picked up automatically:

```csharp
public record UserProfile
{
    [Required]
    [Description("Your full name as it appears on documents")]
    public string FullName { get; init; }

    [DisplayName("Email Address")]
    public string Email { get; init; }

    [Range(18, 120)]
    public int Age { get; init; }

    [Browsable(false)]               // hidden — not rendered at all
    public string InternalId { get; init; }
}

host.Edit(new UserProfile { Age = 25 })
```

`FullName` shows a required indicator and a help-text line. `Email` carries the custom label. `Age` enforces the 18–120 range. `InternalId` is invisible to the user.

---

# Property Type Mapping

The editor chooses a control for each property based on its declared type:

| Property type | Rendered control | Typical example |
|---|---|---|
| `string` | Text field | `public string Name { get; init; }` |
| `int`, `double`, `decimal` | Number field | `public decimal Price { get; init; }` |
| `bool` | Checkbox | `public bool Enabled { get; init; }` |
| `DateTime` | Date/time picker | `public DateTime BirthDate { get; init; }` |

These defaults apply **only when no override attribute is present** — `[UiControl<T>]`, `[Dimension<T>]`, `[MeshNode]`, `[MeshNodeCollection]`, `[Markdown]`, and `[ContentItem]` each substitute their own control. See [Property Attributes](../Attributes) for the full catalogue.

---

# Supported Attributes

Apply any of these attributes directly to a property to alter how the field renders or validates:

| Attribute | Effect |
|---|---|
| `[Required]` | Field cannot be empty; shows a validation error when blank |
| `[Description("...")]` | Adds help text below the field |
| `[DisplayName("...")]` | Replaces the auto-generated label with a custom one |
| `[Browsable(false)]` | Hides the property entirely — it is never rendered |
| `[Range(min, max)]` | Restricts numeric input to the given inclusive range |
| `[Editable(false)]` | Renders the value as read-only; the user cannot change it |
| `[MeshNode("query")]` | Searchable mesh-node picker; stores the selected node's path |
| `[MeshNodeCollection("query")]` | Full-width inline chip collection of node references |
| `[Markdown]` | Markdown display + editor with preview / track-changes options |
| `[ContentItem("collection")]` | Text field + Browse button over a content collection |

---

# Control Override

When the default control is not the right fit, use `[UiControl<T>]` to substitute any compatible control:

```csharp
public record Settings
{
    [UiControl<TextAreaControl>]
    public string Description { get; init; }   // multi-line instead of single-line

    [UiControl<RadioGroupControl>(Options = new[] { "Light", "Dark", "System" })]
    public string Theme { get; init; }          // radio buttons instead of a text field
}

host.Edit(new Settings { Theme = "System" })
```

`Description` expands to a full multi-line text area. `Theme` becomes a three-option radio group with "System" pre-selected.

---

# Dimension Dropdowns

`[Dimension<T>]` populates a field with records from a data source, turning it into a searchable dropdown. The referenced type must declare a `[Key]` property:

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
    public string City { get; init; }

    [Dimension<Country>]
    public string CountryCode { get; init; }   // dropdown populated from all Country records
}
```

At render time, `CountryCode` fetches every `Country` from the data context and presents them in a dropdown keyed by `Code`.

---

# Live Example

The form below is generated **live** from the record definition — each property becomes the field
its type and attributes dictate (`string` → text field, `int` → number field, `bool` → checkbox,
`DateTime` → date/time picker):

```csharp --render EditorDemo --show-code
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

record Person
{
    [Required]
    [DisplayName("Full name")]
    public string Name { get; init; } = "Alice Example";

    [Range(18, 120)]
    public int Age { get; init; } = 34;

    [DisplayName("Active member")]
    public bool IsActive { get; init; } = true;

    [DisplayName("Birth date")]
    public DateTime BirthDate { get; init; } = new(1992, 3, 14);
}

Mesh.Edit(new Person(), "personDemo")
```

---

# How It Works

`Edit<T>()` reflects over all public properties at startup:

```csharp
typeof(T).GetProperties()           // enumerate every public property
    .Aggregate(new EditorControl(), // start with an empty editor container
        serviceProvider.MapToControl)  // add a named field for each property
```

Each property becomes a named area inside the `EditorControl`. The reactive overload wraps that control in a `StackControl` and appends a view that subscribes to form changes via `GetDataStream<T>()`, so the downstream view always sees the latest values.

---

# See Also

- [Property Attributes](../Attributes) — every supported attribute in detail
- [Data Binding](../DataBinding) — how form data flows through the reactive pipeline
- [Stack Control](../ContainerControl/Stack) — the container the reactive overload builds on
- [DataGrid Control](../DataGrid) — tabular data display for read-only collections
