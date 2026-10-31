---
Name: Repairing a Markdown Body Stored Under the Wrong Key
Category: Architecture
Description: Why a Markdown node whose body was stored under `body`, `markdown` or `text` renders as Unreadable, why the record refuses to bind it, and how the detection-driven repair moves the body under `content` without guessing.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M9 15l2 2 4-4"/></svg>
---

# Repairing a Markdown Body Stored Under the Wrong Key

`MarkdownContent` stores its source under one member: `content`. Some rows carry the document
under a member the record does not declare — `body`, `markdown` or `text`. They were written by
hand-built JSON: an installer that wrote `["body"] = …`, or an agent that authored a node through a
raw `create`.

## Why the code does not absorb them

The record deliberately **refuses** to bind such a payload as an empty document. Binding it would
render the *"No content yet"* invitation over a document that is still stored, and the first save
from that invitation overwrites the text. So every read seam keeps the raw JSON, the Markdown
overview marks the node **Unreadable** and names the members it carries, and the log line names
the node path. The defect is in the DATA, and only a pass over the data closes it.

## What the repair does

`MarkdownBodyKeyRepair` (in `MeshWeaver.Hosting.Persistence`) walks the storage tree by path, the
same walk `StaleMainNodeRepair` uses, and reads every node in scope. A node is **repairable** when:

- its content is raw JSON typed `MarkdownContent` (or a `Markdown` node with untyped object content),
- it has no `content` member, and
- it carries exactly one member the record does not declare, a string, under `body`, `markdown`
  or `text`.

The repair moves that string, unchanged, under `content`. Every declared member (tags, authors,
abstract, …) is kept. The write goes through `GetMeshNodeStream(path).Update(…)` under
`RunAsSystem`, and the update lambda re-derives the rename from the node as its owning hub holds
it, so a node fixed in the meantime is left alone.

Anything else that fails to bind is reported as **NeedsReview** and never written: two stray
members, a non-string value, or a key outside the three. Which member is the body is then a
judgement about content, not a mechanical rename. The same holds when the rename would change a
declared member. For example, `tags` stored as a string reads as an empty list, and writing that
back would lose the stored value. It holds as well when a declared member cannot bind to its typed
shape at all, such as `abstract` stored as an object. That row is classified on its own, so it
never aborts the sweep, and the rows beside it are still reported and repaired. A row whose node, at write time, still does not bind is also
reported as NeedsReview rather than Repaired. This covers a row that changed between the scan and
the write.

The pass is idempotent. A repaired row no longer matches, so a second run reads the same rows and
writes nothing. The report lists every finding by path, and it also carries `PathsScanned` and
`NodesRead`, so "found nothing" cannot be confused with "read nothing".

## Using it

```csharp
// Measurement. Writes nothing. Safe on production.
MarkdownBodyKeyRepair.Detect(hub, ["Event"]).Subscribe(r => …);

// The repair. Scope it to partitions, or omit the roots for the whole mesh.
MarkdownBodyKeyRepair.Repair(hub, ["Event", "ReinsuranceDemo"]).Subscribe(r => …);
```

Both return **cold** observables: the sweep runs on `Subscribe`.

🚨 **Nothing self-arms.** No composition root calls it, so deploying an image that contains it
repairs nothing. Running `Repair` against a live portal is a separate operator decision, taken on
the evidence `Detect` produces. Raw SQL is not an alternative: a direct `UPDATE` bypasses the
workspace cache, so live subscriptions keep serving the old value.

🚨 **`Repair` is an impersonation surface — an operator's, never in-mesh code's.** It writes as
System over whatever partitions `roots` names, so user-authored in-mesh code must not reach it.
Both halves of the [in-mesh impersonation guard](../InMeshImpersonation) cover it, and
`StaleMainNodeRepair.Repair` the same way: the compile-time scan reports a reference to either
`Repair` (`InMeshImpersonationReferences.Members`), and on entry the method judges its calling
assembly. An untrusted in-mesh caller is refused under `Enforce`, and the refusal arrives as the
observable's error. `Detect` only reads and is not reported.

## Related

- [Repairing a Stale MainNode](../StaleMainNodeRepair) — the same detection-driven shape for
  another data defect.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — why raw SQL is not
  the repair route.
- [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) — when running as System
  is legitimate.
