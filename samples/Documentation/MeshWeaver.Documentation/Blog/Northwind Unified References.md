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

### Example: Fetching a Collection

To display all territories:

```
@app/Northwind/data/Territory
```

@app/Northwind/data/Territory

### Example: Fetching a Single Entity

To display a specific territory by ID:

```
@app/Northwind/data/Territory/06897
```

@app/Northwind/data/Territory/06897

### Example: Fetching Categories

To display all product categories:

```
@app/Northwind/data/Category
```

@app/Northwind/data/Category

## Content References

Content references embed file content directly in your markdown. The content is rendered based on its mime type. The format is:

```
@addressType/addressId/content/collection/path
```

### Example: Embedding an Image

To display an image from the Northwind collection:

```
@app/Northwind/content/Northwind/images/Northwind.png
```

@app/Northwind/content/Northwind/images/Northwind.png

## Layout Area References

Layout areas display interactive components. The format is:

```
@addressType/addressId/areaName
```

Since `area` is the default keyword, you don't need to specify it explicitly.

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
