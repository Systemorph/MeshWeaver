---
Name: NodeType Configuration Guide
Category: Documentation
Description: How to define, configure, and extend NodeTypes — the schema and behavior templates for all mesh nodes.
Icon: /static/DocContent/DataMesh/NodeTypeConfiguration/icon.svg
---

NodeTypes are the blueprints of the mesh. Every node you create — an Organization, a Story, a Project — is an *instance* of a NodeType that defines its content schema, hub behaviour, and how it renders in the UI. This guide walks through every aspect of creating and configuring them.

---

## What is a NodeType?

A NodeType is itself a mesh node (with `nodeType: "NodeType"`). It acts as a reusable template that combines three concerns:

| Concern | What it controls |
|---|---|
| **Content schema** | The C# record type that models instance data |
| **Hub configuration** | How instance hubs are wired (layout areas, content collections, …) |
| **Display properties** | Icon, description, sort order shown in the UI |

When a user navigates to a node whose `nodeType` points at your definition, the framework compiles the content record, applies the configuration expression, and mounts the resulting layout areas — all dynamically, without a deploy.

<svg viewBox="0 0 760 200" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="20" y="60" width="150" height="80" rx="10" fill="#1e88e5" stroke="#1565c0" stroke-width="1.5"/>
  <text x="95" y="92" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">NodeType</text>
  <text x="95" y="112" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#bbdefb">content schema</text>
  <text x="95" y="128" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#bbdefb">configuration lambda</text>
  <rect x="234" y="60" width="150" height="80" rx="10" fill="#37474f" stroke="#546e7a" stroke-width="1.5"/>
  <text x="309" y="92" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#eceff1">Compile</text>
  <text x="309" y="112" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#b0bec5">C# record → assembly</text>
  <text x="309" y="128" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#b0bec5">cached + invalidated</text>
  <rect x="448" y="60" width="150" height="80" rx="10" fill="#43a047" stroke="#2e7d32" stroke-width="1.5"/>
  <text x="523" y="92" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Instance Hub</text>
  <text x="523" y="112" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#c8e6c9">layout areas wired</text>
  <text x="523" y="128" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#c8e6c9">WithContentType applied</text>
  <rect x="622" y="60" width="118" height="80" rx="10" fill="#f57c00" stroke="#e65100" stroke-width="1.5"/>
  <text x="681" y="92" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">UI Views</text>
  <text x="681" y="112" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ffe0b2">Details · Catalog</text>
  <text x="681" y="128" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ffe0b2">Thumbnail · Settings</text>
  <line x1="170" y1="100" x2="229" y2="100" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="384" y1="100" x2="443" y2="100" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="598" y1="100" x2="617" y2="100" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="201" y="90" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity="0.55">first use</text>
  <text x="414" y="90" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity="0.55">activate</text>
  <text x="607" y="90" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity="0.55">mount</text>
  <text x="380" y="175" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity="0.5">Every instance hub is activated on demand — no restart required when the NodeType definition changes.</text>
</svg>

*NodeType lifecycle: definition compiled on first activation, hub wired from the configuration lambda, views mounted for every instance.*

---

## NodeType Structure

A NodeType has two parts: its **node properties** (the metadata stored in the mesh) and a **`NodeTypeDefinition` content block** (the rich configuration payload).

### Node properties

```json
{
  "id": "Organization",
  "namespace": "",
  "name": "Organization",
  "nodeType": "NodeType",
  "description": "An organization containing projects",
  "icon": "Building",
  "order": 10,
  "isPersistent": true
}
```

### NodeTypeDefinition content

```json
{
  "$type": "NodeTypeDefinition",
  "id": "Organization",
  "namespace": "",
  "displayName": "Organization",
  "icon": "Building",
  "description": "An organization containing projects",
  "order": 10,
  "configuration": "config => config.WithContentType<Organization>().AddDefaultLayoutAreas()"
}
```

The `configuration` field is the heart of the definition — a C# lambda expression evaluated at runtime that wires up the hub for every instance of this type.

---

## The Configuration Expression

The `configuration` string is a C# lambda (`config => …`) that receives an `IHubConfiguration` and returns it, fully wired. The framework compiles and caches this expression the first time an instance hub is activated.

### `WithContentType<T>()`

Binds the named C# record as the authoritative content type for all instances:

```csharp
config.WithContentType<Organization>()
```

### `AddDefaultLayoutAreas()`

Registers the five standard views — **Details**, **Catalog**, **Thumbnail**, **Metadata**, and **Settings** — in one call:

```csharp
config.WithContentType<Organization>().AddDefaultLayoutAreas()
```

### `AddLayout(…)`

Adds custom views on top of the defaults. The view name appears in the navigation bar:

```csharp
config
  .WithContentType<Story>()
  .AddDefaultLayoutAreas()
  .AddLayout(layout => layout
    .WithView("Timeline", StoryViews.Timeline))
```

### `MapContentCollection(…)`

