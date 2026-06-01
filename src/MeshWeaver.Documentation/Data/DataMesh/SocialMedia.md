---
Name: Example — SocialMedia Model Node Type
Category: DataMesh
Description: End-to-end worked example of a custom model node type — data records, reference data, layout areas, NodeType JSON, and instances
Icon: Code
---

# SocialMedia — A Model Node Type, End to End

This page is the **canonical reference example** for a custom model node type. When you — or the Coder agent — are asked to build something "as code" (a typed model with its own data and views), this is the shape to mirror.

> **See also:** [Creating Node Types](@@Doc/DataMesh/CreatingNodeTypes) for step-by-step theory, and [Business Rules](@@Doc/Architecture/BusinessRules) for a calculation-heavy example with charts.

---

## Folder Layout

Every file in this tree has a specific job. The sections below walk through each one.

```
Doc/DataMesh/SocialMedia/
  Post.json                              # NodeType definition (nodeType: "NodeType")
  Post/
    Source/                              # C# compiled at startup
      Platform.cs                        # Reference-data record
      SocialMediaPost.cs                 # Content record
      SocialMediaPostLayoutAreas.cs      # List + Detail layout areas
    Post-001.json                        # Instance (nodeType: "Doc/DataMesh/SocialMedia/Post")
  Profile.json                           # Second NodeType
  Profile/
    Source/
      SocialMediaProfile.cs
      SocialMediaProfileLayoutAreas.cs
    Roland-LinkedIn.json                 # Instance
```

---

## 1. Reference Data — `Platform.cs`

Reference data is a small, closed set of lookups — platforms, statuses, categories. The pattern is always the same: a plain `record` with a `[Key]`, typed static instances, and a static `All[]` array that seeds the in-memory data source.

```csharp
// <meshweaver>
// Id: Platform
// DisplayName: Social Media Platform
// </meshweaver>

public record Platform
{
    [Key] public string Id { get; init; } = string.Empty;
    [Required] public string Name { get; init; } = string.Empty;
    public string Emoji { get; init; } = string.Empty;
    public string Color { get; init; } = "#0a66c2";

    public static readonly Platform LinkedIn  = new() { Id = "LinkedIn",  Name = "LinkedIn",    Emoji = "💼", Color = "#0a66c2" };
    public static readonly Platform Twitter   = new() { Id = "Twitter",   Name = "X / Twitter", Emoji = "🐦", Color = "#000000" };
    public static readonly Platform Instagram = new() { Id = "Instagram", Name = "Instagram",   Emoji = "📷", Color = "#e1306c" };

    public static readonly Platform[] All = [LinkedIn, Twitter, Instagram];
    public static Platform GetById(string? id) => All.FirstOrDefault(p => p.Id == id) ?? LinkedIn;
}
```

---

## 2. Content Record — `SocialMediaPost.cs`

The content record defines the shape of a single instance's `content` payload. Domain attributes drive the editor UI and wire reference-data lookups automatically.

```csharp
// <meshweaver>
// Id: SocialMediaPost
// DisplayName: Social Media Post
// </meshweaver>

using MeshWeaver.Domain;

public record SocialMediaPost
{
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]   // syncs MeshNode.Name with Title
    public string Title { get; init; } = string.Empty;

    [Markdown(EditorHeight = "200px")]
    public string? Body { get; init; }

    [Dimension<Platform>]                        // renders as a Platform picker
    public string Platform { get; init; } = "LinkedIn";

    [DisplayName("Scheduled at")]
    public DateTimeOffset? ScheduledAt { get; init; }

    public int Impressions { get; init; }
    public int Likes { get; init; }
}
```

| Attribute | Purpose |
|---|---|
| `[Required]` | Validation |
| `[MeshNodeProperty(nameof(MeshNode.Name))]` | Mirrors the property value into `MeshNode.Name` |
| `[Dimension<T>]` | Typed lookup rendered as a dropdown against reference data |
| `[Markdown(...)]` | Rich-text editor with configurable height |
| `[DisplayName(...)]` | UI label override |

---

## 3. Layout Areas — `SocialMediaPostLayoutAreas.cs`

