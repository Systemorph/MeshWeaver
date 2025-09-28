## How to Build New Domains (Following Todo Pattern)

### Creating a New Domain Module

**Step 1**: Create domain project structure following Todo:
```
src/MeshWeaverApp1.YourDomain/
├── Domain/
│   ├── YourEntity.cs
│   └── RelatedEntity.cs
├── LayoutAreas/
│   ├── YourDomainLayoutAreas.cs
│   └── YourDomainToolbar.cs
├── SampleData/
│   └── YourDomainSampleData.cs
├── Messages/
│   ├── YourDomainRequests.cs
│   ├── YourDomainResponses.cs
│   └── YourDomainActions.cs
├── YourDomainApplicationAttribute.cs
└── YourDomainApplicationExtensions.cs
```

**Step 2**: Define your domain entity following the Todo pattern:
```csharp
public record YourEntity
{
    [Key]
    [Browsable(false)]
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    [Editable(false)]
    [Browsable(false)]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
```

### Adding Layout Areas with Reactive Streams

**Pattern**: Follow Todo's reactive stream approach for automatic UI updates:

```csharp
public static class YourDomainLayoutAreas
{
    [Display(GroupName = "1. Main Views", Order = 1)]
    public static IObservable<UiControl?> YourMainView(LayoutAreaHost host, RenderingContext context)
    {
        return host.Workspace
            .GetStream<YourEntity>()!
            .CombineLatest(
                host.Workspace.GetStream<RelatedEntity>()!,
                (yourEntities, relatedEntities) =>
                    CreateYourMainView(yourEntities!, relatedEntities!, host)
            )
            .StartWith(Controls.Markdown("# 📋 Your Domain\\n\\n*Loading...*"));
    }

    private static UiControl CreateYourMainView(
        IReadOnlyCollection<YourEntity> entities,
        IReadOnlyCollection<RelatedEntity> relatedEntities,
        LayoutAreaHost host)
    {
        var grid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Add header
        grid = grid.WithView(
            Controls.H2("📋 Your Domain Management")
                .WithStyle(style => style.WithMarginBottom("6px")),
            skin => skin.WithXs(12).WithSm(9).WithMd(10)
        );

        // Add create button following Todo pattern
        grid = grid.WithView(
            Controls.MenuItem("➕ Create New", "add")
                .WithClickAction(_ => { CreateNewEntity(host); return Task.CompletedTask; }),
            skin => skin.WithXs(12).WithSm(3).WithMd(2)
        );

        // List entities with edit actions
        foreach (var entity in entities.OrderBy(e => e.Name))
        {
            var editAction = Controls.MenuItem("✏️ Edit", "edit")
                .WithClickAction(_ => { EditEntity(host, entity); return Task.CompletedTask; });

            grid = grid
                .WithView(Controls.Markdown($"**{entity.Name}** - {entity.Description}"),
                    skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(editAction,
                    skin => skin.WithXs(12).WithSm(3).WithMd(2));
        }

        return grid;
    }
}
```

### Implementing Edit Dialogs (Todo-Style Pattern)

**Core Pattern**: Use `host.Edit()` for automatic data binding and persistence:

