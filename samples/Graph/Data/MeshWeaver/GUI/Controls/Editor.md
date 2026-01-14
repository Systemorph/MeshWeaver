---
Name: Editor Control
Category: Documentation
Description: Generate editable forms from C# records with automatic field rendering
Icon: /static/storage/content/MeshWeaver/GUI/Controls/Editor/icon.svg
---

The Editor control automatically generates editable forms from C# records, mapping property types to appropriate input controls.

## Source Files

| File | Purpose |
|------|---------|
| `src/MeshWeaver.Layout/EditorControl.cs` | The control record definition |
| `src/MeshWeaver.Layout/EditorExtensions.cs` | Extension methods and type mapping logic |

## Basic Usage

### Example 1: Simple Form

```csharp
public record Person
{
    public string Name { get; init; }     // Renders as TextFieldControl
    public int Age { get; init; }         // Renders as NumberFieldControl
    public bool IsActive { get; init; }   // Renders as CheckBoxControl
}

host.Edit(new Person { Name = "Alice", Age = 30, IsActive = true })
```

**What you see above:**
- Text field for Name (string → TextFieldControl)
- Number field for Age (int → NumberFieldControl)
- Checkbox for IsActive (bool → CheckBoxControl)

### Example 2: Form with Reactive Output

```csharp
public record Calculator
{
    public double X { get; init; }   // First operand, renders as NumberFieldControl
    public double Y { get; init; }   // Second operand, renders as NumberFieldControl
}

host.Edit(
    new Calculator { X = 10, Y = 5 },                    // Initial values
    calc => Controls.Label($"Sum: {calc.X + calc.Y}")   // Reactive result, updates as you type
)
```

**What you see above:**
- Two number fields for X and Y
- A label below showing the sum
- The label updates automatically when you change either field

### Example 3: Form with Validation Attributes

```csharp
public record UserProfile
{
    [Required]                                                    // Field cannot be empty
    [Description("Your full name as it appears on documents")]   // Help text below field
    public string FullName { get; init; }

    [DisplayName("Email Address")]   // Custom label instead of "Email"
    public string Email { get; init; }

    [Range(18, 120)]                 // Only accepts values between 18 and 120
    public int Age { get; init; }

    [Browsable(false)]               // Hidden from form, not rendered
    public string InternalId { get; init; }
}

host.Edit(new UserProfile { Age = 25 })
```

**What you see above:**
- FullName: Required field with help text
- Email: Field labeled "Email Address"
- Age: Number field with range validation
- InternalId: Not visible (Browsable=false)

## Property Type Mapping

The editor automatically selects controls based on property type (see `EditorExtensions.cs:243-250`):

| Property Type | Rendered Control | Example |
|--------------|------------------|---------|
| `string` | Text field | `public string Name { get; init; }` |
| `int`, `double`, `decimal` | Number field | `public decimal Price { get; init; }` |
| `bool` | Checkbox | `public bool Enabled { get; init; }` |
| `DateTime` | Date/time picker | `public DateTime BirthDate { get; init; }` |

## Supported Attributes

Customize field behavior with attributes (processed in `EditorExtensions.cs:264-310`):

| Attribute | Effect | Example |
|-----------|--------|---------|
| `[Required]` | Field cannot be empty | `[Required] public string Name` |
| `[Description("...")]` | Help text below field | `[Description("Enter full name")]` |
| `[DisplayName("...")]` | Custom label | `[DisplayName("Full Name")]` |
| `[Browsable(false)]` | Hide from form | `[Browsable(false)] public string Id` |
| `[Range(min, max)]` | Numeric range validation | `[Range(0, 100)]` |
| `[Editable(false)]` | Read-only field | `[Editable(false)] public string Code` |

## Control Override

Use `[UiControl<T>]` to override the default control (see `EditorExtensions.cs:283-285`):

```csharp
public record Settings
{
    [UiControl<TextAreaControl>]   // Multi-line text instead of single-line
    public string Description { get; init; }

    [UiControl<RadioGroupControl>(Options = new[] { "Light", "Dark", "System" })]   // Radio buttons instead of text
    public string Theme { get; init; }
}

host.Edit(new Settings { Theme = "System" })
```

**What you see above:**
- Description: Multi-line text area
- Theme: Radio button group with three options

## Dimension Dropdowns

Use `[Dimension<T>]` to render a dropdown populated from a data source (see `EditorExtensions.cs:288-300`):

```csharp
public record Country
{
    [Key]                           // Primary key for the dimension
    public string Code { get; init; }
    public string Name { get; init; }
}

public record Address
{
    public string Street { get; init; }
    public string City { get; init; }

    [Dimension<Country>]            // Renders as dropdown populated with all Country records
    public string CountryCode { get; init; }
}
```

**Result:** CountryCode field renders as a dropdown populated with all Country records from the data context.

## How It Works

The `Edit<T>()` method (EditorExtensions.cs:71-79) reflects over all properties:

```csharp
typeof(T).GetProperties()                    // Get all public properties
    .Aggregate(new EditorControl(),          // Start with empty editor
        serviceProvider.MapToControl)        // Add a field for each property
```

Each property becomes a named area in the `EditorControl`. The reactive variant (EditorExtensions.cs:88-102) wraps the editor in a `StackControl` and adds a view that subscribes to form changes via `GetDataStream<T>()`.

## See Also

- [Property Attributes](MeshWeaver/GUI/Concepts/Attributes) - All supported attributes in detail
- [Data Binding](MeshWeaver/GUI/Concepts/DataBinding) - How form data flows
- [Stack Control](MeshWeaver/GUI/Controls/Stack) - Container for layouts
- [DataGrid Control](MeshWeaver/GUI/Controls/DataGrid) - Tabular data display
