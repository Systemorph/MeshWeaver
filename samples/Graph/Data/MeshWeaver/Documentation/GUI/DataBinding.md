---
Name: Connecting UI Controls to Data
Category: Documentation
Description: How data flows between controls and the underlying data store
Icon: /static/storage/content/MeshWeaver/Documentation/GUI/DataBinding/icon.svg
---

Data binding in MeshWeaver connects UI controls to data through a reactive stream-based system. Understanding DataContext, data streams, and the binding flow is essential for building interactive forms.

## Core Concepts

### DataContext

Every control can have a `DataContext` - a reference to data in the stream:

```csharp
var editor = new EditorControl()
    .WithDataContext(new JsonPointerReference("myData"));
```

### JsonPointerReference

Points to a specific location in the data store using JSON Pointer syntax (RFC 6901):

```csharp
new JsonPointerReference("customers/123/name")
```

## Reading Data from Stream

### GetDataStream

Returns an observable of data at a specific ID:

```csharp
// Get a stream of data updates
host.GetDataStream<Person>("personId")
    .Subscribe(person => Console.WriteLine($"Name: {person.Name}"));

// Use in a dynamic view
Controls.Stack
    .WithView(host.GetDataStream<Order>("orderId")
        .Select(order => Controls.Label($"Total: {order.Total}")))
```

**Result:** The view updates automatically when the data changes.

## Writing Data

### UpdateData

Updates data in the stream:

```csharp
host.UpdateData("personId", new Person { Name = "Alice", Age = 30 });
```

## The Edit Pattern

When you call `host.Edit()`, it automatically:

1. Creates a unique data ID
2. Stores initial data in the stream
3. Creates an EditorControl with DataContext pointing to that ID
4. Returns controls bound to that data

```csharp
// Simple form
var editor = host.Edit(new Person { Name = "Alice" });

// With reactive result
host.Edit(
    new Calculator { X = 10, Y = 5 },
    calc => Controls.Label($"Sum: {calc.X + calc.Y}")
)
```

## Data Flow Example

### Form with Computed Display

```csharp
public record Order
{
    public decimal UnitPrice { get; init; }
    public int Quantity { get; init; }
}

host.Edit(
    new Order { UnitPrice = 10.00m, Quantity = 1 },
    order => Controls.Label($"Total: ${order.UnitPrice * order.Quantity:F2}")
)
```

**Data flow:**
1. Initial data stored in stream with generated ID
2. Editor fields bound to `UnitPrice` and `Quantity`
3. User edits Quantity field → stream updates
4. Result function receives new order from stream
5. Label re-renders with new total

### Form with External Submit

```csharp
var dataId = "formData";
var editor = host.Edit(new FormModel(), dataId);

var submitButton = Controls.Button("Submit")
    .WithClickAction(async ctx => {
        var data = await ctx.Host.GetDataStream<FormModel>(dataId).FirstAsync();
        await ProcessForm(data);
    });

Controls.Stack
    .WithView(editor)
    .WithView(submitButton)
```

## Template.Bind

The `Template.Bind` method connects data to control templates:

```csharp
Template.Bind(
    new Person { Name = "Alice" },
    p => Controls.Label($"Hello, {p.Name}!")
)
```

This creates a control with its DataContext pointing to the stored data.

## See Also

- [WithView Patterns](MeshWeaver/Documentation/GUI/ContainerControl) - How views subscribe to data
- [Observables](MeshWeaver/Documentation/GUI/Observables) - Static vs dynamic views
- [Editor Control](MeshWeaver/Documentation/GUI/Editor) - Data-bound form generation
