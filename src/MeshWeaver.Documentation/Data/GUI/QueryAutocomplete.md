---
Name: Query Autocomplete
Category: Documentation
Description: How a mesh search bar completes the query language — qualifiers, their values, content properties from the NodeType's schema, reference members, node names and semantic matches.
Icon: /static/DocContent/GUI/icon.svg
---

A search bar that takes the [query language](/Doc/DataMesh/QuerySyntax) completes it as you type.
Two halves work together:

| Half | What it suggests | Where it comes from |
|---|---|---|
| **Grammar** | qualifiers (`nodeType:`, `scope:`, `sort:` …), their fixed values (`descendants`, `all`, `lastModified-desc` …), `content.<property>:` and that property's values | the parser's own vocabulary (`QueryGrammar`) plus the **content schema** of the NodeType in context |
| **Nodes** | for a free-text word: nodes whose name starts with it, and the top semantic matches of the query as typed | `IMeshService.Autocomplete` and the vector index (a free-text word in a query routes there — see [Vector Search](/Doc/Architecture/VectorSearch)) |

Both live in `MeshWeaver.Mesh.Completion` (`MeshWeaver.Mesh.Contract`). The Blazor search bar
(`MeshSearchView`, MeshWeaver.Plugins) wires them into its Monaco editor; the `@`-path completion it
already had keeps working beside them.

---

## What it suggests, by example

