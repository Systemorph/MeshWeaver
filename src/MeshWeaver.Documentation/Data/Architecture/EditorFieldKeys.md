---
Name: Editor Field Keys
Category: Architecture
Description: The node-content editor binds by JSON key, so a field's key is a WIRE name and never a CLR name. Two ways the same tab silently discarded what an admin set — a property renamed behind [JsonPropertyName], and a default-true bool the serializer drops — how each one latched, and the rule that closes both.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7V5a1 1 0 0 1 1-1h14a1 1 0 0 1 1 1v2"/><path d="M9 20h6"/><path d="M12 4v16"/><path d="m17 13 3 3-3 3"/><path d="M20 16h-7"/></svg>
---

# Editor Field Keys

`MeshNodeContentEditorControl` is the framework's answer to "let an admin edit this node's
content" — the [data-bound editor](/Doc/GUI/DataBinding) that replaced hand-rolled forms plus a
save subscription. The backend DECLARES the fields; the GUI reads and writes them straight on the
node stream.

**The declaration is a list of JSON keys.** That one sentence is the whole subject of this page,
and getting it wrong is silent in both directions.

## How the binding works

`MeshNodeEditorField.FromType(contentType)` reflects a content record into a field list, and
`MeshNodeContentEditorView` (MeshWeaver.Plugins) uses each field's `Key` **verbatim, as a key into
the node content's JSON object**, on both sides of the binding:

```csharp
// read  — LoadValues
var value = ToJsonObject(node.Content)?[f.Key];

// write — Persist: a per-field read-modify-write through the node stream
Hub.GetMeshNodeStream(NodePath).Update(node =>
{
    var obj = ToJsonObject(node.Content) ?? new JsonObject();
    obj[key] = value;                       // ← only this key
    return node with { Content = JsonSerializer.SerializeToElement<object>(obj, opts) };
});
```

There is no type registry on the GUI side and no name translation anywhere in between. So **the
key the backend declares is the key the record must carry on the wire** — not the name of the CLR
property it came from.

## Failure 1 — a key that names no field

`FromType` used to derive the key as `p.Name.ToCamelCase()`, ignoring `[JsonPropertyName]`. That is
correct exactly as long as the two never diverge, and it produces no error when they do:

| | the record carries | the editor binds to |
|---|---|---|
| `public UpdatePolicyKind? Policy` | `policy` | `policy` ✅ |
| `[JsonPropertyName("policy")] public UpdatePolicyKind? DeclaredPolicy` | `policy` | `declaredPolicy` ❌ |

Both halves of the binding then miss, and **both are silent**:

- **the read** finds nothing at `declaredPolicy` and renders the control **unset** — over a value
  that is on the record;
- **the write** persists `declaredPolicy`, which the record's deserializer does not recognise, so
  the value lands nowhere. Nothing throws; nothing is logged; the write genuinely succeeds.

🚨 **And it is worse than "written but unread": the value never reaches storage at all.** The owning
per-node hub materialises the content as `UpdatePolicyContent` and re-serialises it, so an unknown
key is dropped on that round trip. Measured by the control in this repo — a wait for `Stable` to
appear on the record under *any* key spends its entire convergence budget and times out.

What the operator sees is therefore a *transient* success. `MeshNodeContentEditorView` keeps the
chosen value in its own `_text[f.Key]` field state, so the dropdown shows it until the next stream
emission runs `LoadValues`, which reads `obj["declaredPolicy"]`, finds nothing, and silently reverts
the control to unset. An admin who sets the strategy and navigates away sees it applied; one who
watches the tab sees it undo itself for no stated reason.

### What that cost

