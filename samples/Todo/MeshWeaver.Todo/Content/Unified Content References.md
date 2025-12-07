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

## Data References

Data references embed live data directly in your markdown, displayed as formatted JSON. The format is:

```
@data:addressType/addressId/collection/entityId
```

### Example: Fetching a Collection

To display all todo items:

```
@data:app/Todo/TodoItem
```

@data:app/Todo/TodoItem

### Example: Fetching a Single Entity

To display a specific todo item by ID:

```
@data:app/Todo/TodoItem/1
```

@data:app/Todo/TodoItem/1

### Example: Fetching Categories

To display all todo categories:

```
@data:app/Todo/TodoCategory
```

@data:app/Todo/TodoCategory

## Content References

Content references embed file content directly in your markdown. The content is rendered based on its mime type. The format is:

```
@content:addressType/addressId/collection/path
```

### Example: Embedding an Image

To display an image from the Todo collection:

```
@content:app/Todo/Todo/images/todoapp.jpeg
```

@content:app/Todo/Todo/images/todoapp.jpeg

## Layout Area References

Layout areas display interactive components. The format is:

```
@area:addressType/addressId/areaName
```

You can omit the `area:` prefix since it's the default:

```
@addressType/addressId/areaName
```

### Example: Embedding the Summary View

To embed the Todo summary dashboard:

```
@app/Todo/Summary
```

@app/Todo/Summary

### Example: Embedding Today's Focus

To embed today's focus view:

```
@app/Todo/TodaysFocus
```

@app/Todo/TodaysFocus
