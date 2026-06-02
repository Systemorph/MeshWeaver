---
Name: Collection Prefix
Category: Documentation
Description: Embed or link to files in named content collections using the @@collection/path prefix syntax
Icon: /static/DocContent/DataMesh/UnifiedPath/ContentPrefix/icon.svg
---

Content collections hold files — images, documents, markdown, code — associated with mesh nodes. The **collection prefix** syntax lets you embed or link to those files anywhere in the mesh by combining a collection name with a relative path.
<svg viewBox="0 0 760 300" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity="0.6"/>
    </marker>
  </defs>
  <rect x="10" y="120" width="160" height="60" rx="10" fill="#1e88e5"/>
  <text x="90" y="145" text-anchor="middle" fill="#fff" font-weight="bold">@@addr/prefix/path</text>
  <text x="90" y="163" text-anchor="middle" fill="#fff" font-size="11">reference</text>
  <line x1="170" y1="150" x2="235" y2="150" stroke="currentColor" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="237" y="120" width="130" height="60" rx="10" fill="#5c6bc0"/>
  <text x="302" y="145" text-anchor="middle" fill="#fff" font-weight="bold">Prefix</text>
  <text x="302" y="163" text-anchor="middle" fill="#fff" font-size="11">resolution</text>
  <line x1="302" y1="120" x2="302" y2="80" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="302" y="72" text-anchor="middle" fill="currentColor" fill-opacity="0.65" font-size="11">reserved?</text>
  <line x1="302" y1="55" x2="302" y2="30" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="237" y="5" width="130" height="44" rx="8" fill="#e53935"/>
  <text x="302" y="24" text-anchor="middle" fill="#fff" font-weight="bold">Reserved</text>
  <text x="302" y="41" text-anchor="middle" fill="#fff" font-size="11">data / schema / area…</text>
  <text x="395" y="148" fill="currentColor" fill-opacity="0.55" font-size="11">not reserved</text>
  <line x1="367" y1="150" x2="430" y2="150" stroke="currentColor" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="432" y="120" width="140" height="60" rx="10" fill="#43a047"/>
  <text x="502" y="145" text-anchor="middle" fill="#fff" font-weight="bold">Collection</text>
  <text x="502" y="163" text-anchor="middle" fill="#fff" font-size="11">user-registered name</text>
  <line x1="572" y1="150" x2="635" y2="150" stroke="currentColor" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="637" y="105" width="110" height="44" rx="8" fill="#f57c00"/>
  <text x="692" y="124" text-anchor="middle" fill="#fff" font-weight="bold">@@</text>
  <text x="692" y="142" text-anchor="middle" fill="#fff" font-size="11">embed inline</text>
  <rect x="637" y="162" width="110" height="44" rx="8" fill="#8e24aa"/>
  <text x="692" y="181" text-anchor="middle" fill="#fff" font-weight="bold">@</text>
  <text x="692" y="199" text-anchor="middle" fill="#fff" font-size="11">navigation link</text>
  <line x1="572" y1="150" x2="620" y2="127" stroke="currentColor" stroke-opacity="0.4" stroke-width="1" marker-end="url(#arr)"/>
  <line x1="572" y1="150" x2="620" y2="184" stroke="currentColor" stroke-opacity="0.4" stroke-width="1" marker-end="url(#arr)"/>
  <text x="380" y="260" text-anchor="middle" fill="currentColor" fill-opacity="0.65" font-size="12">File resolved from collection, then rendered by extension (.svg → image, .md → markdown, …)</text>
</svg>
*Collection prefix resolution: the prefix after the address is checked against reserved keywords first; unknown prefixes route to user-registered collections, and the leading `@@` / `@` controls embed vs. link.*

# How the Prefix Works

Every mesh URL segment that doesn't match a reserved keyword is treated as a **collection name**. A double-`@@` embeds the file inline; a single `@` produces a navigation link.