The property in the table is real. `Admin/UpdatePolicy` carries the platform's auto-update
strategy, and `UpdatePolicyContent.Policy` became `DeclaredPolicy` + `[JsonPropertyName("policy")]`
so that an ABSENT declaration could fail closed to `None` instead of reading as "roll to anything"
(#3607, itself a fix for #3542). From that commit the Updates settings tab's strategy dropdown
wrote `declaredPolicy`.

🚨 **That is what turned a lost setting into a latched one.** An install whose `policy` field had
been dropped fails closed to `None`; under `None` the poller records *"updates are disabled on this
install"* and evaluates nothing at all (#3795) — no check, no hold, no verdict. The ONE surface
built to turn updates back on is the Updates tab, and it had become a no-op that looks like a
success. Nothing else on the install ever disagrees with it, because under `None` nothing else
runs.

Measured on memex.meshweaver.cloud, 2026-09-18: `Admin/UpdatePolicy` carries no `policy` field, its
`lastCheckVerdict` reads *"updates are disabled on this install (Admin/UpdatePolicy = None); the
registry was not listed"*, and its `heldAt` is frozen at 2026-09-11T08:57Z — the last evaluation
that ran while a policy was still on the record. Every `lastCheckedAt` since is a check that decided
nothing.

### The rule

**A field's `Key` is the JSON name.** `[JsonPropertyName]` wins; the camelCase property name is
the fallback, unchanged, because it is the key every stored record was already written under and
re-deriving it differently would re-key live content.

**And `[JsonIgnore]` is not editable.** Such a property is one the record does not persist, so a
control over it can only ever write a key the type drops on read. `UpdatePolicyContent.Policy` — the
computed convenience that folds an absent declaration to `None` — was reflected as a SECOND enum
dropdown over the same values, sitting beside the broken one with no label to tell them apart.
Only the default `Always` form is skipped: `[JsonIgnore(Condition = Never)]` means the opposite and
stays editable, which is the next failure.

## Failure 2 — a value the serializer drops

The hub serializer sets `DefaultIgnoreCondition = WhenWritingDefault`, so **any property whose
value equals its CLR default is omitted on write** — and a record's property INITIALIZER then fills
it back in on read. For a property whose initializer differs from the CLR default, one value
becomes unwritable:

```csharp
public bool RequireCiGreen { get; init; } = true;   // CLR default is false
```

An admin unticking *"Only update to CI-verified (green) builds"* wrote `false`, the serializer
dropped it, and the next read re-applied `true`. A checkbox that can be unticked and never stays
unticked.

The cure is already the house idiom — `NotificationSettings` carries it on all eight of its bools
and `GitHubSyncConfig` on both of its default-true ones, each with a comment saying why:

```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.Never)]   // "always write this, even at its default"
public bool RequireCiGreen { get; init; } = true;
```

**The denominator.** Across the five content types bound to `MeshNodeContentEditorControl.ForType`
in this repo — `UpdatePolicyContent`, `GitHubSyncConfig`, `GitHubPullRequest`, `HomeConfig`,
`NotificationSettings` — `RequireCiGreen` was the ONLY editable property whose initializer differs
from its CLR default and which lacked the attribute. Every editable enum (`SyncDirection`,
`HomeStyle`, `HomeCatalogScope`, `HomeCatalogRender`, `HomeCatalogSort`) defaults to its own zero
member, so its round trip was already lossless; and it is likewise the only one of the five with a
`[JsonPropertyName]` rename or a plain `[JsonIgnore]`.

## Why neither failure was caught

Both are **writes that succeed**. There is no exception to catch, no log line to grep, and the
control re-renders showing the value the operator chose — from its own in-memory state in failure 2,
and from the junk key it just wrote in failure 1. The only way to see either is to compare what the
editor declares against what the serializer does, which is what
`MeshNodeEditorFieldTest` now pins:

- every declared `Key` must appear in the JSON the serializer actually produces for that type;
- `[JsonIgnore]` (the `Always` form) is absent from the field list, `Condition = Never` is present.

And `AdminChosenPolicyReachesTheRecordTest` (`test/Memex.Portal.Shared.Test`) drives the view's
exact per-field write against a real mesh, so "an admin picked Stable" has to end as
`Policy == Stable` on the record — with a positive control on a field carrying neither trap, so
"the edit did not stick" can never be satisfied by a harness that wrote nothing.

## The half that is not in this repo

`MeshNodeContentEditorView.Persist` (MeshWeaver.Plugins) opens with

```csharp
var obj = ToJsonObject(node.Content) ?? new JsonObject();
```

and `ToJsonObject` returns `null` for **two different facts**: "there is no content" (correct — the
`new JsonObject()` is create-on-absent) and "I could not read the content", which its `catch`
swallows. In the second case the per-field write stops being a patch and becomes a **whole-record
replacement by `{ "<key>": value }`** — the same failed-read-becomes-a-written-default shape that
[#3619 removed from every bookkeeping write](../CqrsAndContentAccess) on this very node.

It is narrower than the key defect (it needs content that is present and not serialisable to a JSON
object, rather than any renamed property) and it is invisible to
`DefaultedContentReadInsideAnUpdateLambdaGuard`, whose regex looks for `ContentAs<T>(…) ?? new` and
cannot see a defaulting read behind a helper method. Tracked separately; the cure is the same
tri-state the framework's typed write already implements — absent creates, unreadable refuses.

## Checklist for a new editor-bound content type

1. Does any editable property carry `[JsonPropertyName]`? The key follows the attribute — this is
   now automatic, but a rename is still the moment to re-read this page.
2. Does any editable property have an initializer differing from its CLR default (a default-`true`
   bool, a non-zero enum default, a non-empty string default)? It needs
   `[JsonIgnore(Condition = JsonIgnoreCondition.Never)]` or the operator cannot set the default
   value's opposite.
3. Is any property `[JsonIgnore]`? It is not persisted, so it is not editable — hide it from the
   editor, which `FromType` now does.
4. Is any property both computed and public? `FromType` filters on `[Browsable(false)]` and
   `[JsonIgnore]`, not on settability — a get-only property without either is still offered.

## Related

- [CQRS and Content Access](../CqrsAndContentAccess) — the typed write, and why a failed READ must
  never become a written default.
- [Data Binding](/Doc/GUI/DataBinding) — the golden rule this editor implements.
- [Localization](../Localization) — the `[Description]`/`[Translation]` pair that supplies a field's
  label, independently of its key.
