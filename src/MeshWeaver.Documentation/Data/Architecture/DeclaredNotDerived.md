---
nodeType: Markdown
name: Declared, Not Derived
category: Architecture
description: >-
  What the platform cannot run without — language models and providers, agents, queues, policies,
  credential references — is DECLARED in a deployment record's config or in committed content, and
  that declaration is the source of truth. A boot-time importer or reconciler never deletes a
  declared item because a module did not contribute it on this replica. Why, the incident that
  produced the rule, and how to review a change against it.
icon: /static/NodeTypeIcons/box.svg
---

# Declared, Not Derived

Policy [`declared-not-derived`](../PolicyNotProse). The imperative form is a shared rule block in
`AGENTS.md` — carried by this repository now, and by every repository of the fleet once the spoke
copies land (the register row names what is owed); this page carries the reasoning and the review
checklist.

## The rule

1. **Anything the platform cannot run without is DECLARED** — in the deployment record's config
   (rendered into the instance's environment) or in committed content. That covers language models
   and providers, agents, queues, policies and credential references. The declaration is the
   source of truth.
2. **An absent contributor is not a removal.** A boot-time importer or reconciler never deletes a
   declared item because a module, plugin or registration did not contribute it on this replica.
   A module can fail to load, load into its own service container, or be declined on this
   replica; none of those says the operator withdrew the item.
3. **Pruning removes only what the declaration itself dropped** — the item is absent from the
   current declaration and was present in the previous one. Unknown provenance is never prunable.
   This is the same rule [`prune-requires-provenance`](../PolicyNotProse) states for source imports,
   applied to every catalogue a boot-time pass reconciles.
4. **A change that makes a critical catalogue depend on module wiring alone is a defect**, and so is
   an instance whose critical catalogue exists only as nodes derived at boot. The remedy for the
   second is to declare it — put it in config.

## The incident

The control instance lost every model of its EU provider (`Provider/OpenRouterEU`) at once. Those
model nodes existed only as nodes DERIVED at boot from a module's catalogue registration. A core
change gave each module its own service container (core#6127), which made that module's
contribution invisible to the boot-time Provider import — and the import, finding no contributor,
PRUNED the nodes. The deployment record declared the same models in config
(`OpenRouterEU__Models__0..14`), and nothing read the declaration as the source of truth. The
result was no reviewer model anywhere in the fleet for roughly twelve hours, because code review
runs on that provider only and fails closed ([`code-review-eu-only`](../PolicyNotProse)).

Two separate defects met there, and the rule names both: the import treated *a contributor's
absence* as *a removal* (point 2), and the catalogue depended on module wiring with the
declaration ignored (point 4). Either one alone would have left the models in place.

What this page does not establish: the exact code path of the prune and the fix to the Provider
import are owned by the change that repairs it, not by this page.

## Reviewing a change against it

Ask of any boot-time importer, seeder or reconciler a change touches:

- **What does it delete, and on what evidence?** "No module registered it on this replica" is not
  evidence of removal. "The previous declaration had it and the current one does not" is.
- **Does a critical catalogue still exist if the contributing module is absent, declined, or in its
  own container?** If no, the catalogue depends on wiring alone — a finding.
- **Is the item in the deployment record or committed content?** An item that exists only because
  a module derived it at boot is not declared, however long it has been there.

Related: [Sources Sync on Push](../SourcesSyncOnPush) → *The prune* ·
[Declared Is Not Landed](../DeclaredIsNotLanded) ·
[Model Provider Setup](/Doc/AI/ModelProviderSetup).