```
@@{address}/{collection}/{path}     ← embeds the file
 @{address}/{collection}/{path}     ← hyperlink to the file
```

> **Note:** The legacy colon syntax (`content:file.svg`) is still accepted for backward compatibility, but the slash form is preferred for new content.

## Reserved Keywords

The following prefix names are reserved and route to mesh subsystems rather than collections:

| Prefix | Routes to |
|--------|-----------|
| `data/` | Data entities |
| `schema/` | Type schemas |
| `model/` | Data models |
| `area/` | Layout areas |
| `content/` | Content files (the built-in default collection) |
| `menu/` | Menu structure |

Any prefix **not** in this list is resolved as a user-configured collection name.

# Collection Names

Because the collection name is part of the path — not a hard-coded keyword — you can use whatever name you've registered:

| Reference | Resolves from |
|-----------|---------------|
| `@@Doc/DataMesh/UnifiedPath/content/logo.svg` | The `content` collection at that address |
| `@@MyApp/assets/images/banner.png` | The `assets` collection at `MyApp` |
| `@@MyApp/docs/readme.md` | The `docs` collection at `MyApp` |

# Configuring Collections

Collections are registered during hub setup. Each registration maps a name to a source — a local directory, a remote blob store, or an aliased path inside another collection:

```csharp
config.AddFileSystemContentCollection("content", sp => "./content")
      .AddFileSystemContentCollection("assets", sp => "./assets")
      .MapContentCollection("avatars", "storage", "persons/avatars");
```

- **`AddFileSystemContentCollection`** serves files from a directory on disk.
- **`MapContentCollection`** creates an alias that redirects to a path inside another registered collection.

# Supported File Types

The platform automatically selects a renderer based on file extension:

| Type | Extensions | Rendered as |
|------|------------|-------------|
| Images | `.png` `.jpg` `.gif` `.svg` | Inline image |
| Markdown | `.md` | Rendered markdown |
| PDF | `.pdf` | Embedded PDF viewer |
| Code | `.cs` `.js` `.ts` `.json` | Syntax-highlighted code block |
| Text | `.txt` `.xml` `.yaml` | Preformatted code block |

# Examples

## Embed an SVG Logo

**Syntax:**
```
@@Doc/DataMesh/UnifiedPath/content/logo.svg
```

**Result:**

@@../content:logo.svg

## Link to a Markdown File

Use single `@` to produce a clickable navigation link instead of an inline embed:

**Syntax:**
```
@Doc/DataMesh/UnifiedPath/content/sample.md
```

**Result:**

@../content:sample.md

## Custom Collection Name

Once an `assets` collection is registered, reference it just like the built-in `content` collection:

```
@@MyApp/assets/images/banner.png
```

# Live Demo

The cell below shows the collection-name resolution table as it appears at runtime:

```csharp --render ContentPrefixDemo --show-code
MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Markdown("## Collection Prefix — runtime reference\n\nThe prefix immediately after the address selects the collection."))
    .WithView(MeshWeaver.Layout.Controls.Html(
        "<table style='border-collapse:collapse;width:100%'>" +
        "<thead><tr><th style='border:1px solid #ccc;padding:6px'>Reference form</th><th style='border:1px solid #ccc;padding:6px'>Effect</th></tr></thead>" +
        "<tbody>" +
        "<tr><td style='border:1px solid #ccc;padding:6px'><code>@@addr/content/logo.svg</code></td><td style='border:1px solid #ccc;padding:6px'>Embeds the file inline</td></tr>" +
        "<tr><td style='border:1px solid #ccc;padding:6px'><code>@addr/docs/readme.md</code></td><td style='border:1px solid #ccc;padding:6px'>Renders a hyperlink</td></tr>" +
        "<tr><td style='border:1px solid #ccc;padding:6px'><code>@@addr/assets/banner.png</code></td><td style='border:1px solid #ccc;padding:6px'>Embeds from the <em>assets</em> collection</td></tr>" +
        "</tbody></table>"))
```
