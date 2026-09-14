---
Name: Import-Side Content Degradation
Category: Architecture
Description: A typed node whose own parser is absent degrades to Markdown at IMPORT, and the read-side degradation instrument is structurally blind to it — because the content it wrote types perfectly.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="m9 15 2 2 4-4"/><path d="M4 4 20 20"/></svg>
---

# Import-Side Content Degradation

There are two ways a node's content can end up as something other than the type it declares, and
the platform had an instrument for only one of them.

| | where it happens | what the node holds | what sees it |
|---|---|---|---|
| **READ-side** | a replica reads content whose `$type` resolves to no registered CLR type | the stored typed content, degraded to a `JsonElement` on this read | [`ContentDegradationRegistry`](../ContentTypeRegistration), reported on `/health` as `content-types` |
| **IMPORT-side** | a `.md` file declares a `nodeType` whose parser is not registered on the importing host | `MarkdownContent` — **written that way, permanently** | nothing, until #4319 |

The second is the worse one, and it hid behind the first. The read-side registry answers *"which
node types could this replica not type?"*, and an import-degraded node answers that question
cleanly: its content **is** `MarkdownContent`, `MarkdownContent` **is** registered, so nothing
degrades on any read, ever. The loss happened once, at import, and left no trace.

## How the fallback comes to win

`FileFormatParserRegistry` tries parsers in priority order, contributed parsers first:

```csharp
_parsers =
[
    ..(contributedParsers ?? []),   // e.g. the AI module's agent parser, ahead of Markdown
    new MarkdownFileParser(),       // Fallback for other .md files
    …
];
```

The order is deliberate and correct — `MarkdownFileParser` accepts **every** `.md` file, so a
parser that recognises one front matter has to be ahead of it or it never runs. What the order
cannot do is make the parser ahead of it *succeed*. `TryParse` walks the list until one returns a
node, and **catches a parser that throws**, so the fallback wins in two different situations:

1. the module that owns the declared type is **not loaded** in the host doing the import, so the
   list has no entry for it at all; or
2. that parser **refused the file** — returned null, or threw and was caught.

Only the first is a deployment property. The second is a property of the FILE, and it is the one the
live case turned out to be.

And it succeeds convincingly. `MarkdownFrontMatter` binds `nodeType`, `name`, `category`, `icon`,
`state`, `description`, `order` and the markdown metadata, so the node lands with the right
`NodeType`, the right display name, the right icon, the right category, and the body. Everything a
listing shows is correct. Only the keys the *declared type* added — the ones that made it that type
— are gone, because `IgnoreUnmatchedProperties()` discards what the model does not declare.

## The live case

`Crm/Agent/crm-assistant` on `memex.meshweaver.cloud`, measured 2026-09-14. The source file in
`Systemorph/MeshWeaver.Crm` authors nine front-matter keys:

```yaml
nodeType: Agent
name: CrmAssistant
displayName: CRM Assistant
description: Keeps the client pipeline honest from a conversation …
icon: <svg …>
category: Crm
exposedInNavigator: true
contextMatchPattern: address.nodeType=like=Crm/*
plugins:
  - Mesh
```

The node has `nodeType: Agent`, `name: CrmAssistant`, the icon and the category. Its
`content.$type` is `MarkdownContent`. `displayName`, `exposedInNavigator`, `contextMatchPattern`
and `plugins` — the whole `AgentConfiguration` — reached nothing, and **so did `description`**,
which `MarkdownFrontMatter` *does* bind. It served that way for twelve days and was found by a
human noticing a blank description in the agent dropdown.

### 🚨 The `description` is the tell, and it says the YAML was REFUSED

`description` is bound by this parser (as the `Abstract` alias) and lands on `MeshNode.Description`.
Its absence is therefore not a discarded key — it is evidence that the deserializer never ran to
completion.

Look at the value: `… "new deal at PG3: fund reporting pilot, 45k" …`. A `": "` inside a plain
scalar is not valid YAML. `MarkdownFileParser.Parse` catches the failure and falls back to its
**defensive regex extractor**, which recovers `NodeType`, `Name`/`Title`, `Category`,
`Icon`/`Thumbnail`, `State` and `Order` — and **not** `Description`/`Abstract`. That is exactly the
node on the mesh: right type, right name, right icon, right category, right order, blank
description.

