---
Name: Approvals load as a module
Category: Feature
Description: Approval workflows now activate through the deployment's module list and work on every node type — not only Markdown documents — and a deployment that doesn't want them drops one config line.
Icon: CheckmarkCircle
Order: -20260816
---

# Approvals load as a module

Approval workflows — the Request Approval form, the inline approvals section on a document, and
the approve/reject views — now ship as their own module (`MeshWeaver.Approvals`) and activate
through the deployment's module list (`Modules:Assemblies`), the same lane the view packs and AI
provider packs use. Two things change for you:

- **Approvals work on every node type.** The Request Approval menu entry and the approvals
  section used to be wired to Markdown documents only; the module registers them on every node,
  so sign-off can be requested wherever it is needed.
- **Deployments choose.** An instance that doesn't use approvals drops one line from its module
  list and the whole approvals surface disappears — existing approval records stay stored and
  come back intact when the module is re-listed.

The approval views were also rebuilt on the typed control set, so status badges, detail rows,
and links now follow the portal's theme and language settings.