```csharp
private static void EditEntity(LayoutAreaHost host, YourEntity entity)
{
    var editDataId = $"EditData_{entity.Id}";

    var editForm = Controls.Stack
        .WithView(Controls.H5($"Edit {entity.Name}")
            .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
        .WithView(host.Edit(entity, editDataId)?
            .WithStyle(style => style.WithWidth("100%").WithDisplay("block")), editDataId)
        .WithView(Controls.Stack
            .WithView(Controls.Button("💾 Done")
                .WithClickAction(_ =>
                {
                    host.UpdateArea(DialogControl.DialogArea, null!);
                    return Task.CompletedTask;
                }))
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(10)
            .WithStyle(style => style.WithJustifyContent("center").WithWidth("100%")))
        .WithVerticalGap(15)
        .WithStyle(style => style.WithWidth("100%").WithDisplay("block").WithMargin("0 auto"));

    var dialog = Controls.Dialog(editForm, "Edit Entity")
        .WithSize("M")
        .WithClosable(false);

    host.UpdateArea(DialogControl.DialogArea, dialog);
}

private static void CreateNewEntity(LayoutAreaHost host)
{
    var newEntity = new YourEntity
    {
        Id = Guid.NewGuid().AsString(),
        Name = "",
        Description = ""
    };

    const string newEntityDataId = "NewEntityData";
    // Use same edit pattern - host.Edit works for both create and update
    EditEntityWithDataId(host, newEntity, newEntityDataId, "Create New Entity");
}
```

### Adding Toolbars with Dimensions

**Pattern**: Follow standard toolbar pattern for entity selection:

```csharp
public static class YourDomainToolbar
{
    public static IObservable<UiControl?> YourEntitySelector(LayoutAreaHost host, RenderingContext context)
    {
        return host.Workspace
            .GetStream<YourEntity>()!
            .Select(entities => CreateEntitySelector(entities!, host))
            .StartWith(null);
    }

    private static UiControl CreateEntitySelector(
        IReadOnlyCollection<YourEntity> entities,
        LayoutAreaHost host)
    {
        var selectedEntityId = host.GetDimension(YourDomainDimension.SelectedEntityId);

        return Controls.Select
            .WithItems(entities.Select(e => new SelectItem(e.Id, e.Name)).ToList())
            .WithSelectedValue(selectedEntityId)
            .WithPlaceholder("Select Entity...")
            .WithSelectionChanged(entityId =>
            {
                host.SetDimension(YourDomainDimension.SelectedEntityId, entityId);
                return Task.CompletedTask;
            });
    }
}

public static class YourDomainDimension
{
    public const string SelectedEntityId = nameof(SelectedEntityId);
}
```

### Advanced Toolbar Dimensions with `[Dimension<T>]` Attribute

**Pattern**: Use the `[Dimension<MyDimensionType>]` attribute for strongly-typed dimensions:

```csharp
// Step 1: Create a dimension type that implements INamed
public record YourEntityDimension(string Value) : INamed
{
    public string Name => Value;

    // Static instances for common values
    public static YourEntityDimension All => new("all");
    public static YourEntityDimension Active => new("active");
    public static YourEntityDimension Inactive => new("inactive");
}

// Step 2: Create toolbar method with [Dimension<T>] attribute
public static class YourDomainToolbar
{
    [Dimension<YourEntityDimension>]
    public static IObservable<UiControl?> EntityStatusFilter(LayoutAreaHost host, RenderingContext context)
    {
        return Observable.Return(CreateStatusFilter(host));
    }

    private static UiControl CreateStatusFilter(LayoutAreaHost host)
    {
        var currentFilter = host.GetDimension<YourEntityDimension>() ?? YourEntityDimension.All;

        var filterOptions = new List<SelectItem>
        {
            new(YourEntityDimension.All.Value, "All Entities"),
            new(YourEntityDimension.Active.Value, "Active Only"),
            new(YourEntityDimension.Inactive.Value, "Inactive Only")
        };

        return Controls.Select
            .WithItems(filterOptions)
            .WithSelectedValue(currentFilter.Value)
            .WithPlaceholder("Filter Status...")
            .WithSelectionChanged(filterValue =>
            {
                var dimension = new YourEntityDimension(filterValue);
                host.SetDimension(dimension);
                return Task.CompletedTask;
            });
    }
}

// Step 3: Use dimension in layout areas
public static IObservable<UiControl?> YourMainView(LayoutAreaHost host, RenderingContext context)
{
    return host.Workspace
        .GetStream<YourEntity>()!
        .CombineLatest(
            host.ObserveDimension<YourEntityDimension>(),
            (entities, statusFilter) => CreateFilteredView(entities!, statusFilter, host)
        )
        .StartWith(Controls.Markdown("# Loading..."));
}

private static UiControl CreateFilteredView(
    IReadOnlyCollection<YourEntity> entities,
    YourEntityDimension? statusFilter,
    LayoutAreaHost host)
{
    var filteredEntities = statusFilter?.Value switch
    {
        "active" => entities.Where(e => e.IsActive).ToList(),
        "inactive" => entities.Where(e => !e.IsActive).ToList(),
        _ => entities.ToList()
    };

    return CreateEntityList(filteredEntities, host);
}
```

