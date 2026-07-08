---
Name: Solution — Average Stock
NodeType: Markdown
---

# Reference solution

This is the reference solution. It must run **green** — a course whose solution errors is broken.

```csharp --render AverageStockSolution --show-code
record Product(string Name, decimal Price, int Stock);

var products = new[]
{
    new Product("Widget", 9.99m, 100),
    new Product("Gadget", 24.99m, 50),
    new Product("Gizmo", 14.99m, 75),
};

var averageStock = products.Average(p => p.Stock);

Controls.Markdown($"**Average stock:** {averageStock:N1} units per product.")
```
