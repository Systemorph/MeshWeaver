---
Name: Query Syntax
Category: Documentation
Description: Documentation for the GitHub-style query syntax
Icon: /static/storage/content/MeshWeaver/Documentation/DataMesh/QuerySyntax/icon.svg
---

# Query Syntax Reference

MeshWeaver uses a GitHub-style query syntax for searching and filtering nodes. This document describes all available query features.

## Basic Syntax

Queries consist of space-separated terms. Each term can be:
- **Field filter**: `field:value` - matches nodes where field equals value
- **Negation**: `-field:value` - excludes nodes where field equals value
- **Text search**: `keyword` - searches in name and description

## Field Filters

### Equality
```
nodeType:Organization
name:Acme
status:Active
```

### Negation
```
-status:Archived
```

### Wildcard Patterns
```
name:*claims*     # Contains 'claims'
name:Acme*        # Starts with 'Acme'
name:*Corp        # Ends with 'Corp'
```

### Comparison Operators
```
price:>100        # Greater than 100
price:<50         # Less than 50
price:>=100       # Greater than or equal
price:<=50        # Less than or equal
```

### List Values (OR)
```
status:(Active OR Pending OR Draft)
nodeType:(Organization OR Project)
```

### Empty Values
```
description:       # Matches nodes with no description
```

## Reserved Qualifiers

### namespace
Sets the search location (like a folder). Default scope is `children` (immediate children only):
```
namespace:Systemorph          # Immediate children of Systemorph
namespace:Systemorph/Marketing # Immediate children of Marketing
```

Use `scope:descendants` for recursive search:
```
namespace:Systemorph scope:descendants  # All items under Systemorph (recursive)
```

### scope
Controls the search scope relative to namespace or path:
```
scope:exact           # Only the exact path (default for path:)
scope:children        # Immediate children only (excludes self)
scope:descendants     # All descendants recursively (excludes self)
scope:ancestors       # Parent hierarchy upward (excludes self)
scope:hierarchy       # Ancestors + self + descendants
scope:subtree         # Self + all descendants
scope:ancestorsandself # Self + all ancestors
```

### path
Sets the base path for search (default scope is `exact`):
```
path:Systemorph                # The exact Systemorph node
path:Systemorph scope:children # Immediate children of Systemorph
```

### sort
Specifies sort order:
```
sort:name          # Sort by name ascending
sort:name-desc     # Sort by name descending
sort:lastModified-desc  # Most recently modified first
```

### limit
Limits the number of results:
```
limit:10           # Return at most 10 results
limit:50           # Return at most 50 results
```

### source
Specifies the data source:
```
source:activity    # Query activity records
```

## Complex Queries

Combine multiple filters:
```
namespace:Systemorph nodeType:Project

nodeType:Story name:*claims* sort:lastModified-desc limit:20

namespace:ACME/ProductLaunch nodeType:Todo scope:descendants
```

## Query in Context

### NodeType Catalog
When viewing a NodeType (e.g., Organization), the default query is:
```
nodeType:Organization
```
This finds all instances of Organization.

### Instance Catalog
When viewing an instance (e.g., Systemorph), the default query is:
```
namespace:Systemorph
```
This finds immediate children of Systemorph. Use `scope:descendants` for recursive search.

## Tips

1. **Case insensitive**: All comparisons are case-insensitive
2. **Namespace = folder**: `namespace:X` is like searching in folder X (immediate children)
3. **Add scope:descendants**: For recursive search under a namespace
4. **Wildcards**: Use `*` for flexible pattern matching
