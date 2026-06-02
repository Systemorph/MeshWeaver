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
<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr-sm" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="10" y="30" width="155" height="260" rx="10" fill="#263238" stroke="#546e7a" stroke-width="1.5"/>
  <text x="87" y="56" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#eceff1">Source/ (C#)</text>
  <rect x="24" y="66" width="127" height="30" rx="6" fill="#43a047"/>
  <text x="87" y="86" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">Platform.cs</text>
  <text x="87" y="99" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#a5d6a7">reference data record</text>
  <rect x="24" y="114" width="127" height="30" rx="6" fill="#1e88e5"/>
  <text x="87" y="134" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">SocialMediaPost.cs</text>
  <text x="87" y="147" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#90caf9">content record</text>
  <rect x="24" y="162" width="127" height="30" rx="6" fill="#f57c00"/>
  <text x="87" y="182" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">PostLayoutAreas.cs</text>
  <text x="87" y="195" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#ffe0b2">List + Detail views</text>
  <rect x="24" y="210" width="127" height="30" rx="6" fill="#8e24aa"/>
  <text x="87" y="230" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">SocialMediaProfile.cs</text>
  <text x="87" y="243" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#e1bee7">second NodeType</text>
  <rect x="24" y="258" width="127" height="24" rx="6" fill="#546e7a"/>
  <text x="87" y="275" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">compiled at startup</text>
  <rect x="240" y="80" width="170" height="160" rx="10" fill="#37474f" stroke="#78909c" stroke-width="1.5"/>
  <text x="325" y="107" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#eceff1">NodeType JSON</text>
  <text x="325" y="125" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#b0bec5">Post.json</text>
  <rect x="254" y="134" width="142" height="22" rx="5" fill="#1e3a4a"/>
  <text x="325" y="149" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#80deea">WithContentType&lt;T&gt;()</text>
  <rect x="254" y="162" width="142" height="22" rx="5" fill="#1b3a1b"/>
  <text x="325" y="177" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#a5d6a7">AddData(Platform.All)</text>
  <rect x="254" y="190" width="142" height="22" rx="5" fill="#3e2000"/>
  <text x="325" y="205" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#ffcc80">AddLayout() + views</text>
  <rect x="254" y="218" width="142" height="14" rx="4" fill="#263238"/>
  <text x="325" y="229" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#78909c">nodeType: "NodeType"</text>
  <rect x="500" y="40" width="245" height="240" rx="10" fill="#1b2838" stroke="#546e7a" stroke-width="1.5"/>
  <text x="622" y="66" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#eceff1">Runtime / Instances</text>
  <rect x="514" y="76" width="217" height="40" rx="6" fill="#1e3a4a" stroke="#1e88e5" stroke-width="1"/>
  <text x="622" y="93" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#80deea">Live NodeType Hub</text>
  <text x="622" y="108" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#546e7a">editor, list + detail views, validation</text>
  <rect x="514" y="128" width="100" height="50" rx="6" fill="#263238" stroke="#43a047" stroke-width="1"/>
  <text x="564" y="148" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#a5d6a7">Post-001.json</text>
  <text x="564" y="162" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#546e7a">instance</text>
  <rect x="622" y="128" width="109" height="50" rx="6" fill="#263238" stroke="#8e24aa" stroke-width="1"/>
  <text x="676" y="148" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#e1bee7">Roland-LinkedIn</text>
  <text x="676" y="162" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#546e7a">.json instance</text>
  <rect x="514" y="192" width="217" height="30" rx="6" fill="#1a2e1a"/>
  <text x="622" y="212" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#a5d6a7">nodeType: "Doc/DataMesh/SocialMedia/Post"</text>
  <rect x="514" y="232" width="217" height="30" rx="6" fill="#1a1a2e"/>
  <text x="622" y="252" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#90caf9">Platform dropdown from reference data</text>
  <line x1="169" y1="160" x2="236" y2="160" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr-sm)"/>
  <text x="202" y="152" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#b0bec5" fill-opacity="0.8">defines</text>
  <line x1="414" y1="160" x2="496" y2="110" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr-sm)"/>
  <text x="460" y="128" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#b0bec5" fill-opacity="0.8">wires</text>
  <line x1="414" y1="160" x2="496" y2="153" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr-sm)"/>
</svg>
*Source C# files compile at startup → NodeType JSON wires them into a live hub → instance `.json` files carry typed content.*

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
