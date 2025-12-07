---
Title: "Northwind Unified References"
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
- `data/` - Fetches data entities and displays them as JSON
- `content/` - Fetches file content and renders based on mime type
- `area/` - Displays a layout area (default if no prefix is specified)

For paths containing spaces, use quotes: `@"content/app/Docs/My Report.pdf"`

## Data References

Data references embed live data directly in your markdown, displayed as formatted JSON. The format is:

```
@data/addressType/addressId/collection/entityId
```

### Example: Fetching a Collection

To display all territories:

```
@data/app/Northwind/Territory
```

@data/app/Northwind/Territory

### Example: Fetching a Single Entity

To display a specific territory by ID:

```
@data/app/Northwind/Territory/06897
```

@data/app/Northwind/Territory/06897

### Example: Fetching Categories

To display all product categories:

```
@data/app/Northwind/Category
```

@data/app/Northwind/Category

## Content References

Content references embed file content directly in your markdown. The content is rendered based on its mime type. The format is:

```
@content/addressType/addressId/collection/path
```

### Example: Embedding an Image

To display an image from the Northwind collection:

```
@content/app/Northwind/Northwind/images/Northwind.png
```

@content/app/Northwind/Northwind/images/Northwind.png

## Layout Area References

Layout areas display interactive components. The format is:

```
@area/addressType/addressId/areaName
```

You can omit the `area/` prefix since it's the default:

```
@addressType/addressId/areaName
```

### Example: Embedding the Annual Report Summary

To embed the Northwind annual report summary:

```
@app/Northwind/AnnualReportSummary?Year=2025
```

@app/Northwind/AnnualReportSummary?Year=2025

### Example: Embedding Sales Growth Summary

To embed the sales growth chart:

```
@app/Northwind/SalesGrowthSummary?Year=2025
```

@app/Northwind/SalesGrowthSummary?Year=2025
