---
Name: Property Attributes
Category: Documentation
Description: Attributes that control how properties are rendered and validated in editors
Icon: /static/storage/content/MeshWeaver/GUI/Concepts/Attributes/icon.svg
---

Property attributes control how the Editor control renders and validates form fields. They provide metadata that influences field appearance, behavior, and validation.

## Source Files

| File | Purpose |
|------|---------|
| `src/MeshWeaver.Layout/UiControlAttribute.cs` | Custom control override attribute |
| `src/MeshWeaver.Domain/DimensionAttribute.cs` | Dimension selector attribute |
| `src/MeshWeaver.Layout/EditorExtensions.cs` | Attribute processing logic (line 264-310) |

## Display Attributes

### [Description]

Adds help text below the field (EditorExtensions.cs:275-276):

```csharp
public record Person
{
    [Description("Enter your full legal name as it appears on official documents")]
    public string Name { get; init; }
}
```

**Result:** Help text appears below the input field.

### [DisplayName]

Overrides the label derived from property name (EditorExtensions.cs:267-268):

```csharp
public record Settings
{
    [DisplayName("Enable Email Notifications")]
    public bool NotificationsEnabled { get; init; }
}
```

**Result:** Field label shows "Enable Email Notifications" instead of "Notifications Enabled".

### [Browsable(false)]

Hides the property from the editor (EditorExtensions.cs:264-265):

```csharp
public record Entity
{
    [Browsable(false)]
    public string InternalId { get; init; }

    public string Name { get; init; }
}
```

**Result:** Only the Name field appears in the form.

## Validation Attributes

### [Required]

Makes the field mandatory (EditorExtensions.cs:404-406):

```csharp
public record User
{
    [Required]
    public string Email { get; init; }
}
```

**Result:** Field shows validation error if empty.

### [Range]

Restricts numeric range:

```csharp
public record Product
{
    [Range(0, 10000)]
    public decimal Price { get; init; }

    [Range(1, 100)]
    public int Quantity { get; init; }
}
```

**Result:** Values outside the range show validation errors.

### [Editable(false)]

Makes the field read-only (EditorExtensions.cs:408):

```csharp
public record Order
{
    [Editable(false)]
    public string OrderNumber { get; init; }

    public string Notes { get; init; }
}
```

**Result:** OrderNumber is displayed but cannot be edited.

## Control Override Attributes

### [UiControl<T>]

Overrides the default control type (EditorExtensions.cs:283-285):

```csharp
public record Preferences
{
    [UiControl<TextAreaControl>]
    public string Bio { get; init; }

    [UiControl<RadioGroupControl>(Options = new[] { "Light", "Dark", "System" })]
    public string Theme { get; init; }
}
```

**Result:** Bio renders as a multi-line text area; Theme renders as radio buttons.

### [Dimension<T>]

Renders a dropdown populated from a data source (EditorExtensions.cs:288-300):

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

**Result:** CountryCode renders as a dropdown populated with all Country records.

## Property Type to Control Mapping

Without attributes, the Editor maps types automatically (EditorExtensions.cs:243-250):

| Property Type | Default Control |
|--------------|-----------------|
| `string` | `TextFieldControl` |
| `int`, `double`, `decimal` | `NumberFieldControl` |
| `bool` | `CheckBoxControl` |
| `DateTime` | `DateTimeControl` |

## Combining Attributes

Attributes can be combined for complex validation:

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

## See Also

- [Editor Control](MeshWeaver/GUI/Controls/Editor) - How attributes are used in forms
- [DataBinding](MeshWeaver/GUI/Concepts/DataBinding) - How data flows through forms
