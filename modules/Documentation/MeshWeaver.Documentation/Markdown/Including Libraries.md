---
Title: "Using external libraries"
Abstract: >
    In this article we show how to include external libraries.
Thumbnail: "images/Reactive Dialogs.jpeg"
Published: "2025-03-02"
Authors:
  - "Roland Bürgi"
Tags:
  - "Documentation"
  - "Conceptual"
  - "Libraries"
---

```csharp --render Statistics --show-code
#r "nuget:MathNet.Numerics"
using MathNet.Numerics.Distributions;
using static MeshWeaver.Layout.Controls;
var pareto = new Pareto(2,3);
Markdown($"Mean: {pareto.Mean}, Variance: {pareto.Variance}")
```

