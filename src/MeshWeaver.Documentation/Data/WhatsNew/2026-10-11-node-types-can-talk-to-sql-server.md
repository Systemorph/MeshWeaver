---
Name: Node types can talk to SQL Server
Category: Fix
Description: A node type that referenced Microsoft.Data.SqlClient compiled and then failed the moment it rendered, because only the package's placeholder assembly was ever loaded — the real client sits in a per-platform folder that was ignored. Package references now load the assets built for the platform the portal runs on, and share the portal's own copies of common libraries instead of loading older duplicates.
Icon: Bug
Order: -20261011
---

# Node types can talk to SQL Server

A node type can pull in a NuGet package with `#r "nuget:Package, Version"`. That worked for most
packages — and failed for every .NET SQL Server client. On 11 October a node type that referenced
`Microsoft.Data.SqlClient` compiled cleanly on partnerre.meshweaver.cloud and then broke as soon as
it rendered: *Could not load file or assembly 'Microsoft.Data.SqlClient'*.

## Why

A NuGet package can ship one generic assembly plus replacements built for specific platforms. The SQL
Server clients do exactly that: the generic copy is a placeholder that only says "not supported on
this platform", and the working client lives in a Linux/macOS folder and a Windows folder beside it.
The loader only ever looked at the generic copy.

The same package also brings its own, older copies of libraries the portal already runs — JSON,
dependency injection, logging. Loading those beside the portal's copies makes two versions of the same
types that cannot be passed to each other.

## What changed

- A node type still compiles against the package's generic assembly, but at run time it now loads
  the copy built for the platform the portal runs on (Linux on the hosted portal), falling back
  step by step to the generic one — the same rule `dotnet restore` follows.
- Native libraries a package ships for the platform are found too.
- When the portal already ships a library at the same or a newer version, the node type uses the
  portal's copy instead of loading an older duplicate, and the log says which ones were shared.

The motivating case — connecting to an Azure SQL database with
`Authentication=Active Directory Managed Identity` from a node type — is now covered by a test that
builds such a connection object against the real package. The design is described in
[NuGet Runtime Assets](/Doc/Architecture/NuGetRuntimeAssets).
