---
Title: "Insurance Unified References"
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

### Example: Fetching Reference Data

To display all countries:

```
@data:app/Insurance/Country
```

@data:app/Insurance/Country

### Example: Fetching a Single Reference Entity

To display a specific currency:

```
@data:app/Insurance/Currency/USD
```

@data:app/Insurance/Currency/USD

### Example: Fetching Lines of Business

To display all lines of business:

```
@data:app/Insurance/LineOfBusiness
```

@data:app/Insurance/LineOfBusiness

## Layout Area References

Layout areas display interactive components. The format is:

```
@area:addressType/addressId/areaName
```

You can omit the `area:` prefix since it's the default:

```
@addressType/addressId/areaName
```

### Example: Embedding a Pricing Overview

To embed the Microsoft 2026 pricing overview:

```
@pricing/Microsoft-2026/Overview
```

@pricing/Microsoft-2026/Overview
