---
Name: Controlling Form Fields Through Attributes
Category: Documentation
Description: Standard .NET attributes that control how properties are rendered, labelled, validated, and hidden in the Editor control
Icon: /static/DocContent/GUI/Attributes/icon.svg
---

The Editor control reads standard .NET attributes on your record properties and automatically adjusts how each field is labelled, validated, hidden, or rendered. You annotate the data model once and every form that binds to it picks up the behaviour — no per-field UI code required.

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

## Default Type-to-Control Mapping

When no override attribute is present, the Editor picks the most appropriate control for each property type:

| Property Type | Default Control |
|---|---|
| `string` | `TextFieldControl` |
| `int`, `double`, `decimal` | `NumberFieldControl` |
| `bool` | `CheckBoxControl` |
| `DateTime` | `DateTimeControl` |

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
        "| `[Dimension<T>]` | Dropdown from data source |"
    ))
```

---

## See Also

- [Editor Control](../Editor) — how these attributes are consumed when rendering forms
- [DataBinding](../DataBinding) — how data flows into and out of the Editor
