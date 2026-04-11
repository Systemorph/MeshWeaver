---
Name: Interactive Showcase
Category: Documentation
---

# Interactive Markdown Showcase

This page demonstrates executable code blocks.

## Simple Output

```csharp --execute
Controls.Markdown("**Hello** from the kernel!")
```

## Calculator

```csharp --execute
var a = 21;
var b = 21;
Controls.Markdown($"The answer is **{a + b}**")
```

## Shared State

Variables persist between code blocks:

```csharp --execute
var greeting = "Hello, World!";
Controls.Markdown(greeting)
```

```csharp --execute
Controls.Markdown($"Greeting was: {greeting}")
```