**Benefits of `[Dimension<T>]` Attribute Pattern**:
- **Type Safety**: Strongly-typed dimensions prevent string-based errors
- **IntelliSense**: Full IDE support for dimension values
- **Automatic Integration**: Framework automatically manages dimension lifecycle
- **Observable Dimensions**: Use `host.ObserveDimension<T>()` for reactive updates
- **Named Interface**: `INamed` provides consistent naming across the system

### Creating Sample Data (Following Todo Pattern)

```csharp
public static class YourDomainSampleData
{
    private static SampleDataSet? _cachedData;

    public static SampleDataSet GetSampleDataSet()
    {
        return _cachedData ??= GenerateSampleDataSet();
    }

    public static IReadOnlyList<YourEntity> GetSampleEntities()
    {
        return GetSampleDataSet().Entities;
    }

    private static SampleDataSet GenerateSampleDataSet()
    {
        var entities = GenerateEntities();
        var relatedEntities = GenerateRelatedEntities(entities);

        return new SampleDataSet(entities, relatedEntities);
    }

    private static IReadOnlyList<YourEntity> GenerateEntities()
    {
        return new List<YourEntity>
        {
            new()
            {
                Id = "entity-1",
                Name = "Sample Entity 1",
                Description = "First sample entity"
            },
            new()
            {
                Id = "entity-2",
                Name = "Sample Entity 2",
                Description = "Second sample entity"
            }
        };
    }
}

public record SampleDataSet(
    IReadOnlyList<YourEntity> Entities,
    IReadOnlyList<RelatedEntity> RelatedEntities
);
```

### Configuring Application Extensions (Following Todo Pattern)

**Step 3**: Configure your domain in the application extensions:

```csharp
public static class YourDomainApplicationExtensions
{
    public static MessageHubConfiguration ConfigureYourDomainApplication(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(
                typeof(YourDomainActionType),
                typeof(YourDomainStatus)
            )
            .AddData(data =>
                data.AddSource(dataSource =>
                    dataSource.WithType<YourEntity>(t =>
                        t.WithKey(entity => entity.Id)
                         .WithInitialData(YourDomainSampleData.GetSampleEntities())
                    )
                )
                .AddSource(dataSource =>
                    dataSource.WithType<RelatedEntity>(t =>
                        t.WithKey(related => related.Id)
                         .WithInitialData(YourDomainSampleData.GetSampleRelatedEntities())
                    )
                )
            )
            .AddLayout(layout =>
                layout.WithView(nameof(YourDomainLayoutAreas.YourMainView), YourDomainLayoutAreas.YourMainView)
                      .WithView(nameof(YourDomainLayoutAreas.YourSecondaryView), YourDomainLayoutAreas.YourSecondaryView)
                      .WithThumbnailBasePath("YourDomain/thumbnails")
            );
    }
}

[MessageHubApplication("app/yourdomain")]
public class YourDomainApplicationAttribute : Attribute;
```

### Essential Imports and Dependencies

**Required imports** for layout areas with editing functionality:
```csharp
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Data;              // Essential for host.Edit() functionality
using MeshWeaver.ShortGuid;         // For Guid.AsString() extension
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
```

### Working with Dimensions for State Management

