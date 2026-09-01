---
Name: MainNode and Rebasing
Category: Architecture
Description: Why a `with { Namespace = … }` copy silently un-lists a node, the non-nullable-stored-property trap behind it, and the one rebase API (MeshNode.WithPath) that cannot get it wrong.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M5 9a7 7 0 0 1 7-7 7 7 0 0 1 7 7v6"/><path d="m3 12 2-3 2 3"/><circle cx="19" cy="19" r="3"/></svg>
---

# MainNode and Rebasing

Every mesh node carries a `MainNode`. For an ordinary node it names **itself**; for a satellite
(a comment, an approval, an access grant) it names the primary node the satellite belongs to. The
catalog turns that one field into the difference between *listed* and *not listed*:

```sql
-- what `is:main` means, in the catalog's own SQL
WHERE n.main_node = n.path
```

So a node whose `MainNode` drifts off its own `Path` disappears from every search, every listing,
every `scope:subtree` sweep — while `get` still returns it, `state: Active`, fully formed, with
nothing logged and no status flipped. **That failure mode is the whole reason this page exists.**

## The trap: a stored property with a constructor-time default

`MeshNode` is a record. Two of its members look alike and behave nothing alike:

```csharp
public record MeshNode(string Id, string? Namespace = null)
{
    // COMPUTED — re-evaluated on every read, so it follows a `with` copy.
    public string Path => string.IsNullOrEmpty(Namespace) ? Id : $"{Namespace}/{Id}";

    // STORED — the initializer runs ONCE, at construction, and a `with` copy carries the value over.
    public string MainNode { get; init; } = string.IsNullOrEmpty(Namespace) ? Id : $"{Namespace}/{Id}";
}
```

Rebase such a node with a plain record copy and the two diverge:

```csharp
var minted  = new MeshNode("deployment", "Skill");          // MainNode = "Skill/deployment"
var rebased = minted with { Namespace = "Hosting/Skill" };  // Path     = "Hosting/Skill/deployment"
                                                            // MainNode = "Skill/deployment"  ← stale
```

`Path` moved. `MainNode` did not. The node is now un-listable.

### Why nothing downstream can repair it

`MainNode` is **non-nullable**. On the wire, *"the writer never touched it"* and *"the writer set it
to this node itself"* are the same bytes. Every merge that has to decide whether an incoming node
means to move a stored `MainNode` therefore asks `MeshNode.HasExplicitMainNode` — *does it name
something other than this node's own path?* — because a null check cannot express the question.

For a stale rebase that predicate answers **true**: `Skill/deployment` really is *"something other
than `Hosting/Skill/deployment`"*. The stale default is indistinguishable, by shape alone, from a
deliberate satellite pointer, so every writer downstream faithfully persists it.

And the same non-nullability caps the repair from the other side: a full-instance upsert can move a
`MainNode` **anywhere except back onto the node's own path**, because that intent is exactly what
reads as *untouched*. The one route that can restore a main node is a merge patch, which can see the
key was PRESENT:

```csharp
workspace.GetMeshNodeStream(path).Update(n => n with { MainNode = n.Path })
```

## The rule

> **Never rebase a node with `with { Namespace = … }` or `with { Id = …, Namespace = … }`.
> Use `MeshNode.WithPath(id, ns)` / `MeshNode.WithNamespace(ns)`.**

`WithPath` moves `Path` and `MainNode` together, and preserves a `MainNode` the writer set
deliberately:

```csharp
new MeshNode("deployment", "Skill").WithNamespace("Hosting/Skill");
//   Path = MainNode = "Hosting/Skill/deployment"

MeshNode.Satellite("_Policy", "Teams").WithNamespace("Space/Teams");
//   Path = "Space/Teams/_Policy", MainNode = "Teams"   — explicit, so untouched
```

Two corollaries worth stating:

- **Mint in the right namespace when you can.** `new MeshNode(id, ns)` up front beats any rebase.
  This is what separated `AgentFileParser` (correct from the start) from `SkillFileParser` (which
  delegated the mint to a helper hard-wired to `"Skill"` and patched the namespace afterwards).
- **A satellite is minted with `MeshNode.Satellite(id, mainNode)`**, never with the plain
  constructor plus a later fix-up — see [Access Control](/Doc/Architecture/AccessControl) for what a self-pointing
  `_Policy` does to a package cover.

## The repair guard, and why its trigger is narrow

`HandleCreateNodeRequest` step **1b′** re-stamps a stale self-default before it is ever stored, and
`CreateOrUpdateNodeRequest` runs the same repair on the merged node so a re-import heals a row that
is already corrupted. One helper, three call sites — it was pasted twice before, and the copies were
already drifting.

The trigger is deliberately the exact bug shape, not a blanket `MainNode != Path`:

| Shape | Repaired? | Why |
|---|---|---|
| `MainNode == Id` on a namespaced node | ✅ | Built bare, namespaced later. The bare value routed a thread into a phantom partition — Postgres `42P01`. |
| `MainNode` ends with `/{Id}` **and** names a different first segment (partition) | ✅ | A self-default frozen in the namespace the node was born in. |
| `MainNode` names a parent inside the node's **own** partition | ❌ | Legitimate: `GitHubSyncConfig`'s `MainNode = spacePath`; a `~/Threads` app tile targeting `{owner}/Threads`. |
| `MainNode` names another partition under a **different** id | ❌ | Legitimate: an app tile targeting `Store/Foo` with id `Store-Foo`. |
| Any satellite node type | ❌ | Handled by step 1b, which points it at its owner. |

Both halves of the second row are load-bearing. Either one alone over-reaches, and an over-reaching
repair is the *reverse* defect: it promotes a satellite to a main node, which puts it back in its
owner's listings and re-scopes its grants (they project at `COALESCE(main_node, namespace)`).

## What this cost, twice

- **#2383** — `_Policy` satellites minted with the plain constructor pointed `MainNode` at
  themselves, so *Access Policy* was listed as content on every package cover.
- **#2939 / MeshWeaver.Plugins#1053** — every `Skill` authored as `.md` inside a plugin partition
  imported with `MainNode = "Skill/{id}"`. Six live nodes on `memex.meshweaver.cloud`
  (`Hosting/Skill/{deployment,deployment-activity,instance,platform-update}`, `Essentials/Skill/email`,
  `Store/Skill/ci-policy`) were `Active` and invisible to `search nodeType:Skill scope:subtree` — the
  documented way to find skills. It cost a full investigation on a downstream repo, from the
  confident and wrong premise that a GitSync reporting success had failed to import four nodes.

Both were latent for days behind a green wall. **A field that cannot be observed to be wrong needs a
guard, not care** — which is why the rebase now lives in one method and the repair in one helper.

## Related

- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a search miss and a missing node
  look identical from the outside
- [Access Control](/Doc/Architecture/AccessControl) — grants project at `COALESCE(main_node, namespace)`
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — where `main_node` lives
- [Negative Controls](/Doc/Architecture/NegativeControls) — a pin is only a pin if it fails against the defect
