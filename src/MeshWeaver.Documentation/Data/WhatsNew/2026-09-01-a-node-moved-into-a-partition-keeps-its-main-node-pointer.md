---
Name: A node moved into a partition keeps its main-node pointer
Category: Fix
Description: A record copy moved a node's path but left its main-node pointer behind, dropping it out of every listing while it still opened perfectly. The rebase is now one API that moves both, the repair covers updates as well as creates, and the trap is written down.
Icon: Bug
Order: -20260901
---

# A node moved into a partition keeps its main-node pointer

Some nodes were **absent from search while opening perfectly**. Not missing, not broken, not
erroring — `get` returned the node, `state: Active`, every field where it should be, and
`search "path:Hosting/Skill/deployment"` answered `count: 0`.

Seven of them on the live portal: four skills in `Hosting`, one each in `Essentials`,
`RemoteControl` and `Store`. A reachability sweep across all 53 packages — reading the mesh by two
independent paths, `get` and `search`, and reporting where they disagree — found the unreachable set
to be *exactly* the set whose main-node pointer had drifted, with no false positives.

Finding them cost a full investigation on a downstream repo, starting from the confident and wrong
premise that an import reporting success had silently failed to write four nodes. The nodes were
there the whole time. The "absence" was reported by the defect itself.

## What made them unlistable

Every node carries a `mainNode`. For an ordinary node it names itself; for a satellite — a comment,
an approval, an access grant — it names the node it hangs off. The catalog turns that into the
difference between listed and unlisted with one comparison: `main_node = path`.

`Path` is *computed* from the node's namespace. `MainNode` is *stored*, and its default is worked
out once, when the node is first constructed. Move a node into a different namespace with an
ordinary record copy and the two come apart:

```csharp
var minted  = new MeshNode("deployment", "Skill");          // MainNode = "Skill/deployment"
var rebased = minted with { Namespace = "Hosting/Skill" };  // Path     = "Hosting/Skill/deployment"
                                                            // MainNode = "Skill/deployment"  ← stale
```

The path moved; the pointer did not. And because the field can never be null, *"the writer never
touched it"* and *"the writer meant this"* are the same bytes on the wire — so every writer
downstream read the leftover as deliberate and faithfully wrote it again.

## Three things changed

**One rebase API.** `MeshNode.WithPath(id, ns)` / `WithNamespace(ns)` move the path and the main-node
pointer together, and leave a pointer the author set deliberately alone — a package's access grant
still scopes exactly where its file says. `with { Namespace = … }` is now the shape to grep for and
remove; four independent places in the codebase had written it.

**The repair reaches updates, not just creates.** The create path already re-stamped one flavour of
this (a node built with no namespace at all, whose leftover pointer used to route new chat threads
into a partition that did not exist). It could not see the namespaced flavour, and it never ran on
an update — so re-importing a corrupted node compared equal, skipped as a no-op, and left the
pointer wrong forever. A re-import now repairs it. The trigger stays narrow on purpose: a node that
points at its parent, or at the app a tile opens, is making a deliberate choice and is left alone.

**The trap is written down** — [MainNode and Rebasing](/Doc/Architecture/MainNodeRebasing) has the
mechanism, the one rule, the repair guard's exact trigger table, and the two incidents it has caused.

## Half of a two-part fix

Repairing the pointer is **necessary but not sufficient** for those skills to appear in search
again. A second and unrelated defect produces the identical symptom: a query union fills a legacy
single-query field with only the *first* query, and one of the two node providers reads that field
instead of the union — so anything matched only by the second query is silently absent, which is
exactly where a package's own skills sit. That half is tracked separately, and both are needed.

The origin of the stale pointers is fixed in the plugins repo, where the skill importer now mints
each skill in the partition it belongs to instead of moving it afterwards.
