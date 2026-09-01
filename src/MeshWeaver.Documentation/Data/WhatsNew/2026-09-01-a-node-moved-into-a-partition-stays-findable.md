---
Name: A node moved into a partition stays findable
Category: Fix
Description: A record copy moved a node's path but left its main-node pointer behind, and the node vanished from every search while still opening perfectly. The rebase is now one API that moves both, the repair covers updates as well as creates, and the whole trap is written down.
Icon: Bug
Order: -20260901
---

# A node moved into a partition stays findable

Some nodes were **invisible to search while opening perfectly**. Not missing, not broken, not
erroring — `get` returned the node, `state: Active`, every field where it should be, and
`search "path:Hosting/Skill/deployment"` answered `count: 0`.

Six of them on the live portal: four skills in `Hosting`, one in `Essentials`, one in `Store`. All
authored the same way, all imported by the same path. Finding them cost a full investigation on a
downstream repo, starting from the confident and wrong premise that an import reporting success had
silently dropped four nodes.

## What made them invisible

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
remove; four independent places in the codebase had written it, and each one could produce a node
nobody could find.

**The repair reaches updates, not just creates.** The create path already re-stamped one flavour of
this (a node built with no namespace at all, whose leftover pointer used to route new chat threads
into a partition that did not exist). It could not see the namespaced flavour, and it never ran on
an update — so re-importing a corrupted node compared equal, skipped as a no-op, and left it
invisible forever. A re-import now heals it. The trigger stays narrow on purpose: a node that points
at its parent, or at the app a tile opens, is making a deliberate choice and is left alone.

**The trap is written down** — [MainNode and Rebasing](/Doc/Architecture/MainNodeRebasing) has the
mechanism, the one rule, the repair guard's exact trigger table, and the two incidents it has caused.

Paired with the origin fix in the plugins repo, where the skill importer now mints each skill in the
partition it belongs to instead of moving it afterwards.