**Pattern**: Use dimensions for view-level state (like selected items, filters, etc.):

```csharp
// In your layout area
private static UiControl CreateViewWithSelection(
    IReadOnlyCollection<YourEntity> entities,
    LayoutAreaHost host)
{
    // Get current selection from dimension
    var selectedEntityId = host.GetDimension(YourDomainDimension.SelectedEntityId);
    var selectedEntity = entities.FirstOrDefault(e => e.Id == selectedEntityId);

    if (selectedEntity == null)
    {
        return Controls.Markdown("*Please select an entity in the toolbar.*");
    }

    // Build view based on selection
    return CreateEntityDetailView(selectedEntity, host);
}

// In your toolbar
private static UiControl CreateEntitySelector(
    IReadOnlyCollection<YourEntity> entities,
    LayoutAreaHost host)
{
    var selectedEntityId = host.GetDimension(YourDomainDimension.SelectedEntityId);

    return Controls.Select
        .WithItems(entities.Select(e => new SelectItem(e.Id, e.Name)).ToList())
        .WithSelectedValue(selectedEntityId)
        .WithSelectionChanged(entityId =>
        {
            host.SetDimension(YourDomainDimension.SelectedEntityId, entityId);
            return Task.CompletedTask;
        });
}
```

### Creating Views with Grid Layouts

**Pattern**: Use responsive grid layouts for professional-looking UIs:

```csharp
private static UiControl CreateResponsiveView(
    IReadOnlyCollection<YourEntity> entities,
    LayoutAreaHost host)
{
    var grid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

    // Header with actions
    grid = grid
        .WithView(Controls.H2("📋 Entity Management")
            .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
            skin => skin.WithXs(12).WithSm(9).WithMd(10))
        .WithView(Controls.MenuItem("➕ Create New", "add")
            .WithClickAction(_ => { CreateNewEntity(host); return Task.CompletedTask; })
            .WithAppearance(Appearance.Neutral),
            skin => skin.WithXs(12).WithSm(3).WithMd(2));

    // Entity list with responsive layout
    foreach (var entity in entities.OrderBy(e => e.Name))
    {
        grid = grid
            .WithView(CreateEntitySummary(entity),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(CreateEntityActions(entity, host),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));
    }

    return grid;
}

private static UiControl CreateEntityActions(YourEntity entity, LayoutAreaHost host)
{
    return Controls.MenuItem("✏️ Edit", "edit")
        .WithClickAction(_ => { EditEntity(host, entity); return Task.CompletedTask; })
        .WithAppearance(Appearance.Neutral)
        .WithStyle(style =>
            style.WithDisplay("flex")
                 .WithAlignItems("center")
                 .WithJustifyContent("flex-end"));
}
```

## Domain Design Best Practices

### Entity Design Guidelines

**Use proven patterns from Todo domain**:
- `record` types for immutable entities
- `required string Id` for entity identifiers (follow "kebab-case" like "entity-1")
- Strategic use of `[Key]`, `[Browsable(false)]`, `[Editable(false)]` attributes
- Meaningful default values for properties

### Building Multi-Entity Domains

**Pattern**: Separate concerns for different workflows:

```csharp
// Core entity
public record Customer
{
    [Key] [Browsable(false)]
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}

// Related entity for different workflow
public record CustomerOrder
{
    [Key] [Browsable(false)]
    public required string Id { get; init; }

    public string CustomerId { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; } = DateTime.UtcNow;
    public decimal Amount { get; init; } = 0m;
}

// Workflow tracking entity
public record OrderStatus
{
    [Key] [Browsable(false)]
    public required string Id { get; init; }

    public string OrderId { get; init; } = string.Empty;
    public string Status { get; init; } = "Pending";
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}
```

This separation enables:
- Independent CRUD operations on each entity
- Clean data relationships without complex joins
- Flexible reporting and workflow management
- Easy testing of individual entity behaviors

