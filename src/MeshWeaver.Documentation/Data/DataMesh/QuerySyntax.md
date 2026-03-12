---
Name: Query Syntax Reference
Category: Documentation
Description: Documentation for the GitHub-style query syntax
Icon: /static/DocContent/DataMesh/QuerySyntax/icon.svg
---

MeshWeaver uses a GitHub-style query syntax for searching and filtering nodes. This document describes all available query features.

# Basic Syntax

Queries consist of space-separated terms. Each term can be:
- **Field filter**: `field:value` - matches nodes where field equals value
- **Negation**: `-field:value` - excludes nodes where field equals value
- **Text search**: `keyword` - searches in name and description

# Field Filters

## Equality
```
nodeType:Organization
name:Acme
status:Active
```

## Negation
```
-status:Archived
```

## Wildcard Patterns
```
name:*claims*     # Contains 'claims'
name:Acme*        # Starts with 'Acme'
name:*Corp        # Ends with 'Corp'
```

## Comparison Operators
```
price:>100        # Greater than 100
price:<50         # Less than 50
price:>=100       # Greater than or equal
price:<=50        # Less than or equal
```

## List Values (OR)
```
status:(Active OR Pending OR Draft)
nodeType:(Organization OR Project)
```

## Empty Values
```
description:       # Matches nodes with no description
```

# Reserved Qualifiers

## namespace
Sets the search location (like a folder). Default scope is `children` (immediate children only):
```
namespace:Systemorph          # Immediate children of Systemorph
namespace:Systemorph/Marketing # Immediate children of Marketing
```

Use `scope:descendants` for recursive search:
```
namespace:Systemorph scope:descendants  # All items under Systemorph (recursive)
```

## scope
Controls the search scope relative to namespace or path:
```
scope:descendants     # All descendants recursively (excludes self)
scope:ancestors       # Parent hierarchy upward (excludes self)
scope:hierarchy       # Ancestors + self + descendants
scope:subtree         # Self + all descendants
scope:ancestorsandself # Self + all ancestors
```

## path
Sets the base path for search (default scope is `exact`):
```
path:Systemorph                # The exact Systemorph node
namespace:Systemorph           # Immediate children of Systemorph
```

## sort
Specifies sort order:
```
sort:name          # Sort by name ascending
sort:name-desc     # Sort by name descending
sort:lastModified-desc  # Most recently modified first
```

## limit
Limits the number of results:
```
limit:10           # Return at most 10 results
limit:50           # Return at most 50 results
```

## source
Specifies the data source:
```
source:activity    # Results ordered by user's last access time
```

When `source:activity` is specified:
- Results are ordered by the current user's most recent access time (most recently accessed first)
- Items the user has not yet accessed appear after activity-tracked items
- All other filters (`nodeType:`, `namespace:`, text search, etc.) still apply
- Combine with filters for scoped activity views:
```
source:activity nodeType:Thread namespace:ACME scope:descendants  # Recently accessed threads in ACME
source:activity nodeType:Document limit:10                        # Last 10 accessed documents
```

## is
Filters by node classification:
```
is:main            # Only main nodes (excludes satellite content like comments, threads)
```

Satellite nodes exist in support of a main node (e.g., comments on a document, threads started from a page). Main nodes have `MainNode == Path` (or null). Satellite nodes have `MainNode` pointing to their primary node's path.

Combine with other qualifiers:
```
namespace:ACME is:main                    # Main nodes directly under ACME
namespace:ACME scope:descendants is:main  # All main nodes under ACME (recursive)
is:main context:search                    # Main nodes visible in search
```

## context
Filters results by visibility context. Nodes (or their NodeType definitions) can declare contexts from which they should be excluded via the `ExcludeFromContext` property. This enables different views of the same data:
```
context:search         # Exclude nodes hidden from search
context:create         # Exclude nodes hidden from create menus
```

Combine with other qualifiers:
```
nodeType:NodeType context:create          # NodeTypes visible in create menus
namespace:ACME scope:descendants context:search  # Searchable nodes under ACME
```

Nodes are **inclusive by default** — a node without `ExcludeFromContext` is visible in all contexts. A node with `ExcludeFromContext: ["search"]` is excluded only from `context:search` queries but visible everywhere else.

## select
Projects results to include only the specified properties. Returns lightweight dictionaries instead of full nodes:
```
select:name                          # Single property
select:name,nodeType,icon            # Multiple properties (comma-separated)
```

Combine with other qualifiers:
```
namespace:Systemorph select:name,nodeType
nodeType:Story select:path,name sort:name limit:10
```

# Complex Queries

Combine multiple filters:
```
namespace:Systemorph nodeType:Project

nodeType:Story name:*claims* sort:lastModified-desc limit:20

namespace:ACME/ProductLaunch nodeType:Todo scope:descendants
```

# Query in Context

## NodeType Catalog
When viewing a NodeType (e.g., Organization), the default query is:
```
nodeType:Organization
```
This finds all instances of Organization.

## Instance Catalog
When viewing an instance (e.g., Systemorph), the default query is:
```
namespace:Systemorph
```
This finds immediate children of Systemorph. Use `scope:descendants` for recursive search.

# Select (Property Lookup)

The `SelectAsync` API provides an efficient way to retrieve a single property value from a node at a given path, without loading the full content blob.

```csharp
// Get the name of a node
var name = await meshQuery.SelectAsync<string>("Systemorph/Marketing", "Name");

// Get the node type
var nodeType = await meshQuery.SelectAsync<string>("ACME/Project", "NodeType");

// Get the icon
var icon = await meshQuery.SelectAsync<string>("ACME", "Icon");
```

This is useful when you only need one property and want to avoid the overhead of deserializing the entire node. Returns `default` if the node is not found or the property is null.

Available properties include any property on `MeshNode`: `Name`, `NodeType`, `Path`, `Icon`, `Description`, etc.

# Tips

1. **Case insensitive**: All comparisons are case-insensitive
2. **Namespace = folder**: `namespace:X` is like searching in folder X (immediate children)
3. **Add scope:descendants**: For recursive search under a namespace
4. **Wildcards**: Use `*` for flexible pattern matching
5. **Select for single values**: Use `SelectAsync` when you only need one property from a known path
