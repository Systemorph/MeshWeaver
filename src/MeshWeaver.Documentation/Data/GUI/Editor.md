---
Name: Adding Editable Forms to a UI
Category: Documentation
Description: Automatically generate editable forms from C# records, with type-driven field rendering, validation attributes, and reactive live output.
Icon: /static/DocContent/GUI/Editor/icon.svg
---

The Editor control turns a plain C# record into a fully interactive form — no markup, no field wiring. Annotate your properties with standard .NET attributes and the editor selects the right input control, applies validation, and streams live updates back to any reactive view you attach.

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

The snippet below shows the form structure the editor would generate for a `Person`-style record. Paste it into any interactive notebook area to see the output:

```csharp --render EditorDemo --show-code
MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Markdown(
        "**Editor control mapping**\n\n" +
        "| Property | Type | Rendered as |\n" +
        "|---|---|---|\n" +
        "| `Name` | `string` | Text field |\n" +
        "| `Age` | `int` | Number field |\n" +
        "| `IsActive` | `bool` | Checkbox |\n" +
        "| `BirthDate` | `DateTime` | Date/time picker |\n\n" +
        "> Annotate any property with `[Required]`, `[DisplayName]`, `[Range]`, or `[Browsable(false)]` " +
        "to refine rendering and validation."
    ))
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
