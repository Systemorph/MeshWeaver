# MeshWeaver.Documentation

Serves the MeshWeaver platform documentation as embedded markdown resources under the `Doc/` namespace, automatically converting them to read-only MeshNodes with YAML front matter parsing.

## Features

- Loads markdown files from embedded resources in the `Data/` folder at startup
- Parses YAML front matter for metadata: title, authors, tags, category, icon, abstract
- Registers a read-only `PartitionAccessPolicy` so documentation cannot be modified
- Grants all authenticated users read access via a public `AccessAssignment`
- Serves static content (icons, images) from the `Content/` embedded resources
- Covers Architecture, DataMesh, GUI, and AI Integration topics

## Usage

```csharp
// In MeshBuilder setup
builder.AddDocumentation();
```

This registers the `DocumentationNodeProvider` as a static node provider and creates a `Doc` partition visible in Global Settings. Documentation is then accessible at paths like `Doc/Architecture/ActorModel`.

## Dependencies

- `MeshWeaver.Mesh.Contract` -- `MeshNode`, `MeshBuilder`, and static node provider interfaces
- `MeshWeaver.Markdown` -- `MarkdownContent` parsing
- `MeshWeaver.ContentCollections` -- embedded resource content serving
