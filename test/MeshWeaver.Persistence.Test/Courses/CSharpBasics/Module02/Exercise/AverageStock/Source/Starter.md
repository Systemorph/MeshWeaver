---
Name: Starter — Average Stock
NodeType: Markdown
---

# Your turn

Complete the cell below so it renders the average stock. The starter compiles as-is (so the page
loads and the trainee can Run it), but its intended assertion is not yet satisfied — that is the
trainee's job.

```csharp --render AverageStockStarter --show-code
record Product(string Name, decimal Price, int Stock);

var products = new[]
{
    new Product("Widget", 9.99m, 100),
    new Product("Gadget", 24.99m, 50),
    new Product("Gizmo", 14.99m, 75),
};

// TODO: replace 0 with the real average stock (products.Average(p => p.Stock)).
var averageStock = 0.0;

// The starter deliberately asserts the (not-yet-correct) result so "Run" shows red until fixed.
// This cell COMPILES (the contract the test enforces for stubs) but throws at runtime by design.
if (averageStock == 0.0)
    throw new InvalidOperationException("Not implemented yet — compute the average stock.");

Controls.Markdown($"**Average stock:** {averageStock:N1} units per product.")
```
