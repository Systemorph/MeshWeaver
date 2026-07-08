---
Name: Hello, Controls
NodeType: Markdown
---

# Lesson 1 — Your first rendered cell

Every course lesson is a live notebook. A ` ```csharp --render <Area> ` fence is submitted
to the same Roslyn kernel the portal runs, and its last expression is rendered below the cell.

The simplest thing you can render is a piece of text:

```csharp --render HelloText --show-code
Controls.Markdown("**Hello from a course cell!**")
```

## Composing controls

Controls compose into a `Stack`. A trainee reading this page sees the live result immediately:

```csharp --render StackOfLabels --show-code
Controls.Stack
    .WithView(Controls.Markdown("### Course modules"))
    .WithView(Controls.Text("Theory — read the concept"))
    .WithView(Controls.Text("Example — see it run"))
    .WithView(Controls.Text("Exercise — your turn"))
```

## Using ordinary C#

Cells are ordinary C#: records, LINQ, and the framework's `Controls` factory are all available.

```csharp --render ModuleList --show-code
record Module(int Order, string Title);

var modules = new[]
{
    new Module(1, "C# Basics"),
    new Module(2, "Working with Data"),
    new Module(3, "Building Views"),
};

Controls.Stack
    .WithView(Controls.Markdown($"You are enrolled in **{modules.Length} modules**."))
    .WithView(Controls.Markdown(
        string.Join("\n", modules.OrderBy(m => m.Order).Select(m => $"{m.Order}. {m.Title}"))))
```