Layout areas are the **views** for instances of the type. They return `IObservable<UiControl?>` — never `Task<…>`, never `async`. Compose with Rx operators:

```csharp
public static IObservable<UiControl?> List(LayoutAreaHost host, RenderingContext _)
{
    var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
    return meshService
        .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:Doc/DataMesh/SocialMedia/Post"))
        .Scan(ImmutableDictionary<string, MeshNode>.Empty, ApplyChanges)
        .Select(dict => (UiControl?)BuildList(dict.Values.ToImmutableList()));
}
```

The extension method below is how the layout areas get wired into the NodeType configuration:

```csharp
public static LayoutDefinition AddSocialMediaPostLayoutAreas(this LayoutDefinition layout) =>
    layout.WithView("List", List).WithView("Detail", Detail);
```

---

## 4. NodeType JSON — `Post.json`

The JSON is the binding glue. It registers the type, points at its content record, seeds reference data, and wires custom layout areas.

```json
{
  "id": "Post",
  "namespace": "Doc/DataMesh/SocialMedia",
  "name": "Social Media Post",
  "nodeType": "NodeType",
  "content": {
    "$type": "NodeTypeDefinition",
    "configuration": "config => config
      .WithContentType<SocialMediaPost>()
      .AddData(data => data.AddSource(source => source
        .WithType<Platform>(t => t.WithInitialData(Platform.All))))
      .AddDefaultLayoutAreas()
      .AddLayout(layout => layout
        .AddSocialMediaPostLayoutAreas()
        .WithDefaultArea(\"List\"))"
  }
}
```

**Configuration-lambda quick reference:**

| Call | Purpose |
|---|---|
| `WithContentType<T>()` | The record type for new instances |
| `AddData(data => data.AddSource(…))` | Seed in-memory data sources (reference data) |
| `AddDefaultLayoutAreas()` | Overview, Edit, Threads, Files |
| `AddLayout(layout => layout.AddXxxLayoutAreas())` | Custom views |
| `WithDefaultArea("List")` | Which view opens by default |

---

## 5. Instances — `Post/Post-001.json`

An instance sets `nodeType` to the **namespace-qualified path** of the NodeType (`Doc/DataMesh/SocialMedia/Post`), and its `content` matches the record (`$type` = class name).

> **Naming convention:** Instance IDs should be meaningful — e.g. `Roland-LinkedIn`, `Post-001` — not generic like `SamplePost`.

```json
{
  "id": "Post-001",
  "namespace": "Doc/DataMesh/SocialMedia/Post",
  "name": "Why we bet on the actor model",
  "nodeType": "Doc/DataMesh/SocialMedia/Post",
  "content": {
    "$type": "SocialMediaPost",
    "title": "Why we bet on the actor model",
    "body": "Reactive systems live or die on isolation …",
    "profilePath": "Doc/DataMesh/SocialMedia/Profile/Roland-LinkedIn",
    "platform": "LinkedIn",
    "scheduledAt": "2026-04-05T09:00:00+02:00",
    "impressions": 4321,
    "likes": 187
  }
}
```

---

## Live Profile Instance

The embedded view below is the `Roland-LinkedIn` profile instance, rendered by its `Detail` layout area:

@@Doc/DataMesh/SocialMedia/Profile/Roland-LinkedIn

---

## Copy-This Checklist

When building a new model node type "as code", work through this list in order:

1. ☐ Create a namespace folder under your target location.
2. ☐ Add one `.cs` per content record in `Source/`, each with the `<meshweaver>` frontmatter.
3. ☐ Add reference-data `.cs` files with `[Key]`, static instances, and `All[]`.
4. ☐ Add a `XxxLayoutAreas.cs` with `List`/`Detail` views returning `IObservable<UiControl?>`.
5. ☐ Write the `Type.json` with `nodeType: "NodeType"` and a configuration lambda.
6. ☐ Write **at least one** instance JSON with `nodeType` set to the namespace-qualified path.
7. ☐ **Do not** substitute a Markdown node for a typed view — Markdown is for documents, not structured data.
