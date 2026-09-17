---
Name: Content Is Validated Against Its Declared Shape On Write
Category: Architecture
Description: >-
  Typed node content that cannot bind to the content type its NodeType declares was stored verbatim
  and then read as absent on every consumer. The two shapes the write boundary now refuses, the
  narrow rule that keeps legitimate writers landing, and the three places this guard deliberately
  says nothing.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/><path d="M12 8v4"/><path d="M12 16h.01"/></svg>
---

# Content Is Validated Against Its Declared Shape On Write

**A write whose `MeshNode.Content` cannot bind to the content type its NodeType declares is refused
at the write boundary, naming the member and the type that member was declared as.** Storing it and
letting the failure surface at read time converts a caller's mistake into durable corruption that
nobody can attribute.

## What was measured

memex.systemorph.com, 2026-09-17 ([#4601](https://github.com/Systemorph/MeshWeaver/issues/4601)).
A `patch` that put a JSON object into a member declared `public string?`:

```json
{"content": {"country": {"$type": "CountryReference", "id": "CH"},
             "industry": {"$type": "IndustryReference", "id": "MedicalTechnology"}}}
```

returned `Patched: … (v2 → v3)` and read back at v3 with both objects stored **verbatim**. Neither
`CountryReference` nor `IndustryReference` exists in any model — both names were invented for the
probe. The record then no longer deserialised as its declared type, so every reader's
`ContentAs<T>` answered `null`.

The same silence accepted a `Markdown` **create** whose text sat under a member named `markdown`,
which `MarkdownContent` does not declare. That node rendered as *"No content yet. Use the menu to
start editing."* over a full document from v1 — the read-side half is
[#4600](https://github.com/Systemorph/MeshWeaver/issues/4600).

## Why the write boundary, and not tolerance at the reader

The read seams are already as tolerant as they can be, and that is the right posture there:
[`ContentAs<T>`](/Doc/Architecture/CqrsAndContentAccess) recovers a degraded `JsonElement`, and
`IMeshContentTypeRegistry.TryRecoverForNodeType` resolves a content type the reading hub never
registered ([Content-Type Registration](/Doc/Architecture/ContentTypeRegistration)). Neither can
invent data that was never stored in a shape anything can read.

Tolerance is the wrong posture for a NEW write. The caller who sent the payload is the one who can
fix it, in one retry, right now. Discovering it months later means finding the producer from a page
that renders empty — which is what the read-side placeholder made impossible to even notice.

## The two shapes that are refused

`ContentSchemaValidator` (`src/MeshWeaver.Graph/Security/`) runs in the Create and Update validation
chains, beside [`ContentDiscriminatorValidator`](/Doc/Architecture/ContentTypeRegistration).

| Shape | Why it cannot be caught any other way |
|---|---|
| A member whose value contradicts its declared type — an object into a `string?`, a string into an `int` | `System.Text.Json` throws, but the wire converter deliberately **preserves the raw JSON** rather than faulting the read (a throw wedges the grain), so the payload reaches the store intact |
| Content **none** of whose members the declared type knows | `UnmappedMemberHandling.Skip` makes this bind *cleanly* to an instance carrying none of the authored data — no exception exists to catch |

The refusal names the member and the declared type, and is worded in the caller's language
(`content.schema.memberTypeMismatch`, `content.schema.noDeclaredMember` — see
[Localization](/Doc/Architecture/Localization)).

## 🚨 The narrow rule, and why it is narrow

Two deliberate limits keep this from becoming a schema-strictness change that breaks writers who
were never the problem:

- **A bind failure is refused only when `JsonException.Path` names a MEMBER** (`$.country`), never
  when it blames the document as a whole (`$`). A missing `required` member is the ordinary
  partial-content shape a legitimate writer produces; refusing it is a different decision, and not
  this one.
- **Unmapped members are refused only in the TOTAL case** — at least one member present and not one
  of them declared. Content carrying an extra member *alongside* real ones is what an older or newer
  writer of the same record produces all the time, and the read path's `WarnIfLossy` already reports
  what it drops. Measured over this repository's 544 seeded node-content objects: exactly one shape
  carries no `$type` at all (`Systemorph/Marketing/Post`), and every one of its members is declared.

## Where it deliberately says nothing

Each of these answers Valid, and each is a decision rather than an omission:

1. **No entry for the NodeType in `IMeshContentTypeRegistry`.** A type nothing declares a content
   type for legitimately stores free-form JSON, and an in-mesh type whose first instance has not
   activated yet must not have its writes refused for a fact this process has not learned. Static
   definitions are covered from boot by `ContentTypeRegistrationSweep`; a compiled one registers at
   its first instance activation.
2. **Typed (in-process) CLR content.** It already bound. See the blind spot below.
3. **Content whose own `$type` names a different record than the declared one** — the same
   `DiscriminatorAdmits` short-name rule the recovery path applies. Judging such content by the
   declared type would be reshaping it; the discriminator guard owns that case.
4. **An Update whose content is byte-identical to what is stored.** Re-asserting a row already on
   disk is not a new write of bad content, and refusing it would make an existing broken node
   impossible to move or repair.

## The blind spot this does NOT close

**A payload whose `$type` resolves on the WRITING hub binds at the wire, and its unmapped members are
dropped there — before any validator sees the node.** `ObjectPolymorphicConverter.Read`
deserialises to the resolved type, `UnmappedMemberHandling.Skip` discards what that type does not
declare, and the validator is then handed a well-formed CLR instance of an impoverished record.

In practice the shapes that matter reach the boundary as raw JSON — an in-mesh compiled content type
is never on the writing hub's `$type` registry, and a member-level mismatch makes the converter
preserve the raw JSON on purpose — which is why both measured cases are covered. The residue is a
payload that both resolves *and* carries extra members, on a hub that knows the type. Closing it
means judging the RAW bytes at the API boundary, before deserialisation, and that is a separate
change with its own cost.

## Related

- [Update Validators See Typed Content](/Doc/Architecture/UpdateValidatorsSeeTypedContent) — what the
  update pipeline owes a validator, and the retype case where it must not.
- [Content-Type Registration](/Doc/Architecture/ContentTypeRegistration) — how a NodeType's content
  type becomes known, which is the precondition for judging anything here.
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why `ContentAs<T>` and never a
  cast, and what a silent null looks like from outside.