Maps a named property to a blob-storage collection (files, images, …):

```csharp
config.WithContentType<Organization>()
  .AddDefaultLayoutAreas()
  .MapContentCollection("logos", "storage", $"logos/{config.Address.Segments.Last()}")
```

---

## Defining the Content Record

Each NodeType references a C# record that models its instance data. Place the source in a `Source/dataModel.json` file alongside the NodeType definition:

```csharp
using System.ComponentModel.DataAnnotations;

public record Organization
{
    [Key]
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Icon { get; init; } = "Building";
}
```

The `[Key]` attribute marks the identifier property. MeshWeaver compiles this record dynamically — no manual build step required. The compiled assembly is cached and invalidated automatically when the source node changes.

---

## Namespace Hierarchy

NodeTypes participate in the same namespace hierarchy as all other nodes.

### Root-namespace types (global)

A NodeType with an empty namespace defines a global type available everywhere:

- `Organization` lives at path `Organization` with `namespace: ""`
- Instances such as `Systemorph` also carry `namespace: ""`

### Scoped types

NodeTypes can be restricted to a namespace, so they only appear and are creatable within that context:

- `Systemorph/Project` lives at path `Systemorph/Project` with `namespace: "Systemorph"`
- Instances like `Systemorph/Marketing` carry `namespace: "Systemorph"`

This scoping lets you model domain-specific types (a `Story` inside `Systemorph/Marketing`) without polluting the global namespace.

---

## Catalog Behaviour

The **Catalog** view for a NodeType automatically queries for all instances within the current namespace. The framework builds the query dynamically:

- `namespace:{current}` — scopes the search to the active namespace
- `nodeType:{nodeTypePath}` — filters to instances of this specific type

No manual `childrenQuery` configuration is needed. Adding a new instance immediately appears in the catalog.

---

## Default Views Reference

`AddDefaultLayoutAreas()` registers five views that cover the most common UI needs:

| View | Purpose |
|---|---|
| **Details** | Full content editor / reader for a single instance |
| **Catalog** | Paginated list of all instances of this type |
| **Thumbnail** | Compact card for use in grid / gallery layouts |
| **Metadata** | Technical properties (path, version, timestamps) |
| **Settings** | NodeType definitions scoped to this namespace |

---

## End-to-End Example: a `Story` NodeType

The following three files create a fully working `Story` type inside the `Systemorph/Marketing` namespace.

### 1. Story.json — the NodeType definition

```json
{
  "id": "Story",
  "namespace": "Systemorph/Marketing",
  "name": "Story",
  "nodeType": "NodeType",
  "content": {
    "$type": "NodeTypeDefinition",
    "id": "Story",
    "namespace": "Systemorph/Marketing",
    "displayName": "Story",
    "configuration": "config => config.WithContentType<Story>().AddDefaultLayoutAreas()"
  }
}
```

### 2. Story/Source/dataModel.json — the content record

```json
{
  "code": "public record Story { [Key] public string Id { get; init; } public string Title { get; init; } public string? Markdown { get; init; } }"
}
```

### 3. An instance — ClaimsProcessing.json

```json
{
  "id": "ClaimsProcessing",
  "namespace": "Systemorph/Marketing",
  "name": "Claims Processing",
  "nodeType": "Systemorph/Marketing/Story",
  "content": {
    "$type": "Story",
    "id": "ClaimsProcessing",
    "title": "Claims Processing Pipeline",
    "markdown": "# Overview\n..."
  }
}
```

Once these files are saved, MeshWeaver compiles the record, wires the hub, and the instance is immediately navigable — no restart required.

---

## Live Preview

The cell below renders a summary card showing how a NodeType's display properties map to the UI. Adapt the values to match your own type.

```csharp --render NodeTypePreview --show-code
var props = new[]
{
    ("Name",        "Story"),
    ("Namespace",   "Systemorph/Marketing"),
    ("Icon",        "BookOpen"),
    ("Description", "A narrative story within a marketing campaign"),
    ("Order",       "20"),
    ("Views",       "Details · Catalog · Thumbnail · Metadata · Settings"),
};

var rows = string.Join("\n", props.Select(p =>
    $"| **{p.Item1}** | `{p.Item2}` |"));

MeshWeaver.Layout.Controls.Markdown(
    $"### NodeType summary card\n\n| Property | Value |\n|---|---|\n{rows}");
```

---

## Best Practices

1. **Use meaningful namespaces.** Group related types together so scoped queries and catalog views stay focused.
2. **Set `order` explicitly.** Controls sorting in catalog and type-picker UI; lower numbers appear first.
3. **Choose Fluent UI icons.** Browse available names at the Fluent UI icon gallery — they render consistently across themes.
4. **Write clear descriptions.** The description surfaces in the Catalog header and the type-picker tooltip; a single sentence is enough.
5. **Keep content records small.** Large records with many optional fields slow schema compilation. Prefer composition (satellite nodes) over monolithic records.