### Creating Professional UI Layouts

**Pattern**: Use consistent styling and responsive design:

```csharp
private static UiControl CreateProfessionalHeader(string title, string subtitle, LayoutAreaHost host)
{
    return Controls.Stack
        .WithView(Controls.H2($"📊 {title}")
            .WithStyle(style => style
                .WithMarginBottom("4px")
                .WithColor("var(--color-fg-default)")
                .WithFontWeight("600")))
        .WithView(Controls.Markdown($"*{subtitle}*")
            .WithStyle(style => style
                .WithMarginBottom("20px")
                .WithColor("var(--color-fg-muted)")
                .WithFontSize("14px")))
        .WithVerticalGap(2);
}
```

### Working with Data Calculations

**Pattern**: Follow proven calculation approaches:

```csharp
private static UiControl CreateCalculatedSummary(
    IReadOnlyCollection<OrderEntity> orders,
    IReadOnlyCollection<CustomerEntity> customers)
{
    var totalOrders = orders.Count;
    var totalRevenue = orders.Sum(o => o.Amount);
    var avgOrderValue = totalOrders > 0 ? totalRevenue / totalOrders : 0;

    var summary = Controls.LayoutGrid
        .WithView(CreateMetricCard("Total Orders", totalOrders.ToString("N0")),
            skin => skin.WithXs(12).WithSm(4))
        .WithView(CreateMetricCard("Total Revenue", $"${totalRevenue:N2}"),
            skin => skin.WithXs(12).WithSm(4))
        .WithView(CreateMetricCard("Avg Order", $"${avgOrderValue:N2}"),
            skin => skin.WithXs(12).WithSm(4));

    return summary;
}

private static UiControl CreateMetricCard(string label, string value)
{
    return Controls.Stack
        .WithView(Controls.Markdown($"**{value}**")
            .WithStyle(style => style.WithFontSize("24px").WithTextAlign("center")))
        .WithView(Controls.Markdown(label)
            .WithStyle(style => style.WithColor("var(--color-fg-muted)").WithTextAlign("center")))
        .WithStyle(style => style.WithPadding("16px").WithBorder("1px solid var(--color-border-default)").WithBorderRadius("8px"));
}
```

**Key Data Binding Concepts**:

1. **JsonPointerReference**: Points to specific array indices in the data stream
   - `LayoutAreaReference.GetDataPointer(key)` gets the pointer to your data
   - `/{i}` references the specific array index for each month/item

2. **Data Stream Pattern**: Two-way binding with reactive streams
   - **Load**: `host.Workspace.GetStream<T>()` → `host.UpdateData(key, array)`
   - **Save**: `host.GetDataStream<double[]>(key)` → `DataChangeRequest`

3. **ImmutableDictionary Caching**: Efficient change detection
   - Cache entities by key for fast lookups
   - Detect actual changes before posting updates
   - Handle both updates and deletions

4. **Controls.Number Binding**: Real-time editable number fields
   - `Controls.Number(JsonPointerReference, typeof(Double))` for double values
   - Automatically syncs UI changes to data stream
   - Supports various numeric types (int, double, decimal)

**Alternative: Template.Bind Pattern** 

Use this pattern to data bind entity types to views.
```csharp
private static UiControl UpdatingView()
{
    var toolbar = new Toolbar(2024);

    return Controls
        .Stack
        .WithView(Template.Bind(toolbar, tb => Controls.Text(tb.Year), nameof(toolbar)), "Toolbar")
        .WithView((area, _) =>
            area.GetDataStream<Toolbar>(nameof(toolbar))
                .Select(tb => Controls.Html($"Report for year {tb?.Year}")), "Content");
}
```

If you have an array of types, use BindMany:

```csharp
private object ItemTemplate(IReadOnlyCollection<DataRecord> data) =>
    data.BindMany(record => Controls.Text(record.DisplayName).WithId(record.SystemName));
```