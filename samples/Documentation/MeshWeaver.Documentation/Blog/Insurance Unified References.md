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
@addressType/addressId[/keyword[/path]]
```

The keyword determines how the content is fetched and rendered:
- `data` - Fetches data entities and displays them as JSON
- `content` - Fetches file content and renders based on mime type
- `area` - Displays a layout area (this is the default if no keyword is specified)

For paths containing spaces, use quotes: `@"app/Docs/content/My Report.pdf"`

## Data References

Data references embed live data directly in your markdown, displayed as formatted JSON. The format is:

```
@addressType/addressId/data/collection/entityId
```

### Example: Fetching Reference Data

To display all countries:

```
@app/Insurance/data/Country
```

@app/Insurance/data/Country

### Example: Fetching a Single Reference Entity

To display a specific currency:

```
@app/Insurance/data/Currency/USD
```

@app/Insurance/data/Currency/USD

### Example: Fetching Lines of Business

To display all lines of business:

```
@app/Insurance/data/LineOfBusiness
```

@app/Insurance/data/LineOfBusiness

## Layout Area References

Layout areas display interactive components. The format is:

```
@addressType/addressId/areaName
```

Since `area` is the default keyword, you don't need to specify it explicitly.

### Example: Embedding a Pricing Overview

To embed the Microsoft 2026 pricing overview:

```
@pricing/Microsoft-2026/Overview
```

@pricing/Microsoft-2026/Overview

You can also configure a default view for the pricing and then just reference:

```
@pricing/Microsoft-2026
```

@pricing/Microsoft-2026
