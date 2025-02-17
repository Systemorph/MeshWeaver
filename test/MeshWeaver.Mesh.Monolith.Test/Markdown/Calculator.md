---
Title: "Calculator"
Abstract: "This the calculator."
Thumbnail: "images/thumbnail.jpg"
Published: "2024-10-24"
Authors:
  - "Roland Bürgi"
Tags:
  - "Calcultor"
---

```csharp --render calculator
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using static MeshWeaver.Layout.Controls;
record Calculator(double Summand1, double Summand2);
static object CalculatorSum(Calculator c) => Markdown($"**Sum**: {c.Summand1 + c.Summand2}");
Mesh.Edit(new Calculator(1,2), CalculatorSum)
```