| You type (`^` = caret) | Hidden query | Suggestions |
|---|---|---|
| `nodeTy^` | — | `nodeType:` |
| `scope:an^` | — | `ancestors`, `ancestorsAndSelf` |
| `sort:lastModified-^` | — | `lastModified-desc`, `lastModified-asc` |
| `sort:^` | `nodeType:Crm/Opportunity` | `name`, `lastModified`, `subtreeLastModified`, … and `content.amount`, `content.state`, … (each also as `-desc`) |
| `nodeType:Crm/^` | — | every NodeType under `Crm` you can read |
| `content.st^` | `nodeType:Crm/Opportunity` | `state:` — shown with its members and its description |
| `content.state:^` | `nodeType:Crm/Opportunity` | `Open`, `Won`, `Lost`, `OnHold` (the C# enum's members) |
| `content.offering:^` | `nodeType:Crm/Opportunity` | the member nodes of `[MeshNode("nodeType:Crm/Offering")]` |
| `date^` | a type with date properties | `createdDate:`, then `content.<x>:` for every property whose name contains `date` or that holds a date (`format: date` / `date-time`) |
| `content.due^` | a type with `dueDate`, `followUpDue`, `overdue` | `dueDate:` (prefix), then `followUpDue:` (a camelCase word part), then `overdue:` (anywhere in the name) |
| `content.cedent:hai^` | `nodeType:Underwriting/Submission` | `*hai*` (*contains the typed text*, first), then `Shanghai Re` — a member of the SECOND reference leg, matched in the middle of its name |
| `content.cedent:star^` | `nodeType:Underwriting/Submission` | `*star*`, then `Lone Star Mutual` (`LoneStarMutual/Profile`) — in a partition no leg names, found among the catalog's own hits |
| `content.inception:^` | a type whose `inception` is a `date` | `>2026-11-01` *after today*, `<2026-11-01` *before today*, `>=2026-11-01` *today or later*, `<=2026-11-01` *today or earlier* |
| `content.inception:>^` | the same | `2026-11-01` (*after today*) and `=2026-11-01` (completing `>=`, *today or later*) |
| `content.followUpDue:^` | a type whose `followUpDue` is a `date-time` | `>=2026-11-02` *after today*, `<2026-11-01` *before today*, `>=2026-11-01` *today or later*, `<2026-11-02` *today or earlier* — a timestamp is bounded by calendar days |
| `lastModified:^` | — | the same four timestamp comparisons; `createdDate:` too |
| `quarterly rev^` | `namespace:acme` | nodes named `quarterly rev…`, then the semantic hits of `namespace:acme quarterly rev` |

Accepting a node suggestion inserts `path:<its path>`, which narrows the result list to exactly that
node. A value with a space or query syntax in it is inserted as a quoted literal (`"Hosting Plus"`),
never as two terms.

### Contains, never only a prefix

**Field names match by contains** (`QueryGrammarCompleter.FieldMatch`), case-insensitively, ranked:
a prefix first (`last` → `lastModified`), then the start of a camelCase word part or a dotted/dashed
segment (`date` → `createdDate`, `time` → `creationTime`), then anywhere in the name (`due` →
`overdue`). That covers the qualifiers, the node fields `QueryGrammar.NodeFields` lists, `sort:`
fields and the content properties of the NodeType in context; a content property that holds a date
is also found by `date` / `time` whatever it is called. Fixed qualifier VALUES (`scope:an` →
`ancestors`) keep the stricter prefix-or-segment rule (`Match`), where a mid-word hit is noise.

**Reference values match by contains on the referenced node's name OR its path**
(`QueryGrammarCompleter.NodeMatch`), labelled with the node's name; accepting one inserts the stored
value — the path — quoted per the literal rules.

**The contains QUERY is a suggestion, not a new meaning.** `field:value` stays an EXACT match — that
is the language's contract. Typing a plain value of a text property offers, first, the literal
contains filter `*typed*` (*contains the typed text*): `content.cedent:*ha*` is a `Like` glob every
executor evaluates as contains — the in-memory `QueryEvaluator` (`QueryWildcard.IsLikeMatch`),
PostgreSQL and Snowflake (`ILIKE '%ha%'`), Cosmos (`CONTAINS(…, true)`). It is not offered for an
empty, quoted or operator-prefixed value, for a value carrying query syntax, for an enum, boolean,
number or date property, or for a property the schema has not (yet) described — while the schema
read is pending the completer cannot tell a text property from a boolean.

Within one match tier the list position breaks ties, capped below the next tier, so a prefix match
always outranks a word-part or infix match however many reference legs were merged before it.

**Dates complete as comparisons against today, as a calendar day.** A date-valued field — a content
property whose schema `format` is `date` or `date-time`, or a node field in `QueryGrammar.DateFields`
(`lastModified`, `createdDate`) — with nothing typed yet, or only an operator, offers *after today*,
*before today*, *today or later*, *today or earlier*, written as ISO literals (the completer's
`TimeProvider`; days are UTC days). A `date` field compares days: `>D`, `<D`, `>=D`, `<=D`. A
timestamp compares instants and a bare date is its MIDNIGHT, so the day is bounded by the next one:
`>=D+1`, `<D`, `>=D`, `<D+1` — otherwise *today or earlier* (`<=D`) would exclude a value at noon
today and *after today* (`>D`) would include it. After a typed operator, the comparisons that begin
with it complete the rest. There is no relative-date syntax (`today`, `-7d`):
the parser compares literals, and adding one is a separate language decision. Executors differ on
the comparison itself (measured 2026-11-01 in MeshWeaver.Plugins): the in-memory evaluator parses
both sides as dates, PostgreSQL casts a content field to `timestamptz`, Cosmos compares the ISO
strings — and Snowflake casts a content field to a NUMBER (`TRY_CAST(… AS NUMBER)`), so a date
comparison on `content.x` matches nothing there until that generator learns dates.

---

## The grammar half — pure and tested

```text
QueryCaret.At(text, caret)              → QueryCaretToken   (what the token under the caret IS)
QueryGrammarCompleter.Complete(token, facts) → items        (what the language accepts there)
```

`QueryCaret` classifies the caret token with the parser's own lexical rules — whitespace separates
terms, `field:value` is a comparison, a leading `-` negates, a quoted value is literal, `a|b` is
alternation, `>=`/`<=`/`>`/`<` prefix an ordered value, `select:` takes a comma list, and
`content.x` names a content property. Each token carries the exact span an accepted item replaces.
Nothing is completed inside a free-text quote or inside a `field:(A OR B …)` list.

`QueryGrammar` is the single vocabulary the completer offers from, and it holds only words the
parser acts on: an unknown `scope:` value silently becomes `exact`, so it must never be suggested.
`QueryGrammarCompletionTest` walks the parser's reserved-qualifier set and parses every offered scope
back, so a qualifier the parser learns without the grammar learning it fails the build.

```csharp --render GrammarDemo --show-code
var token = MeshWeaver.Mesh.Completion.QueryCaret.At("scope:an", 8);
var items = MeshWeaver.Mesh.Completion.QueryGrammarCompleter.Complete(
    token, MeshWeaver.Mesh.Completion.QueryCompletionFacts.None);
MeshWeaver.Layout.Controls.Markdown(string.Join(", ", items.Select(i => $"`{i.InsertText}`")))
```

---

## Content properties come from the NodeType's schema

The NodeTypes in context are every positive `nodeType:` value in the control's hidden query and in
the visible text. For each (at most three) the completer takes the type's CLR content type from the
mesh-wide `IMeshContentTypeRegistry` and reads it through `ContentJsonSchema` — the same generator
`@{type}/schema/` answers with, so the two can never disagree. `ContentSchemaFacts` reads the
properties out of that schema: the camelCase name, the JSON type, the enum members, the description
(the property's XML doc, rendered as authored), and, for a `[MeshNode("…")]` reference, the queries
whose hits are its allowed values.

A content type is registered when a hub of that NodeType activates in the process (and by the boot
registration sweep). When the type is not known yet, the completer reads ONE record of it through
`GetMeshNodeStream` — the sanctioned node read — which activates that record's hub and so registers
the type; it subscribes to the registration before the read and re-checks the registry after it, so
neither order of events is lost. A type of which the viewer can read no record contributes no
properties: there is nothing of it to search.

Two schema facts make this possible:

- **A node-addressed schema describes the node's CONTENT.** A data source that declares a content
  type implements `IContentTypeSource` (`MeshDataSource` does, from `WithContentType<T>()`), and a
  `SchemaReference` with no type name describes that type. Before, it fell to "the first registered
  type that is not `MeshNode`", which on a node whose content has no type source of its own meant a
  satellite type (`Comment`) or `MeshNode` itself.
- **A reference states its members.** The schema generator writes a `[MeshNode("…")]` property's
  queries as the `x-meshNode` extension (`ContentSchemaFacts.MeshNodeExtension`), so a reader with
  no CLR type — this completer, an agent writing content — can resolve them:

```json
"offering": { "type": ["string","null"], "x-meshNode": ["nodeType:Crm/Offering"] }
```

A reference's members come from two kinds of source, merged by path as they answer:

- **Every reference leg.** A property declares one anchored query per partition —
  `[MeshNode("namespace:Reinsurance scope:descendants nodeType:Reinsurance/Cedent", "namespace:ReinsuranceDemo scope:descendants nodeType:Reinsurance/Cedent")]`
  — so every leg is read, not the first (`QueryCompleter.ReferenceLegReads`). Each runs bounded
  (`limit:50`), filtered by what was typed on the member's name (`name:*ha*`) and, in a second read,
  on its whole path (`mainNode:*ha*` — a main node's `mainNode` IS its path, so a parent segment
  matches too, the store applies it before the limit, and unlike `path:*ha*` it filters instead of
  re-anchoring the leg), declared `partitions:all` when it anchors nothing, under the viewer's
  identity. A template query (`{node.X}`) needs the record being edited, which a search box does not
  have, so it is skipped.
- **The catalog's own hits.** The members a book actually references often live in partitions no leg
  names — production keeps one top-level partition per company, a cedent at `LoneStarMutual/Profile`.
  The hidden query defines the catalog's result set, so each hidden sub-query is run (its
  `sort:`/`limit:`/`select:` replaced, narrowed to hits that carry the property — `content.cedent:*` —
  with the content selected, at most `HitScanLimit` = 200 hits), the distinct values those hits carry
  are collected (at most 50), and the ones that are node paths are resolved to their nodes in one
  `path:a|b|…` read for the label. A value that resolves to nothing is offered as stored.

---

## The reactive half — `QueryCompleter`

```text
new QueryCompleter(meshService, hub).Complete(new QueryCompletionRequest(text, caret, hiddenQuery, contextPath))
    → IObservable<QueryCompletionResult>   // growing snapshots; COMPLETES when every source has answered
```

- **Reactive, never async.** A cold observable; each source is `Take(1)` or bounded, so the stream
  completes. The first snapshot (the grammar alone) is immediate; schema, catalog, reference and
  node suggestions merge in as they answer.
- **Bounded.** Every source has a budget (`QueryCompleter.SourceBudget`, 2 s) and a size cap
  (`SemanticTopK` = 5, `NameTopK` = 8, `ValueReadLimit` = 50). A source that fails or overruns
  contributes nothing to that suggest list and is logged at Warning; the grammar's own suggestions
  still show.
- **As the viewer.** Every query and node read carries the ambient viewer, so a suggestion names
  only what the viewer could find with the query themselves.
- **One instance per query box.** It caches content schemas per NodeType for its own lifetime in a
  `PromiseCache` (a failed fetch is retried by the next request, never replayed) and holds no static
  state. A recompiled NodeType's new properties appear in a box opened after the recompile.
- **Semantic suggestions** run `SemanticQuery(request)`: the first hidden sub-query plus the visible
  text, with its `sort:`/`limit:`/`select:` removed and `partitions:all` added when nothing anchors
  it. On a store with an embedding provider its free-text words rank by cosine similarity; without
  one they match lexically. They are offered for a word of three or more characters that is not the
  start of a qualifier, and ranked below the name matches.

The ORDER of `QueryCompletionResult.Items` is the ranking (grammar, then name matches, then semantic
matches); a client must not re-sort it. Labels and descriptions that the platform owns are carried as
localization keys (`DescriptionKey`, `CategoryKey`, all under `query.completion.*`) and follow the
viewer's language; a schema description or a node name is authored content and renders as written.