So the cause is the FILE, and a contributed parser would have to be more tolerant than YAML itself
to escape it: it refuses, `TryParse` catches, and the one parser with a regex rescue wins by
construction. `ATypedNodeDegradedToMarkdownSaysSoTest` reproduces the refusal from the file's
verbatim front matter.

### The sibling is not a control — it has the same wound

`Crm/Skill/crm`, read the same day, is degraded **identically**: `content.$type: MarkdownContent`,
no description, with `name: /crm`, `category: Skills`, `icon: 🤝` and `order: 12` intact. Its file
carries the same malformed `description:` (`"/crm new deal at PG3: fund reporting pilot"`). An
earlier reading of this page used it as a control for "the two files met different parser sets" —
that reading was wrong, and measuring the sibling's content is what falsified it.

🚨 **`Crm` having no `_GitSync` entry is not the cause and not a defect either.** That space is
populated by the PLUGIN CATALOG — `memex-cloud`'s deployment record lists `Crm` under both
`pluginRepos[].isRegistrySource` and `preInstall` — and a package-installed space has no `_GitSync`
by design. Nor is its content stale: 15 of the space's 29 children carry a 2026-09-14 timestamp, and
`Crm/Incidents` reached the mesh 38 minutes after it was committed. Only the two malformed files are
stuck, and they are stuck because their bytes have not changed since 2026-08-30, so every
incremental update correctly skipped them.

## What the platform records now

`MarkdownFileParser` names what it discarded, on the node:

```jsonc
"content": {
  "$type": "MarkdownContent",
  "content": "You are the **CRM Assistant** …",
  "unboundFrontMatter": ["displayName", "exposedInNavigator", "contextMatchPattern", "plugins"]
}
```

`MarkdownContent.UnboundFrontMatter` is the import-side twin of `UnknownMembers`. The rules it
follows, and why:

- **It names keys; it does not keep values.** It is a diagnosis, never data, and nothing may read
  it as configuration. It is also `[Browsable(false)]`, because
  `MeshNodeContentEditorControl.FromType` puts every property of a content record on the form —
  a diagnosis a viewer can clear while the loss remains is worse than no diagnosis. The repair is
  to fix the file (or load the type's parser) and import again, at which point the fallback does
  not run and the record is not written.
- **It reads the keys with the SAME parser that discarded them.** A key grammar of its own —
  unquoted identifiers, say — would miss a quoted or dotted key that YamlDotNet accepts and this
  parser discards, and a detector that sees less than the loss is the one failure this record must
  not have. A narrower regex remains only as the fallback for text the parser cannot read at all,
  which is precisely the refused-YAML case above.
- **It is gated on the file DECLARING a `nodeType`.** A declared type is a claim that some parser
  knows how to build this node's configuration, so a key the fallback cannot bind is that
  configuration going missing. An untyped markdown page makes no such claim: its extra keys are
  the author's own metadata, not a degradation. Measured over this repository's 1,637
  front-matter `.md` files, that gate is the difference between 41 flagged files and one.
- **It changes no export bytes.** `MarkdownFileParser.Serialize` is untouched, so no git mirror
  sees a diff because of this.

## The residue, stated

Two things this does NOT do, deliberately:

1. **A mesh→repo export still drops the unbound keys.** The record says which keys were lost; it
   does not restore them to the file. Re-emitting them would mean storing their values and would
   produce an export diff on every already-degraded file in the fleet — a large, surprising
   change that belongs to its own decision, not to making the loss visible.
2. **`SlideContent` nodes get no record.** The slide branch builds a different content type, which
   has nowhere to carry one. A slide's own degradation path is the compiled type branch documented
   in [Declarative export and import](../DeclarativeImportExport).

## Related

- [Content-Type Registration](../ContentTypeRegistration) — the READ-side half, and
  the registry that answers `/health`'s `content-types`
- [Import Write Ordering](../ImportWriteOrdering) — the other ordering hazard an
  import has: a NodeType must land before the instances that name it
- [Install Completeness](../InstallCompleteness) — what an install declared landed,
  against what is in the mesh; it compares PATHS, so a node that exists but is degraded is a pass
  there by construction
