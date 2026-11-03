---
Name: UML Class Diagram
Category: Documentation
Description: Controls.UmlClassDiagram — class boxes with an attribute compartment, joined by associations (role and multiplicities), compositions and generalizations, laid out by a deterministic layered layout and projected straight from the content schemas a mesh publishes.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="8" height="7" rx="1"/><path d="M3 6.5h8"/><rect x="13" y="14" width="8" height="7" rx="1"/><path d="M13 17.5h8"/><path d="M7 10v7h6"/></svg>
---

# UML Class Diagram

`Controls.UmlClassDiagram(classes, relations)` draws a UML class diagram. Each class is a box with a header and an attribute compartment. The header holds an optional `«stereotype»`, the name, and the package of a foreign class. Each attribute line reads `name : Type [multiplicity]`.

Classes are joined by relations:

| Relation | Notation |
|---|---|
| Association | open arrowhead, role name and multiplicity |
| Composition | filled diamond at the whole |
| Generalization | hollow triangle at the base class |

A box with an `Href` is a link.

```csharp
var (classes, relations) = UmlSchemaProjection.FromContentSchemas(
    typePaths.Select(p => new UmlSchemaInput(p, schemaOf(p))),
    path => $"/DataModelling/Explorer/Type/{Uri.EscapeDataString(path)}");
return Controls.UmlClassDiagram(classes, relations).WithMaxAttributes(6);
```

## The model is the schemas

`UmlSchemaProjection.FromContentSchemas` reads the JSON schemas `@{type}/schema/` publishes:

- **Attributes.** Every property except `$type` is an attribute. An array is `[*]` and a nullable value is `[0..1]`. The description is the property's XML doc.
- **Relations.** A property carrying `x-meshNode` (a `[MeshNode("nodeType:…")]` reference) becomes an association to each `nodeType:{Path}` it names. The association is labelled with the property name, and its target multiplicity is `*`, `0..1` or `1`.
- **Foreign classes.** A referenced type that is not one of the inputs becomes a **foreign** class labelled with its package, so references across plugins show.

The projection needs no CLR type and no instance. It is the same answer the schema read gives an agent.

## The geometry is pure

`UmlClassDiagramControl.Layout` is a layered (Sugiyama-style) layout, unit-tested in `UmlClassDiagramTest`:

- **Layers.** A referenced class or a part sits below the class that references or owns it. A base class sits above its subclass, because UML draws the triangle up.
- **Cycles** are broken by reversing the back edges of a depth-first walk in input order.
- **Order within a layer** comes from four barycentre sweeps, with ties kept in input order.
- **Edges** are orthogonal. Each box spreads its ports so parallel edges do not coincide.
- **Guarantees:** no two boxes overlap, and the canvas encloses every box and route.

Renderers only draw what `Layout` returns. Every view of the same model therefore draws the same picture, and a renderer needs no JavaScript layout engine.

The layout is computed in C# rather than with a bundled JS engine (ELK, dagre) for three reasons:

- it is deterministic and testable without a browser;
- it runs where the data is;
- it adds no script asset to the portal.

The platform already ships Mermaid, but it lays out its class diagrams itself and gives a caller no control over notation or interaction.

## Size

`WithMaxAttributes(n)` caps the attribute lines per box; the rest read `+ n more`. For a large model, pass the classes the page is about plus their direct neighbours rather than the whole package.

Open vocabularies (string constants, never enums): `UmlClassKind` (`local`, `foreign`, `featured`) and `UmlRelationKind` (`association`, `composition`, `generalization`). A renderer draws a value it does not know plainly.
