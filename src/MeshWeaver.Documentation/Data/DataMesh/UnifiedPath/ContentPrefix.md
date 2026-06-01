---
Name: Collection Prefix
Category: Documentation
Description: Embed or link to files in named content collections using the @@collection/path prefix syntax
Icon: /static/DocContent/DataMesh/UnifiedPath/ContentPrefix/icon.svg
---

Content collections hold files — images, documents, markdown, code — associated with mesh nodes. The **collection prefix** syntax lets you embed or link to those files anywhere in the mesh by combining a collection name with a relative path.

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
