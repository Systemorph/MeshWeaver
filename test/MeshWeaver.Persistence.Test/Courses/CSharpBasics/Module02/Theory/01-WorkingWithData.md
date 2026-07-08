---
Name: Working with Data
NodeType: Markdown
---

# Lesson 2 — Tabular data with `DataGrid`

The framework renders structured data with a **control**, never hand-built HTML. Bind a plain
row record to `Controls.DataGrid` and get sorting, formatting, and theming for free.

```csharp --render ProductGrid --show-code
record Product(string Name, decimal Price, int Stock);

var products = new[]
{
    new Product("Widget", 9.99m, 100),
    new Product("Gadget", 24.99m, 50),
    new Product("Gizmo", 14.99m, 75),
};

Controls.DataGrid(products)
    .WithColumn(new PropertyColumnControl<decimal> { Property = "price" }
        .WithTitle("Unit price")
        .WithFormat("N2"))
```

## Aggregating with LINQ

A cell can compute over the data and render a summary control:

```csharp --render InventoryValue --show-code
record Product(string Name, decimal Price, int Stock);

var products = new[]
{
    new Product("Widget", 9.99m, 100),
    new Product("Gadget", 24.99m, 50),
    new Product("Gizmo", 14.99m, 75),
};

var totalValue = products.Sum(p => p.Price * p.Stock);

Controls.Markdown($"**Total inventory value:** {totalValue:N2} across {products.Length} products.")
```
