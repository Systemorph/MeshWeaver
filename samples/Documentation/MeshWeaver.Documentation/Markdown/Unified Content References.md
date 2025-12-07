---
Title: "Unified Content References"
Abstract: >
  MeshWeaver introduces a unified notation for referencing any form of downloadable content.
  This includes data entities, file content, and layout areas - all accessible through a consistent
  path-based syntax that can be embedded directly in markdown documents.
Thumbnail: "images/UnifiedReferences.svg"
Published: "2025-12-06"
Authors:
  - "Roland Bürgi"
Tags:
  - "Documentation"
  - "Conceptual"
  - "Markdown"
  - "Data"
  - "Content"
---

MeshWeaver provides a unified notation for referencing any form of content. Whether you need to embed data, include file content, or display layout areas, the syntax follows a consistent pattern:

```
@prefix:addressType/addressId/path
```

The prefix determines how the content is fetched and rendered:
- `data:` - Fetches data entities and displays them as JSON
- `content:` - Fetches file content and renders based on mime type
- `area:` - Displays a layout area (default if no prefix is specified)

For paths containing spaces, use quotes: `@"content:app/Docs/My Report.pdf"`

## Content References

Content references embed file content directly in your markdown. The content is rendered based on its mime type. The format is:

```
@content:addressType/addressId/collection/path
```

### Example: Embedding an Image

To display an image from the Documentation collection:

```
@content:app/Documentation/Documentation/images/meshbros.png
```

@content:app/Documentation/Documentation/images/meshbros.png

### Example: Embedding a Markdown Document

To include content from another markdown file:

```
@content:app/Documentation/Documentation/embedded.md
```

@content:app/Documentation/Documentation/embedded.md

## Layout Area References

Layout areas display interactive components. The format is:

```
@area:addressType/addressId/areaName
```

You can omit the `area:` prefix since it's the default:

```
@addressType/addressId/areaName
```

### Example: Embedding the Calculator

The Calculator layout area demonstrates a simple interactive component:

```
@app/Documentation/Calculator
```

@app/Documentation/Calculator

### Example: Embedding the Counter

The Counter layout area demonstrates stateful views with click actions:

```
@app/Documentation/Counter
```

@app/Documentation/Counter

### Example: Embedding Progress Indicators

The Progress layout area demonstrates progress bars:

```
@app/Documentation/Progress
```

@app/Documentation/Progress
